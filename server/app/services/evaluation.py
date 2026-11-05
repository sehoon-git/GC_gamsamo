from time import perf_counter
from typing import Literal, TypedDict, Union

from pydantic import Field, create_model

from langgraph.graph import END, START, StateGraph

from app.core.config import ExperimentError, InvalidModelOutput
from app.llm.client import compact
from app.schemas.models import (
    CaseState, EvaluateRequest, Evaluation, EvidenceChoice, EvidenceProposal, Judgment,
    Metrics, Model, ScoreProposal, StateResponse, now, records,
)


class EvalWork(TypedDict, total=False):
    request: EvaluateRequest
    metrics: Metrics
    source: list[dict]
    prepared: dict
    judgment: Judgment


def numbered_source(original):
    """Keep every source character while giving short passages selectable IDs."""
    aliases, source = {}, []
    for record in original:
        remaining = record["text"]
        while remaining:
            end = min(len(remaining), 180)
            if end < len(remaining):
                boundary = remaining.rfind(". ", 0, end)
                if boundary >= 40:
                    end = boundary + 2
            passage, remaining = remaining[:end], remaining[end:]
            alias = f"M{len(source) + 1:04d}"
            aliases[alias] = record["id"]
            source.append({**record, "id": alias, "text": passage})
    return aliases, source


class EvaluationService:
    def __init__(self, llm, catalog):
        self.llm, self.catalog = llm, catalog
        graph = StateGraph(EvalWork)
        graph.add_node("prepare_evidence", self.prepare)
        graph.add_node("score", self.score)
        graph.add_edge(START, "prepare_evidence")
        graph.add_edge("prepare_evidence", "score")
        graph.add_edge("score", END)
        self.graph = graph.compile()

    def messages(self, work, prompt, payload):
        return [{"role": "system", "content": self.catalog.prompts[prompt]}, {"role": "user", "content": compact({
            "rubric": self.catalog.patient["rubric"],
            "current_session_id": work["request"].session_id,
            "fixed_story": self.catalog.patient,
            **payload,
        })}]

    def validate_evidence(self, criterion_id, evidence, record_map, session_id):
        if criterion_id not in self.catalog.rubric:
            raise InvalidModelOutput("알 수 없는 평가 항목입니다.")
        axis = self.catalog.rubric[criterion_id]["axis"]
        for e in evidence:
            r = record_map.get(e.message_id)
            if r is None:
                raise InvalidModelOutput(f"{criterion_id}: 발언 {e.message_id}가 없습니다. 제공된 발언 번호만 사용하세요.")
            if not e.quote.strip() or e.quote not in r["text"]:
                raise InvalidModelOutput(f"{criterion_id}: {e.message_id}의 인용이 원문과 다릅니다. 짧은 연속 구절을 그대로 복사하세요. 원문: {r['text'][:400]}")
            if r["speaker"] != axis:
                raise InvalidModelOutput(f"{criterion_id}: {e.message_id}는 {r['speaker']}의 발언입니다. 이 항목은 {axis} 발언을 선택해야 합니다.")
            if axis == "counselor" and r["session_id"] != session_id:
                raise InvalidModelOutput("상담자 점수는 이번 상담의 발언만 사용하세요.")

    def validate_judgment(self, work, value):
        if {s.criterion_id for s in value.scores} != self.catalog.rubric.keys():
            raise InvalidModelOutput("8개 평가 항목을 중복 없이 모두 반환해야 합니다.")
        record_map = {r["id"]: r for r in work["source"]}
        for item in value.scores:
            self.validate_evidence(item.criterion_id, item.evidence, record_map, work["request"].session_id)
            if item.score > 0 and not any(not e.counter for e in item.evidence):
                raise InvalidModelOutput(f"{item.criterion_id}: 양수 점수에는 counter=false인 실제 발언 번호가 필요합니다. 지지 근거가 없으면 score=0으로 반환하세요.")

    async def extract(self, work, payload):
        source_map = {r["id"]: r for r in work["source"]}
        provided = None
        if "evidence" in payload:
            provided = {(i["criterion_id"], e["message_id"], e["quote"], e["counter"])
                        for i in payload["evidence"] for e in i["evidence"]}
        else:
            # A long original utterance may occur as adjacent slices with the same ID.
            source_map = {}
            for record in payload["records"]:
                if record["id"] in source_map:
                    source_map[record["id"]]["text"] += record["text"]
                else:
                    source_map[record["id"]] = dict(record)

        def validate(value):
            ids = [i.criterion_id for i in value.criteria]
            if len(ids) != len(set(ids)):
                raise InvalidModelOutput("근거 추출 항목이 중복되었습니다.")
            for item in value.criteria:
                self.validate_evidence(item.criterion_id, item.evidence, source_map, work["request"].session_id)
                if provided is not None and any((item.criterion_id, e.message_id, e.quote, e.counter) not in provided for e in item.evidence):
                    raise InvalidModelOutput("재압축 시 제공된 근거의 ID·인용·찬반 구분을 유지하세요.")

        def resolve(value):
            from app.schemas.models import EvidenceCollection
            return EvidenceCollection(criteria=[{
                "criterion_id": item.criterion_id,
                "evidence": [{**e.model_dump(), "quote": source_map.get(e.message_id, {}).get("text", "missing")}
                             for e in item.evidence],
            } for item in value.criteria])

        value = await self.llm.generate("evidence", self.messages(work, "evidence", payload), EvidenceProposal,
                                        work["metrics"], lambda value: validate(resolve(value)))
        return [i.model_dump() for i in resolve(value).criteria]

    async def prepare(self, work):
        direct = {"records": work["source"]}
        if self.llm.fits(self.messages(work, "evaluation", direct), "evaluation"):
            return {"prepared": direct}
        # Visit EVERY source record. Long individual records are split without changing citation IDs.
        pending = list(work["source"])
        evidence = []
        while pending:
            count = len(pending)
            while count and not self.llm.fits(self.messages(work, "evidence", {"records": pending[:count]}), "evidence"):
                count //= 2
            if not count:
                item = pending.pop(0)
                if len(item["text"]) < 64:
                    raise ExperimentError("평가 지시문이 문맥 한도를 초과합니다.", "context_overflow")
                middle = len(item["text"]) // 2
                pending[:0] = [{**item, "text": item["text"][:middle]}, {**item, "text": item["text"][middle:]}]
                continue
            evidence.extend(await self.extract(work, {"records": pending[:count]}))
            del pending[:count]
        # Hierarchical reduction keeps exact citations and counter-evidence, not a conversational summary.
        while not self.llm.fits(self.messages(work, "evaluation", {"evidence": evidence}), "evaluation"):
            old_size = len(compact(evidence))
            remaining = evidence
            reduced = []
            while remaining:
                count = len(remaining)
                while count and not self.llm.fits(self.messages(work, "evidence", {"evidence": remaining[:count]}), "evidence"):
                    count //= 2
                if not count:
                    raise ExperimentError("평가 근거를 문맥에 넣을 수 없습니다.", "context_overflow")
                reduced.extend(await self.extract(work, {"evidence": remaining[:count]}))
                remaining = remaining[count:]
            if len(compact(reduced)) >= old_size:
                raise ExperimentError("평가 근거 압축이 충분하지 않았습니다. 원문은 보존되어 있으며 평가만 재시도할 수 있습니다.", "evidence_overflow", 502)
            evidence = reduced
        return {"prepared": {"evidence": evidence}}

    async def score(self, work):
        sources = {r["id"]: r for r in work["source"]}
        provided = None
        if "evidence" in work["prepared"]:
            provided = {(item["criterion_id"], e["message_id"], e["counter"])
                        for item in work["prepared"]["evidence"] for e in item["evidence"]}
        fields = {}
        for criterion, definition in self.catalog.rubric.items():
            eligible = [r["id"] for r in sources.values() if r["speaker"] == definition["axis"]
                        and (definition["axis"] == "patient" or r["session_id"] == work["request"].session_id)
                        and (provided is None or any(p[0] == criterion and p[1] == r["id"] for p in provided))]
            if eligible:
                choice = create_model(f"Evidence_{criterion}", __base__=EvidenceChoice,
                                      message_id=(Literal[tuple(eligible)], ...))
                evidence = (list[choice], Field(max_length=2))
            else:
                evidence = (list[EvidenceChoice], Field(max_length=0))
            zero = create_model(f"Zero_{criterion}", __base__=ScoreProposal,
                                score=(Literal[0], ...), evidence=evidence)
            if eligible:
                positive = create_model(f"Positive_{criterion}", __base__=ScoreProposal,
                    score=(int, Field(strict=True, ge=1, le=25)), evidence=(list[choice], Field(min_length=1, max_length=2)))
                item = Union[zero, positive]
            else:
                item = zero
            fields[criterion] = (item, ...)
        schema = create_model("JudgmentProposal", __base__=Model, **fields)

        def resolve(proposal):
            return Judgment(scores=[{
                "criterion_id": criterion, "score": item["score"], "explanation": item["explanation"],
                "evidence": [{**e, "quote": sources[e["message_id"]]["text"]} for e in item["evidence"]],
            } for criterion, item in proposal.model_dump().items()])

        def validate(proposal):
            judgment = resolve(proposal)
            self.validate_judgment(work, judgment)
            if provided is not None and any((s.criterion_id, e.message_id, e.counter) not in provided
                                            for s in judgment.scores for e in s.evidence):
                raise InvalidModelOutput("추출된 근거의 항목·발언 번호·찬반 구분을 유지하세요.")

        value = await self.llm.generate("evaluation", self.messages(work, "evaluation", work["prepared"]), schema,
                                        work["metrics"], validate)
        return {"judgment": resolve(value)}

    async def evaluate(self, request: EvaluateRequest):
        self.catalog.validate_state(request.state)
        if request.session_id != request.state.current.id:
            raise ExperimentError("현재 상담 ID가 일치하지 않습니다.", "stale_session", 409)
        if request.state.current.phase not in {"pending_evaluation", "evaluated"}:
            raise ExperimentError("마무리 원문을 저장한 상담만 최종 평가할 수 있습니다.", "session_open", 409)
        if request.state.current.evaluation:
            return StateResponse(case_id=request.state.case_id, session_id=request.session_id, request_id=request.request_id,
                                 base_revision=request.state.revision, state=request.state, replayed=True)
        started = perf_counter()
        metrics = Metrics()
        original = [r.model_dump() for r in records(request.state)]
        # Number exact passages; choosing a passage never requires the model to copy a quote.
        # Every character of every original utterance is included, in the original order.
        aliases, source = numbered_source(original)
        # Older counselor statements are context only; validation forbids their use for counselor scores.
        result = await self.graph.ainvoke({"request": request, "metrics": metrics, "source": source})
        scores = result["judgment"].scores
        for item in scores:
            for evidence in item.evidence:
                evidence.message_id = aliases[evidence.message_id]
        totals = {axis: sum(s.score for s in scores if self.catalog.rubric[s.criterion_id]["axis"] == axis)
                  for axis in ["patient", "counselor"]}
        metrics.seconds = perf_counter() - started
        case = request.state.model_copy(deep=True)
        case.current.evaluation = Evaluation(
            scores=scores, patient_score=totals["patient"], counselor_score=totals["counselor"],
            cleared=all(v >= self.catalog.patient["clear_threshold"] for v in totals.values()), evaluated_at=now(),
            model_revision=self.llm.config["revision"], prompt_version=self.catalog.prompt_version,
            source_messages=len(original), metrics=metrics,
        )
        case.current.phase = "evaluated"
        case.revision += 1
        case = CaseState.model_validate(case.model_dump())
        return StateResponse(case_id=case.case_id, session_id=case.current.id, request_id=request.request_id,
                             base_revision=request.state.revision, state=case)
