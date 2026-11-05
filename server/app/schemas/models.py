from datetime import datetime, timezone
from typing import Annotated, Literal
from uuid import uuid4

from pydantic import BaseModel, ConfigDict, Field, model_validator

CONTENT_VERSION = "seoyun-1.0"
Id = Annotated[str, Field(pattern=r"^[A-Za-z0-9_-]{1,80}$")]
Score = Annotated[int, Field(strict=True, ge=0, le=25)]


def new_id(prefix: str) -> str:
    return prefix + "_" + uuid4().hex


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


class Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Topic(Model):
    id: Id = Field(default_factory=lambda: new_id("topic"))
    label: str = Field(min_length=1, max_length=200)


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
    kind: Literal["neutral", "followup", "new_topic"]
    topic_id: str | None
    queued_topics: list[str] = Field(max_length=10)
    disclose_fact_ids: list[str] = Field(max_length=3)


class PatientReply(Model):
    # xgrammar cannot combine pattern + length keys; the output token cap bounds length.
    text: str = Field(pattern=r"^[^\u4e00-\u9fff\u3040-\u30ff]+$")
    disclosed_fact_ids: list[str] = Field(max_length=3)


class Closure(Model):
    ready: bool
    unresolved_questions: list[str] = Field(max_length=12)
    reason: str = Field(max_length=320)

    @model_validator(mode="after")
    def consistent(self):
        if self.ready and self.unresolved_questions:
            raise ValueError("미해결 후속질문이 있으면 종료할 수 없습니다.")
        return self


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
    kind: Literal["neutral", "followup", "new_topic", "deferred"]
    created_at: str = Field(default_factory=now)
    metrics: Metrics = Field(default_factory=Metrics)

    @property
    def complete_reply(self) -> str:
        return "\n\n".join(t for t in [self.patient_text, self.closing_text] if t)


class Session(Model):
    id: Id = Field(default_factory=lambda: new_id("session"))
    started_at: str = Field(default_factory=now)
    closed_at: str | None = None
    topics: list[Topic] = Field(default_factory=list, max_length=5)
    post_budget_turns: int = Field(default=0, ge=0)
    unresolved_questions: list[str] = Field(default_factory=list)
    turns: list[Turn] = Field(default_factory=list)
    evaluation: Evaluation | None = None


class CaseState(Model):
    format_version: Literal[1] = 1
    content_version: Literal["seoyun-1.0"] = CONTENT_VERSION
    case_id: Id = Field(default_factory=lambda: new_id("case"))
    patient_id: Literal["seoyun"] = "seoyun"
    revision: int = Field(default=0, ge=0)
    created_at: str = Field(default_factory=now)
    revealed_fact_ids: list[str] = Field(default_factory=list)
    deferred_topics: list[str] = Field(default_factory=list)
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
            if len(session.topics) != sum(t.kind == "new_topic" for t in session.turns):
                raise ValueError("주질문 수와 대화 기록이 일치하지 않습니다.")
            if len({t.id for t in session.topics}) != len(session.topics):
                raise ValueError("중복 주제 ID입니다.")
            if session.evaluation and not session.closed_at:
                raise ValueError("진행 중인 상담에는 최종 평가가 있을 수 없습니다.")
            if i < len(self.sessions) - 1 and (not session.closed_at or not session.evaluation):
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


class StateResponse(Model):
    case_id: Id
    session_id: Id
    request_id: Id
    base_revision: int
    state: CaseState
    replayed: bool = False


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
