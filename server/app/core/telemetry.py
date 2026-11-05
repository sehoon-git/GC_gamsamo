"""Per-operation timings. Never attach transcripts, credentials or model reasoning."""
from contextvars import ContextVar
from dataclasses import dataclass, field
import json
import logging


@dataclass
class Trace:
    queue_seconds: float = 0.0
    model_seconds: float = 0.0
    postprocess_seconds: float = 0.0
    total_seconds: float = 0.0
    max_context_tokens: int = 0
    prompt_tokens: int = 0
    completion_tokens: int = 0
    calls: int = 0
    role_seconds: dict[str, float] = field(default_factory=dict)

    def timings(self):
        return {"queue": self.queue_seconds, "model": self.model_seconds,
                "postprocess": self.postprocess_seconds,
                "app": max(0, self.total_seconds - self.queue_seconds - self.model_seconds - self.postprocess_seconds)}


current_trace: ContextVar[Trace | None] = ContextVar("ai_trace", default=None)


def emit(event, **safe_fields):
    # Callers explicitly whitelist fields; never pass request/response/exception objects.
    logging.getLogger("uvicorn.error").info(json.dumps({"event": event, **safe_fields}, ensure_ascii=False))
