from collections.abc import Callable
from time import perf_counter
from typing import TypeVar
from pathlib import Path
import json

import httpx
from pydantic import BaseModel, ValidationError

from app.core.config import ExperimentError, InvalidModelOutput, Settings
from app.core.dialogue_limits import PATIENT_MAX_OUTPUT_TOKENS
from app.core.telemetry import current_trace
from app.schemas.models import Metrics

T = TypeVar("T", bound=BaseModel)


def compact(value) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


class VllmClient:
    def __init__(self, settings: Settings, tokenizer=None):
        self.settings = settings
        self.config = settings.model
        self.tokenizer = tokenizer
        headers = {"Authorization": f"Bearer {settings.vllm_api_key}"} if settings.vllm_api_key else {}
        self.http = httpx.AsyncClient(base_url=settings.vllm_url, headers=headers, timeout=httpx.Timeout(180, connect=5))

    def load_tokenizer(self):
        from transformers import AutoTokenizer
        manifest = self.settings.model_path / ".seoyun-model.json"
        metadata = json.loads(manifest.read_text("utf-8")) if manifest.exists() else {}
        if (metadata.get("revision"), metadata.get("model_id")) != (self.config["revision"], self.config["model_id"]):
            raise ExperimentError("고정 리비전 모델을 먼저 다운로드하세요: python tools/download_model.py", "model_missing", 503)
        self.tokenizer = AutoTokenizer.from_pretrained(self.settings.model_path, local_files_only=True, trust_remote_code=False)

    def count(self, messages: list[dict], role: str) -> int:
        if self.tokenizer is None:
            self.load_tokenizer()
        tokens = self.tokenizer.apply_chat_template(
            messages, tokenize=True, add_generation_prompt=True, return_dict=False,
            **self.template_kwargs(role),
        )
        trace = current_trace.get()
        if trace is not None:
            trace.max_context_tokens = max(trace.max_context_tokens, len(tokens))
        return len(tokens)

    def template_kwargs(self, role: str) -> dict:
        return {**self.config.get("chat_template_kwargs", {}),
                "enable_thinking": self.config["roles"][role]["thinking"]}

    def fits(self, messages: list[dict], role: str) -> bool:
        return self.count(messages, role) + self.output_budget(role) + 128 <= self.config["max_model_len"]

    def output_budget(self, role: str) -> int:
        configured = self.config["roles"][role]["max_tokens"]
        return min(configured, PATIENT_MAX_OUTPUT_TOKENS) if role == "patient" else configured

    async def health(self) -> dict:
        try:
            response = await self.http.get("/v1/models", timeout=5)
            response.raise_for_status()
            selected = next((m for m in response.json()["data"] if m["id"] == self.config["served_model_name"]), {})
            matches = (Path(selected.get("root", "")).resolve() == self.settings.model_path.resolve()
                       and selected.get("max_model_len") == self.config["max_model_len"])
            return {"ready": matches, "profile_matches": matches, "model": self.config["served_model_name"]}
        except (httpx.HTTPError, KeyError, ValueError):
            return {"ready": False, "model": self.config["served_model_name"]}

    async def generate(self, role: str, messages: list[dict], schema: type[T], metrics: Metrics,
                       validate: Callable[[T], None] | None = None) -> T:
        request_messages = list(messages)
        role_config = self.config["roles"][role]
        error = ""
        for attempt in range(2):
            if not self.fits(request_messages, role):
                raise ExperimentError("문맥 한도를 초과했습니다. 입력을 줄이거나 기억 압축 상태를 확인하세요.", "context_overflow")
            payload = {
                "model": self.config["served_model_name"], "messages": request_messages,
                "stream": False, "temperature": role_config["temperature"],
                "max_tokens": self.output_budget(role),
                "chat_template_kwargs": self.template_kwargs(role),
                "response_format": {"type": "json_schema", "json_schema": {"name": schema.__name__, "schema": schema.model_json_schema(), "strict": True}},
            }
            for key in ("top_p", "top_k", "presence_penalty", "repetition_penalty", "seed"):
                if key in role_config:
                    payload[key] = role_config[key]
            if role_config.get("thinking_token_budget"):
                payload["thinking_token_budget"] = role_config["thinking_token_budget"]
            started = perf_counter()
            try:
                response = await self.http.post("/v1/chat/completions", json=payload)
                response.raise_for_status()
                data = response.json()
            except httpx.TimeoutException as exc:
                raise ExperimentError("모델 응답 대기 시간이 초과되었습니다. 상태는 확정하지 않았습니다.", "model_timeout", 504) from exc
            except (httpx.HTTPError, ValueError) as exc:
                raise ExperimentError("모델 서버 연결 또는 응답에 실패했습니다. 상태는 확정하지 않았습니다.", "model_unavailable", 503) from exc
            finally:
                seconds = perf_counter() - started
                metrics.calls += 1
                metrics.role_seconds[role] = metrics.role_seconds.get(role, 0) + seconds
                trace = current_trace.get()
                if trace is not None:
                    trace.calls += 1
                    trace.model_seconds += seconds
                    trace.role_seconds[role] = trace.role_seconds.get(role, 0) + seconds
            processed = perf_counter()
            try:
                usage = data.get("usage", {})
                metrics.prompt_tokens += usage.get("prompt_tokens", 0)
                metrics.completion_tokens += usage.get("completion_tokens", 0)
                if trace is not None:
                    trace.prompt_tokens += usage.get("prompt_tokens", 0)
                    trace.completion_tokens += usage.get("completion_tokens", 0)
                choice = data["choices"][0]
                if choice.get("finish_reason") != "stop":
                    raise InvalidModelOutput("출력이 끝나기 전에 잘렸습니다. 더 짧게 작성하세요.")
                # Only content is consumed. Reasoning is neither displayed nor persisted.
                value = schema.model_validate_json(choice["message"]["content"])
                if validate:
                    validate(value)
                return value
            except (ValidationError, InvalidModelOutput, ValueError, KeyError, TypeError, IndexError) as exc:
                error = ("; ".join(item["msg"] for item in exc.errors(include_input=False, include_url=False))
                         if role == "patient" and isinstance(exc, ValidationError) else str(exc))[:700]
                if attempt == 0:
                    feedback = "형식/검증 오류입니다. 원래 과제를 다시 수행하고 JSON만 짧게 반환하세요. 잘린 출력은 더 간결하게 만드세요. 오류: " + error
                    if role == "patient":
                        feedback += " 이번 수정의 text는 공백 포함 35자 안팎의 완결된 한 문장으로 끝내세요. 가장 중요한 대답 하나를 고르고, 추가 설명은 다음 대화로 미루세요."
                    request_messages = messages + [{"role": "user", "content": feedback}]
            finally:
                if trace is not None:
                    trace.postprocess_seconds += perf_counter() - processed
        # Validation feedback can contain source quotes. Keep it inside the model
        # repair request, never in a default error response or application log.
        raise ExperimentError(f"{role} 모델 출력 검증이 두 번 실패했습니다. 저장된 상태에서 같은 요청으로 재시도하세요.", "invalid_model_output", 502)

    async def close(self):
        await self.http.aclose()
