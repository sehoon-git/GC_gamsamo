from time import perf_counter
from typing import Literal, TypedDict
import re

from pydantic import Field, create_model

from langgraph.graph import END, START, StateGraph

from app.core.config import ExperimentError, InvalidModelOutput
from app.llm.client import compact
from app.schemas.models import (
    CaseState, Classification, Closure, Metrics, PatientReply, StateResponse,
    Topic, Turn, TurnRequest, now,
)
from app.services.memory import MemoryManager, recent_records


class TurnWork(TypedDict, total=False):
    case: CaseState
    request: TurnRequest
    metrics: Metrics
    classification: Classification
    kind: str
    topic_label: str
    allowed: list[str]
    reply: PatientReply
    closure: Closure
    closing_text: str
    classification_messages: list[dict]


class DialogueService:
    def __init__(self, llm, catalog):
        self.llm, self.catalog = llm, catalog
        self.memory = MemoryManager(llm, catalog)
        graph = StateGraph(TurnWork)
        for name, node in [("prepare_memory", self.prepare_memory), ("classify", self.classify), ("respond", self.respond), ("check_closure", self.check_closure), ("close", self.close_session)]:
            graph.add_node(name, node)
        graph.add_edge(START, "prepare_memory")
        graph.add_edge("prepare_memory", "classify")
        graph.add_edge("classify", "respond")
        graph.add_edge("respond", "check_closure")
        graph.add_conditional_edges("check_closure", lambda s: "close" if s.get("closure") and s["closure"].ready else END)
        graph.add_edge("close", END)
        # Deliberately no checkpointer: each invocation receives the client's complete state.
        self.graph = graph.compile()

    def management_messages(self, work, prompt, additional=None):
        case = work["case"]
        payload = {
            "patient": self.catalog.patient,
            "current_topics": [t.model_dump() for t in case.current.topics],
            "previously_revealed_fact_ids": case.revealed_fact_ids,
            "deferred_topics": case.deferred_topics[-12:],
            "memory": case.memory.model_dump(),
            "recent_records": recent_records(case),
        }
        if additional:
            payload.update(additional)
        latest = "현재 상담자 발언 (이 발언을 판단하세요):\n" + work["request"].text
        if additional and "current_patient_reply" in additional:
            latest += "\n\n그 발언에 대한 현재 서윤의 답변:\n" + additional["current_patient_reply"]
        return [
            {"role": "system", "content": self.catalog.prompts[prompt]},
            {"role": "user", "content": "참고용 설정·이전 기록:\n" + compact(payload)},
            {"role": "user", "content": latest},
        ]

    async def prepare_memory(self, work: TurnWork):
        render = lambda: self.management_messages(work, "controller")
        messages = await self.memory.prepare(work["case"], render, "controller", work["metrics"])
        return {"classification_messages": messages}

    async def classify(self, work: TurnWork):
        case = work["case"]
        messages = work["classification_messages"]

        def validate(value: Classification):
            topic_ids = {t.id for t in case.current.topics}
            if value.kind == "followup" and value.topic_id not in topic_ids:
                raise InvalidModelOutput("후속질문의 topic_id는 현재 상담의 실제 주제 ID여야 합니다.")
            if value.kind != "followup" and value.topic_id is not None:
                raise InvalidModelOutput("새 주제와 인사의 topic_id는 null입니다.")
            if value.kind == "new_topic" and not value.topic_label.strip():
                raise InvalidModelOutput("새 주제에는 topic_label이 필요합니다.")
            if not set(value.disclose_fact_ids) <= self.catalog.facts.keys():
                raise InvalidModelOutput("공개 후보에 알 수 없는 정보 ID가 있습니다.")
            if value.kind == "neutral" and value.disclose_fact_ids:
                raise InvalidModelOutput("인사·맞장구로 새로운 숨긴 정보를 공개하지 않습니다.")

        # The grammar can only emit an existing topic identifier, never a near-match UUID.
        topic_type = Literal[tuple([None, *(t.id for t in case.current.topics)])]
        fields = {"topic_id": (topic_type, ...),
                  "disclose_fact_ids": (list[Literal[tuple(self.catalog.facts)]], Field(max_length=3))}
        if not case.current.topics:
            fields["kind"] = (Literal["neutral", "new_topic"], ...)
        schema = create_model("Classification", __base__=Classification, **fields)
        decision = await self.llm.generate("controller", messages, schema, work["metrics"], validate)
        budget = self.catalog.patient["main_question_budget"]
        was_exhausted = len(case.current.topics) >= budget
        kind = decision.kind
        if kind == "new_topic":
            if was_exhausted:
                kind = "deferred"
                case.deferred_topics.append(decision.topic_label)
            else:
                case.current.topics.append(Topic(label=decision.topic_label.strip()))
                case.deferred_topics = [q for q in case.deferred_topics if q.casefold().strip() != decision.topic_label.casefold().strip()]
        if was_exhausted:
            case.current.post_budget_turns += 1
        case.deferred_topics = list(dict.fromkeys([*case.deferred_topics, *decision.queued_topics]))
        allowed = decision.disclose_fact_ids if kind not in {"neutral", "deferred"} else []
        return {"classification": decision, "kind": kind, "topic_label": decision.topic_label, "allowed": allowed}

    def patient_messages(self, work, closing=False):
        case = work["case"]
        visible = set(case.revealed_fact_ids) | (set() if closing else set(work["allowed"]))
        character = {key: self.catalog.patient[key] for key in ["name", "speech", "public_facts", "direction", "unspecified"]}
        character["private_facts"] = [fact for fact in self.catalog.patient["private_facts"] if fact["id"] in visible]
        policy = {
            "fixed_character": character,
            "memory": case.memory.model_dump(),
            "current_topic": work["topic_label"],
            "queued_topics": work["classification"].queued_topics,
            "allowed_fact_ids": [] if closing else work["allowed"],
            "previously_revealed_fact_ids": case.revealed_fact_ids,
            "defer_new_topic": work["kind"] == "deferred",
            "finish_existing_followups": len(case.current.topics) == 5 and case.current.post_budget_turns > 0,
        }
        prompt = self.catalog.prompts["closing" if closing else "patient"]
        messages = [{"role": "system", "content": prompt + "\n설정과 현재 상태:\n" + compact(policy)}]
        for record in recent_records(case):
            messages.append({"role": "user" if record["speaker"] == "counselor" else "assistant", "content": record["text"]})
        messages.append({"role": "user", "content": work["request"].text})
        if closing:
            messages.append({"role": "assistant", "content": work["reply"].text})
            messages.append({"role": "user", "content": "[진행자 안내] 이미 제기된 후속 대화가 마무리되었습니다. 새 질문 없이 서윤의 짧은 마지막 말로 상담을 마칩니다."})
        return messages

    def validate_reply(self, work, value, closing=False):
        allowed = set(work["case"].revealed_fact_ids) | set(work["allowed"])
        if not value.text.strip() or "<think>" in value.text or "</think>" in value.text:
            raise InvalidModelOutput("대사에는 비어 있지 않은 한국어 발언만 넣으세요.")
        if not re.search(r"[가-힣]", value.text) or re.search(r"[\u4e00-\u9fff\u3040-\u30ff]", value.text):
            raise InvalidModelOutput("대사는 한자·중국어·일본어를 섞지 않은 자연스러운 한국어 존댓말로 다시 작성하세요.")
        if not set(value.disclosed_fact_ids) <= allowed:
            raise InvalidModelOutput("허용되지 않은 정보 ID를 공개했습니다.")
        if closing and value.disclosed_fact_ids:
            raise InvalidModelOutput("마무리에서 새로운 정보를 공개하지 않습니다.")

    async def respond(self, work):
        render = lambda: self.patient_messages(work)
        messages = await self.memory.prepare(work["case"], render, "patient", work["metrics"])
        reply = await self.llm.generate("patient", messages, self.reply_schema(work), work["metrics"], lambda r: self.validate_reply(work, r))
        work["case"].revealed_fact_ids = sorted(set(work["case"].revealed_fact_ids) | set(reply.disclosed_fact_ids))
        return {"reply": reply}

    def reply_schema(self, work, closing=False):
        allowed = sorted(set(work["case"].revealed_fact_ids) | set(work["allowed"])) if not closing else []
        field = (list[Literal[tuple(allowed)]], Field(max_length=3)) if allowed else (list[str], Field(max_length=0))
        return create_model("PatientReply", __base__=PatientReply, disclosed_fact_ids=field)

    async def check_closure(self, work):
        session = work["case"].current
        if len(session.topics) < self.catalog.patient["main_question_budget"] or session.post_budget_turns < 1:
            return {"closure": None}
        render = lambda: self.management_messages(work, "closure", {
            "current_patient_reply": work["reply"].text,
            "defer_new_topic": work["kind"] == "deferred",
            "previous_unresolved_questions": session.unresolved_questions,
        })
        messages = await self.memory.prepare(work["case"], render, "controller", work["metrics"])
        closure = await self.llm.generate("controller", messages, Closure, work["metrics"])
        session.unresolved_questions = closure.unresolved_questions
        return {"closure": closure}

    async def close_session(self, work):
        render = lambda: self.patient_messages(work, closing=True)
        messages = await self.memory.prepare(work["case"], render, "patient", work["metrics"])
        reply = await self.llm.generate("patient", messages, self.reply_schema(work, closing=True), work["metrics"], lambda r: self.validate_reply(work, r, closing=True))
        work["case"].current.closed_at = now()
        return {"closing_text": reply.text}

    async def turn(self, request: TurnRequest) -> StateResponse:
        self.catalog.validate_state(request.state)
        if request.session_id != request.state.current.id:
            raise ExperimentError("현재 상담 ID가 일치하지 않습니다.", "stale_session", 409)
        for session in request.state.sessions:
            for old in session.turns:
                if old.id == request.turn_id:
                    if session.id != request.session_id or old.user_text != request.text:
                        raise ExperimentError("다른 입력에서 사용한 턴 ID입니다.", "duplicate_turn", 409)
                    return StateResponse(case_id=request.state.case_id, session_id=session.id, request_id=request.turn_id,
                                         base_revision=request.state.revision, state=request.state, replayed=True)
        if request.state.cleared or request.state.current.closed_at:
            raise ExperimentError("종료된 상담입니다. 평가 또는 재상담/새 실험 명령을 사용하세요.", "session_closed", 409)
        if not request.text.strip():
            raise ExperimentError("발언을 입력하세요.")
        case = request.state.model_copy(deep=True)
        metrics = Metrics()
        started = perf_counter()
        result = await self.graph.ainvoke({"case": case, "request": request, "metrics": metrics, "closing_text": ""})
        metrics.seconds = perf_counter() - started
        case.current.turns.append(Turn(id=request.turn_id, user_text=request.text, patient_text=result["reply"].text,
                                       closing_text=result.get("closing_text", ""), kind=result["kind"], metrics=metrics))
        case.revision += 1
        case = CaseState.model_validate(case.model_dump())
        return StateResponse(case_id=case.case_id, session_id=case.current.id, request_id=request.turn_id,
                             base_revision=request.state.revision, state=case)
