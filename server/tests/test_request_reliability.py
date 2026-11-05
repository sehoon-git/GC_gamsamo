"""Transport faults use a model double, never fake successful results in gameplay."""
import asyncio
from dataclasses import replace
import json
from types import SimpleNamespace

import httpx
import pytest

from app.core.config import ExperimentError, Settings
from app.core.requests import RequestCoordinator
from app.llm.mock import MockLLM
from app.main import create_app
from app.schemas.models import CaseState, StartRequest, StateResponse
from conftest import closed_case, opened_case


class CountingModel(MockLLM):
    def __init__(self, settings, blocked=False):
        super().__init__(settings)
        self.calls = 0
        self.started = asyncio.Event()
        self.release = asyncio.Event()
        if not blocked:
            self.release.set()

    async def generate(self, *args, **kwargs):
        self.calls += 1
        self.started.set()
        await self.release.wait()
        return await super().generate(*args, **kwargs)


def start_body(**extra):
    # Real clients may omit created_at: Pydantic defaults must not break replay.
    return {"game_progress_id": "game", "patient_id": "harin", "case_id": "case", "session_id": "one", "request_id": "opening", **extra}


def client(app):
    return httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test")


@pytest.mark.asyncio
async def test_inflight_and_lost_response_share_one_result_without_changing_revision_semantics():
    settings = Settings(mock=True)
    model = CountingModel(settings, blocked=True)
    app = create_app(settings, model)
    async with client(app) as http:
        first = asyncio.create_task(http.post("/v1/dialogue/start", json=start_body()))
        await model.started.wait()
        second = asyncio.create_task(http.post("/v1/dialogue/start", json=start_body()))
        await asyncio.sleep(.01)
        model.release.set()
        a, b = await asyncio.gather(first, second)
        again = await http.post("/v1/dialogue/start", json=start_body())
        assert a.status_code == b.status_code == again.status_code == 200
        assert a.json() == b.json() == again.json()
        assert not again.json()["replayed"]  # Client has not committed revision 1 yet.
        assert a.json()["base_revision"] == 0 and a.json()["state"]["revision"] == 1
        assert {a.headers["x-ai-cache"], b.headers["x-ai-cache"]} == {"miss", "joined"}
        assert again.headers["x-ai-cache"] == "hit" and "model;dur=0.000" in again.headers["server-timing"]
        assert model.calls == 1
        stored = again.json()["state"]
        replay = await http.post("/v1/dialogue/open", json={"state": stored, "session_id": "one", "request_id": "opening"})
        assert replay.json()["replayed"] and replay.json()["state"] == stored


@pytest.mark.asyncio
async def test_cancelled_http_waiter_does_not_cancel_work():
    settings = Settings(mock=True)
    coordinator = RequestCoordinator(settings)
    started, release = asyncio.Event(), asyncio.Event()
    calls = 0
    parsed = StartRequest(**start_body())
    expected = StateResponse(case_id="case", session_id="one", request_id="opening", base_revision=0,
                             state=CaseState())
    async def operation():
        nonlocal calls
        calls += 1
        started.set()
        await release.wait()
        return expected
    async def send():
        return await coordinator.execute("start", parsed, start_body(), SimpleNamespace(state=SimpleNamespace()), operation)
    waiter = asyncio.create_task(send())
    await started.wait()
    waiter.cancel()
    with pytest.raises(asyncio.CancelledError):
        await waiter
    retry = asyncio.create_task(send())
    release.set()
    assert await retry == expected and calls == 1
    await coordinator.close()


@pytest.mark.asyncio
async def test_key_contains_progress_patient_case_session_and_action():
    settings = Settings(mock=True)
    model = CountingModel(settings)
    async with client(create_app(settings, model)) as http:
        for changed in ({}, {"patient_id": "seoyun"}, {"game_progress_id": "other"}, {"case_id": "other"}, {"session_id": "other"}):
            result = await http.post("/v1/dialogue/start", json=start_body(**changed))
            assert result.status_code == 200
            state = result.json()["state"]
            for key in ("patient_id", "game_progress_id", "case_id"):
                assert state[key] == start_body(**changed)[key]
        assert model.calls == 5
        state = opened_case().model_dump(mode="json")
        request = {"state": state, "session_id": state["sessions"][-1]["id"], "turn_id": "turn", "text": "제 고민을 이야기할게요."}
        result = await http.post("/v1/dialogue/turn", json=request)
        assert result.status_code == 200
        changed = await http.post("/v1/dialogue/turn", json={**request, "text": "다른 입력"})
        assert changed.status_code == 409 and changed.json()["detail"]["code"] == "request_conflict"
        assert not changed.json()["detail"]["retryable"]
        wrong = await http.post("/v1/dialogue/close", json={"state": state, "session_id": "wrong", "request_id": "close"})
        assert wrong.status_code == 409 and wrong.json()["detail"]["code"] == "stale_session"


@pytest.mark.asyncio
async def test_close_and_saved_replay_do_not_wait_for_model_and_queue_timeout_is_retryable():
    settings = Settings(mock=True, queue_timeout_seconds=.02)
    model = CountingModel(settings, blocked=True)
    app = create_app(settings, model)
    async with client(app) as http:
        pending = asyncio.create_task(http.post("/v1/dialogue/start", json=start_body()))
        await model.started.wait()
        state = opened_case().model_dump(mode="json")
        close = await asyncio.wait_for(http.post("/v1/dialogue/close", json={"state": state, "session_id": state["sessions"][-1]["id"], "request_id": "close"}), .5)
        assert close.status_code == 200 and close.json()["state"]["sessions"][-1]["phase"] == "pending_evaluation"
        assert not model.release.is_set()
        wrong = await http.post("/v1/dialogue/evaluate", json={"state": state, "session_id": "stale", "request_id": "bad"})
        assert wrong.status_code == 409 and wrong.json()["detail"]["code"] == "stale_session"
        queued = await http.post("/v1/dialogue/start", json=start_body(case_id="queued"))
        assert queued.status_code == 503 and queued.json()["detail"]["retryable"]
        assert queued.headers["retry-after"] == "2"
        model.release.set()
        await pending
        retry = await http.post("/v1/dialogue/start", json=start_body(case_id="queued"))
        assert retry.status_code == 200


@pytest.mark.asyncio
async def test_operation_timeout_failure_can_retry_and_does_not_cache_fake_success():
    settings = Settings(mock=True, operation_timeout_seconds=.02)
    model = CountingModel(settings, blocked=True)
    app = create_app(settings, model)
    async with client(app) as http:
        timeout = await http.post("/v1/dialogue/start", json=start_body())
        assert timeout.status_code == 504 and timeout.json()["detail"]["code"] == "request_timeout"
        assert "state" not in timeout.json() and not app.state.coordinator.gate.locked()
        assert all(entry.task is None for entry in app.state.coordinator.entries.values())
        changed = await http.post("/v1/dialogue/start", json={**start_body(), "created_at": "different"})
        assert changed.status_code == 409 and changed.json()["detail"]["code"] == "request_conflict"
        model.release.set()
        retry = await http.post("/v1/dialogue/start", json=start_body())
        assert retry.status_code == 200 and len(retry.json()["state"]["sessions"]) == 1
        assert model.calls == 2


@pytest.mark.asyncio
async def test_restart_recomputes_uncommitted_snapshot_but_saved_evaluation_replays_without_model():
    settings = Settings(mock=True)
    model = CountingModel(settings)
    before = closed_case(1).model_dump(mode="json")
    body = {"state": before, "session_id": before["sessions"][-1]["id"], "request_id": "evaluation"}
    async with client(create_app(settings, model)) as http:
        first = await http.post("/v1/dialogue/evaluate", json=body)
        retry = await http.post("/v1/dialogue/evaluate", json=body)
        assert first.json() == retry.json() and model.calls == 1
    # A new app has no durable cache. Same base snapshot still produces ONE evaluation.
    async with client(create_app(settings, model)) as http:
        lost = await http.post("/v1/dialogue/evaluate", json=body)
        assert model.calls == 2 and lost.json()["state"]["revision"] == before["revision"] + 1
        saved = lost.json()["state"]
        restored = await http.post("/v1/dialogue/evaluate", json={**body, "state": saved})
        assert restored.json()["replayed"] and restored.json()["state"] == saved and model.calls == 2


@pytest.mark.asyncio
async def test_cache_limits_and_shutdown_do_not_keep_unbounded_transcripts():
    settings = Settings(mock=True, response_cache_entries=1)
    app = create_app(settings)
    async with client(app) as http:
        for case_id in ("one", "two", "three"):
            assert (await http.post("/v1/dialogue/start", json=start_body(case_id=case_id))).status_code == 200
            assert len(app.state.coordinator.entries) <= 1
        await app.state.coordinator.close()
        assert not app.state.coordinator.entries
    app = create_app(replace(settings, response_cache_bytes=1))
    async with client(app) as http:
        assert (await http.post("/v1/dialogue/start", json=start_body())).status_code == 200
        assert not app.state.coordinator.entries
    app = create_app(replace(settings, response_cache_seconds=.02))
    async with client(app) as http:
        assert (await http.post("/v1/dialogue/start", json=start_body())).status_code == 200
        await asyncio.sleep(.04)
        assert not app.state.coordinator.entries  # No second HTTP request needed to expire.


@pytest.mark.asyncio
async def test_admission_limit_does_not_reject_a_retry_of_the_existing_operation():
    settings = Settings(mock=True, max_pending_requests=1)
    model = CountingModel(settings, blocked=True)
    app = create_app(settings, model)
    async with client(app) as http:
        first = asyncio.create_task(http.post("/v1/dialogue/start", json=start_body()))
        await model.started.wait()
        joined = asyncio.create_task(http.post("/v1/dialogue/start", json=start_body()))
        full = await http.post("/v1/dialogue/start", json=start_body(case_id="other"))
        assert full.status_code == 503 and full.json()["detail"]["code"] == "server_busy"
        model.release.set()
        a, b = await asyncio.gather(first, joined)
        assert a.json() == b.json() and model.calls == 1


@pytest.mark.asyncio
async def test_default_logs_and_health_have_provenance_but_no_dialogue_or_key(caplog):
    caplog.set_level("INFO", logger="uvicorn.error")
    settings = Settings(mock=True, api_key="secret-key", code_revision="test-revision")
    async with client(create_app(settings)) as http:
        health = (await http.get("/health")).json()
        assert health["api_version"] == "0.4.3" and health["code_revision"] == "test-revision"
        assert health["max_model_len"] == 16384 and "request_timing_v1" in health["capabilities"]
        state = opened_case().model_dump(mode="json")
        result = await http.post("/v1/dialogue/turn", headers={"Authorization": "Bearer secret-key"}, json={"state": state, "session_id": state["sessions"][-1]["id"], "turn_id": "trace-one", "text": "비공개 상담 검사 문장"})
        assert result.status_code == 200 and result.headers["x-request-id"] == "trace-one"
        assert "비공개 상담 검사 문장" not in caplog.text and "secret-key" not in caplog.text
        events = [json.loads(r.message) for r in caplog.records if r.name == "uvicorn.error"]
        assert {e["event"] for e in events} == {"ai.operation", "ai.request"}
