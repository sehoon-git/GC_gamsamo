import json
import os

import pytest

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError
from app.schemas.models import CaseState, Metrics, StateResponse, Turn
from app.services.memory import MemoryManager, recent_records
from conftest import FakeLLM, opened_case


def test_auto_save_pairs_and_new_case_preserve_history(tmp_path):
    store, state = SaveStore(tmp_path), opened_case()
    old_path = store.save(state)
    original = old_path.read_bytes()
    state.current.turns.append(Turn(id="hello", user_text="안녕하세요", patient_text="안녕하세요."))
    state.revision = 1
    path = store.save(state, expected_revision=0)
    assert old_path.read_bytes() == original
    assert store.load() == state
    raw = json.loads(path.with_name("transcript.json").read_text("utf-8"))
    assert raw["revision"] == 1 and raw["records"][1]["text"] == "안녕하세요"
    assert raw["records"][0]["speaker"] == "patient" and raw["records"][0]["id"] == "opening:patient"
    new = CaseState()
    store.save(new)
    assert store.load().case_id == new.case_id
    assert store.load(case_id=state.case_id) == state


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
    case = opened_case()
    for i in range(20):
        case.current.turns.append(Turn(id=f"long_{i}", user_text="요즘 어떤 마음인가요? " * 100, patient_text="조금 답답하지만 생각하고 있어요. " * 100))
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
    assert state.format_version == 4


def test_memory_cannot_cite_unknown_or_unsummarized_records(catalog):
    from app.schemas.models import Memory, MemoryItem
    state = CaseState()
    state.memory = Memory(items=[MemoryItem(category="fact", speaker="patient", text="근거 없는 기억", source_ids=["missing:patient"])])
    with pytest.raises(ExperimentError, match="출처"):
        catalog.validate_state(state)


@pytest.mark.asyncio
async def test_response_with_wrong_turn_cannot_commit(tmp_path, settings):
    import httpx
    state = opened_case()
    store = SaveStore(tmp_path)
    store.save(state)
    def handle(request):
        body = json.loads(request.content)
        result = state.model_copy(deep=True)
        result.current.turns.append(Turn(id="wrong", user_text=body["text"], patient_text="반갑습니다."))
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
    from conftest import closed_case
    state = closed_case()
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


@pytest.mark.parametrize("fail_at", [1, 2, 3])
def test_each_save_commit_failure_leaves_previous_pair(tmp_path, monkeypatch, fail_at):
    import app.client as module
    store, state = SaveStore(tmp_path), CaseState()
    store.save(state)
    old = store.load()
    state.revision += 1
    original = module.atomic_json
    calls = 0
    def write(path, value):
        nonlocal calls
        calls += 1
        if calls == fail_at:
            raise OSError("disk failure")
        return original(path, value)
    monkeypatch.setattr(module, "atomic_json", write)
    with pytest.raises(OSError):
        store.save(state)
    assert store.load() == old


def test_legacy_save_is_preserved_and_never_reinterpreted(tmp_path):
    from app.client import LegacySaveError
    legacy = tmp_path / "active.json"
    legacy.write_text('{"case_id":"old"}')
    store = SaveStore(tmp_path)
    with pytest.raises(LegacySaveError):
        store.load()
    store.save(CaseState())
    assert legacy.read_text() == '{"case_id":"old"}'
    assert store.load().format_version == 4


def test_raw_transcript_corruption_is_rejected(tmp_path):
    store = SaveStore(tmp_path)
    path = store.save(CaseState())
    path.with_name("transcript.json").write_text('{}')
    with pytest.raises(ValueError, match="원문"):
        store.load()


def test_concurrent_revision_cannot_overwrite_newer_save(tmp_path):
    store, state = SaveStore(tmp_path), CaseState()
    store.save(state)
    one = state.model_copy(deep=True)
    one.revision = 1
    store.save(one, expected_revision=0)
    other = state.model_copy(deep=True)
    other.revision = 2
    with pytest.raises(ValueError, match="다른 실행"):
        store.save(other, expected_revision=0)
    assert store.load() == one


@pytest.mark.asyncio
async def test_save_retry_reuses_received_response_and_confirmed_facts(tmp_path, settings, monkeypatch):
    import httpx
    from app.main import create_app
    from conftest import classification
    state, store = opened_case(), SaveStore(tmp_path)
    store.save(state)
    llm = FakeLLM([("Classification", classification(disclose_fact_ids=["cafe_strength"])),
        ("PatientReply", {"text": "카페에서 신입 교육을 맡아요.", "disclosed_fact_ids": ["cafe_strength"]})])
    confirmed = []
    def confirm(candidates, state):
        confirmed.append(candidates)
        return candidates
    http = httpx.AsyncClient(base_url="http://test", transport=httpx.ASGITransport(app=create_app(settings, llm)))
    client = SessionClient(settings, store, state, http=http, confirm_disclosures=confirm)
    original = store.save
    def fail(*args, **kwargs):
        raise OSError("no disk space")
    monkeypatch.setattr(store, "save", fail)
    with pytest.raises(OSError):
        await client.turn("카페에서 어떤 일을 맡으세요?")
    assert client.pending_response and client.state.revision == store.load().revision == 0
    with pytest.raises(ExperimentError):
        await client.turn("추가 질문?")
    monkeypatch.setattr(store, "save", original)
    await client.retry()
    assert client.state == store.load()
    assert client.state.revealed_fact_ids == ["cafe_strength"]
    assert len(llm.calls) == 2 and len(confirmed) == 1
    await client.close()


@pytest.mark.asyncio
async def test_network_retry_keeps_identical_request_id_and_body(tmp_path, settings):
    import httpx
    requests = []
    def handle(request):
        requests.append(request.content)
        raise httpx.ConnectError("offline")
    client = SessionClient(settings, SaveStore(tmp_path), CaseState(), http=httpx.AsyncClient(base_url="http://test", transport=httpx.MockTransport(handle)))
    with pytest.raises(ExperimentError):
        await client.turn("그 마음이 궁금해요.")
    with pytest.raises(ExperimentError):
        await client.retry()
    assert len(requests) == 2 and requests[0] == requests[1]
    await client.close()


@pytest.mark.asyncio
async def test_resume_above_five_then_explicit_closing_and_evaluated(tmp_path):
    import httpx
    from app.core.config import Settings
    from app.main import create_app
    from app.schemas.models import CLOSING_TEXT, next_session, request_closing
    from conftest import case_with_questions
    settings = Settings(mock=True)
    state, store = case_with_questions(4), SaveStore(tmp_path)
    store.save(state)
    def make_client():
        http = httpx.AsyncClient(base_url="http://test", transport=httpx.ASGITransport(app=create_app(settings)))
        return SessionClient(settings, store, store.load(), http=http)
    client = make_client()
    await client.turn("지금 마음은 어떤가요?")
    await client.close()
    client = make_client()
    assert client.state.current.phase == "active" and len(client.state.current.turns) == 5
    await client.turn("지금 마음은 어떤가요?")
    assert client.state.current.phase == "active" and len(client.state.current.turns) == 6
    client.replace(request_closing(client.state))
    await client.close()
    client = make_client()
    assert client.state.current.phase == "closing"
    assert await client.finish() == CLOSING_TEXT
    await client.close()
    client = make_client()
    assert client.state.current.phase == "pending_evaluation"
    await client.evaluate()
    assert client.state.current.phase == "evaluated"
    client.replace(next_session(client.state))
    assert not client.state.current.turns and len(client.state.sessions) == 2
    previous = client.state.case_id
    client.replace(CaseState())
    assert len(store.load(previous).sessions) == 2
    await client.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('action', ['finish','evaluate'])
async def test_phase_retry_id_survives_process_restart(tmp_path, settings, action):
    import httpx
    from conftest import case_with_questions, closed_case
    state=closed_case() if action=='evaluate' else case_with_questions()
    store=SaveStore(tmp_path);store.save(state)
    requests=[]
    def handle(request):
        requests.append(request.content)
        return httpx.Response(503,json={'detail':{'code':'model_unavailable','message':'retry'}})
    for _ in range(2):
        http=httpx.AsyncClient(base_url='http://test',transport=httpx.MockTransport(handle))
        client=SessionClient(settings,store,store.load(),http=http)
        with pytest.raises(ExperimentError):
            await getattr(client,action)()
        await client.close()
    assert requests[0]==requests[1]


def test_v2_save_is_preserved_without_automatic_conversion(tmp_path):
    from app.client import LegacySaveError
    old = tmp_path / "v2/index.json"
    old.parent.mkdir()
    old.write_text('{"format_version":2,"active_case_id":"old","cases":{}}')
    original = old.read_bytes()
    store = SaveStore(tmp_path)
    with pytest.raises(LegacySaveError, match="형식 4"):
        store.load()
    store.save(CaseState())
    assert old.read_bytes() == original and store.load().format_version == 4


@pytest.mark.asyncio
@pytest.mark.parametrize("failure_phase", ["closing", "pending_evaluation"])
async def test_close_save_failure_blocks_input_and_retry_keeps_one_notice(tmp_path, settings, monkeypatch, failure_phase):
    import httpx
    from app.main import create_app
    from app.schemas.models import CLOSING_TEXT, records
    state, store, llm = CaseState(), SaveStore(tmp_path), FakeLLM()
    store.save(state)
    calls = []
    async def track(request):
        calls.append(request.url.path)
    http = httpx.AsyncClient(base_url="http://test", transport=httpx.ASGITransport(app=create_app(settings, llm)), event_hooks={"request": [track]})
    client = SessionClient(settings, store, state, http=http)
    original = store.save
    def fail(state, **kwargs):
        if state.current.phase == failure_phase:
            raise OSError("disk full")
        return original(state, **kwargs)
    monkeypatch.setattr(store, "save", fail)
    with pytest.raises(OSError):
        await client.finish()
    assert store.load().current.phase == ("active" if failure_phase == "closing" else "closing")
    assert len(calls) == (0 if failure_phase == "closing" else 1)
    with pytest.raises(ExperimentError):
        await client.turn("새 질문?")
    monkeypatch.setattr(store, "save", original)
    await client.retry()
    assert store.load() == client.state and client.state.current.phase == "pending_evaluation"
    assert len(records(client.state)) == 1 and records(client.state)[0].text == CLOSING_TEXT
    assert len(calls) == 1 and not llm.calls
    await client.finish()
    assert len(calls) == 1
    await client.close()


@pytest.mark.asyncio
async def test_finish_cannot_drop_a_failed_turn_request(tmp_path, settings):
    import httpx
    state, store = CaseState(), SaveStore(tmp_path)
    store.save(state)
    def offline(request):
        raise httpx.ConnectError("offline")
    client = SessionClient(settings, store, state, http=httpx.AsyncClient(base_url="http://test", transport=httpx.MockTransport(offline)))
    with pytest.raises(ExperimentError):
        await client.turn("지금 마음은 어떤가요?")
    pending = client.pending_request
    with pytest.raises(ExperimentError, match="먼저"):
        await client.finish()
    with pytest.raises(ExperimentError, match="먼저"):
        await client.evaluate()
    assert client.pending_request == pending and store.load() == state
    await client.close()


@pytest.mark.asyncio
async def test_evaluation_cannot_replace_pending_close_request(tmp_path, settings):
    import httpx
    state, store = CaseState(), SaveStore(tmp_path)
    store.save(state)
    def offline(request):
        raise httpx.ConnectError("offline")
    client = SessionClient(settings, store, state, http=httpx.AsyncClient(base_url="http://test", transport=httpx.MockTransport(offline)))
    with pytest.raises(ExperimentError):
        await client.finish()
    pending = client.pending_request
    with pytest.raises(ExperimentError, match="먼저"):
        await client.evaluate()
    assert client.pending_request == pending and store.load().current.phase == "closing"
    await client.close()
