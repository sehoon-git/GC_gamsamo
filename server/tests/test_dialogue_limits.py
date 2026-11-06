import json

import httpx
import pytest
from pydantic import ValidationError

from app.core.config import ExperimentError
from app.core.dialogue_limits import PATIENT_MAX_CHARACTERS, PATIENT_MAX_OUTPUT_TOKENS
from app.llm.client import VllmClient
from app.schemas.models import CaseState, Metrics, OpenRequest, PatientReply
from app.services.dialogue import DialogueService


@pytest.mark.parametrize("text", ["가" * 61, "줄바꿈\n안 돼요.", "탭\t안 돼요.", "숨김\u200b문자예요.", "문단\u2028구분이에요.", ""])
def test_new_reply_rejects_oversize_and_invisible_layout_breaks(text):
    with pytest.raises(ValidationError):
        PatientReply(text=text, disclosed_fact_ids=[])


def test_exact_boundary_and_existing_long_transcript_are_not_cut():
    text = "가" * (PATIENT_MAX_CHARACTERS - 1) + "."
    assert PatientReply(text=text, disclosed_fact_ids=[]).text == text
    from app.schemas.models import Opening, Turn
    old = ("예전에 저장된 긴 상담 기록입니다.\n" * 30)
    state = CaseState()
    state.current.opening = Opening(id="old_open", patient_text=old)
    state.current.turns = [Turn(id="old_turn", user_text="그랬군요.", patient_text=old)]
    restored = CaseState.model_validate_json(state.model_dump_json())
    assert restored.current.opening.patient_text == restored.current.turns[0].patient_text == old


@pytest.mark.asyncio
@pytest.mark.parametrize("failure", ["oversize", "truncated", "unfinished", "missing_choice"])
@pytest.mark.parametrize("repair_succeeds", [True, False])
async def test_budget_repair_and_failure_leave_input_state_intact(settings, catalog, failure, repair_succeeds):
    calls = []
    def handle(request):
        payload = json.loads(request.content)
        calls.append(payload)
        assert payload["max_tokens"] == PATIENT_MAX_OUTPUT_TOKENS
        good = repair_succeeds and len(calls) == 2
        if failure == "missing_choice" and not good:
            return httpx.Response(200, json={"choices": []})
        text = "제 마음을 더 이야기하고 싶어요." if good else {"oversize": "가" * 61 + ".", "truncated": "대답하다가", "unfinished": "제 마음이 아직"}[failure]
        return httpx.Response(200, json={"choices": [{"finish_reason": "length" if failure == "truncated" and not good else "stop",
            "message": {"content": json.dumps({"text": text, "disclosed_fact_ids": []}, ensure_ascii=False)}}]})
    llm = VllmClient(settings)
    llm.config["roles"]["patient"]["max_tokens"] = 512  # Also clamp old/private profiles.
    llm.count = lambda *args: 1
    await llm.http.aclose()
    llm.http = httpx.AsyncClient(transport=httpx.MockTransport(handle), base_url="http://test")
    state = CaseState()
    original = state.model_dump_json()
    try:
        operation = DialogueService(llm, catalog).open_session(OpenRequest(state=state, session_id=state.current.id, request_id="length_test"))
        if repair_succeeds:
            result = await operation
            assert result.state.current.opening.patient_text == "제 마음을 더 이야기하고 싶어요."
            assert result.state.revision == state.revision + 1
        else:
            with pytest.raises(ExperimentError) as error:
                await operation
            assert error.value.code == "invalid_model_output"
        assert len(calls) == 2 and state.model_dump_json() == original
        if failure == "oversize":
            # Compression has its source meaning, but never appends that rejected
            # draft to the saved conversation or substitutes a clipped substring.
            feedback = calls[-1]["messages"][-1]["content"]
            assert "핵심 뜻·감정·사실을 그대로 유지" in feedback and "가" * 61 in feedback
        assert llm.output_budget("evaluation") == llm.config["roles"]["evaluation"]["max_tokens"]
        assert llm.output_budget("summary") == llm.config["roles"]["summary"]["max_tokens"]
    finally:
        await llm.close()
