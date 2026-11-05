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

    @classmethod
    def from_env(cls):
        return cls(
            vllm_url=os.getenv("SEOYUN_VLLM_URL", cls.vllm_url).rstrip("/"),
            api_url=os.getenv("SEOYUN_API_URL", cls.api_url).rstrip("/"),
            api_key=os.getenv("SEOYUN_API_KEY", ""),
            vllm_api_key=os.getenv("SEOYUN_VLLM_API_KEY", ""),
        )

    @property
    def model(self) -> dict:
        return json.loads((self.root / "ai/configs/seoyun.json").read_text("utf-8"))

    @property
    def model_path(self) -> Path:
        return self.root / self.model["local_directory"]

    @property
    def prompts(self) -> Path:
        return self.root / "server/prompts"

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
