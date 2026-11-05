"""Export the real OpenAPI schema and deterministic mock HTTP examples."""
import argparse
import asyncio
import json
from pathlib import Path

import httpx

from app.core.config import ROOT, Settings
from app.main import create_app
from app.schemas.models import CaseState, Session, request_closing


FIXED_TIME = "2026-11-06T00:00:00+00:00"


def normalized(value):
    if isinstance(value, list):
        return [normalized(x) for x in value]
    if isinstance(value, dict):
        return {key: FIXED_TIME if key in {"created_at", "started_at", "closed_at", "evaluated_at"} and item else
                0.0 if key == "seconds" else normalized(item) for key, item in value.items()}
    return value


async def documents():
    app = create_app(Settings(mock=True, api_key="example-key"))
    result = {"dialogue.openapi.json": app.openapi()}
    state = CaseState(case_id="case_example", sessions=[Session(id="session_example")]).model_dump(mode="json")
    result["initial-state.example.json"] = state
    async with httpx.AsyncClient(base_url="http://example", transport=httpx.ASGITransport(app=app), headers={"Authorization": "Bearer example-key"}) as client:
        async def send(label, endpoint, body, expected=200):
            response = await client.post(endpoint, json=body)
            assert response.status_code == expected, response.text
            result[f"{label}.request.example.json"] = body
            result[f"{label}.{'response' if expected == 200 else 'error'}.example.json"] = response.json()
            return response.json()
        def turn(text, turn_id):
            return {"state": state, "session_id": state["sessions"][-1]["id"], "turn_id": turn_id, "text": text}
        def step(request_id):
            return {"state": state, "session_id": state["sessions"][-1]["id"], "request_id": request_id}
        result["health.response.example.json"] = (await client.get("/health")).json()
        await send("opening-required", "/v1/dialogue/turn", turn("안녕하세요", "before_opening"), 409)
        await send("unsupported-patient", "/v1/dialogue/start", {"patient_id": "demo_only", "case_id": "case_unsupported", "session_id": "session_unsupported", "request_id": "opening_unsupported", "created_at": FIXED_TIME}, 404)
        state = (await send("start", "/v1/dialogue/start", {"patient_id": "seoyun", "case_id": "case_example", "session_id": "session_example", "request_id": "opening_example", "created_at": FIXED_TIME}))["state"]
        await send("open-retry", "/v1/dialogue/open", step("opening_example"))
        state = (await send("greeting", "/v1/dialogue/turn", turn("안녕하세요. 와주셔서 감사합니다.", "hello_example")))["state"]
        state = (await send("compound", "/v1/dialogue/turn", turn("면접은 어땠나요? 친구와는 어떤가요?", "compound_example")))["state"]
        for i in range(1, 7):
            label = "turn" if i == 1 else "fifth" if i == 5 else "sixth" if i == 6 else f"question-{i}"
            state = (await send(label, "/v1/dialogue/turn", turn("그때 마음이 궁금해요.", f"turn_example_{i}")))["state"]
        await send("retry", "/v1/dialogue/turn", turn("그때 마음이 궁금해요.", "turn_example_6"))
        state = request_closing(CaseState.model_validate(state)).model_dump(mode="json")
        result["closing-intent.state.example.json"] = state
        state = (await send("close", "/v1/dialogue/close", step("close_example")))["state"]
        await send("closed-turn", "/v1/dialogue/turn", turn("지금 마음은 어떤가요?", "after_close_example"), 409)
        await send("close-retry", "/v1/dialogue/close", step("close_example"))
        state = (await send("evaluate", "/v1/dialogue/evaluate", step("evaluation_example")))["state"]
        await send("evaluate-retry", "/v1/dialogue/evaluate", step("evaluation_example"))
        bad = step("wrong_session_example")
        bad["session_id"] = "other_session"
        await send("stale-session", "/v1/dialogue/close", bad, 409)
        await send("revisit", "/v1/dialogue/revisit", {"state": state, "session_id": "session_revisit", "request_id": "opening_revisit", "created_at": FIXED_TIME})
        state = result["greeting.response.example.json"]["state"]
        await send("early-close", "/v1/dialogue/close", step("early_close_example"))
        state = result["start.response.example.json"]["state"]
        state = (await send("opening-only-close", "/v1/dialogue/close", step("opening_only_close")))["state"]
        await send("opening-only-evaluate", "/v1/dialogue/evaluate", step("opening_only_evaluation"))
        state = result["initial-state.example.json"]
        state = (await send("empty-close", "/v1/dialogue/close", step("empty_close_example")))["state"]
        await send("empty-evaluate", "/v1/dialogue/evaluate", step("empty_evaluation_example"))
    result["unauthorized.error.example.json"] = {"detail": {"code": "unauthorized", "message": "접근 키를 확인하세요."}}
    result["legacy.error.example.json"] = {"detail": {"code": "unsupported_save_version", "message": "이전 기록은 보존하고 형식 4의 새 실험을 시작하세요."}}
    return {name: json.dumps(normalized(value), ensure_ascii=False, indent=2) + "\n" for name, value in result.items()}


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="생성 결과와 커밋된 계약의 일치 검사")
    args = parser.parse_args()
    mismatches = []
    for name, content in (await documents()).items():
        path = ROOT / "contracts" / name
        if args.check:
            if not path.exists() or path.read_text("utf-8") != content:
                mismatches.append(name)
        else:
            path.write_text(content, "utf-8")
    if mismatches:
        raise SystemExit("계약 갱신 필요: " + ", ".join(mismatches))
    print("API schema and mock examples match." if args.check else "Exported schema and synthetic examples.")


if __name__ == "__main__":
    asyncio.run(main())
