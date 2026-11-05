import pytest

from app.core.config import ExperimentError
from app.schemas.models import CaseState, CloseRequest, TurnRequest
from app.services.dialogue import DialogueService
from conftest import FakeLLM, case_with_questions, classification, reply


@pytest.mark.asyncio
@pytest.mark.parametrize("text,count", [("안녕하세요", "none"), ("많이 힘드셨겠어요.", "none"),
    ("그때 어떤 마음이었나요?", "one"), ("같은 질문을 다시 해도 될까요?", "one"), ("그때 마음이 궁금해요.", "one")])
async def test_question_delta_for_neutral_followup_repeated_and_indirect(catalog, text, count):
    state = case_with_questions(1)
    llm = FakeLLM([("Classification", classification(count)), ("PatientReply", reply())])
    result = await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="next", text=text))
    assert result.state.current.questions_used == 1 + (count == "one")
    assert result.state.current.turns[-1].question_delta == (count == "one")
    assert state.current.questions_used == 1


@pytest.mark.asyncio
async def test_fifth_answer_then_separate_closing_and_no_sixth_turn(catalog):
    state = case_with_questions(4)
    llm = FakeLLM([("Classification", classification()), ("PatientReply", reply()), ("PatientReply", reply("오늘 한 말을 생각해 볼게요."))])
    service = DialogueService(llm, catalog)
    result = await service.turn(TurnRequest(state=state, session_id=state.current.id, turn_id="fifth", text="언제 쉬고 싶으세요?"))
    assert result.state.current.phase == "closing"
    assert result.state.current.questions_used == 5
    assert not result.state.current.closed_at
    assert len(llm.calls) == 2
    with pytest.raises(ExperimentError) as error:
        await service.turn(TurnRequest(state=result.state, session_id=state.current.id, turn_id="sixth", text="고마워요"))
    assert error.value.code == "session_closed"
    closed = await service.close_session(CloseRequest(state=result.state, session_id=state.current.id, request_id="closing"))
    assert closed.state.current.phase == "pending_evaluation"
    assert closed.state.current.turns[-1].closing_text
    assert closed.state.revision == result.state.revision + 1
    assert not result.state.current.closed_at
    replay = await service.close_session(CloseRequest(state=closed.state, session_id=state.current.id, request_id="closing"))
    assert replay.replayed and replay.state == closed.state and len(llm.calls) == 3


@pytest.mark.asyncio
async def test_compound_rejected_before_answer_without_mutating_input(catalog):
    state = case_with_questions(4)
    original = state.model_dump_json()
    llm = FakeLLM([("Classification", classification("multiple"))])
    with pytest.raises(ExperimentError) as error:
        await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="compound", text="면접은요? 친구는요?"))
    assert error.value.code == "needs_split"
    assert state.model_dump_json() == original and len(llm.calls) == 1


@pytest.mark.asyncio
async def test_disclosure_is_proposal_only(catalog):
    state = CaseState()
    llm = FakeLLM([("Classification", classification(disclose_fact_ids=["cafe_strength"])),
                   ("PatientReply", {"text": "카페에서 신입 교육도 맡고 있어요.", "disclosed_fact_ids": ["cafe_strength"]})])
    result = await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="fact", text="카페에서 맡은 일이 궁금해요."))
    assert result.disclosure_candidates == result.state.current.turns[-1].candidate_fact_ids == ["cafe_strength"]
    assert result.state.revealed_fact_ids == []


@pytest.mark.asyncio
@pytest.mark.parametrize("value", [{"text": "비밀을 말할게요.", "disclosed_fact_ids": ["not_a_fact"]}, reply("지금 状況이 어렵네요.")])
async def test_bad_reveal_or_language_never_commits(catalog, value):
    state = CaseState()
    original = state.model_dump_json()
    llm = FakeLLM([("Classification", classification()), ("PatientReply", value)])
    with pytest.raises(ValueError):
        await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="bad", text="이야기해 주세요."))
    assert state.model_dump_json() == original


@pytest.mark.asyncio
async def test_duplicate_committed_turn_is_replayed_without_inference(catalog):
    case, llm = case_with_questions(1), FakeLLM()
    request = TurnRequest(state=case, session_id=case.current.id, turn_id="turn_0", text=case.current.turns[0].user_text)
    response = await DialogueService(llm, catalog).turn(request)
    assert response.replayed and len(response.state.current.turns) == 1 and not llm.calls
    request.text = "다른 입력"
    with pytest.raises(ExperimentError):
        await DialogueService(llm, catalog).turn(request)


@pytest.mark.asyncio
async def test_patient_prompt_never_receives_unapproved_private_details(catalog):
    state = CaseState()
    llm = FakeLLM([("Classification", classification("none")), ("PatientReply", reply())])
    await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="hello", text="안녕하세요"))
    prompt = next(c[2][0]["content"] for c in llm.calls if c[1] == "PatientReply")
    assert "친한 친구가 서윤이 지원했다 떨어진 회사에 합격했다" not in prompt
    assert "신입 교육도 맡는다" not in prompt


@pytest.mark.asyncio
async def test_failed_closing_leaves_fifth_answer_recoverable(catalog):
    state = case_with_questions()
    before = state.model_dump_json()
    llm = FakeLLM([("PatientReply", RuntimeError("inference failed"))])
    with pytest.raises(RuntimeError):
        await DialogueService(llm, catalog).close_session(CloseRequest(state=state, session_id=state.current.id, request_id="closing"))
    assert state.model_dump_json() == before and state.current.phase == "closing"


@pytest.mark.asyncio
async def test_echoing_counselor_question_is_not_committed(catalog):
    state = CaseState()
    text = "지금 이야기해도 괜찮다면 가장 부담스러운 점 하나만 말씀해 주세요."
    llm = FakeLLM([("Classification", classification()), ("PatientReply", reply(text))])
    with pytest.raises(ValueError, match="그대로 반복"):
        await DialogueService(llm, catalog).turn(TurnRequest(state=state, session_id=state.current.id, turn_id="echo", text=text))
    assert not state.current.turns and state.revision == 0
