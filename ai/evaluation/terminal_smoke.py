"""Exercise the real API without touching the user's save slots.

Run after both services are ready: python ai/evaluation/terminal_smoke.py
The resulting transcript is an experiment artifact, not a clinical assessment.
"""
import asyncio
import json
from pathlib import Path
from statistics import median
from time import perf_counter

import httpx

from app.core.config import Settings
from app.schemas.models import CaseState, EvaluateRequest, TurnRequest, StateResponse, new_id, now

QUESTIONS = [
    "안녕하세요. 오늘 와주셔서 감사합니다. 편하게 이야기해 주세요.",
    "면접을 앞두고 잠을 못 주무셨다고 들었어요. 그때 가장 부담됐던 건 어떤 점이었나요?",
    "또 평가를 받아야 한다는 부담이 컸겠어요. 면접에 가지 않은 날에는 어떤 마음이 들었나요?",
    "한편으로 편했던 마음도 잘못된 건 아니라고 생각해요. 그 느낌을 지금은 어떻게 바라보고 계세요?",
    "다른 이야기로, 부모님이 보내는 채용 정보를 받으면 어떤 생각이나 감정이 드시나요?",
    "응원인 걸 알아도 재촉처럼 느껴질 수 있겠어요. 부모님께 어떤 방식의 응원을 받고 싶은지 말씀해 주실래요?",
    "이번에는 친구 관계 이야기를 해보고 싶어요. 취업한 친구들과 연락할 때 요즘 어떤 마음이 드시나요?",
    "친구를 축하하는 마음과 내 상황이 속상한 마음은 같이 있을 수 있어요. 친구에게 지금 마음을 표현한다면 어떻게 말하고 싶으세요?",
    "카페에서 일할 때의 경험도 듣고 싶어요. 잘해냈거나 다른 사람을 도와준 일이 있었나요?",
    "신입에게 일을 알려줄 수 있다는 건 경험과 능력이 있다는 뜻 같아요. 그 경험을 돌아보면 자신의 어떤 점을 인정할 수 있을까요?",
    "마지막으로 생활비 부담도 고려해서 앞으로 지원할 회사와 쉬는 시간을 어떻게 정하고 싶은지 이야기해 볼까요?",
    "남의 기준보다 스스로 감당할 수 있는 계획을 고르는 게 중요하겠어요. 지금 가능한 작은 첫걸음과 쉴 시간을 스스로 정해 본다면 어떻게 하고 싶으세요?",
    "취업 결과가 서윤 씨의 가치 전체를 정하는 건 아니라는 생각을 오늘의 경험과 연결해서 말해 볼 수 있을까요?",
    "오늘 여러 마음을 들려주셔서 감사합니다. 이야기하면서 마음에 남은 점이 있다면 듣고 싶어요.",
    "그 마음을 존중할게요. 오늘 이야기한 내용을 바탕으로 스스로 고른 속도로 이어가셔도 괜찮습니다.",
]


async def main():
    settings = Settings.from_env()
    output = settings.root / "ai/outputs/real-model-smoke.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    report = {"started_at": now(), "model": settings.model, "turns": [], "evaluation": None}
    headers = {"Authorization": "Bearer " + settings.api_key} if settings.api_key else {}
    state = CaseState()
    async with httpx.AsyncClient(base_url=settings.api_url, headers=headers, timeout=600) as client:
        try:
            ready = await client.get("/health")
            ready.raise_for_status()
            report["server"] = ready.json()
            # Separate warm-up case: no warm-up messages are included in the measured consultation.
            warm = CaseState()
            request = TurnRequest(state=warm, session_id=warm.current.id, turn_id=new_id("warmup"), text="안녕하세요.")
            response = await client.post("/v1/dialogue/turn", json=request.model_dump(mode="json"))
            response.raise_for_status()
            print("Warm-up completed", flush=True)
            for text in QUESTIONS:
                report["current_input"] = text
                request = TurnRequest(state=state, session_id=state.current.id, turn_id=new_id("turn"), text=text)
                start = perf_counter()
                response = await client.post("/v1/dialogue/turn", json=request.model_dump(mode="json"))
                if response.is_error:
                    raise RuntimeError(f"HTTP {response.status_code}: {response.text[:3000]}")
                state = StateResponse.model_validate_json(response.text).state
                turn = state.current.turns[-1]
                entry = {"input": text, "reply": turn.complete_reply, "kind": turn.kind, "main_questions": len(state.current.topics),
                         "seconds": perf_counter() - start, "metrics": turn.metrics.model_dump(), "closed": bool(state.current.closed_at),
                         "revealed_fact_ids": state.revealed_fact_ids}
                report["turns"].append(entry)
                report.pop("current_input", None)
                print(json.dumps(entry, ensure_ascii=False), flush=True)
                output.write_text(json.dumps({**report, "state": state.model_dump(mode="json")}, ensure_ascii=False, indent=2), "utf-8")
                if state.current.closed_at:
                    request = EvaluateRequest(state=state, session_id=state.current.id, request_id=new_id("evaluation"))
                    response = await client.post("/v1/dialogue/evaluate", json=request.model_dump(mode="json"))
                    if response.is_error:
                        raise RuntimeError(f"Evaluation HTTP {response.status_code}: {response.text[:3000]}")
                    state = StateResponse.model_validate_json(response.text).state
                    report["evaluation"] = state.current.evaluation.model_dump()
                    print("Evaluation:", state.current.evaluation.patient_score, state.current.evaluation.counselor_score, flush=True)
                    break
            # Include repair requests in normal latency; exclude summary/closure work.
            normal = [t["seconds"] for t in report["turns"] if not t["closed"]
                      and not t["metrics"]["role_seconds"].get("summary")
                      and (t["main_questions"] < 5 or t["kind"] == "new_topic")]
            report["ordinary_turn_median_seconds"] = median(normal) if normal else None
            report["natural_closure"] = bool(state.current.closed_at)
            if not report["natural_closure"] or report["evaluation"] is None:
                raise RuntimeError("The scripted consultation did not reach natural closure and evaluation.")
        except Exception as error:
            report["error"] = str(error)
            raise
        finally:
            report["finished_at"] = now()
            report["state"] = state.model_dump(mode="json")
            output.write_text(json.dumps(report, ensure_ascii=False, indent=2), "utf-8")
            print("Report:", output, flush=True)


if __name__ == "__main__":
    asyncio.run(main())
