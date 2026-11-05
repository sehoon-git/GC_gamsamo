"""A contract tests. Mock responses here never represent language-quality or clearance evidence."""
import json

import httpx
import pytest

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError, Settings
from app.main import create_app
from app.schemas.models import CaseState, PATIENT_IDS, StateResponse


@pytest.mark.asyncio
async def test_final_roster_alias_and_removed_patient():
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(Settings(mock=True, api_key="test"))), base_url="http://test") as http:
        assert (await http.get("/v1/patients")).status_code == 401
        http.headers["Authorization"] = "Bearer test"
        roster = (await http.get("/v1/patients")).json()["patients"]
        assert [p["id"] for p in roster] == list(PATIENT_IDS)
        assert len(roster) == 5 and all(p["ready"] for p in roster)
        assert next(p for p in roster if p["id"] == "yeonwoo")["aliases"] == ["jieun"]
        assert all(set(p) == {"id", "name", "aliases", "ready", "content_version", "unavailable_reason"} for p in roster)
        removed = await http.post("/v1/dialogue/start", json=dict(patient_id="minseok", case_id="case", session_id="one", request_id="opening", game_progress_id="game"))
        assert removed.status_code == 404 and removed.json()["detail"]["code"] == "unsupported_patient"
        assert "game_progress_scope" in (await http.get("/health")).json()["capabilities"]


@pytest.mark.asyncio
async def test_interleaved_progress_patient_case_and_revisit(tmp_path):
    settings = Settings(mock=True)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(settings)), base_url="http://test") as http:
        clients = []
        for progress, patient in [("game_a", "seoyun"), ("game_a", "harin"), ("game_b", "seoyun")]:
            response = await http.post("/v1/dialogue/start", json=dict(patient_id=patient, game_progress_id=progress, case_id="same_case", session_id="one", request_id="opening"))
            assert response.status_code == 200, response.text
            state = CaseState.model_validate(response.json()["state"])
            assert state.game_progress_id == progress
            store = SaveStore(tmp_path, patient, progress)
            store.save(state)
            clients.append(SessionClient(settings, store, state, http=http))
        for i, client in enumerate(clients):
            await client.turn(f"이번 기록 {i}만의 이야기예요. 그때 마음이 어땠나요?")
        for i, client in enumerate(clients):
            await client.finish()
            await client.evaluate()
            saved = client.store.load("same_case")
            assert saved.current.turns[0].user_text.startswith(f"이번 기록 {i}만")
            assert len(saved.sessions) == 1 and saved.current.phase == "evaluated"
            assert saved.patient_id == client.store.patient_id and saved.game_progress_id == client.store.game_progress_id
            before = saved.model_dump(mode="json")
            request = dict(state=before, session_id="two", request_id="revisit", created_at="2026-11-06T01:00:00Z")
            reply = await http.post("/v1/dialogue/revisit", json=request)
            assert reply.status_code == 200, reply.text
            after = CaseState.model_validate(reply.json()["state"])
            assert after.sessions[0] == saved.current and after.current.id == "two"
            assert after.memory == saved.memory and after.revealed_fact_ids == saved.revealed_fact_ids
            assert after.game_progress_id == saved.game_progress_id
            client.store.save(after)
            replay = await http.post("/v1/dialogue/revisit", json={**request, "state": after.model_dump(mode="json")})
            assert replay.status_code == 200 and replay.json()["replayed"]
            assert len(replay.json()["state"]["sessions"]) == 2
        with pytest.raises(ValueError):
            clients[0].store.save(clients[2].state)
        for client in clients:
            another = CaseState(patient_id=client.state.patient_id, game_progress_id=client.state.game_progress_id, case_id="new_case")
            client.store.save(another)
            assert len(client.store.cases()) == 2
            assert client.store.load("same_case").current.id == "two"


@pytest.mark.asyncio
async def test_cross_progress_response_rejected_and_storage_pair_unchanged(tmp_path):
    settings = Settings(mock=True)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(settings)), base_url="http://test") as http:
        state = CaseState(patient_id="harin", game_progress_id="game_a")
        store = SaveStore(tmp_path, "harin", "game_a"); store.save(state)
        client = SessionClient(settings, store, state, http=http)
        response = await http.post("/v1/dialogue/open", json=dict(state=state.model_dump(mode="json"), session_id=state.current.id, request_id="opening"))
        payload = response.json(); payload["state"]["game_progress_id"] = "game_b"
        with pytest.raises(ExperimentError, match="게임 진행"):
            client.accept(StateResponse.model_validate(payload), client.token("opening"))
        assert store.load() == state
        payload["state"]["game_progress_id"] = "game_a"
        accepted = StateResponse.model_validate(payload).state
        store.save(accepted)
        raw = json.loads(next(store.root.glob("*/*/transcript.json")).read_text())
        assert raw["game_progress_id"] == "game_a" and raw["patient_id"] == "harin"
        with pytest.raises(ValueError):
            SessionClient(settings, SaveStore(tmp_path, "harin", "other"), accepted, http=http)


def test_legacy_state_serialization_remains_compatible():
    state = CaseState()
    assert state.game_progress_id == "legacy" and "game_progress_id" not in state.model_dump(mode="json")
    assert CaseState.model_validate_json(state.model_dump_json()) == state


def test_contract_normalization_does_not_replace_openapi_properties():
    import importlib.util
    from app.core.config import ROOT
    spec = importlib.util.spec_from_file_location("export_contracts", ROOT / "tools/export_contracts.py")
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    schema = create_app(Settings(mock=True)).openapi()
    assert module.normalized(schema) == schema
    assert schema["components"]["schemas"]["CaseState"]["properties"]["created_at"]["type"] == "string"
    assert schema["components"]["schemas"]["Metrics"]["properties"]["seconds"]["type"] == "number"
    assert module.normalized({"created_at": "some time", "seconds": 4.5}) == {"created_at": module.FIXED_TIME, "seconds": 0.0}


@pytest.mark.parametrize("bad", ["../game", "game/other", "", "a" * 81])
def test_progress_path_ids_reject_traversal_and_invalid_names(tmp_path, bad):
    with pytest.raises(ValueError):
        SaveStore(tmp_path, "seoyun", bad)
