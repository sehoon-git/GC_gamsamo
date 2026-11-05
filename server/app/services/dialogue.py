from time import perf_counter
from typing import Literal, TypedDict
import re

from pydantic import Field, create_model
from langgraph.graph import END, START, StateGraph

from app.core.config import ExperimentError, InvalidModelOutput
from app.llm.client import compact
from app.schemas.models import (
    CLOSING_TEXT, CaseState, Classification, CloseRequest, Metrics, Opening, OpenRequest,
    PatientReply, RevisitRequest, Session, StartRequest, StateResponse, Turn, TurnRequest, next_session, now,
)
from app.services.memory import MemoryManager, recent_records


class TurnWork(TypedDict, total=False):
    case: CaseState
    request: TurnRequest | CloseRequest | OpenRequest
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
        return {"classification": decision, "allowed": decision.disclose_fact_ids}

    def patient_messages(self, work):
        case = work["case"]
        visible = set(case.revealed_fact_ids) | set(work["allowed"])
        character = {key: self.catalog.patient[key] for key in ["name", "speech", "public_facts", "direction", "unspecified"]}
        character["private_facts"] = [fact for fact in self.catalog.patient["private_facts"] if fact["id"] in visible]
        policy = {
            "fixed_character": character, "memory": case.memory.model_dump(),
            "current_topic": work["classification"].topic_label,
            "allowed_fact_ids": work["allowed"],
            "previously_revealed_fact_ids": case.revealed_fact_ids,
        }
        messages = [{"role": "system", "content": self.catalog.prompts["patient"] + "\n설정과 현재 상태:\n" + compact(policy)}]
        messages.extend({"role": "user" if r["speaker"] == "counselor" else "assistant", "content": r["text"]}
                        for r in recent_records(case))
        if isinstance(work["request"], OpenRequest):
            # This is a system-directed opening, never a fabricated counselor record.
            messages[0]["content"] += ("\n새 상담을 시작했습니다. 상담자의 선행 발언은 없습니다. "
                "서윤이 먼저 인사하고 지금 이야기하고 싶은 마음을 자연스럽게 2~4문장으로 말하세요. "
                "이전 회차가 있으면 확인된 기억만 이어가고 미공개 정보를 새로 노출하지 마세요.")
        else:
            messages.append({"role": "user", "content": work["request"].text})
        return messages

    def validate_reply(self, work, value):
        if isinstance(work["request"], TurnRequest) and re.sub(r"\s+", "", value.text) == re.sub(r"\s+", "", work["request"].text):
            raise InvalidModelOutput("상담자의 질문을 그대로 반복하지 말고 서윤의 입장에서 답하세요.")
        if not value.text.strip() or "<think>" in value.text or "</think>" in value.text:
            raise InvalidModelOutput("비어 있지 않은 한국어 발언만 반환하세요.")
        if not re.search(r"[가-힣]", value.text) or re.search(r"[\u4e00-\u9fff\u3040-\u30ff]", value.text):
            raise InvalidModelOutput("한자·중국어·일본어를 섞지 않은 한국어 존댓말로 다시 작성하세요.")
        if not set(value.disclosed_fact_ids) <= set(work["case"].revealed_fact_ids) | set(work["allowed"]):
            raise InvalidModelOutput("허용되지 않은 정보 ID를 공개했습니다.")

    def reply_schema(self, work):
        allowed = sorted(set(work["case"].revealed_fact_ids) | set(work["allowed"]))
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

    async def start(self, request: StartRequest) -> StateResponse:
        if request.patient_id != self.catalog.patient["id"]:
            raise ExperimentError("이 환자의 AI 상담 콘텐츠는 아직 준비되지 않았습니다.", "unsupported_patient", 404)
        state = CaseState(case_id=request.case_id, created_at=request.created_at,
                          sessions=[Session(id=request.session_id, started_at=request.created_at)])
        return await self.open_session(OpenRequest(state=state, session_id=request.session_id, request_id=request.request_id))

    async def open_session(self, request: OpenRequest) -> StateResponse:
        self.check_request(request)
        current = request.state.current
        if current.opening:
            if current.opening.id != request.request_id:
                raise ExperimentError("이미 첫 발화가 저장되었습니다. 저장된 발화를 표시하세요.", "opening_exists", 409)
            return StateResponse(case_id=request.state.case_id, session_id=current.id, request_id=request.request_id,
                                 base_revision=request.state.revision, state=request.state, replayed=True,
                                 disclosure_candidates=current.opening.candidate_fact_ids)
        if request.state.cleared or current.phase != "active":
            raise ExperimentError("종료된 상담에 새 발화를 만들 수 없습니다.", "session_closed", 409)
        if any(request.request_id == t.id for s in request.state.sessions for t in ([s.opening] if s.opening else []) + s.turns):
            raise ExperimentError("다른 발언에 사용한 요청 ID입니다.", "duplicate_turn", 409)
        case = request.state.model_copy(deep=True)
        metrics, started = Metrics(), perf_counter()
        work = {"case": case, "request": request, "metrics": metrics, "allowed": [],
                "classification": Classification(reason="환자 첫 발화", topic_label="상담 시작", disclose_fact_ids=[])}
        result = await self.respond(work)
        metrics.seconds = perf_counter() - started
        candidates = sorted(set(result["reply"].disclosed_fact_ids))
        case.current.opening = Opening(id=request.request_id, patient_text=result["reply"].text,
                                       candidate_fact_ids=candidates, metrics=metrics)
        case.revision += 1
        case = CaseState.model_validate(case.model_dump())
        return StateResponse(case_id=case.case_id, session_id=case.current.id, request_id=request.request_id,
                             base_revision=request.state.revision, state=case, disclosure_candidates=candidates)

    async def revisit(self, request: RevisitRequest) -> StateResponse:
        self.catalog.validate_state(request.state)
        if request.session_id == request.state.current.id and request.state.current.opening:
            return await self.open_session(OpenRequest(**request.model_dump(exclude={"created_at"})))
        if any(s.id == request.session_id for s in request.state.sessions):
            raise ExperimentError("이미 사용한 상담 회차 ID입니다.", "stale_session", 409)
        if request.state.cleared:
            raise ExperimentError("이미 완료한 환자입니다. 기존 결과를 확인하세요.", "case_cleared", 409)
        if request.state.current.phase != "evaluated":
            raise ExperimentError("이전 상담의 종료와 평가를 먼저 완료하세요.", "session_open", 409)
        state = next_session(request.state)
        state.current.id, state.current.started_at = request.session_id, request.created_at
        # New visit and opening form one accepted revision in this endpoint.
        state.revision = request.state.revision
        return await self.open_session(OpenRequest(state=state, session_id=request.session_id, request_id=request.request_id))

    async def turn(self, request: TurnRequest) -> StateResponse:
        self.check_request(request)
        for session in request.state.sessions:
            if session.opening and session.opening.id == request.turn_id:
                raise ExperimentError("첫 발화에 사용한 요청 ID입니다.", "duplicate_turn", 409)
            for old in session.turns:
                if old.id == request.turn_id:
                    if session.id != request.session_id or old.user_text != request.text:
                        raise ExperimentError("다른 입력에서 사용한 턴 ID입니다.", "duplicate_turn", 409)
                    return StateResponse(case_id=request.state.case_id, session_id=session.id, request_id=request.turn_id,
                                         base_revision=request.state.revision, state=request.state, replayed=True,
                                         disclosure_candidates=old.candidate_fact_ids)
        if request.state.cleared or request.state.current.phase != "active":
            raise ExperimentError("상담 종료가 요청되었습니다. 종료 안내 저장·평가를 완료하세요.", "session_closed", 409)
        if request.state.current.opening is None:
            raise ExperimentError("환자가 먼저 이야기하도록 첫 발화를 요청하세요.", "opening_required", 409)
        if not request.text.strip():
            raise ExperimentError("발언을 입력하세요.")
        case = request.state.model_copy(deep=True)
        metrics = Metrics()
        started = perf_counter()
        result = await self.graph.ainvoke({"case": case, "request": request, "metrics": metrics})
        metrics.seconds = perf_counter() - started
        candidates = sorted(set(result["reply"].disclosed_fact_ids))
        case.current.turns.append(Turn(id=request.turn_id, user_text=request.text, patient_text=result["reply"].text,
                                       topic_label=result["classification"].topic_label,
                                       candidate_fact_ids=candidates, metrics=metrics))
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
        case = request.state.model_copy(deep=True)
        # Closing is an explicit user action, independent of question count and inference.
        case.current.closing_text = CLOSING_TEXT
        case.current.closed_at = now()
        case.current.closing_request_id = request.request_id
        case.current.closing_metrics = Metrics()
        case.current.phase = "pending_evaluation"
        case.revision += 1
        case = CaseState.model_validate(case.model_dump())
        return StateResponse(case_id=case.case_id, session_id=case.current.id, request_id=request.request_id,
                             base_revision=request.state.revision, state=case)
