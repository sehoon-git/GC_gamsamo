from time import perf_counter
from typing import Literal, TypedDict
import re

from pydantic import Field, create_model
from langgraph.graph import END, START, StateGraph

from app.core.config import ExperimentError, InvalidModelOutput
from app.llm.client import compact
from app.schemas.models import (
    CaseState, Classification, CloseRequest, Metrics, PatientReply, StateResponse,
    Turn, TurnRequest, now,
)
from app.services.memory import MemoryManager, recent_records


class TurnWork(TypedDict, total=False):
    case: CaseState
    request: TurnRequest | CloseRequest
    metrics: Metrics
    classification: Classification
    allowed: list[str]
    reply: PatientReply
    classification_messages: list[dict]


class DialogueService:
    def __init__(self, llm, catalog):
        self.llm, self.catalog = llm, catalog
        self.memory = MemoryManager(llm, catalog)
        graph = StateGraph(TurnWork)
        for name, node in [("prepare_memory", self.prepare_memory), ("classify", self.classify), ("respond", self.respond)]:
            graph.add_node(name, node)
        graph.add_edge(START, "prepare_memory")
        graph.add_edge("prepare_memory", "classify")
        graph.add_edge("classify", "respond")
        graph.add_edge("respond", END)
        # Each invocation transforms the client's snapshot; it never owns saved state.
        self.graph = graph.compile()

    def management_messages(self, work):
        case = work["case"]
        payload = {
            "patient": self.catalog.patient,
            "questions_used": case.current.questions_used,
            "previously_revealed_fact_ids": case.revealed_fact_ids,
            "memory": case.memory.model_dump(),
            "recent_records": recent_records(case),
        }
        return [
            {"role": "system", "content": self.catalog.prompts["controller"]},
            {"role": "user", "content": "참고용 설정·이전 기록:\n" + compact(payload)},
            {"role": "user", "content": "분류할 현재 상담자 발언:\n" + work["request"].text},
        ]

    async def prepare_memory(self, work):
        render = lambda: self.management_messages(work)
        messages = await self.memory.prepare(work["case"], render, "controller", work["metrics"])
        return {"classification_messages": messages}

    async def classify(self, work):
        def validate(value):
            if not set(value.disclose_fact_ids) <= self.catalog.facts.keys():
                raise InvalidModelOutput("공개 후보에는 제공된 정보 ID만 사용하세요.")
        schema = create_model("Classification", __base__=Classification,
                              disclose_fact_ids=(list[Literal[tuple(self.catalog.facts)]], Field(max_length=3)))
        decision = await self.llm.generate("controller", work["classification_messages"], schema, work["metrics"], validate)
        if decision.question_count == "multiple":
            raise ExperimentError("질문을 하나씩 나누어 보내주세요. 이번 입력에는 답변하거나 질문을 차감하지 않았습니다.", "needs_split", 422)
        return {"classification": decision, "allowed": decision.disclose_fact_ids}

    def patient_messages(self, work, closing=False):
        case = work["case"]
        visible = set(case.revealed_fact_ids) | set(work["allowed"])
        character = {key: self.catalog.patient[key] for key in ["name", "speech", "public_facts", "direction", "unspecified"]}
        character["private_facts"] = [fact for fact in self.catalog.patient["private_facts"] if fact["id"] in visible]
        policy = {
            "fixed_character": character, "memory": case.memory.model_dump(),
            "current_topic": work["classification"].topic_label if not closing else "이번 상담 마무리",
            "allowed_fact_ids": work["allowed"],
            "previously_revealed_fact_ids": case.revealed_fact_ids,
        }
        messages = [{"role": "system", "content": self.catalog.prompts["closing" if closing else "patient"] + "\n설정과 현재 상태:\n" + compact(policy)}]
        messages.extend({"role": "user" if r["speaker"] == "counselor" else "assistant", "content": r["text"]}
                        for r in recent_records(case))
        messages.append({"role": "user", "content": "[진행자 안내] 상담을 마칩니다. 방금까지 한 말을 바탕으로 서윤의 짧은 마지막 말만 하세요. 새 질문이나 답변 요구 없이 끝내세요." if closing else work["request"].text})
        return messages

    def validate_reply(self, work, value, closing=False):
        if not closing and re.sub(r"\s+", "", value.text) == re.sub(r"\s+", "", work["request"].text):
            raise InvalidModelOutput("상담자의 질문을 그대로 반복하지 말고 서윤의 입장에서 답하세요.")
        if not value.text.strip() or "<think>" in value.text or "</think>" in value.text:
            raise InvalidModelOutput("비어 있지 않은 한국어 발언만 반환하세요.")
        if not re.search(r"[가-힣]", value.text) or re.search(r"[\u4e00-\u9fff\u3040-\u30ff]", value.text):
            raise InvalidModelOutput("한자·중국어·일본어를 섞지 않은 한국어 존댓말로 다시 작성하세요.")
        if not set(value.disclosed_fact_ids) <= set(work["case"].revealed_fact_ids) | set(work["allowed"]):
            raise InvalidModelOutput("허용되지 않은 정보 ID를 공개했습니다.")
        if closing and (value.disclosed_fact_ids or re.search(r"[?？]", value.text)):
            raise InvalidModelOutput("마무리에서는 새 정보 공개나 질문 없이 짧게 끝내세요.")

    def reply_schema(self, work, closing=False):
        allowed = sorted(set(work["case"].revealed_fact_ids) | set(work["allowed"])) if not closing else []
        field = (list[Literal[tuple(allowed)]], Field(max_length=3)) if allowed else (list[str], Field(max_length=0))
        return create_model("PatientReply", __base__=PatientReply, disclosed_fact_ids=field)

    async def respond(self, work):
        render = lambda: self.patient_messages(work)
        messages = await self.memory.prepare(work["case"], render, "patient", work["metrics"])
        reply = await self.llm.generate("patient", messages, self.reply_schema(work), work["metrics"], lambda r: self.validate_reply(work, r))
        return {"reply": reply}

    def check_request(self, request):
        self.catalog.validate_state(request.state)
        if request.session_id != request.state.current.id:
            raise ExperimentError("현재 상담 ID가 일치하지 않습니다.", "stale_session", 409)

    async def turn(self, request: TurnRequest) -> StateResponse:
        self.check_request(request)
        for session in request.state.sessions:
            for old in session.turns:
                if old.id == request.turn_id:
                    if session.id != request.session_id or old.user_text != request.text:
                        raise ExperimentError("다른 입력에서 사용한 턴 ID입니다.", "duplicate_turn", 409)
                    return StateResponse(case_id=request.state.case_id, session_id=session.id, request_id=request.turn_id,
                                         base_revision=request.state.revision, state=request.state, replayed=True,
                                         disclosure_candidates=old.candidate_fact_ids)
        if request.state.cleared or request.state.current.phase != "active":
            raise ExperimentError("질문을 모두 사용했습니다. 마무리·저장·평가를 완료하세요.", "session_closed", 409)
        if not request.text.strip():
            raise ExperimentError("발언을 입력하세요.")
        case = request.state.model_copy(deep=True)
        metrics = Metrics()
        started = perf_counter()
        result = await self.graph.ainvoke({"case": case, "request": request, "metrics": metrics})
        metrics.seconds = perf_counter() - started
        delta = int(result["classification"].question_count == "one")
        candidates = sorted(set(result["reply"].disclosed_fact_ids))
        case.current.turns.append(Turn(id=request.turn_id, user_text=request.text, patient_text=result["reply"].text,
                                       question_delta=delta, topic_label=result["classification"].topic_label,
                                       candidate_fact_ids=candidates, metrics=metrics))
        case.current.questions_used += delta
        if case.current.questions_used == self.catalog.patient["question_budget"]:
            case.current.phase = "closing"
        case.revision += 1
        case = CaseState.model_validate(case.model_dump())
        # Revealed IDs remain unchanged here. The client confirms candidates before saving.
        return StateResponse(case_id=case.case_id, session_id=case.current.id, request_id=request.turn_id,
                             base_revision=request.state.revision, state=case, disclosure_candidates=candidates)

    async def close_session(self, request: CloseRequest) -> StateResponse:
        self.check_request(request)
        if request.state.current.phase in {"pending_evaluation", "evaluated"}:
            return StateResponse(case_id=request.state.case_id, session_id=request.session_id, request_id=request.request_id,
                                 base_revision=request.state.revision, state=request.state, replayed=True)
        if request.state.current.phase != "closing":
            raise ExperimentError("다섯 번째 답변을 저장한 뒤 마무리할 수 있습니다.", "session_open", 409)
        case = request.state.model_copy(deep=True)
        work = {"case": case, "request": request, "metrics": Metrics(), "allowed": []}
        render = lambda: self.patient_messages(work, closing=True)
        started = perf_counter()
        messages = await self.memory.prepare(case, render, "patient", work["metrics"])
        reply = await self.llm.generate("patient", messages, self.reply_schema(work, closing=True), work["metrics"],
                                        lambda r: self.validate_reply(work, r, closing=True))
        work["metrics"].seconds = perf_counter() - started
        case.current.turns[-1].closing_text = reply.text
        case.current.closed_at = now()
        case.current.closing_request_id = request.request_id
        case.current.closing_metrics = work["metrics"]
        case.current.phase = "pending_evaluation"
        case.revision += 1
        case = CaseState.model_validate(case.model_dump())
        return StateResponse(case_id=case.case_id, session_id=case.current.id, request_id=request.request_id,
                             base_revision=request.state.revision, state=case)
