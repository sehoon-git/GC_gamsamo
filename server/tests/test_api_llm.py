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
    llm = FakeLLM([("Classification", classification("none")), ("PatientReply", reply())])
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=create_app(settings, llm)), base_url="http://test") as client:
        assert (await client.get("/health")).status_code == 200
        state = CaseState()
        body = TurnRequest(state=state, session_id=state.current.id, turn_id="hello", text="안녕하세요").model_dump()
        assert (await client.post("/v1/dialogue/turn", json=body)).status_code == 401
        response = await client.post("/v1/dialogue/turn", json=body, headers={"Authorization": "Bearer test-key"})
        assert response.status_code == 200, response.text
        assert response.json()["request_id"] == "hello"
        assert response.json()["state"]["sessions"][0]["questions_used"] == 0


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


@pytest.mark.asyncio
async def test_legacy_version_and_compound_have_stable_error_envelopes():
    settings = Settings(mock=True, api_key="test-key")
    async with httpx.AsyncClient(base_url="http://test", transport=httpx.ASGITransport(app=create_app(settings)), headers={"Authorization": "Bearer test-key"}) as client:
        assert (await client.get("/health")).json()["mode"] == "mock"
        state = CaseState()
        request = TurnRequest(state=state, session_id=state.current.id, turn_id="multi", text="면접은요? 친구는요?").model_dump()
        result = await client.post("/v1/dialogue/turn", json=request)
        assert result.status_code == 422 and result.json()["detail"]["code"] == "needs_split"
        request["state"]["format_version"] = 1
        result = await client.post("/v1/dialogue/turn", json=request)
        assert result.status_code == 422 and result.json()["detail"]["code"] == "unsupported_save_version"
        for endpoint in ["close", "evaluate"]:
            body = {"state": state.model_dump(), "session_id": state.current.id, "request_id": endpoint}
            response = await client.post(f"/v1/dialogue/{endpoint}", json=body)
            assert response.status_code == 409 and response.json()["detail"]["code"] == "session_open"
            assert (await client.post(f"/v1/dialogue/{endpoint}", json=body, headers={"Authorization": "Bearer wrong"})).status_code == 401


@pytest.mark.asyncio
async def test_model_sampling_and_template_settings_are_forwarded():
    import json
    settings = Settings(model_config="ai/configs/qwen38-27b-fp8.json")
    class TemplateTokenizer(TinyTokenizer):
        def apply_chat_template(self, *args, **kwargs):
            assert kwargs["enable_thinking"] is False
            assert kwargs["preserve_thinking"] is False
            return super().apply_chat_template(*args, **kwargs)
    def handle(request):
        body = json.loads(request.content)
        assert body["chat_template_kwargs"] == {"enable_thinking": False, "preserve_thinking": False}
        assert (body["top_p"], body["top_k"], body["presence_penalty"]) == (0.8, 20, 1.5)
        return httpx.Response(200, json={"choices": [{"finish_reason": "stop", "message": {"content": '{"text":"안녕하세요.","disclosed_fact_ids":[]}'}}]})
    llm = VllmClient(settings, tokenizer=TemplateTokenizer())
    await llm.http.aclose()
    llm.http = httpx.AsyncClient(base_url="http://test", transport=httpx.MockTransport(handle))
    await llm.generate("patient", [{"role": "user", "content": "안녕하세요"}], PatientReply, Metrics())
    await llm.close()


@pytest.mark.asyncio
async def test_shared_model_alias_does_not_hide_wrong_weights_or_context(settings):
    root = str(settings.model_path)
    responses = iter([
        {'id':'seoyun','root':'/different/model','max_model_len':16384},
        {'id':'seoyun','root':root,'max_model_len':8192},
        {'id':'seoyun','root':root,'max_model_len':16384},
    ])
    llm = VllmClient(settings, tokenizer=TinyTokenizer())
    await llm.http.aclose()
    llm.http = httpx.AsyncClient(base_url='http://test', transport=httpx.MockTransport(lambda request: httpx.Response(200,json={'data':[next(responses)]})))
    assert not (await llm.health())['ready']
    assert not (await llm.health())['ready']
    assert (await llm.health())['ready']
    await llm.close()
