"""Cross-patient isolation and authored story timing; fixtures do not grade language quality."""

import httpx
import pytest

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError, Settings
from app.llm.mock import MockLLM
from app.main import create_app
from app.schemas.models import CaseState, Classification, Metrics, OpenRequest, PATIENT_IDS, Session
from app.services.catalog import Catalog
from app.services.dialogue import DialogueService


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
        assert '이거 꼭 보여줘!' not in prompt
        # The lifecycle test above independently validates a real revisit. Here inspect the authored scene selector.
        next_visit = state.model_copy(deep=True)
        next_visit.sessions.append(Session())
        work['case'] = next_visit
        assert '이거 꼭 보여줘!' in service.patient_messages(work)[0]['content']
        assert '이번 첫 발화에서 그 사건을 먼저 꺼내세요' in service.patient_messages(work)[0]['content']


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


@pytest.mark.asyncio
@pytest.mark.parametrize('state', [{'patient_id': 42}, None, []])
async def test_malformed_patient_state_is_a_client_error(state):
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(Settings(mock=True))), base_url='http://test') as http:
        response = await http.post('/v1/dialogue/open', json=dict(state=state, session_id='session', request_id='request'))
        assert response.status_code == 422
