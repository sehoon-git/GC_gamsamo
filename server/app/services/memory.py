from collections.abc import Callable
from typing import Literal, Union

from pydantic import Field, create_model

from app.core.config import ExperimentError, InvalidModelOutput
from app.llm.client import compact
from app.schemas.models import CaseState, Memory, MemoryContent, MemoryItem, Metrics, records


def all_turns(case: CaseState):
    return [t for s in case.sessions for t in s.turns]


def remaining_turns(case: CaseState):
    turns = all_turns(case)
    marker = case.memory.through_turn_id
    start = next((i + 1 for i, t in enumerate(turns) if t.id == marker), 0)
    return turns[start:]


def recent_records(case: CaseState):
    ids = {t.id for t in remaining_turns(case)}
    return [r.model_dump(exclude={"kind"}) for r in records(case) if r.kind == "dialogue" and r.id.rsplit(":", 1)[0] in ids]


class MemoryManager:
    def __init__(self, llm, catalog):
        self.llm = llm
        self.catalog = catalog

    async def prepare(self, case: CaseState, render: Callable[[], list[dict]], role: str, metrics: Metrics):
        # The caller works on a deep copy. A failed summary or dialogue never mutates the saved case.
        while self.llm.count(render(), role) >= self.llm.config["summary_threshold"] or not self.llm.fits(render(), role):
            remaining = remaining_turns(case)
            if not remaining:
                raise ExperimentError("현재 입력과 고정 설정이 문맥 한도를 초과합니다. 입력을 줄여주세요.", "context_overflow")
            count = max(1, len(remaining) - self.llm.config["recent_turns"])
            available = {r.id: r for r in records(case) if r.kind == "dialogue"}
            aliases = {original: f"{'P' if record.speaker == 'patient' else 'C'}{i + 1:04d}"
                       for i, (original, record) in enumerate(available.items())}
            originals = {alias: original for original, alias in aliases.items()}
            previous_ids = {ref for item in case.memory.items for ref in item.source_ids}
            previous = [{**item.model_dump(), "source_ids": [aliases[ref] for ref in item.source_ids]}
                        for item in case.memory.items]
            while count:
                subset = remaining[:count]
                turn_ids = {t.id for t in subset}
                chunk = [r.model_dump(exclude={"kind"}) for r in available.values() if r.id.rsplit(":", 1)[0] in turn_ids]
                messages = [
                    {"role": "system", "content": self.catalog.prompts["summary"]},
                    {"role": "user", "content": compact({"previous_memory": previous,
                      "new_records": [{**record, "id": aliases[record["id"]]} for record in chunk]})},
                ]
                if self.llm.fits(messages, "summary"):
                    break
                count //= 2
            if not count:
                raise ExperimentError("한 대화 턴이 요약 문맥 한도보다 큽니다. 원문을 보존했으니 더 짧은 입력으로 다시 시도하세요.", "context_overflow")
            allowed = previous_ids | {r["id"] for r in chunk}

            def validate(value: MemoryContent):
                for item in value.items:
                    if not set(item.source_ids) <= allowed:
                        raise InvalidModelOutput("요약에 제공되지 않은 발언 ID가 있습니다.")
                    if item.speaker != "both" and any(available[i].speaker != item.speaker for i in item.source_ids):
                        raise InvalidModelOutput("요약의 발언자와 출처가 일치하지 않습니다.")

            item_models = []
            for speaker in ("patient", "counselor"):
                refs = sorted(aliases[ref] for ref in allowed if available[ref].speaker == speaker)
                if refs:
                    item_models.append(create_model(f"Memory_{speaker}", __base__=MemoryItem,
                        speaker=(Literal[speaker], ...), source_ids=(list[Literal[tuple(refs)]], Field(min_length=1, max_length=4))))
            item_type = Union[tuple(item_models)]
            schema = create_model("MemoryContent", __base__=MemoryContent, items=(list[item_type], Field(max_length=20)))

            def resolve(value):
                return MemoryContent(items=[{**item.model_dump(), "source_ids": [originals[ref] for ref in item.source_ids]}
                                            for item in value.items])

            proposal = await self.llm.generate("summary", messages, schema, metrics, lambda value: validate(resolve(value)))
            value = resolve(proposal)
            case.memory = Memory(items=value.items, through_turn_id=subset[-1].id)
        return render()
