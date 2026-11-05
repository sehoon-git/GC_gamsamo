"""Local real-model benchmark. Default output has no keys, addresses or conversation text.

Use --private-records explicitly to retain synthetic requests/responses for a paired replay.
No scores are inserted into a gameplay save. Never restarts the shared server or model.
"""
import argparse
import asyncio
import copy
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import tempfile
from time import perf_counter

import httpx

from app.core.config import Settings
from app.llm.client import VllmClient
from app.main import create_app
from app.schemas.models import CaseState, PATIENT_IDS, StateResponse, records
from app.services.catalog import Catalog


class ObservedClient(VllmClient):
    def count(self, messages, role):
        started = perf_counter()
        result = super().count(messages, role)
        self.tokenization_seconds += perf_counter() - started
        self.max_context_tokens = max(self.max_context_tokens, result)
        return result

    def reset(self):
        self.tokenization_seconds = 0
        self.max_context_tokens = 0
        self.observed_calls = 0
        self.observed_roles = {}

    async def generate(self, role, messages, schema, metrics, validate=None):
        before_calls = metrics.calls
        before_seconds = metrics.role_seconds.get(role, 0)
        try:
            return await super().generate(role, messages, schema, metrics, validate)
        finally:
            self.observed_calls += metrics.calls - before_calls
            self.observed_roles[role] = self.observed_roles.get(role, 0) + metrics.role_seconds.get(role, 0) - before_seconds


def write_private(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".measurement-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def source_fingerprint(root):
    digest = hashlib.sha256()
    paths = list((root / "server/app").rglob("*.py")) + list((root / "server/prompts").glob("*.txt"))
    paths += list((root / "Assets/StreamingAssets/GameData/Patients").glob("*.json"))
    paths.append(root / "Assets/StreamingAssets/GameData/patient-catalog.json")
    for path in sorted(paths):
        digest.update(str(path.relative_to(root)).encode() + b"\0" + path.read_bytes() + b"\0")
    return digest.hexdigest()


def summary(rows):
    result = {}
    def group(row):
        return row["operation"] + ("_cached" if row.get("response_cache") == "hit" else "")
    for action in sorted({group(r) for r in rows}):
        selected = [r for r in rows if group(r) == action and r["ok"] and not r["shared_server_busy"]]
        times = sorted(r["wall_seconds"] for r in selected)
        item = {"n": len(times), "failed": sum(not r["ok"] for r in rows if group(r) == action),
                "excluded_busy": sum(r["shared_server_busy"] for r in rows if group(r) == action)}
        if times:
            item.update(mean_seconds=statistics.mean(times), min_seconds=times[0], max_seconds=times[-1])
        # Tail estimates from a handful of patient cases are not a p95 claim.
        if len(times) >= 20:
            item["p50_seconds"] = statistics.median(times)
        if len(times) >= 100:
            item["p95_seconds"] = times[__import__("math").ceil(len(times)*.95)-1]
        result[action] = item
    return result


async def run(args):
    profile = json.loads(args.profile.read_text())
    settings = Settings(root=args.source_root, model_config=str(args.profile),
                        vllm_url=args.model_url, vllm_api_key=os.getenv("SEOYUN_VLLM_API_KEY", ""))
    engine = ObservedClient(settings)
    engine.reset()
    assert (await engine.health())["ready"], "The fixed model profile does not match the running model."
    cold_started = perf_counter(); engine.load_tokenizer(); cold_seconds = perf_counter()-cold_started
    app = create_app(settings, llm=engine)
    scenarios = json.loads(args.scenarios.read_text())
    try:
        hardware = subprocess.check_output(["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader"], text=True).strip().splitlines()
    except (OSError, subprocess.CalledProcessError):
        hardware = ["unavailable"]
    public_profile = {k: profile[k] for k in ("model_id", "revision", "served_model_name", "max_model_len",
        "summary_threshold", "recent_turns", "gpu_memory_utilization", "max_num_seqs", "roles", "reasoning_parser", "chat_template_kwargs") if k in profile}
    report = {"label": args.label, "code_revision": args.code_revision, "source_sha256": source_fingerprint(args.source_root),
              "started_at": datetime.now(timezone.utc).isoformat(), "hardware_inventory": hardware,
              "model": public_profile, "tokenizer_revision": profile["revision"],
              "model_context_limit": profile["max_model_len"], "tokenizer_cold_load_seconds": cold_seconds,
              "scenario_sha256": hashlib.sha256(args.scenarios.read_bytes()).hexdigest(),
              "conditions": "one benchmark request at a time; shared model; production busy observations excluded from aggregates",
              "quantile_minimums": {"p50": 20, "p95": 100}, "rows": [], "cases": []}
    packets = []
    out = args.output / (args.label + ".json")
    raw = args.output / (args.label + ".private.json")
    async with httpx.AsyncClient(base_url="http://benchmark", transport=httpx.ASGITransport(app=app), timeout=240) as api, \
               httpx.AsyncClient(timeout=3, trust_env=False) as public:
        async def idle():
            for _ in range(300):
                h = await public.get(args.production_health)
                h.raise_for_status()
                if not h.json()["busy"]: return
                await asyncio.sleep(1)
            raise RuntimeError("Shared server remained busy; benchmark did not interrupt it.")

        async def send(patient, scenario, action, body, step):
            await idle()
            busy = False
            async def watch():
                nonlocal busy
                while True:
                    try:
                        busy |= (await public.get(args.production_health)).json()["busy"]
                    except (httpx.HTTPError, ValueError, KeyError): busy = True
                    await asyncio.sleep(.5)
            monitor = asyncio.create_task(watch())
            engine.reset()
            started = perf_counter()
            try:
                response = await api.post("/v1/dialogue/"+action, json=body)
                elapsed = perf_counter()-started
            finally:
                monitor.cancel()
                try: await monitor
                except asyncio.CancelledError: pass
            data = response.json()
            row = {"patient_id": patient, "scenario": scenario, "operation": action, "step": step,
                   "request_id": body.get("request_id", body.get("turn_id")), "status": response.status_code,
                   "request_sha256": hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode()).hexdigest(),
                   "ok": response.status_code == 200, "wall_seconds": elapsed, "shared_server_busy": busy,
                   "max_context_tokens": engine.max_context_tokens, "tokenization_seconds": engine.tokenization_seconds,
                   "server_timing": response.headers.get("server-timing"), "response_cache": response.headers.get("x-ai-cache")}
            if row["ok"]:
                accepted = StateResponse.model_validate(data)
                state = accepted.state
                Catalog(settings, patient).validate_state(state)
                original = body.get("state", body)
                assert state.game_progress_id == original.get("game_progress_id", "legacy")
                assert state.case_id == original["case_id"] and state.current.id == body["session_id"]
                assert accepted.request_id == (body.get("request_id") or body["turn_id"])
                assert accepted.base_revision == original.get("revision", 0)
                assert state.revision == accepted.base_revision + (0 if accepted.replayed else 1)
                if "state" in body:
                    prior = CaseState.model_validate(body["state"])
                    for index, session in enumerate(prior.sessions):
                        assert state.sessions[index].turns[:len(session.turns)] == session.turns
                        if session.opening is not None:
                            assert state.sessions[index].opening == session.opening
                    if action == "revisit" and not accepted.replayed:
                        assert len(state.sessions) == len(prior.sessions) + 1
                        assert state.sessions[:-1] == prior.sessions
                current = state.current
                metrics = current.evaluation.metrics if action == "evaluate" else current.closing_metrics if action == "close" else current.turns[-1].metrics if action == "turn" else current.opening.metrics
                row.update(model_service_seconds=sum(engine.observed_roles.values()), model_calls=engine.observed_calls,
                           role_seconds=engine.observed_roles, prompt_tokens=metrics.prompt_tokens if engine.observed_calls else 0,
                           completion_tokens=metrics.completion_tokens if engine.observed_calls else 0)
                row["non_model_seconds"] = max(0, elapsed-row["model_service_seconds"])
                if action == "evaluate":
                    evaluation = current.evaluation
                    row["evaluation"] = {"patient_score": evaluation.patient_score, "counselor_score": evaluation.counselor_score,
                        "cleared": evaluation.cleared, "source_messages": evaluation.source_messages,
                        "criteria": {score.criterion_id: score.score for score in evaluation.scores}, "exact_evidence_validated": True}
            else:
                row["error_code"] = data.get("detail", {}).get("code", "unknown")
            report["rows"].append(row)
            report["summary"] = summary(report["rows"])
            if args.private_records:
                packets.append({"patient_id": patient, "scenario": scenario, "operation": action, "step": step,
                                "request": copy.deepcopy(body), "response": copy.deepcopy(data)})
                write_private(raw, packets)
            write_private(out, report)
            print(patient, scenario, action, "HTTP_OK" if row["ok"] else "HTTP_FAIL", flush=True)
            if not row["ok"]:
                raise RuntimeError("Request failed: " + row["error_code"])
            updated = data["state"]
            updated["revealed_fact_ids"] = sorted(set(updated["revealed_fact_ids"]) | set(data["disclosure_candidates"]))
            return updated

        if args.replay:
            for packet in json.loads(args.replay.read_text()):
                if packet["operation"] not in args.operations: continue
                try:
                    await send(packet["patient_id"], packet["scenario"], packet["operation"], packet["request"], packet["step"])
                except Exception as error:
                    report["cases"].append({"patient_id": packet["patient_id"], "scenario": packet["scenario"],
                                            "operation": packet["operation"], "failure_type": type(error).__name__})
                    write_private(out, report)
                    print(packet["patient_id"], packet["operation"], "INCOMPLETE", type(error).__name__, flush=True)
        else:
            for patient in args.patients:
                for scenario in args.flows:
                    key = patient+"_"+scenario
                    try:
                        state = await send(patient, scenario, "start", {"patient_id": patient, "game_progress_id": "quality_run",
                            "case_id": "case_"+key, "session_id": "session_"+key, "request_id": "open_"+key,
                            "created_at": "2026-11-06T00:00:00Z"}, 0)
                        texts = scenarios["supportive"][patient] if scenario == "supportive" else scenarios[scenario]
                        for i, text in enumerate(texts):
                            state = await send(patient, scenario, "turn", {"state": state, "session_id": state["sessions"][-1]["id"],
                                "turn_id": f"turn_{key}_{i}", "text": text}, i+1)
                        state = await send(patient, scenario, "close", {"state": state, "session_id": state["sessions"][-1]["id"], "request_id": "close_"+key}, len(texts)+1)
                        state = await send(patient, scenario, "evaluate", {"state": state, "session_id": state["sessions"][-1]["id"], "request_id": "eval_"+key}, len(texts)+2)
                        evaluation = state["sessions"][-1]["evaluation"]
                        item = {"patient_id": patient, "scenario": scenario, "patient_score": evaluation["patient_score"],
                                "counselor_score": evaluation["counselor_score"], "cleared": evaluation["cleared"],
                                "confirmed_fact_ids": state["revealed_fact_ids"],
                                "dialogue_records": sum(r.kind == "dialogue" for r in records(CaseState.model_validate(state)))}
                        report["cases"].append(item)
                        if not evaluation["cleared"]:
                            state = await send(patient, scenario, "revisit", {"state": state, "session_id": "second_"+key,
                                "request_id": "revisit_"+key, "created_at": "2026-11-06T01:00:00Z"}, len(texts)+3)
                            item["revisit_sessions"] = len(state["sessions"])
                        write_private(out, report)
                    except Exception as error:
                        report["cases"].append({"patient_id": patient, "scenario": scenario, "failure_type": type(error).__name__})
                        write_private(out, report)
                        print(patient, scenario, "INCOMPLETE", type(error).__name__, flush=True)
    await engine.close()
    print("Benchmark completed; private artifacts are not quality approval or game progress.", flush=True)
    if any(not r["ok"] for r in report["rows"]) or any("failure_type" in c for c in report["cases"]): raise SystemExit(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--scenarios", type=Path, default=Path(__file__).with_name("quality_scenarios.json"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--code-revision", required=True)
    parser.add_argument("--patients", nargs="+", choices=PATIENT_IDS, default=list(PATIENT_IDS))
    parser.add_argument("--flows", nargs="+", choices=["insufficient", "inappropriate", "supportive"], default=["insufficient", "inappropriate", "supportive"])
    parser.add_argument("--operations", nargs="+", default=["start", "turn", "close", "evaluate", "revisit"])
    parser.add_argument("--model-url", default="http://127.0.0.1:8000")
    parser.add_argument("--production-health", default="http://127.0.0.1:8001/health")
    parser.add_argument("--private-records", action="store_true")
    parser.add_argument("--replay", type=Path)
    args = parser.parse_args()
    if not args.label.replace("-", "").replace("_", "").isalnum(): parser.error("label must contain letters/digits/-/_")
    asyncio.run(run(args))


if __name__ == "__main__": main()
