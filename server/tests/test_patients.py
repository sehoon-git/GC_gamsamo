"""Cross-patient isolation and authored story timing; fixtures do not grade language quality."""

import httpx
import json
import pytest

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError, InvalidModelOutput, Settings
from app.llm.mock import MockLLM
from app.main import create_app
from app.schemas.models import CaseState, Classification, EvaluateRequest, Metrics, OpenRequest, PATIENT_IDS, Session
from app.schemas.models import TurnRequest, Opening, PatientReply, CloseRequest, next_session
from app.services.catalog import Catalog
from app.services.dialogue import DialogueService
from app.services.evaluation import EvaluationService


@pytest.mark.asyncio
@pytest.mark.parametrize('patient_id', PATIENT_IDS)
async def test_patient_lifecycle_and_isolated_save(patient_id, tmp_path):
    settings = Settings(mock=True)
    catalog = Catalog(settings, patient_id)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(settings)), base_url='http://test') as http:
        payload = dict(patient_id=patient_id, case_id='case_'+patient_id, session_id='visit_one', request_id='opening')
        response = await http.post('/v1/dialogue/start', json=payload)
        assert response.status_code == 200, response.text
        state = CaseState.model_validate(response.json()['state'])
        assert state.patient_id == patient_id and state.content_version == patient_id+'-4.0'
        if patient_id != 'seoyun':
            assert catalog.patient['name'] in state.current.opening.patient_text
            assert '면접' not in state.current.opening.patient_text
        store = SaveStore(tmp_path, patient_id=patient_id)
        store.save(state)
        client = SessionClient(settings, store, state, http=http)
        await client.turn('그 마음은 어떠셨나요? 서두르지 않으셔도 괜찮아요.')
        await client.finish()
        result = await client.evaluate()
        assert {s.criterion_id for s in result.scores} == set(catalog.rubric)
        assert result.patient_score == result.counselor_score == 0 and not result.cleared
        prior = client.state.model_dump(mode='json')
        again = await http.post('/v1/dialogue/revisit', json=dict(state=prior, session_id='visit_two', request_id='opening_two'))
        assert again.status_code == 200, again.text
        updated = CaseState.model_validate(again.json()['state'])
        assert updated.patient_id == patient_id and updated.sessions[0] == client.state.current
        assert store.load().patient_id == patient_id
        other = 'harin' if patient_id != 'harin' else 'seoyun'
        with pytest.raises(ValueError):
            SaveStore(tmp_path, patient_id=other).save(client.state)
        with pytest.raises(ExperimentError) as mismatch:
            Catalog(settings, other).validate_state(client.state)
        assert mismatch.value.code == 'patient_mismatch'


@pytest.mark.parametrize('patient_id', PATIENT_IDS)
def test_opening_contains_only_this_character_and_available_story(patient_id):
    settings = Settings(mock=True)
    catalog = Catalog(settings, patient_id)
    service = DialogueService(MockLLM(settings), catalog)
    state = CaseState(patient_id=patient_id)
    work = dict(case=state, request=OpenRequest(state=state, session_id=state.current.id, request_id='opening'),
                metrics=Metrics(), allowed=[], classification=Classification(reason='첫 발화', topic_label='상담 시작', disclose_fact_ids=[]))
    prompt = service.patient_messages(work)[0]['content']
    assert catalog.patient['name'] in prompt
    for private in catalog.facts.values():
        assert private['text'] not in prompt
    if patient_id != 'seoyun':
        assert '서윤' not in prompt and '면접 날짜만 잡히면' not in prompt
    if patient_id == 'yeonwoo':
        assert all('이거 꼭 보여줘!' not in message['content'] for message in service.patient_messages(work))
        evaluator = EvaluationService(MockLLM(settings), catalog)
        evaluation_work = {'request': EvaluateRequest(state=state, session_id=state.current.id, request_id='evaluation')}
        assert '이거 꼭 보여줘!' not in evaluator.messages(evaluation_work, 'evaluation', {})[1]['content']
        # The lifecycle test above independently validates a real revisit. Here inspect the authored scene selector.
        next_visit = state.model_copy(deep=True)
        next_visit.sessions.append(Session())
        work['case'] = next_visit
        assert '이거 꼭 보여줘!' in service.patient_messages(work)[0]['content']
        assert '이번 첫 발화에서 그 사건을 먼저 꺼내세요' in service.patient_messages(work)[0]['content']
        assert '2번째 상담' in service.patient_messages(work)[-1]['content']
        assert '이거 꼭 보여줘!' in service.patient_messages(work)[-1]['content']


@pytest.mark.asyncio
async def test_cross_patient_version_and_disclosure_rejected():
    settings = Settings(mock=True)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(settings)), base_url='http://test') as http:
        state = CaseState(patient_id='harin').model_dump(mode='json')
        state['content_version'] = 'seoyun-4.0'
        reply = await http.post('/v1/dialogue/open', json=dict(state=state, session_id=state['sessions'][0]['id'], request_id='open'))
        assert reply.status_code == 422 and reply.json()['detail']['code'] == 'unsupported_save_version'
        start = await http.post('/v1/dialogue/start', json=dict(patient_id='harin', case_id='case_harin', session_id='one', request_id='opening'))
        state = start.json()['state']
        state['sessions'][0]['opening']['candidate_fact_ids'] = ['cafe_strength']
        state['revealed_fact_ids'] = ['cafe_strength']
        reply = await http.post('/v1/dialogue/turn', json=dict(state=state, session_id='one', turn_id='bad', text='안녕하세요'))
        assert reply.status_code == 422
        for unsupported in ['minseok', 'jieun', '../seoyun']:
            reply = await http.post('/v1/dialogue/start', json=dict(patient_id=unsupported, case_id='unsupported', session_id='one', request_id='opening'))
            assert reply.status_code in (404, 422)


def test_saved_patient_cannot_be_reinterpreted_with_another_catalog(tmp_path):
    settings = Settings(mock=True)
    store = SaveStore(tmp_path, patient_id='dohyeon')
    state = CaseState(patient_id='dohyeon')
    store.save(state)
    with pytest.raises(ValueError):
        SaveStore(store.directory, patient_id='seoyun').load()
    with pytest.raises(ExperimentError):
        Catalog(settings, 'taejun').validate_state(state)


@pytest.mark.parametrize('patient_id', ['harin', 'dohyeon', 'taejun', 'yeonwoo'])
def test_character_specific_rubric_and_authored_choices(patient_id):
    patient = Catalog(Settings(), patient_id).patient
    assert len(patient['private_facts']) == 3
    assert all(item['id'].startswith(patient_id+'_') for item in patient['private_facts'])
    assert {r['axis'] for r in patient['rubric']} == {'patient', 'counselor'}
    assert patient['termination'] == 'user_requested' and patient['clear_threshold'] == 70
    assert patient['boundaries'] and patient['unspecified']


@pytest.mark.parametrize('patient_id', PATIENT_IDS)
def test_reduced_context_preserves_all_authored_facts_conditions_and_rubric_once(patient_id):
    settings = Settings(mock=True)
    catalog = Catalog(settings, patient_id)
    state = CaseState(patient_id=patient_id)
    dialogue = DialogueService(MockLLM(settings), catalog)
    work = {'case': state, 'request': TurnRequest(state=state, session_id=state.current.id, turn_id='one', text='어떤 마음인가요?')}
    control = json.loads(dialogue.management_messages(work)[1]['content'].split('\n', 1)[1])['patient']
    evaluation = EvaluationService(MockLLM(settings), catalog)
    payload = json.loads(evaluation.messages({'request': EvaluateRequest(state=state, session_id=state.current.id, request_id='grade')}, 'evaluation', {})[-1]['content'])
    assert payload['rubric'] == catalog.patient['rubric']
    assert 'rubric' not in control and 'rubric' not in payload['fixed_story']
    for key in ('public_facts', 'private_facts', 'direction', 'unspecified', 'possible_change', 'boundaries'):
        if key in catalog.patient:
            assert control[key] == payload['fixed_story'][key] == catalog.patient[key]
    assert control['story_events'] == payload['fixed_story']['story_events'] == catalog.active_story_events(state)


@pytest.mark.asyncio
@pytest.mark.parametrize('patient_id', PATIENT_IDS)
async def test_revisit_prompt_uses_real_prior_dialogue_and_rejects_identical_introduction(patient_id):
    settings = Settings(mock=True)
    service = DialogueService(MockLLM(settings), Catalog(settings, patient_id))
    state = CaseState(patient_id=patient_id)
    original = '안녕하세요. 오늘은 마음에 남은 고민을 이야기하고 싶어요.'
    state.current.opening = Opening(id='previous', patient_text=original)
    closed = await service.close_session(CloseRequest(state=state, session_id=state.current.id, request_id='close'))
    evaluated = await EvaluationService(service.llm, service.catalog).evaluate(EvaluateRequest(state=closed.state, session_id=state.current.id, request_id='grade'))
    state = next_session(evaluated.state)
    state.current.id = 'second'
    work = dict(case=state, request=OpenRequest(state=state, session_id='second', request_id='new'),
                allowed=[], classification=Classification(reason='재방문', topic_label='상담 시작', disclose_fact_ids=[]))
    messages = service.patient_messages(work)
    assert any(m['role'] == 'assistant' and m['content'] == original for m in messages)
    assert '자신이 마지막으로 말했던 고민' in messages[0]['content']
    # Undiscussed background/future aspirations cannot masquerade as a memory.
    for fact in service.catalog.patient['public_facts']:
        assert fact not in messages[0]['content']
    if patient_id == 'yeonwoo':
        assert '이거 꼭 보여줘!' in messages[0]['content']
    with pytest.raises(InvalidModelOutput, match='그대로 반복'):
        service.validate_reply(work, PatientReply(text=original, disclosed_fact_ids=[]))
    service.validate_reply(work, PatientReply(text='지난번에 말한 고민이 아직 남아 있어서 더 이야기하고 싶어요.', disclosed_fact_ids=[]))


@pytest.mark.asyncio
@pytest.mark.parametrize('state', [{'patient_id': 42}, None, []])
async def test_malformed_patient_state_is_a_client_error(state):
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(Settings(mock=True))), base_url='http://test') as http:
        response = await http.post('/v1/dialogue/open', json=dict(state=state, session_id='session', request_id='request'))
        assert response.status_code == 422


@pytest.mark.parametrize('text', [
    '아이 아빠가 영상을 보냈어요.', '아이의 엄마와 이야기했어요.',
    '전 남편과 통화했어요.', '우리 딸이 신나 보여요.',
])
def test_yeonwoo_rejects_unapproved_family_gender(text):
    settings = Settings(mock=True)
    service = DialogueService(MockLLM(settings), Catalog(settings, 'yeonwoo'))
    state = CaseState(patient_id='yeonwoo')
    work = dict(case=state, request=OpenRequest(state=state, session_id=state.current.id, request_id='opening'), allowed=[])
    with pytest.raises(InvalidModelOutput, match='미정인 성별'):
        service.validate_reply(work, PatientReply(text=text, disclosed_fact_ids=[]))


@pytest.mark.parametrize('text', ['다른 부모가 영상을 보내 줬어요.', '아이가 즐거웠다고 해서 기뻐요.', '이전처럼 아이에게 제 마음을 달래 달라고 하고 싶지는 않아요.'])
def test_yeonwoo_accepts_authored_neutral_family_roles(text):
    settings = Settings(mock=True)
    service = DialogueService(MockLLM(settings), Catalog(settings, 'yeonwoo'))
    state = CaseState(patient_id='yeonwoo')
    work = dict(case=state, request=TurnRequest(state=state, session_id=state.current.id, turn_id='reply', text='마음이 어떤가요?'), allowed=[])
    service.validate_reply(work, PatientReply(text=text, disclosed_fact_ids=[]))
