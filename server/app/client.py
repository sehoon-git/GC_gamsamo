from dataclasses import dataclass
from pathlib import Path
import json
import os
import tempfile

import httpx
from pydantic import TypeAdapter

from app.core.config import ExperimentError, ROOT, Settings
from app.schemas.models import CaseState, EvaluateRequest, Id, StateResponse, TurnRequest, new_id
from app.services.catalog import Catalog


def atomic_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, temporary = tempfile.mkstemp(prefix=".saving-", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(value, output, ensure_ascii=False, indent=2)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
        directory_fd = os.open(path.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    finally:
        Path(temporary).unlink(missing_ok=True)


class SaveStore:
    def __init__(self, directory: Path | None = None):
        base = Path(os.getenv("XDG_DATA_HOME", str(Path.home() / ".local/share")))
        self.directory = Path(directory or os.getenv("SEOYUN_DATA_DIR", str(base / "gc-gamsamo/seoyun"))).expanduser().resolve()
        if self.directory.is_relative_to(ROOT):
            raise ValueError("상담 기록은 프로젝트 밖에 저장해야 합니다. SEOYUN_DATA_DIR을 확인하세요.")

    def active_id(self):
        path = self.directory / "active.json"
        if not path.exists():
            return None
        return TypeAdapter(Id).validate_python(json.loads(path.read_text("utf-8"))["case_id"])

    def save(self, state: CaseState, slot="auto"):
        if slot not in {"auto", "manual"}:
            raise ValueError("저장 슬롯은 auto 또는 manual입니다.")
        checked = CaseState.model_validate(state.model_dump())
        path = self.directory / checked.case_id / (slot + ".json")
        atomic_json(path, checked.model_dump(mode="json"))
        atomic_json(self.directory / "active.json", {"case_id": checked.case_id})
        return path

    def load(self, slot="auto", case_id=None):
        if slot not in {"auto", "manual"}:
            raise ValueError("저장 슬롯은 auto 또는 manual입니다.")
        case_id = case_id or self.active_id()
        if case_id is None:
            return None
        case_id = TypeAdapter(Id).validate_python(case_id)
        path = self.directory / case_id / (slot + ".json")
        if not path.exists():
            raise ValueError(f"{slot} 저장 파일이 없습니다.")
        state = CaseState.model_validate_json(path.read_text("utf-8"))
        if state.case_id != case_id:
            raise ValueError("저장 파일의 실험 ID가 일치하지 않습니다.")
        return state

    def cases(self):
        return sorted(p.parent.name for p in self.directory.glob("*/auto.json"))


@dataclass(frozen=True)
class RequestToken:
    epoch: int
    case_id: str
    session_id: str
    revision: int
    request_id: str


class SessionClient:
    def __init__(self, settings: Settings, store: SaveStore, state: CaseState, http=None):
        self.settings, self.store = settings, store
        self.catalog = Catalog(settings)
        self.catalog.validate_state(state)
        self.state = state
        self.epoch = 0
        self.active_request = None
        self.pending_text = ""
        headers = {"Authorization": "Bearer " + settings.api_key} if settings.api_key else {}
        self.http = http or httpx.AsyncClient(base_url=settings.api_url, headers=headers, timeout=httpx.Timeout(600, connect=5))

    def replace(self, state: CaseState):
        self.catalog.validate_state(state)
        self.state = state.model_copy(deep=True)
        self.epoch += 1
        self.active_request = None
        self.pending_text = ""

    def token(self, request_id):
        return RequestToken(self.epoch, self.state.case_id, self.state.current.id, self.state.revision, request_id)

    def accept(self, response: StateResponse, token: RequestToken):
        if token != self.token(token.request_id):
            raise ExperimentError("불러오기 또는 상태 변경 이전에 시작된 응답을 무시했습니다.", "stale_response", 409)
        if (response.case_id, response.state.case_id, response.session_id, response.state.current.id,
            response.request_id, response.base_revision) != (
            token.case_id, token.case_id, token.session_id, token.session_id, token.request_id, token.revision
        ):
            raise ExperimentError("응답의 실험·상담·요청 정보가 일치하지 않습니다.", "invalid_response", 502)
        expected = token.revision if response.replayed else token.revision + 1
        if response.state.revision != expected:
            raise ExperimentError("응답의 상태 버전이 일치하지 않습니다.", "invalid_response", 502)
        self.catalog.validate_state(response.state)
        self.store.save(response.state, "auto")
        self.state = response.state

    async def send(self, endpoint, request, request_id):
        if self.active_request is not None:
            raise ExperimentError("이전 응답을 기다리고 있습니다.", "request_pending", 409)
        token = self.token(request_id)
        self.active_request = token
        try:
            response = await self.http.post(endpoint, json=request.model_dump(mode="json"))
            if response.is_error:
                body = response.json().get("detail", {})
                message = body.get("message", "요청 데이터 또는 서버 상태를 확인하세요.") if isinstance(body, dict) else "요청 데이터 검증에 실패했습니다."
                raise ExperimentError(message, "api_error", response.status_code)
            result = StateResponse.model_validate_json(response.text)
            if isinstance(request, TurnRequest):
                turns = result.state.current.turns
                if not turns or (turns[-1].id, turns[-1].user_text) != (request.turn_id, request.text):
                    raise ExperimentError("응답의 마지막 턴이 보낸 발언과 일치하지 않습니다.", "invalid_response", 502)
            elif result.state.current.evaluation is None:
                raise ExperimentError("응답에 평가 결과가 없습니다.", "invalid_response", 502)
            self.accept(result, token)
            return result
        except httpx.HTTPError as exc:
            raise ExperimentError("API 연결에 실패했습니다. 확정 상태는 유지됩니다.", "connection_failed", 503) from exc
        finally:
            if self.active_request == token:
                self.active_request = None

    async def turn(self, text):
        if self.active_request is not None:
            raise ExperimentError("이전 응답을 기다리고 있습니다.", "request_pending", 409)
        self.pending_text = text
        request = TurnRequest(state=self.state, session_id=self.state.current.id, turn_id=new_id("turn"), text=text)
        result = await self.send("/v1/dialogue/turn", request, request.turn_id)
        self.pending_text = ""
        return result.state.current.turns[-1]

    async def evaluate(self):
        request = EvaluateRequest(state=self.state, session_id=self.state.current.id, request_id=new_id("evaluation"))
        result = await self.send("/v1/dialogue/evaluate", request, request.request_id)
        return result.state.current.evaluation

    async def close(self):
        await self.http.aclose()
