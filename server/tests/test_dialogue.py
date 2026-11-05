import pytest

from app.core.config import ExperimentError, InvalidModelOutput
from app.schemas.models import CaseState, TurnRequest
from app.services.dialogue import DialogueService
from conftest import FakeLLM, case_with_topics, classification, reply


@pytest.mark.asyncio
async def test_fifth_main_question_cannot_close_before_followup(catalog):
    state = case_with_topics(4)
    llm = FakeLLM([
        ("Classification", classification("new_topic", None, topic_label="휴식 계획")),
        ("PatientReply", reply()),
    ])
    result = await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="fifth", text="언제 쉬고 싶으세요?"))
    assert len(result.state.current.topics) == 5
    assert result.state.current.post_budget_turns == 0
    assert result.state.current.closed_at is None
    assert len(state.current.topics) == 4
    assert [x[1] for x in llm.calls] == ["Classification", "PatientReply"]


@pytest.mark.asyncio
async def test_followup_can_close_without_sixth_main_question(catalog):
    state = case_with_topics()
    llm = FakeLLM([
        ("Classification", classification()), ("PatientReply", reply()),
        ("Closure", {"ready": True, "unresolved_questions": [], "reason": "후속 대화 완료"}),
        ("PatientReply", reply("오늘 이야기한 것을 조금 생각해 보고 싶어요.")),
    ])
    result = await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="followup", text="지금 정한 계획은 어떤 느낌인가요?"))
    assert len(result.state.current.topics) == 5
    assert result.state.current.post_budget_turns == 1
    assert result.state.current.closed_at
    assert result.state.current.turns[-1].closing_text
    assert result.state.current.evaluation is None


@pytest.mark.asyncio
async def test_new_topic_after_budget_is_deferred(catalog):
    state = case_with_topics()
    llm = FakeLLM([
        ("Classification", classification("new_topic", None, topic_label="새 주제", disclose_fact_ids=["friend_envy"])),
        ("PatientReply", reply()),
        ("Closure", {"ready": False, "unresolved_questions": ["아직 답변을 기다리는 질문"], "reason": "후속 질문 남음"}),
    ])
    result = await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="sixth", text="다른 이야기도 해볼까요?"))
    assert len(result.state.current.topics) == 5
    assert result.state.current.turns[-1].kind == "deferred"
    assert "새 주제" in result.state.deferred_topics
    assert not result.state.revealed_fact_ids
    assert not result.state.current.closed_at


@pytest.mark.asyncio
async def test_bad_reveal_does_not_commit_classification_or_input(catalog):
    state = CaseState()
    original = state.model_dump_json()
    llm = FakeLLM([
        ("Classification", classification("new_topic", None)),
        ("PatientReply", {"text": "비밀을 말할게요.", "disclosed_fact_ids": ["not_a_fact"]}),
    ])
    with pytest.raises(ValueError):
        await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="bad", text="이야기해 주세요."))
    assert state.model_dump_json() == original


@pytest.mark.asyncio
async def test_greeting_and_compound_queue(catalog):
    llm = FakeLLM([
        ("Classification", classification("neutral", None)), ("PatientReply", reply()),
        ("Classification", classification("new_topic", None, topic_label="면접", queued_topics=["친구"])), ("PatientReply", reply()),
        ("Classification", classification("new_topic", None, topic_label="친구")), ("PatientReply", reply()),
    ])
    service = DialogueService(llm, catalog)
    case = CaseState()
    for i, text in enumerate(["안녕하세요", "면접과 친구 이야기를 해 주세요", "친구 이야기를 이어갈까요?"]):
        result = await service.turn(TurnRequest(state=case, session_id=case.current.id, turn_id=f"compound_{i}", text=text))
        case = result.state
        assert len(case.current.topics) == i
    assert case.deferred_topics == []


@pytest.mark.asyncio
async def test_duplicate_committed_turn_is_replayed_without_inference(catalog):
    case = case_with_topics(1)
    llm = FakeLLM()
    request = TurnRequest(state=case, session_id=case.current.id, turn_id="turn_0", text=case.current.turns[0].user_text)
    response = await DialogueService(llm, catalog).turn(request)
    assert response.replayed
    assert len(response.state.current.turns) == 1
    assert not llm.calls
    request.text = "다른 입력"
    with pytest.raises(ExperimentError):
        await DialogueService(llm, catalog).turn(request)


@pytest.mark.asyncio
async def test_patient_prompt_never_receives_unapproved_private_details(catalog):
    state = CaseState()
    llm = FakeLLM([("Classification", classification("neutral", None)), ("PatientReply", reply())])
    await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="hello", text="안녕하세요"))
    patient_prompt = next(c[2][0]["content"] for c in llm.calls if c[1] == "PatientReply")
    assert "친한 친구가 서윤이 지원했다 떨어진 회사에 합격했다" not in patient_prompt
    assert "신입 교육도 맡는다" not in patient_prompt


@pytest.mark.asyncio
async def test_language_mixing_is_rejected_without_committing(catalog):
    state = CaseState()
    llm = FakeLLM([("Classification", classification("neutral", None)), ("PatientReply", reply("지금 状況이 어렵네요."))])
    with pytest.raises(ValueError):
        await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="mixed", text="안녕하세요"))
    assert state.current.turns == []
