"""Exercise real summarization and chunked grading using repeated test dialogue.

Run after terminal_smoke.py. This is a context/storage check, not a quality score:
the transcript is synthetically extended and never enters the user's save slots.
"""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
from tempfile import TemporaryDirectory

from app.client import SaveStore
from app.core.config import Settings
from app.llm.client import VllmClient, compact
from app.schemas.models import CLOSING_TEXT, CaseState, EvaluateRequest, Metrics, TurnRequest, new_id, now, records
from app.services.catalog import Catalog
from app.services.dialogue import DialogueService
from app.services.evaluation import EvaluationService, numbered_source
from app.services.memory import remaining_turns


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", default="real-model")
    args = parser.parse_args()
    settings = Settings.from_env()
    output = settings.root / f"ai/outputs/{args.label}-long-context.json"
    source = json.loads((output.parent / f"{args.label}-smoke.json").read_text("utf-8"))
    case = CaseState.model_validate(source["state"])
    if not case.current.turns:
        raise RuntimeError("Run terminal_smoke.py first.")
    # Copy actual test utterances; do not manufacture new character facts.
    originals = [t.model_copy(deep=True) for t in case.current.turns]
    case.current.evaluation = None
    case.current.closed_at = None
    case.current.closing_request_id = None
    case.current.phase = "active"
    case.current.closing_text = ""
    case.memory.items = []
    case.memory.through_turn_id = None
    engine = VllmClient(settings)
    catalog = Catalog(settings)
    service = DialogueService(engine, catalog)
    metrics = Metrics()
    request = TurnRequest(state=case, session_id=case.current.id, turn_id="memory_probe", text="오늘 나눈 마음을 다시 돌아보고 싶어요.")
    work = {"case": case, "request": request, "metrics": metrics}
    render = lambda: service.management_messages(work)
    report = {"started_at": now(), "synthetic_extension": True, "prompt_version": catalog.prompt_version}
    try:
        index = 0
        evaluator = EvaluationService(engine, catalog)
        evaluation_request = EvaluateRequest(state=case, session_id=case.current.id, request_id="long_evaluation")
        def evaluation_fits():
            # Match EvaluationService.evaluate exactly: closing records and the
            # record-kind metadata never enter the grading prompt. Including
            # them can overestimate size and accidentally skip the chunked path.
            _, numbered = numbered_source([r.model_dump(exclude={"kind"}) for r in records(case) if r.kind == "dialogue"])
            return engine.fits(evaluator.messages({"request": evaluation_request}, "evaluation", {"records": numbered}), "evaluation")
        while engine.count(render(), "controller") < 14000 or evaluation_fits():
            if index >= 250:
                raise RuntimeError("Token count did not grow as expected; stop rather than creating an unbounded fixture.")
            turn = originals[index % len(originals)].model_copy(deep=True)
            turn.id = new_id("long")
            # Insert synthetic context; the latest six complete pairs must remain verbatim.
            case.current.turns.insert(-1, turn)
            index += 1
        before = compact([r.model_dump() for r in records(case)])
        report["input_tokens_before"] = engine.count(render(), "controller")
        report["source_messages"] = len(records(case))
        print("Before summary:", report["input_tokens_before"], "tokens,", report["source_messages"], "messages", flush=True)
        await service.memory.prepare(case, render, "controller", metrics)
        assert compact([r.model_dump() for r in records(case)]) == before
        catalog.validate_state(case)
        assert case.memory.through_turn_id is not None
        report.update(input_tokens_after=engine.count(render(), "controller"),
                      recent_turns=len(remaining_turns(case)), memory=case.memory.model_dump(),
                      summary_metrics=metrics.model_dump(), raw_sha256=hashlib.sha256(before.encode()).hexdigest())
        print("After summary:", report["input_tokens_after"], "tokens; recent turns:", report["recent_turns"], flush=True)
        with TemporaryDirectory(prefix="seoyun-memory-check-") as directory:
            store = SaveStore(Path(directory))
            store.save(case)
            assert store.load().model_dump() == case.model_dump()
        case.current.closed_at = now()
        case.current.closing_request_id = "synthetic_closing"
        case.current.closing_text = CLOSING_TEXT
        case.current.phase = "pending_evaluation"
        result = await evaluator.evaluate(evaluation_request)
        value = result.state.current.evaluation
        report["evaluation"] = value.model_dump()
        assert value.metrics.role_seconds.get("evidence", 0) > 0, "Expected chunked evidence extraction."
        report["passed"] = True
    except Exception as exc:
        report["error"] = str(exc)
        report["passed"] = False
        raise
    finally:
        report["finished_at"] = now()
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2), "utf-8")
        await engine.close()
        print("Report:", output)


if __name__ == "__main__":
    asyncio.run(main())
