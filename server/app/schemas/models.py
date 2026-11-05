from datetime import datetime, timezone
from typing import Annotated, Literal
from uuid import uuid4

from pydantic import BaseModel, ConfigDict, Field, model_validator

CONTENT_VERSION = "seoyun-2.0"
Id = Annotated[str, Field(pattern=r"^[A-Za-z0-9_-]{1,80}$")]
Score = Annotated[int, Field(strict=True, ge=0, le=25)]


def new_id(prefix: str) -> str:
    return prefix + "_" + uuid4().hex


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


class Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class MemoryItem(Model):
    category: Literal["fact", "emotion", "plan", "unresolved", "deferred"]
    speaker: Literal["patient", "counselor", "both"]
    text: str = Field(min_length=1, max_length=320)
    source_ids: list[str] = Field(min_length=1, max_length=4)


class MemoryContent(Model):
    items: list[MemoryItem] = Field(default_factory=list, max_length=20)


class Memory(MemoryContent):
    through_turn_id: Id | None = None


class Classification(Model):
    reason: str = Field(max_length=240)
    topic_label: str = Field(max_length=200)
    question_count: Literal["none", "one", "multiple"]
    disclose_fact_ids: list[str] = Field(max_length=3)


class PatientReply(Model):
    # xgrammar cannot combine pattern + length keys; the output token cap bounds length.
    text: str = Field(pattern=r"^[^\u4e00-\u9fff\u3040-\u30ff]+$")
    disclosed_fact_ids: list[str] = Field(max_length=3)


class Evidence(Model):
    message_id: str
    quote: str = Field(min_length=1, max_length=400)
    counter: bool = False


class CriterionScore(Model):
    criterion_id: str
    score: Score
    explanation: str = Field(min_length=1, max_length=500)
    evidence: list[Evidence] = Field(max_length=4)


class Judgment(Model):
    scores: list[CriterionScore] = Field(min_length=8, max_length=8)


class ExtractedEvidence(Model):
    criterion_id: str
    evidence: list[Evidence] = Field(max_length=2)


class EvidenceCollection(Model):
    criteria: list[ExtractedEvidence] = Field(max_length=8)


class EvidenceChoice(Model):
    message_id: str
    counter: bool = False


class ScoreProposal(Model):
    score: Score
    explanation: str = Field(min_length=1, max_length=500)
    evidence: list[EvidenceChoice] = Field(max_length=2)


class EvidenceChoiceGroup(Model):
    criterion_id: str
    evidence: list[EvidenceChoice] = Field(max_length=2)


class EvidenceProposal(Model):
    criteria: list[EvidenceChoiceGroup] = Field(max_length=8)


class Metrics(Model):
    seconds: float = 0
    calls: int = 0
    prompt_tokens: int = 0
    completion_tokens: int = 0
    role_seconds: dict[str, float] = Field(default_factory=dict)


class Evaluation(Judgment):
    patient_score: int = Field(ge=0, le=100)
    counselor_score: int = Field(ge=0, le=100)
    cleared: bool
    evaluated_at: str
    model_revision: str
    prompt_version: str
    source_messages: int
    metrics: Metrics = Field(default_factory=Metrics)


class Turn(Model):
    id: Id
    user_text: str = Field(min_length=1)
    patient_text: str = Field(min_length=1)
    closing_text: str = ""
    question_delta: Literal[0, 1]
    topic_label: str = Field(default="", max_length=200)
    candidate_fact_ids: list[str] = Field(default_factory=list, max_length=3)
    created_at: str = Field(default_factory=now)
    metrics: Metrics = Field(default_factory=Metrics)

    @property
    def complete_reply(self) -> str:
        return "\n\n".join(t for t in [self.patient_text, self.closing_text] if t)


class Session(Model):
    id: Id = Field(default_factory=lambda: new_id("session"))
    started_at: str = Field(default_factory=now)
    closed_at: str | None = None
    questions_used: int = Field(default=0, strict=True, ge=0, le=5)
    phase: Literal["active", "closing", "pending_evaluation", "evaluated"] = "active"
    closing_request_id: Id | None = None
    closing_metrics: Metrics = Field(default_factory=Metrics)
    turns: list[Turn] = Field(default_factory=list)
    evaluation: Evaluation | None = None


class CaseState(Model):
    format_version: Literal[2] = 2
    content_version: Literal["seoyun-2.0"] = CONTENT_VERSION
    case_id: Id = Field(default_factory=lambda: new_id("case"))
    patient_id: Literal["seoyun"] = "seoyun"
    revision: int = Field(default=0, ge=0)
    created_at: str = Field(default_factory=now)
    revealed_fact_ids: list[str] = Field(default_factory=list)
    memory: Memory = Field(default_factory=Memory)
    sessions: list[Session] = Field(default_factory=lambda: [Session()], min_length=1)

    @property
    def current(self) -> Session:
        return self.sessions[-1]

    @property
    def cleared(self) -> bool:
        return any(s.evaluation and s.evaluation.cleared for s in self.sessions)

    @model_validator(mode="after")
    def consistent(self):
        sessions = [s.id for s in self.sessions]
        turns = [t.id for s in self.sessions for t in s.turns]
        if len(set(sessions)) != len(sessions) or len(set(turns)) != len(turns):
            raise ValueError("중복 상담/턴 ID입니다.")
        if self.memory.through_turn_id and self.memory.through_turn_id not in turns:
            raise ValueError("기억의 원문 위치가 없습니다.")
        for i, session in enumerate(self.sessions):
            used = 0
            for turn in session.turns:
                if used == 5:
                    raise ValueError("다섯 번째 질문 이후 대화 턴을 추가할 수 없습니다.")
                used += turn.question_delta
            if session.questions_used != used:
                raise ValueError("질문 사용량과 확정 대화 기록이 일치하지 않습니다.")
            if (session.phase == "active") != (used < 5):
                raise ValueError("질문 사용량과 상담 단계가 일치하지 않습니다.")
            closed = session.phase in {"pending_evaluation", "evaluated"}
            if bool(session.closed_at) != closed or bool(session.closing_request_id) != closed:
                raise ValueError("마무리 저장 상태와 상담 단계가 일치하지 않습니다.")
            if bool(session.evaluation) != (session.phase == "evaluated"):
                raise ValueError("평가 결과와 상담 단계가 일치하지 않습니다.")
            for j, turn in enumerate(session.turns):
                if bool(turn.closing_text) != (closed and j == len(session.turns) - 1):
                    raise ValueError("마지막 질문의 마무리 발화가 상담 단계와 일치하지 않습니다.")
            if i < len(self.sessions) - 1 and session.phase != "evaluated":
                raise ValueError("이전 상담의 종료와 평가를 먼저 완료해야 합니다.")
        return self


class TurnRequest(Model):
    state: CaseState
    session_id: Id
    turn_id: Id
    text: str = Field(min_length=1)


class EvaluateRequest(Model):
    state: CaseState
    session_id: Id
    request_id: Id


class CloseRequest(EvaluateRequest):
    pass


class StateResponse(Model):
    case_id: Id
    session_id: Id
    request_id: Id
    base_revision: int
    state: CaseState
    replayed: bool = False
    disclosure_candidates: list[str] = Field(default_factory=list, max_length=3)


class Record(Model):
    id: str
    session_id: Id
    speaker: Literal["patient", "counselor"]
    text: str


def records(state: CaseState) -> list[Record]:
    return [record for s in state.sessions for t in s.turns for record in (
        Record(id=t.id + ":user", session_id=s.id, speaker="counselor", text=t.user_text),
        Record(id=t.id + ":patient", session_id=s.id, speaker="patient", text=t.complete_reply),
    )]


def next_session(state: CaseState) -> CaseState:
    if state.cleared:
        raise ValueError("완료된 실험입니다. /new로 새 실험을 시작하세요.")
    if state.current.evaluation is None:
        raise ValueError("현재 상담의 종료와 평가를 먼저 완료하세요.")
    result = state.model_copy(deep=True)
    result.sessions.append(Session())
    result.revision += 1
    return result
