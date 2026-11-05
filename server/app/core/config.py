from dataclasses import dataclass
from pathlib import Path
import json
import os

ROOT = Path(__file__).resolve().parents[3]


@dataclass(frozen=True)
class Settings:
    root: Path = ROOT
    vllm_url: str = "http://127.0.0.1:8000"
    api_url: str = "http://127.0.0.1:8001"
    api_key: str = ""
    vllm_api_key: str = ""
    model_config: str = "ai/configs/seoyun.json"
    mock: bool = False
    code_revision: str = "unknown"
    queue_timeout_seconds: float = 30.0
    # A long evaluation can need several evidence calls before scoring. Individual
    # model calls still time out after 180s; disconnected clients can rejoin the work.
    operation_timeout_seconds: float = 600.0
    max_pending_requests: int = 16
    response_cache_seconds: float = 600.0
    response_cache_entries: int = 256
    response_cache_bytes: int = 32 * 1024 * 1024

    @classmethod
    def from_env(cls):
        return cls(
            vllm_url=os.getenv("SEOYUN_VLLM_URL", cls.vllm_url).rstrip("/"),
            api_url=os.getenv("SEOYUN_API_URL", cls.api_url).rstrip("/"),
            api_key=os.getenv("SEOYUN_API_KEY", ""),
            vllm_api_key=os.getenv("SEOYUN_VLLM_API_KEY", ""),
            model_config=os.getenv("SEOYUN_MODEL_CONFIG", cls.model_config),
            mock=os.getenv("SEOYUN_MOCK", "0") == "1",
            code_revision=os.getenv("AI_CODE_REVISION", "unknown"),
        )

    @property
    def model(self) -> dict:
        return json.loads((self.root / self.model_config).read_text("utf-8"))

    @property
    def model_path(self) -> Path:
        return self.root / self.model["local_directory"]

    @property
    def prompts(self) -> Path:
        return self.root / "server/prompts"

    @property
    def patients_path(self) -> Path:
        return self.root / "Assets/StreamingAssets/GameData/Patients"

    @property
    def patient_path(self) -> Path:
        return self.root / "Assets/StreamingAssets/GameData/Patients/seoyun.json"


class ExperimentError(Exception):
    def __init__(self, message: str, code: str = "experiment_error", status: int = 422):
        super().__init__(message)
        self.code = code
        self.status = status


class InvalidModelOutput(ValueError):
    pass
