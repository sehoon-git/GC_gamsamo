import httpx
import pytest

from app.core.config import Settings, ExperimentError
from app.llm.client import VllmClient
from app.main import create_app
from app.schemas.models import CaseState, Metrics, PatientReply, TurnRequest
from conftest import FakeLLM, classification, reply


@pytest.mark.asyncio
async def test_api_auth_and_response_contract():
    settings = Settings(api_key="test-key")
    llm = FakeLLM([("Classification", classification("neutral", None)), ("PatientReply", reply())])
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(settings, llm)), base_url="http://test") as client:
        assert (await client.get("/health")).status_code == 200
        state = CaseState()
        body = TurnRequest(state=state, session_id=state.current.id, turn_id="hello", text="안녕하세요").model_dump()
        assert (await client.post("/v1/dialogue/turn", json=body)).status_code == 401
        response = await client.post("/v1/dialogue/turn", json=body, headers={"Authorization": "Bearer test-key"})
        assert response.status_code == 200, response.text
        assert response.json()["request_id"] == "hello"
        assert len(response.json()["state"]["sessions"][0]["topics"]) == 0


class TinyTokenizer:
    def apply_chat_template(self, *args, **kwargs):
        # Transformers 5 defaults to BatchEncoding; len(BatchEncoding) counts fields.
        assert kwargs["return_dict"] is False
        return [1] * 50


@pytest.mark.asyncio
async def test_context_budget_counts_token_ids_instead_of_encoding_fields(settings):
    class LongTokenizer(TinyTokenizer):
        def apply_chat_template(self, *args, **kwargs):
            super().apply_chat_template(*args, **kwargs)
            return [1] * 13000
    llm = VllmClient(settings, tokenizer=LongTokenizer())
    messages = [{"role": "user", "content": "긴 기록"}]
    assert llm.count(messages, "evaluation") == 13000
    assert not llm.fits(messages, "evaluation")
    await llm.close()


@pytest.mark.asyncio
async def test_invalid_model_output_retries_once_and_never_exposes_reasoning(settings):
    requests = []
    def handle(request):
        requests.append(request)
        content = '{"wrong":true}' if len(requests) == 1 else '{"text":"안녕하세요.","disclosed_fact_ids":[]}'
        return httpx.Response(200, json={"choices": [{"finish_reason": "stop", "message": {"content": content, "reasoning": "not persisted"}}], "usage": {"prompt_tokens": 50, "completion_tokens": 20}})
    llm = VllmClient(settings, tokenizer=TinyTokenizer())
    await llm.http.aclose()
    llm.http = httpx.AsyncClient(transport=httpx.MockTransport(handle), base_url="http://test")
    metrics = Metrics()
    result = await llm.generate("patient", [{"role": "user", "content": "인사"}], PatientReply, metrics)
    assert result.text == "안녕하세요."
    assert metrics.calls == 2
    assert "reasoning" not in result.model_dump()
    await llm.close()


@pytest.mark.asyncio
async def test_two_invalid_outputs_fail_without_silent_fallback(settings):
    def handle(request):
        return httpx.Response(200, json={"choices": [{"finish_reason": "length", "message": {"content": "{"}}]})
    llm = VllmClient(settings, tokenizer=TinyTokenizer())
    await llm.http.aclose()
    llm.http = httpx.AsyncClient(transport=httpx.MockTransport(handle), base_url="http://test")
    metrics = Metrics()
    with pytest.raises(ExperimentError, match="두 번"):
        await llm.generate("patient", [{"role": "user", "content": "인사"}], PatientReply, metrics)
    assert metrics.calls == 2
    await llm.close()
