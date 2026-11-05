import json
import os

import pytest

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError
from app.schemas.models import CaseState, Metrics, StateResponse, Turn
from app.services.memory import MemoryManager, recent_records
from conftest import FakeLLM


def test_slots_restore_independently_and_new_case_preserves_old(tmp_path):
    store = SaveStore(tmp_path)
    state = CaseState()
    store.save(state, "manual")
    state.current.turns.append(Turn(id="hello", user_text="안녕하세요", patient_text="안녕하세요.", kind="neutral"))
    state.revision = 1
    store.save(state)
    assert len(store.load("manual").current.turns) == 0
    assert len(store.load("auto").current.turns) == 1
    new = CaseState()
    store.save(new)
    assert store.load().case_id == new.case_id
    assert store.load(case_id=state.case_id).current.turns[0].patient_text == "안녕하세요."


def test_atomic_write_failure_preserves_previous_snapshot(tmp_path, monkeypatch):
    store, state = SaveStore(tmp_path), CaseState()
    store.save(state)
    state.revision = 1
    def fail(*args):
        raise OSError("simulated replacement failure")
    monkeypatch.setattr(os, "replace", fail)
    with pytest.raises(OSError):
        store.save(state)
    assert store.load().revision == 0
    assert not list(tmp_path.rglob("*.tmp"))


@pytest.mark.asyncio
async def test_late_response_and_duplicate_response_cannot_overwrite_load(tmp_path, settings):
    state = CaseState()
    client = SessionClient(settings, SaveStore(tmp_path), state)
    token = client.token("request")
    updated = state.model_copy(deep=True)
    updated.revision += 1
    response = StateResponse(case_id=state.case_id, session_id=state.current.id, request_id="request", base_revision=0, state=updated)
    client.replace(state)
    with pytest.raises(ExperimentError, match="이전"):
        client.accept(response, token)
    token = client.token("request")
    client.accept(response, token)
    with pytest.raises(ExperimentError):
        client.accept(response, token)
    assert client.state.revision == 1
    await client.close()


@pytest.mark.asyncio
async def test_summary_preserves_originals_and_keeps_citations(catalog):
    case = CaseState()
    for i in range(20):
        case.current.turns.append(Turn(id=f"long_{i}", user_text="요즘 어떤 마음인가요? " * 500, patient_text="조금 답답하지만 생각하고 있어요. " * 300, kind="neutral"))
    before = [t.model_dump() for t in case.current.turns]
    llm = FakeLLM()
    render = lambda: [{"role": "user", "content": json.dumps({"memory": case.memory.model_dump(), "records": recent_records(case)}, ensure_ascii=False)}]
    await MemoryManager(llm, catalog).prepare(case, render, "patient", Metrics())
    assert llm.fits(render(), "patient")
    assert case.memory.through_turn_id
    from app.schemas.models import records
    assert case.memory.items[0].source_ids[0] in {r.id for r in records(case)}
    catalog.validate_state(case)
    assert [t.model_dump() for t in case.current.turns] == before
    assert recent_records(case)[-1]["id"] == "long_19:patient"


@pytest.mark.asyncio
async def test_second_send_cannot_replace_pending_input(tmp_path, settings):
    import asyncio
    import httpx
    started, release = asyncio.Event(), asyncio.Event()
    async def handle(request):
        started.set()
        await release.wait()
        raise httpx.ConnectError("simulated connection failure")
    http = httpx.AsyncClient(base_url="http://test", transport=httpx.MockTransport(handle))
    client = SessionClient(settings, SaveStore(tmp_path), CaseState(), http=http)
    original = client.state.model_dump_json()
    first = asyncio.create_task(client.turn("첫 발언"))
    await started.wait()
    with pytest.raises(ExperimentError, match="기다리고"):
        await client.turn("두 번째 발언")
    assert client.pending_text == "첫 발언"
    release.set()
    with pytest.raises(ExperimentError):
        await first
    assert client.state.model_dump_json() == original
    assert client.pending_text == "첫 발언"
    await client.close()


def test_corrupt_or_future_save_is_not_loaded(tmp_path):
    store, state = SaveStore(tmp_path), CaseState()
    path = store.save(state)
    value = state.model_dump()
    value["format_version"] = 999
    path.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(ValueError):
        store.load()
    assert state.format_version == 1


def test_memory_cannot_cite_unknown_or_unsummarized_records(catalog):
    from app.schemas.models import Memory, MemoryItem
    state = CaseState()
    state.memory = Memory(items=[MemoryItem(category="fact", speaker="patient", text="근거 없는 기억", source_ids=["missing:patient"])])
    with pytest.raises(ExperimentError, match="출처"):
        catalog.validate_state(state)


@pytest.mark.asyncio
async def test_response_with_wrong_turn_cannot_commit(tmp_path, settings):
    import httpx
    state = CaseState()
    store = SaveStore(tmp_path)
    store.save(state)
    def handle(request):
        body = json.loads(request.content)
        result = state.model_copy(deep=True)
        result.current.turns.append(Turn(id="wrong", user_text=body["text"], patient_text="반갑습니다.", kind="neutral"))
        result.revision += 1
        response = StateResponse(case_id=state.case_id, session_id=state.current.id, request_id=body["turn_id"], base_revision=0, state=result)
        return httpx.Response(200, json=response.model_dump())
    http = httpx.AsyncClient(base_url="http://test", transport=httpx.MockTransport(handle))
    client = SessionClient(settings, store, state, http=http)
    with pytest.raises(ExperimentError, match="마지막 턴"):
        await client.turn("안녕하세요")
    assert store.load().revision == client.state.revision == 0
    assert client.pending_text == "안녕하세요"
    await client.close()


@pytest.mark.asyncio
async def test_failed_evaluation_keeps_closed_dialogue_and_allows_retry(tmp_path, settings):
    import httpx
    from app.schemas.models import now
    from conftest import case_with_topics
    state = case_with_topics()
    state.current.closed_at = now()
    store = SaveStore(tmp_path)
    store.save(state)
    def handle(request):
        return httpx.Response(502, json={"detail": {"message": "평가 형식 오류"}})
    http = httpx.AsyncClient(base_url="http://test", transport=httpx.MockTransport(handle))
    client = SessionClient(settings, store, state, http=http)
    with pytest.raises(ExperimentError, match="평가 형식 오류"):
        await client.evaluate()
    assert store.load().model_dump() == state.model_dump()
    assert client.state.current.closed_at and client.state.current.evaluation is None
    assert client.active_request is None
    await client.close()
