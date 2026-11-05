from collections.abc import Callable
from time import perf_counter
from typing import TypeVar
import json

import httpx
from pydantic import BaseModel, ValidationError

from app.core.config import ExperimentError, InvalidModelOutput, Settings
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
        if not manifest.exists() or json.loads(manifest.read_text("utf-8"))["revision"] != self.config["revision"]:
            raise ExperimentError("고정 리비전 모델을 먼저 다운로드하세요: python tools/download_model.py", "model_missing", 503)
        self.tokenizer = AutoTokenizer.from_pretrained(self.settings.model_path, local_files_only=True, trust_remote_code=False)

    def count(self, messages: list[dict], role: str) -> int:
        if self.tokenizer is None:
            self.load_tokenizer()
        tokens = self.tokenizer.apply_chat_template(
            messages, tokenize=True, add_generation_prompt=True, return_dict=False,
            **self.template_kwargs(role),
        )
        return len(tokens)

    def template_kwargs(self, role: str) -> dict:
        return {**self.config.get("chat_template_kwargs", {}),
                "enable_thinking": self.config["roles"][role]["thinking"]}

    def fits(self, messages: list[dict], role: str) -> bool:
        return self.count(messages, role) + self.config["roles"][role]["max_tokens"] + 128 <= self.config["max_model_len"]

    async def health(self) -> dict:
        try:
            response = await self.http.get("/v1/models", timeout=5)
            response.raise_for_status()
            available = {m["id"] for m in response.json()["data"]}
            return {"ready": self.config["served_model_name"] in available, "model": self.config["served_model_name"]}
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
                "max_tokens": role_config["max_tokens"],
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
            except (httpx.HTTPError, ValueError) as exc:
                raise ExperimentError("모델 서버 연결 또는 응답에 실패했습니다. 상태는 확정하지 않았습니다.", "model_unavailable", 503) from exc
            finally:
                seconds = perf_counter() - started
                metrics.calls += 1
                metrics.role_seconds[role] = metrics.role_seconds.get(role, 0) + seconds
            try:
                usage = data.get("usage", {})
                metrics.prompt_tokens += usage.get("prompt_tokens", 0)
                metrics.completion_tokens += usage.get("completion_tokens", 0)
                choice = data["choices"][0]
                if choice.get("finish_reason") != "stop":
                    raise InvalidModelOutput("출력이 끝나기 전에 잘렸습니다. 더 짧게 작성하세요.")
                # Only content is consumed. Reasoning is neither displayed nor persisted.
                value = schema.model_validate_json(choice["message"]["content"])
                if validate:
                    validate(value)
                return value
            except (ValidationError, InvalidModelOutput, ValueError, KeyError, TypeError, IndexError) as exc:
                error = str(exc)[:700]
                if attempt == 0:
                    request_messages = messages + [{"role": "user", "content": "형식/검증 오류입니다. 원래 과제를 다시 수행하고 JSON만 짧게 반환하세요. 잘린 출력은 더 간결하게 만드세요. 오류: " + error}]
        raise ExperimentError(f"{role} 모델 출력 검증이 두 번 실패했습니다: " + error, "invalid_model_output", 502)

    async def close(self):
        await self.http.aclose()
