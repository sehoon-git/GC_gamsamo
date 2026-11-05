import pytest

from app.core.config import InvalidModelOutput
from app.schemas.models import EvaluateRequest, Evaluation, Metrics, Session, now, records
from app.services.evaluation import EvaluationService
from conftest import FakeLLM, case_with_questions, closed_case


def test_numbered_passages_preserve_every_source_character():
    from app.services.evaluation import numbered_source
    original = [{"id": "raw", "speaker": "patient", "session_id": "session", "text": "한글과 공백을 보존합니다.\n다음 이야기입니다. " * 40}]
    aliases, source = numbered_source(original)
    assert len(source) > 1
    assert "".join(record["text"] for record in source) == original[0]["text"]
    assert all(aliases[record["id"]] == "raw" and len(record["text"]) <= 180 for record in source)


def judgment(catalog, case, patient=70, counselor=70):
    scores = {}
    for axis, total in [("patient", patient), ("counselor", counselor)]:
        index, source = next((i, r) for i, r in enumerate(records(case)) if r.speaker == axis and r.session_id == case.current.id)
        for criterion in [r for r in catalog.rubric.values() if r["axis"] == axis]:
            value, total = min(25, total), max(0, total - 25)
            scores[criterion["id"]] = {"score": value, "explanation": "실제 발언을 근거로 평가했습니다.",
                                      "evidence": [{"message_id": f"M{index + 1:04d}", "counter": False}] if value else []}
    return scores


@pytest.mark.asyncio
@pytest.mark.parametrize("patient,counselor,passed", [(69, 100, False), (100, 69, False), (70, 70, True), (0, 0, False)])
async def test_both_thresholds_are_required(catalog, patient, counselor, passed):
    case = closed_case()
    llm = FakeLLM([("JudgmentProposal", judgment(catalog, case, patient, counselor))])
    response = await EvaluationService(llm, catalog).evaluate(EvaluateRequest(state=case, session_id=case.current.id, request_id="grade"))
    evaluation = response.state.current.evaluation
    assert (evaluation.patient_score, evaluation.counselor_score, evaluation.cleared) == (patient, counselor, passed)
    assert all(e.message_id in {r.id for r in records(case)} for s in evaluation.scores for e in s.evidence)
    assert case.current.evaluation is None
    again = await EvaluationService(llm, catalog).evaluate(EvaluateRequest(state=response.state, session_id=case.current.id, request_id="retry"))
    assert again.replayed
    assert len(llm.calls) == 1


@pytest.mark.asyncio
async def test_unknown_passage_cannot_award_points(catalog):
    case = closed_case()
    value = judgment(catalog, case)
    value["self_worth"]["evidence"][0]["message_id"] = "M9999"
    with pytest.raises(ValueError):
        await EvaluationService(FakeLLM([("JudgmentProposal", value)]), catalog).evaluate(EvaluateRequest(state=case, session_id=case.current.id, request_id="bad"))
    assert case.current.evaluation is None


def test_old_counselor_evidence_is_rejected_but_old_patient_evidence_allowed(catalog):
    service = EvaluationService(FakeLLM(), catalog)
    from app.schemas.models import Evidence
    mapping = {
        "old:user": {"id": "old:user", "speaker": "counselor", "session_id": "old", "text": "예전의 상담자 발언"},
        "old:patient": {"id": "old:patient", "speaker": "patient", "session_id": "old", "text": "예전의 서윤 발언"},
    }
    with pytest.raises(InvalidModelOutput):
        service.validate_evidence("empathy", [Evidence(message_id="old:user", quote="상담자 발언")], mapping, "current")
    service.validate_evidence("self_worth", [Evidence(message_id="old:patient", quote="서윤 발언")], mapping, "current")


@pytest.mark.asyncio
async def test_long_evaluation_visits_all_records(catalog):
    from app.schemas.models import EvidenceProposal
    class ExtractingLLM(FakeLLM):
        def __init__(self):
            super().__init__()
            self.visited = set()
        def fits(self, messages, role):
            import json
            payload = json.loads(messages[-1]["content"])
            return len(payload.get("records", [])) <= 2
        async def generate(self, role, messages, schema, metrics, validate=None):
            import json
            self.visited.update(r["id"] for r in json.loads(messages[-1]["content"]).get("records", []))
            assert schema is EvidenceProposal
            return EvidenceProposal(criteria=[])
    case = case_with_questions()
    llm = ExtractingLLM()
    work = {"request": EvaluateRequest(state=case, session_id=case.current.id, request_id="long"),
            "metrics": Metrics(), "source": [r.model_dump() for r in records(case)]}
    result = await EvaluationService(llm, catalog).prepare(work)
    assert llm.visited == {r.id for r in records(case)}
    assert result == {"prepared": {"evidence": []}}


@pytest.mark.asyncio
async def test_retry_consultation_carries_memory_but_resets_question_budget(catalog):
    from app.schemas.models import Memory, MemoryItem, next_session
    case = closed_case()
    case.current.turns[0].candidate_fact_ids = ["cafe_strength"]
    case.revealed_fact_ids = ["cafe_strength"]
    case.memory = Memory(through_turn_id="turn_4", items=[MemoryItem(category="emotion", speaker="patient", text="마음을 돌아봄", source_ids=["turn_4:patient"])])
    case.current.closed_at = now()
    llm = FakeLLM([("JudgmentProposal", judgment(catalog, case, 60, 70))])
    response = await EvaluationService(llm, catalog).evaluate(EvaluateRequest(state=case, session_id=case.current.id, request_id="grade"))
    continued = next_session(response.state)
    assert len(continued.sessions) == 2
    assert continued.current.questions_used == 0 and not continued.current.turns
    assert continued.memory == case.memory
    assert continued.revealed_fact_ids == ["cafe_strength"]
    catalog.validate_state(continued)


@pytest.mark.asyncio
async def test_cleared_case_cannot_start_another_consultation(catalog):
    from app.schemas.models import next_session
    case = closed_case()
    llm = FakeLLM([("JudgmentProposal", judgment(catalog, case, 70, 70))])
    result = await EvaluationService(llm, catalog).evaluate(EvaluateRequest(state=case, session_id=case.current.id, request_id="grade"))
    with pytest.raises(ValueError, match="완료된"):
        next_session(result.state)


@pytest.mark.asyncio
async def test_positive_scores_keep_support_and_counter_evidence(catalog):
    case = closed_case()
    proposal = judgment(catalog, case, 70, 70)
    proposal['self_worth']['counter_evidence'] = [{'message_id': 'M0004', 'counter': True}]
    result = await EvaluationService(FakeLLM([('JudgmentProposal', proposal)]), catalog).evaluate(
        EvaluateRequest(state=case, session_id=case.current.id, request_id='both-sides'))
    evidence = next(s for s in result.state.current.evaluation.scores if s.criterion_id == 'self_worth').evidence
    assert [e.counter for e in evidence] == [False, True]
    assert all(e.quote in next(r.text for r in records(case) if r.id==e.message_id) for e in evidence)


@pytest.mark.asyncio
async def test_counter_only_positive_score_is_rejected_by_output_schema(catalog):
    case = closed_case()
    proposal = judgment(catalog, case)
    proposal['self_worth']['evidence'][0]['counter'] = True
    with pytest.raises(ValueError):
        await EvaluationService(FakeLLM([('JudgmentProposal', proposal)]), catalog).evaluate(
            EvaluateRequest(state=case, session_id=case.current.id, request_id='counter-only'))
    assert case.current.evaluation is None
