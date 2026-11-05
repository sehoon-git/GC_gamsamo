import json
from pathlib import Path

import pytest

from app.core.config import Settings
from app.schemas.models import CLOSING_TEXT, MemoryContent, Metrics, Opening, Record, CaseState, Turn, now
from app.services.catalog import Catalog


@pytest.fixture
def settings():
    return Settings()


@pytest.fixture
def catalog(settings):
    return Catalog(settings)


class FakeLLM:
    def __init__(self, script=()):
        self.config = Settings().model
        self.script = list(script)
        self.calls = []
        self.summarized_ids = []

    def count(self, messages, role):
        return sum(len(m["content"]) for m in messages) // 3

    def fits(self, messages, role):
        return self.count(messages, role) + self.config["roles"][role]["max_tokens"] + 128 < self.config["max_model_len"]

    async def generate(self, role, messages, schema, metrics, validate=None):
        self.calls.append((role, schema.__name__, messages))
        metrics.calls += 1
        if schema.__name__ == "MemoryContent":
            data = json.loads(messages[-1]["content"])
            self.summarized_ids.extend(r["id"] for r in data["new_records"])
            source = data["new_records"][-1]
            result = schema(items=[{"category": "fact", "speaker": source["speaker"], "text": source["text"][:30], "source_ids": [source["id"]]}])
        else:
            expected, value = self.script.pop(0)
            assert expected == schema.__name__, (expected, schema.__name__)
            if isinstance(value, Exception):
                raise value
            result = schema.model_validate(value)
        if validate:
            validate(result)
        return result

    async def health(self):
        return {"ready": True, "model": "seoyun"}

    async def close(self):
        pass


def case_with_questions(count=5):
    state = CaseState()
    if count:
        state.current.opening = Opening(id="opening", patient_text="안녕하세요. 오늘은 제 마음을 이야기하고 싶어요.")
    for i in range(count):
        state.current.turns.append(Turn(id=f"turn_{i}", user_text=f"질문 {i}: 어떤 마음인가요?", patient_text=f"답변 {i}: 제 마음을 생각해 봤어요."))
    state.revision = count
    return state


def opened_case():
    state = CaseState()
    state.current.opening = Opening(id="opening", patient_text="안녕하세요. 오늘은 제 마음을 이야기하고 싶어요.")
    return state


def closed_case(count=5):
    state = case_with_questions(count)
    state.current.closed_at = now()
    state.current.closing_request_id = "closing"
    state.current.closing_text = CLOSING_TEXT
    state.current.phase = "pending_evaluation"
    state.revision += 1
    return state


def classification(**extra):
    return {"topic_label": "현재 마음", "disclose_fact_ids": [], "reason": "현재 발언의 맥락", **extra}


def reply(text="아직 어렵지만 조금 생각해 볼 수 있을 것 같아요."):
    return {"text": text, "disclosed_fact_ids": []}
