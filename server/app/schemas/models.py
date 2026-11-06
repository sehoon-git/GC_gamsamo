from datetime import datetime, timezone
from typing import Annotated, Literal
from uuid import uuid4

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_serializer, model_validator
from pydantic_core import PydanticCustomError
from app.patient_registry import PATIENT_IDS, content_version
from app.core.dialogue_limits import PATIENT_MAX_CHARACTERS, PATIENT_TEXT_PATTERN

CONTENT_VERSION = content_version("seoyun")  # Existing Seoyun saves remain compatible.
FORMAT_VERSION = 4
CLOSING_TEXT = "상담은 여기까지 하겠습니다. 오늘 수고하셨습니다!"
Id = Annotated[str, Field(pattern=r"^[A-Za-z0-9_-]{1,80}$")]
Score = Annotated[int, Field(strict=True, ge=0, le=25)]


def new_id(prefix: str) -> str:
    return prefix + "_" + uuid4().hex


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


class Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class PatientInfo(Model):
    id: Id
    name: str
    aliases: list[Id]
    ready: bool
    content_version: str | None
    unavailable_reason: str


class PatientCatalogResponse(Model):
    catalog_version: Literal[1] = 1
    patients: list[PatientInfo]


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
    disclose_fact_ids: list[str] = Field(max_length=3)


class PatientReply(Model):
    # Only newly generated speech is bounded. Saved openings/turns remain compatible.
    text: str = Field(pattern=PATIENT_TEXT_PATTERN)
    disclosed_fact_ids: list[str] = Field(max_length=3)

    @field_validator("text")
    @classmethod
    def complete_short_reply(cls, value):
        if len(value) > PATIENT_MAX_CHARACTERS:
            raise PydanticCustomError("patient_reply_too_long", f"대사가 너무 깁니다. 한 가지 마음만 25~45자의 완결된 한국어 문장으로 다시 쓰세요. 최대 {PATIENT_MAX_CHARACTERS}자입니다.")
        return value


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


class Opening(Model):
    """A patient's first utterance, with no invented counselor message."""
    id: Id
    patient_text: str = Field(min_length=1)
    candidate_fact_ids: list[str] = Field(default_factory=list, max_length=3)
    created_at: str = Field(default_factory=now)
    metrics: Metrics = Field(default_factory=Metrics)


class Turn(Model):
    id: Id
    user_text: str = Field(min_length=1)
    patient_text: str = Field(min_length=1)
    topic_label: str = Field(default="", max_length=200)
    candidate_fact_ids: list[str] = Field(default_factory=list, max_length=3)
    created_at: str = Field(default_factory=now)
    metrics: Metrics = Field(default_factory=Metrics)

    @property
    def complete_reply(self) -> str:
        return self.patient_text


class Session(Model):
    id: Id = Field(default_factory=lambda: new_id("session"))
    started_at: str = Field(default_factory=now)
    closed_at: str | None = None
    opening: Opening | None = None
    phase: Literal["active", "closing", "pending_evaluation", "evaluated"] = "active"
    closing_request_id: Id | None = None
    closing_text: str = ""
    closing_speaker: Literal["counselor"] = "counselor"
    closing_metrics: Metrics = Field(default_factory=Metrics)
    turns: list[Turn] = Field(default_factory=list)
    evaluation: Evaluation | None = None


class CaseState(Model):
    format_version: Literal[4] = FORMAT_VERSION
    content_version: str = CONTENT_VERSION
    case_id: Id = Field(default_factory=lambda: new_id("case"))
    patient_id: Id = "seoyun"
    game_progress_id: Id = "legacy"
    revision: int = Field(default=0, ge=0)
    created_at: str = Field(default_factory=now)
    revealed_fact_ids: list[str] = Field(default_factory=list)
    memory: Memory = Field(default_factory=Memory)
    sessions: list[Session] = Field(default_factory=lambda: [Session()], min_length=1)

    @model_serializer(mode="wrap")
    def serialize_compatible(self, handler):
        result = handler(self)
        # Existing format-4 snapshots/replay bodies remain byte-structure compatible.
        if self.game_progress_id == "legacy":
            result.pop("game_progress_id", None)
        return result

    @model_validator(mode="before")
    @classmethod
    def default_content_version(cls, value):
        if isinstance(value, dict) and "content_version" not in value and isinstance(value.get("patient_id", "seoyun"), str):
            value = {**value, "content_version": content_version(value.get("patient_id", "seoyun"))}
        return value

    @property
    def current(self) -> Session:
        return self.sessions[-1]

    @property
    def cleared(self) -> bool:
        return any(s.evaluation and s.evaluation.cleared for s in self.sessions)

    @model_validator(mode="after")
    def consistent(self):
        if self.content_version != content_version(self.patient_id):
            raise ValueError("환자와 콘텐츠 버전이 일치하지 않습니다.")
        sessions = [s.id for s in self.sessions]
        turns = [t.id for s in self.sessions for t in ([s.opening] if s.opening else []) + s.turns]
        if len(set(sessions)) != len(sessions) or len(set(turns)) != len(turns):
            raise ValueError("중복 상담/턴 ID입니다.")
        if self.memory.through_turn_id and self.memory.through_turn_id not in turns:
            raise ValueError("기억의 원문 위치가 없습니다.")
        for i, session in enumerate(self.sessions):
            if session.turns and session.opening is None:
                raise ValueError("환자 첫 발화를 먼저 저장해야 합니다.")
            closed = session.phase in {"pending_evaluation", "evaluated"}
            if bool(session.closed_at) != closed or bool(session.closing_request_id) != closed:
                raise ValueError("마무리 저장 상태와 상담 단계가 일치하지 않습니다.")
            if bool(session.evaluation) != (session.phase == "evaluated"):
                raise ValueError("평가 결과와 상담 단계가 일치하지 않습니다.")
            if session.closing_text != (CLOSING_TEXT if closed else ""):
                raise ValueError("고정 종료 안내와 상담 단계가 일치하지 않습니다.")
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


class OpenRequest(EvaluateRequest):
    pass


class StartRequest(Model):
    patient_id: Id
    game_progress_id: Id = "legacy"
    case_id: Id
    session_id: Id
    request_id: Id
    created_at: str = Field(default_factory=now)


class RevisitRequest(EvaluateRequest):
    # session_id is the new visit's ID; prior sessions remain in state.
    created_at: str = Field(default_factory=now)


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
    kind: Literal["dialogue", "closing"] = "dialogue"


def records(state: CaseState) -> list[Record]:
    result = []
    for session in state.sessions:
        if session.opening:
            result.append(Record(id=session.opening.id + ":patient", session_id=session.id,
                                 speaker="patient", text=session.opening.patient_text))
        for turn in session.turns:
            result.extend([
                Record(id=turn.id + ":user", session_id=session.id, speaker="counselor", text=turn.user_text),
                Record(id=turn.id + ":patient", session_id=session.id, speaker="patient", text=turn.patient_text),
            ])
        if session.closing_text:
            result.append(Record(id=session.closing_request_id + ":closing", session_id=session.id,
                                 speaker="counselor", text=session.closing_text, kind="closing"))
    return result


def request_closing(state: CaseState) -> CaseState:
    """The client persists the user's explicit end request before calling /close."""
    result = state.model_copy(deep=True)
    if result.current.phase == "active":
        result.current.phase = "closing"
        result.revision += 1
    return result


def next_session(state: CaseState) -> CaseState:
    if state.cleared:
        raise ValueError("완료된 실험입니다. /new로 새 실험을 시작하세요.")
    if state.current.evaluation is None:
        raise ValueError("현재 상담의 종료와 평가를 먼저 완료하세요.")
    result = state.model_copy(deep=True)
    result.sessions.append(Session())
    result.revision += 1
    return result
