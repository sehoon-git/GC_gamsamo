"""Verify an explicitly selected AI API. Does not deploy, restart, or save game progress.

Set SEOYUN_API_KEY locally; never put credentials in command arguments or reports.
Without --lifecycle this performs read-only health/catalog/auth checks.
"""
import argparse
import asyncio
import json
import os
from pathlib import Path
import sys
from uuid import uuid4

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "server"))

from app.schemas.models import CaseState, PATIENT_IDS
from app.core.config import Settings
from app.services.catalog import Catalog


async def check(args):
    key = os.getenv("SEOYUN_API_KEY", "")
    headers = {"Authorization": "Bearer " + key} if key else {}
    async with httpx.AsyncClient(base_url=args.url.rstrip("/"), headers=headers, timeout=300, trust_env=False) as http:
        response = await http.get("/health")
        assert response.status_code == 200, "Health is not ready"
        health = response.json()
        assert health["inference"]["ready"] and health["format_version"] == 4
        assert health["mode"] == args.expect_mode
        assert set(health["supported_patient_ids"]) == set(PATIENT_IDS)
        assert {"game_progress_scope", "patient_catalog_v1", "request_timing_v1", "volatile_request_replay_v1"} <= set(health["capabilities"])
        if args.expect_revision:
            assert health["code_revision"] == args.expect_revision, "Unexpected code revision"
        catalog_response = await http.get("/v1/patients")
        assert catalog_response.status_code == 200
        assert {p["id"] for p in catalog_response.json()["patients"] if p["ready"]} == set(PATIENT_IDS)
        if key:
            unauthorized = await http.get("/v1/patients", headers={"Authorization": "Bearer invalid-smoke-key"})
            assert unauthorized.status_code == 401
        print(json.dumps({field: health[field] for field in ("api_version", "code_revision", "mode", "model_revision", "prompt_version", "max_model_len", "capabilities")}, ensure_ascii=False))
        if not args.lifecycle:
            return
        progress = "smoke_" + uuid4().hex
        catalog = Catalog(Settings(), args.patient)
        async def step(action, body):
            if action == "start" and args.timeout_probe:
                try:
                    await http.post("/v1/dialogue/" + action, json=body,
                                    timeout=httpx.Timeout(.05, connect=5))
                except httpx.ReadTimeout:
                    print("start: client read timeout observed; retrying the original request")
                else:
                    raise AssertionError("Opening completed too quickly to exercise the timeout probe")
            first = await http.post("/v1/dialogue/" + action, json=body)
            assert first.status_code == 200, f"{action}: HTTP {first.status_code}"
            if action == "start" and args.timeout_probe:
                assert first.headers["x-ai-cache"] in {"joined", "hit"}, "Retry started duplicate work"
            result = first.json()
            state = CaseState.model_validate(result["state"])
            catalog.validate_state(state)
            assert state.game_progress_id == progress and state.patient_id == args.patient
            assert state.case_id == "smoke_case" and state.current.id == body["session_id"]
            replay = await http.post("/v1/dialogue/" + action, json=body)
            assert replay.status_code == 200 and replay.json() == result, f"{action}: lost-response replay changed"
            assert replay.headers["x-ai-cache"] == "hit"
            assert first.headers["x-request-id"] == (body.get("request_id") or body["turn_id"])
            state.revealed_fact_ids = sorted(set(state.revealed_fact_ids) | set(result["disclosure_candidates"]))
            print(action + ": validated response + identical lost-response retry")
            return state.model_dump(mode="json")
        state = await step("start", {"game_progress_id": progress, "patient_id": args.patient,
                                   "case_id": "smoke_case", "session_id": "one", "request_id": "opening"})
        state = await step("turn", {"state": state, "session_id": "one", "turn_id": "turn",
                                  "text": "천천히 말씀하셔도 괜찮아요. 오늘 어떤 마음으로 오셨는지 들어 보고 싶어요."})
        state = await step("close", {"state": state, "session_id": "one", "request_id": "close"})
        state = await step("evaluate", {"state": state, "session_id": "one", "request_id": "evaluation"})
        score = state["sessions"][-1]["evaluation"]
        print(json.dumps({k: score[k] for k in ("patient_score", "counselor_score", "cleared")}, ensure_ascii=False))
        if not score["cleared"]:
            state = await step("revisit", {"state": state, "session_id": "two", "request_id": "opening_two"})
            assert len(state["sessions"]) == 2
        print("API smoke passed; Unity display, disk commit and reward settlement are separate checks.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True, help="Explicit target; use http://127.0.0.1:8002 for a local candidate")
    parser.add_argument("--expect-mode", choices=["mock", "model"], default="model")
    parser.add_argument("--expect-revision")
    parser.add_argument("--patient", choices=PATIENT_IDS, default="seoyun")
    parser.add_argument("--lifecycle", action="store_true", help="Generate a new isolated synthetic case (uses the model)")
    parser.add_argument("--timeout-probe", action="store_true", help="With --lifecycle, interrupt the first HTTP read at 50ms and rejoin the original opening")
    args = parser.parse_args()
    if args.timeout_probe and not args.lifecycle:
        parser.error("--timeout-probe requires --lifecycle")
    try:
        asyncio.run(check(args))
    except httpx.HTTPError as error:
        raise SystemExit("API connection failed: " + type(error).__name__) from None


if __name__ == "__main__":
    main()
