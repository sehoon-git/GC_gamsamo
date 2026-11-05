import json

import httpx
import pytest

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError, Settings
from app.main import create_app
from app.schemas.models import CaseState, Metrics, OpenRequest, TurnRequest, records
from app.services.dialogue import DialogueService
from app.services.memory import MemoryManager, recent_records, remaining_turns
from conftest import FakeLLM, case_with_questions, reply


@pytest.mark.asyncio
async def test_opening_has_no_fabricated_counselor_and_replays_saved_text(catalog):
    state = CaseState()
    before = state.model_dump_json()
    llm = FakeLLM([("PatientReply", reply("안녕하세요. 요즘 마음이 복잡해서 찾아왔어요."))])
    service = DialogueService(llm, catalog)
    response = await service.open_session(OpenRequest(state=state, session_id=state.current.id, request_id="first"))
    assert response.base_revision == 0 and response.state.revision == 1
    assert state.model_dump_json() == before
    assert not response.state.current.turns
    assert [(r.id, r.speaker) for r in records(response.state)] == [("first:patient", "patient")]
    user_messages = [message["content"] for message in llm.calls[0][2] if message["role"] == "user"]
    assert len(user_messages) == 1 and "상담자 발언 아님" in user_messages[0]
    assert all(user_messages[0] not in r.text for r in records(response.state))
    replay = await service.open_session(OpenRequest(state=response.state, session_id=state.current.id, request_id="first"))
    assert replay.replayed and replay.state == response.state and len(llm.calls) == 1
    with pytest.raises(ExperimentError) as error:
        await service.turn(TurnRequest(state=response.state, session_id=state.current.id, turn_id="first", text="안녕하세요"))
    assert error.value.code == "duplicate_turn"


@pytest.mark.asyncio
async def test_failed_opening_does_not_commit_or_expose_private_facts(catalog):
    state = CaseState()
    llm = FakeLLM([("PatientReply", {"text": "처음부터 비밀을 말할게요.", "disclosed_fact_ids": ["cafe_strength"]})])
    with pytest.raises(ValueError):
        await DialogueService(llm, catalog).open_session(OpenRequest(state=state, session_id=state.current.id, request_id="first"))
    assert state.current.opening is None and state.revision == 0


@pytest.mark.asyncio
async def test_start_freeform_unlimited_finish_evaluate_and_revisit_contract():
    settings = Settings(mock=True, api_key="test-key")
    async with httpx.AsyncClient(base_url="http://test", transport=httpx.ASGITransport(app=create_app(settings)),
                                 headers={"Authorization": "Bearer test-key"}) as client:
        health = (await client.get("/health")).json()
        assert health["format_version"] == 4
        assert health["supported_patient_ids"] == ["seoyun"]
        start = {"patient_id": "seoyun", "case_id": "case_demo", "session_id": "session_one",
                 "request_id": "opening_one", "created_at": "2026-11-06T00:00:00+00:00"}
        assert (await client.post("/v1/dialogue/start", json=start, headers={"Authorization": "Bearer wrong"})).status_code == 401
        unsupported = await client.post("/v1/dialogue/start", json={**start, "patient_id": "other"})
        assert unsupported.status_code == 404 and unsupported.json()["detail"]["code"] == "unsupported_patient"
        response = await client.post("/v1/dialogue/start", json=start)
        assert response.status_code == 200, response.text
        state = response.json()["state"]
        assert state["revision"] == 1 and state["sessions"][0]["opening"]["id"] == "opening_one"
        assert state["created_at"] == start["created_at"]
        assert "questions_used" not in state["sessions"][0]
        assert len(records(CaseState.model_validate(state))) == 1
        inputs = ["많이 힘드셨겠어요.", "지금은 어떤 마음인가요? 다음에는 무엇을 해보고 싶으세요?",
                  "상담 그만", "그만할게요", "오늘 상담을 마칠게요.", "상담 마치기"] * 2
        for index, text in enumerate(inputs):
            response = await client.post("/v1/dialogue/turn", json={"state": state, "session_id": "session_one", "turn_id": f"reply_{index}", "text": text})
            assert response.status_code == 200, response.text
            state = response.json()["state"]
            assert state["sessions"][0]["phase"] == "active"
            assert state["sessions"][0]["closed_at"] is None
            assert not state["sessions"][0]["closing_text"]
            assert len(state["sessions"][0]["turns"]) == index + 1
            assert state["sessions"][0]["turns"][-1]["user_text"] == text
        for endpoint in ("close", "evaluate"):
            response = await client.post(f"/v1/dialogue/{endpoint}", json={"state": state, "session_id": "session_one", "request_id": endpoint})
            assert response.status_code == 200, response.text
            state = response.json()["state"]
        assert state["sessions"][0]["evaluation"]["source_messages"] == 25
        old_session = state["sessions"][0]
        base_revision = state["revision"]
        body = {"state": state, "session_id": "session_two", "request_id": "opening_two", "created_at": start["created_at"]}
        response = await client.post("/v1/dialogue/revisit", json=body)
        assert response.status_code == 200, response.text
        result = response.json()
        assert result["base_revision"] == base_revision and result["state"]["revision"] == base_revision + 1
        assert result["state"]["sessions"][0] == old_session
        assert len(result["state"]["sessions"]) == 2 and result["state"]["sessions"][-1]["opening"]["id"] == "opening_two"
        replay = await client.post("/v1/dialogue/revisit", json={**body, "state": result["state"]})
        assert replay.status_code == 200 and replay.json()["replayed"]
        blocked = await client.post("/v1/dialogue/revisit", json={**body, "state": result["state"], "session_id": "session_three"})
        assert blocked.status_code == 409 and blocked.json()["detail"]["code"] == "session_open"


@pytest.mark.asyncio
async def test_opening_is_saved_before_display_and_disk_retry_never_regenerates(tmp_path, settings, monkeypatch):
    state, store = CaseState(), SaveStore(tmp_path)
    store.save(state)
    llm = FakeLLM([("PatientReply", reply())])
    http = httpx.AsyncClient(base_url="http://test", transport=httpx.ASGITransport(app=create_app(settings, llm)))
    client = SessionClient(settings, store, state, http=http)
    original = store.save
    monkeypatch.setattr(store, "save", lambda *a, **k: (_ for _ in ()).throw(OSError("disk full")))
    with pytest.raises(OSError):
        await client.open()
    assert client.state.current.opening is None and store.load().current.opening is None
    assert client.pending_response is not None
    monkeypatch.setattr(store, "save", original)
    await client.retry()
    assert client.state == store.load() and client.state.current.opening is not None
    assert await client.open() == client.state.current.opening
    assert len(llm.calls) == 1
    await client.close()


@pytest.mark.asyncio
async def test_summary_never_discards_recent_six_pairs_and_opening_counts_separately(catalog):
    state = case_with_questions(8)
    llm = FakeLLM()
    llm.config = {**llm.config, "summary_threshold": 1}
    render = lambda: [{"role": "user", "content": json.dumps(recent_records(state), ensure_ascii=False)}]
    await MemoryManager(llm, catalog).prepare(state, render, "patient", Metrics())
    assert [t.id for t in remaining_turns(state)] == [f"turn_{i}" for i in range(2, 8)]
    assert "P0001" in llm.summarized_ids  # The patient-only opening can be summarized with old dialogue.
    assert len(llm.summarized_ids) == 5  # Opening + two pairs, never six recent pairs.
    assert len(state.current.turns) == 8 and state.current.opening is not None
    catalog.validate_state(state)
    llm.fits = lambda *args: False
    with pytest.raises(ExperimentError) as error:
        await MemoryManager(llm, catalog).prepare(state, render, "patient", Metrics())
    assert error.value.code == "context_overflow"


@pytest.mark.asyncio
@pytest.mark.parametrize("operation", ["open", "turn", "finish", "evaluate"])
async def test_client_rejects_unsolicited_extra_dialogue_for_every_operation(tmp_path, operation):
    from app.schemas.models import Turn
    from conftest import closed_case, opened_case
    settings = Settings(mock=True)
    state = CaseState() if operation == "open" else closed_case() if operation == "evaluate" else opened_case()
    store = SaveStore(tmp_path)
    store.save(state)
    upstream = httpx.AsyncClient(base_url="http://test", transport=httpx.ASGITransport(app=create_app(settings)))
    async def forged(request):
        response = await upstream.post(request.url.path, content=request.content, headers={"Content-Type": "application/json"})
        assert response.status_code == 200, response.text
        value = response.json()
        turns = value["state"]["sessions"][-1]["turns"]
        extra = Turn(id="unsolicited", user_text="보내지 않은 상담자 발언", patient_text="요청하지 않은 답변").model_dump()
        turns.insert(-1 if operation == "turn" else len(turns), extra)
        return httpx.Response(200, json=value)
    client = SessionClient(settings, store, state, http=httpx.AsyncClient(base_url="http://test", transport=httpx.MockTransport(forged)))
    with pytest.raises(ExperimentError) as error:
        await (client.turn("실제 입력") if operation == "turn" else getattr(client, operation)())
    assert error.value.code == "invalid_response"
    assert not any(t.id == "unsolicited" for t in store.load().current.turns)
    await client.close()
    await upstream.aclose()
