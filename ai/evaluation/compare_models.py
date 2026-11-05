"""20 authored situations x 2, same API/prompt, full response+save latency.

Human ratings are deliberately separate. No automatic Korean-quality pass.
"""
import argparse
import asyncio
import hashlib
import json
import math
from pathlib import Path
from tempfile import TemporaryDirectory
from time import perf_counter

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError, Settings
from app.schemas.models import CaseState, Turn, now
from app.services.catalog import Catalog


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", required=True)
    args = parser.parse_args()
    if not args.label.replace("-", "").replace("_", "").isalnum():
        raise SystemExit("Invalid label")
    settings = Settings.from_env()
    scenarios_path = settings.root / "ai/evaluation/scenarios.json"
    scenarios = json.loads(scenarios_path.read_text("utf-8"))
    output = settings.root / "ai/outputs" / f"comparison-{args.label}.json"
    catalog = Catalog(settings)
    report = {"label": args.label, "started_at": now(), "model": settings.model,
              "prompt_version": catalog.prompt_version, "scenarios_sha256": hashlib.sha256(scenarios_path.read_bytes()).hexdigest(),
              "human_review": "pending", "samples": []}
    with TemporaryDirectory(prefix="seoyun-comparison-") as folder:
        store = SaveStore(Path(folder))
        for repeat in range(2):
            for scenario in scenarios:
                state = CaseState()
                for i, (question, answer) in enumerate(scenario["context"]):
                    state.current.turns.append(Turn(id=f"context_{i}", user_text=question, patient_text=answer, question_delta=1))
                    state.current.questions_used += 1
                state.revision = len(state.current.turns)
                store.save(state)
                client = SessionClient(settings, store, state)
                sample = {"scenario": scenario["id"], "repeat": repeat, "input": scenario["text"], "context": scenario["context"], "focus": scenario["focus"]}
                try:
                    health = await client.http.get("/health")
                    health.raise_for_status()
                    assert health.json()["mode"] == "model", "Mock outputs must not be used as model-quality evidence"
                    assert health.json()["model_revision"] == settings.model["revision"], "API and benchmark model profiles differ"
                    # Warm-up is a separate case; never counted as a measured situation.
                    if not report["samples"]:
                        start = perf_counter()
                        warm = CaseState()
                        body = {"state": warm.model_dump(), "session_id": warm.current.id, "turn_id": "warmup", "text": "안녕하세요."}
                        response = await client.http.post("/v1/dialogue/turn", json=body)
                        response.raise_for_status()
                        report["warmup_seconds"] = perf_counter() - start
                    start = perf_counter()
                    turn = await client.turn(scenario["text"])
                    sample.update(seconds=perf_counter()-start, reply=turn.patient_text, question_delta=turn.question_delta,
                                  metrics=turn.metrics.model_dump(), candidates=turn.candidate_fact_ids,
                                  rule_pass=turn.question_delta == scenario["expected_delta"])
                    assert store.load() == client.state
                except ExperimentError as error:
                    sample.update(seconds=perf_counter()-start, error=error.code, message=str(error),
                                  rule_pass=error.code == "needs_split" and scenario["expected_delta"] is None)
                    assert client.state == state and store.load(state.case_id) == state
                finally:
                    await client.close()
                report["samples"].append(sample)
                output.write_text(json.dumps(report, ensure_ascii=False, indent=2), "utf-8")
                print(f"{len(report['samples'])}/40 {scenario['id']} rule={sample['rule_pass']}", flush=True)
    ordinary = sorted(s["seconds"] for s in report["samples"] if "reply" in s and not s["metrics"]["role_seconds"].get("summary"))
    report["ordinary_sample_p95_seconds"] = ordinary[math.ceil(.95 * len(ordinary))-1] if ordinary else None
    report["rule_checks_passed"] = sum(s["rule_pass"] for s in report["samples"])
    report["finished_at"] = now()
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), "utf-8")
    print("Comparison captured; human quality review remains pending.", flush=True)


if __name__ == "__main__":
    asyncio.run(main())
