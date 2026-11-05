"""Real API: five questions, closing, automatic save, evaluation and resume.

Uses a disposable save directory. Output stays in ignored ai/outputs.
"""
import argparse
import asyncio
import json
from pathlib import Path
from tempfile import TemporaryDirectory
from time import perf_counter

import httpx

from app.client import SaveStore, SessionClient
from app.core.config import ExperimentError, Settings
from app.schemas.models import CaseState, now

INPUTS = [
    ("안녕하세요. 오늘 와주셔서 감사합니다.", 0),
    ("면접을 앞두고 잠을 못 주무셨다고 들었어요. 그때 가장 부담됐던 건 어떤 점이었나요?", 1),
    ("부담이 많이 컸겠어요.", 0),
    ("면접에 가지 않은 날에는 어떤 마음이 들었나요?", 1),
    ("그때 느낀 마음을 지금은 어떻게 바라보고 계세요?", 1),
    ("카페에서 스스로 잘 해냈다고 느낀 경험이 궁금해요.", 1),
    ("생활비 부담도 고려할 때 지금 감당할 수 있는 작은 계획은 무엇일까요?", 1),
]


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", default="real-model")
    args = parser.parse_args()
    if not args.label.replace("-", "").replace("_", "").isalnum():
        raise SystemExit("label must contain letters, numbers, hyphens or underscores")
    settings = Settings.from_env()
    output = settings.root / "ai/outputs" / f"{args.label}-smoke.json"
    report = {"started_at": now(), "model": settings.model, "turns": [], "passed": False}
    with TemporaryDirectory(prefix="seoyun-smoke-") as folder:
        store = SaveStore(Path(folder))
        state = CaseState()
        store.save(state)
        client = SessionClient(settings, store, state)
        try:
            response = await client.http.get("/health")
            response.raise_for_status()
            report["server"] = response.json()
            for text, delta in INPUTS:
                start = perf_counter()
                turn = await client.turn(text)
                entry = {"input": text, "reply": turn.patient_text, "delta": turn.question_delta,
                         "seconds": perf_counter() - start, "metrics": turn.metrics.model_dump()}
                report["turns"].append(entry)
                assert turn.question_delta == delta, f"Unexpected question count: {text}"
                assert store.load() == client.state
                print(f"Question {client.state.current.questions_used}/5 saved", flush=True)
                if client.state.current.questions_used == 4:
                    await client.close()
                    client = SessionClient(settings, store, store.load())
            before = client.state.model_dump_json()
            for text, code in [("다른 질문도 해도 될까요?", "session_closed")]:
                try:
                    await client.turn(text)
                    raise AssertionError("Sixth turn accepted")
                except ExperimentError as error:
                    assert error.code == code
            assert client.state.model_dump_json() == before
            await client.close()
            client = SessionClient(settings, store, store.load())
            assert client.state.current.phase == "closing"
            start = perf_counter()
            report["closing_text"] = await client.finish()
            report["closing_seconds"] = perf_counter() - start
            assert store.load().current.phase == "pending_evaluation"
            await client.close()
            client = SessionClient(settings, store, store.load())
            start = perf_counter()
            evaluation = await client.evaluate()
            report["evaluation_seconds"] = perf_counter() - start
            report["evaluation"] = evaluation.model_dump()
            assert client.state == store.load()
            original = client.state.model_dump()
            await client.evaluate()
            assert client.state.model_dump() == original
            report["passed"] = True
        except Exception as error:
            report["error"] = str(error)
            raise
        finally:
            report["state"] = client.state.model_dump(mode="json")
            report["finished_at"] = now()
            output.write_text(json.dumps(report, ensure_ascii=False, indent=2), "utf-8")
            await client.close()
            print(f"Smoke passed: {report['passed']}. Report: {output}", flush=True)


if __name__ == "__main__":
    asyncio.run(main())
