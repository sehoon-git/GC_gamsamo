from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path
from uuid import uuid4
import json
import hashlib
import os
import tempfile

import httpx
from pydantic import TypeAdapter

from app.core.config import ExperimentError, ROOT, Settings
from app.schemas.models import CaseState, CloseRequest, EvaluateRequest, Id, StateResponse, TurnRequest, new_id, records
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
        if os.name != "nt":
            directory_fd = os.open(path.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
            try:
                os.fsync(directory_fd)
            finally:
                os.close(directory_fd)
    finally:
        Path(temporary).unlink(missing_ok=True)


class LegacySaveError(ValueError):
    pass


class SaveStore:
    """Immutable state/transcript pairs; one index atomically commits all pointers."""
    def __init__(self, directory: Path | None = None):
        base = Path(os.getenv("XDG_DATA_HOME", str(Path.home() / ".local/share")))
        self.directory = Path(directory or os.getenv("SEOYUN_DATA_DIR", str(base / "gc-gamsamo/seoyun"))).expanduser().resolve()
        if self.directory.is_relative_to(ROOT):
            raise ValueError("상담 기록은 프로젝트 밖에 저장해야 합니다. SEOYUN_DATA_DIR을 확인하세요.")
        self.root = self.directory / "v2"

    def _index(self):
        path = self.root / "index.json"
        if not path.exists():
            return {"format_version": 2, "active_case_id": None, "cases": {}}
        data = json.loads(path.read_text("utf-8"))
        if not isinstance(data, dict) or data.get("format_version") != 2 or not isinstance(data.get("cases"), dict):
            raise ValueError("자동 저장 인덱스가 손상되었거나 지원하지 않는 형식입니다.")
        active = data.get("active_case_id")
        for case_id, head in data["cases"].items():
            TypeAdapter(Id).validate_python(case_id)
            TypeAdapter(Id).validate_python(head["snapshot"])
            if type(head["revision"]) is not int or head["revision"] < 0:
                raise ValueError("저장 revision이 올바르지 않습니다.")
        if active is not None and active not in data["cases"]:
            raise ValueError("현재 실험의 저장 포인터가 없습니다.")
        return data

    @contextmanager
    def _lock(self):
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        with (self.root / ".save.lock").open("a+b") as handle:
            if os.name == "nt":
                import msvcrt
                handle.seek(0)
                if not handle.read(1):
                    handle.write(b"0")
                    handle.flush()
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_LOCK, 1)
            else:
                import fcntl
                fcntl.flock(handle, fcntl.LOCK_EX)
            try:
                yield
            finally:
                if os.name == "nt":
                    handle.seek(0)
                    msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(handle, fcntl.LOCK_UN)

    def active_id(self):
        return self._index()["active_case_id"]

    def _read(self, case_id, head):
        folder = self.root / case_id / head["snapshot"]
        state = CaseState.model_validate_json((folder / "state.json").read_text("utf-8"))
        if state.case_id != case_id or state.revision != head["revision"]:
            raise ValueError("저장 파일의 실험 ID 또는 revision이 일치하지 않습니다.")
        transcript = json.loads((folder / "transcript.json").read_text("utf-8"))
        if transcript != self.transcript(state):
            raise ValueError("원문과 상태가 일치하지 않습니다. 기존 기록을 보존했습니다.")
        return state

    @staticmethod
    def transcript(state):
        return {"case_id": state.case_id, "revision": state.revision,
                "records": [r.model_dump(mode="json") for r in records(state)]}

    def save(self, state: CaseState, *, expected_revision=None):
        checked = CaseState.model_validate(state.model_dump())
        with self._lock():
            index = self._index()
            head = index["cases"].get(checked.case_id)
            if head:
                previous = self._read(checked.case_id, head)
                if previous == checked:
                    if index["active_case_id"] != checked.case_id:
                        index["active_case_id"] = checked.case_id
                        atomic_json(self.root / "index.json", index)
                    return self.root / checked.case_id / head["snapshot"] / "state.json"
                if checked.revision <= previous.revision:
                    raise ValueError("이미 저장된 상태보다 오래되거나 충돌하는 revision입니다.")
            if expected_revision is not None and (head["revision"] if head else 0) != expected_revision:
                raise ValueError("다른 실행이 저장 상태를 변경했습니다. 자동 저장을 다시 불러오세요.")
            snapshot = f"r{checked.revision}_{uuid4().hex}"
            folder = self.root / checked.case_id / snapshot
            atomic_json(folder / "state.json", checked.model_dump(mode="json"))
            atomic_json(folder / "transcript.json", self.transcript(checked))
            if os.name != "nt":
                # Persist the new revision directory's entry before committing the index.
                descriptor = os.open(folder.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
            index["cases"][checked.case_id] = {"snapshot": snapshot, "revision": checked.revision}
            index["active_case_id"] = checked.case_id
            # Only this last write makes the new pair visible to readers.
            atomic_json(self.root / "index.json", index)
            return folder / "state.json"

    def load(self, case_id=None):
        index = self._index()
        case_id = case_id or index["active_case_id"]
        if case_id is None:
            if (self.directory / "active.json").exists():
                raise LegacySaveError("이전 형식의 기록은 보존했습니다. --new로 형식 2의 새 실험을 시작하세요.")
            return None
        case_id = TypeAdapter(Id).validate_python(case_id)
        head = index["cases"].get(case_id)
        if head is None:
            if (self.directory / case_id / "auto.json").exists():
                raise LegacySaveError("주제 기반 형식 1은 이어하기로 변환하지 않습니다. 이전 기록을 보존하고 새 실험을 시작하세요.")
            raise ValueError("이 실험의 자동 저장 파일이 없습니다.")
        return self._read(case_id, head)

    def cases(self):
        return sorted(self._index()["cases"])


@dataclass(frozen=True)
class RequestToken:
    epoch: int
    case_id: str
    session_id: str
    revision: int
    request_id: str


class SessionClient:
    def __init__(self, settings: Settings, store: SaveStore, state: CaseState, http=None, confirm_disclosures=None):
        self.settings, self.store = settings, store
        self.catalog = Catalog(settings)
        self.catalog.validate_state(state)
        self.state = state
        self.epoch = 0
        self.active_request = None
        self.pending_text = ""
        self.pending_request = None
        self.pending_response = None
        self.pending_state = None
        # The terminal demo confirms the validated proposals. Unity owns its policy here.
        self.confirm_disclosures = confirm_disclosures or (lambda candidates, state: candidates)
        headers = {"Authorization": "Bearer " + settings.api_key} if settings.api_key else {}
        self.http = http or httpx.AsyncClient(base_url=settings.api_url, headers=headers, timeout=httpx.Timeout(180, connect=5))

    def replace(self, state):
        self.catalog.validate_state(state)
        self.store.save(state)
        self.state = state.model_copy(deep=True)
        self.epoch += 1
        self.active_request = self.pending_request = self.pending_response = None
        self.pending_state = None
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
        before, after = self.state, response.state
        if before.sessions[:-1] != after.sessions[:-1] or len(before.sessions) != len(after.sessions):
            raise ExperimentError("응답이 이전 회차 원문을 변경했습니다.", "invalid_response", 502)
        if (before.created_at, before.current.started_at) != (after.created_at, after.current.started_at):
            raise ExperimentError("응답이 상담 생성 정보를 변경했습니다.", "invalid_response", 502)
        old_turns = [t.model_dump() for t in before.current.turns]
        kept_turns = [t.model_dump() for t in after.current.turns[:len(old_turns)]]
        if before.current.phase == "closing" and kept_turns:
            kept_turns[-1]["closing_text"] = old_turns[-1]["closing_text"]
        if kept_turns != old_turns or (response.replayed and after != before):
            raise ExperimentError("응답이 확정된 대화 원문을 변경했습니다.", "invalid_response", 502)
        if response.state.revealed_fact_ids != self.state.revealed_fact_ids:
            raise ExperimentError("서버가 클라이언트의 공개 확정 상태를 변경했습니다.", "invalid_response", 502)
        candidates = set(response.disclosure_candidates)
        turn_candidates = set(response.state.current.turns[-1].candidate_fact_ids) if response.state.current.turns else set()
        if not candidates <= turn_candidates:
            raise ExperimentError("공개 후보가 대화 기록과 일치하지 않습니다.", "invalid_response", 502)
        updated = self.pending_state or response.state.model_copy(deep=True)
        if self.pending_state is None:
            confirmed = set(self.confirm_disclosures(sorted(candidates), updated.model_copy(deep=True)))
            if not confirmed <= candidates:
                raise ExperimentError("확정 정보는 제공된 후보에서 선택해야 합니다.", "invalid_disclosure", 422)
            updated.revealed_fact_ids = sorted(set(updated.revealed_fact_ids) | confirmed)
        self.catalog.validate_state(updated)
        self.pending_response = (response, token)
        self.pending_state = updated
        self.store.save(updated, expected_revision=token.revision)
        self.state = updated
        self.pending_response = self.pending_request = None
        self.pending_state = None
        self.pending_text = ""

    async def send(self, endpoint, request, request_id):
        if self.active_request is not None:
            raise ExperimentError("이전 응답을 기다리고 있습니다.", "request_pending", 409)
        if self.pending_response is not None:
            raise ExperimentError("받은 응답의 자동 저장을 먼저 /retry로 완료하세요.", "save_pending", 409)
        token = self.token(request_id)
        if (request.state.case_id, request.state.revision) != (token.case_id, token.revision):
            raise ExperimentError("재시도할 요청의 상태가 변경되었습니다.", "stale_request", 409)
        self.active_request = token
        self.pending_request = (endpoint, request, request_id)
        try:
            response = await self.http.post(endpoint, json=request.model_dump(mode="json"))
            if response.is_error:
                body = response.json().get("detail", {})
                if isinstance(body, dict):
                    code, message = body.get("code", "api_error"), body.get("message", "요청 데이터 또는 서버 상태를 확인하세요.")
                else:
                    code, message = "api_error", "요청 데이터 검증에 실패했습니다."
                if code == "needs_split":
                    self.pending_request = None
                raise ExperimentError(message, code, response.status_code)
            result = StateResponse.model_validate_json(response.text)
            if isinstance(request, TurnRequest):
                turns = result.state.current.turns
                if not turns or (turns[-1].id, turns[-1].user_text) != (request.turn_id, request.text):
                    raise ExperimentError("응답의 마지막 턴이 보낸 발언과 일치하지 않습니다.", "invalid_response", 502)
            elif isinstance(request, CloseRequest):
                if result.state.current.phase not in {"pending_evaluation", "evaluated"}:
                    raise ExperimentError("응답에 마무리 결과가 없습니다.", "invalid_response", 502)
            elif result.state.current.evaluation is None:
                raise ExperimentError("응답에 평가 결과가 없습니다.", "invalid_response", 502)
            self.accept(result, token)
            return result
        except httpx.HTTPError as exc:
            raise ExperimentError("API 연결에 실패했습니다. 확정 상태는 유지됩니다.", "connection_failed", 503) from exc
        finally:
            if self.active_request == token:
                self.active_request = None

    async def retry(self):
        if self.pending_response is not None:
            response, token = self.pending_response
            self.accept(response, token)
            return self.state
        if self.pending_request is None:
            raise ExperimentError("재시도할 요청이 없습니다. 복합 질문은 하나씩 나눠 보내세요.", "nothing_to_retry", 409)
        await self.send(*self.pending_request)
        return self.state

    async def turn(self, text):
        if self.active_request is not None or self.pending_response is not None:
            raise ExperimentError("이전 응답을 기다리고 있거나 자동 저장을 완료해야 합니다.", "request_pending", 409)
        self.pending_text = text
        request = TurnRequest(state=self.state, session_id=self.state.current.id, turn_id=new_id("turn"), text=text)
        await self.send("/v1/dialogue/turn", request, request.turn_id)
        return self.state.current.turns[-1]

    def ensure_saved(self):
        saved = self.store.load(self.state.case_id)
        if saved != self.state:
            raise ExperimentError("확정 상태와 자동 저장이 일치하지 않습니다. 저장부터 완료하세요.", "save_required", 409)

    async def finish(self):
        self.ensure_saved()
        request = CloseRequest(state=self.state, session_id=self.state.current.id, request_id=self.phase_request_id("closing"))
        await self.send("/v1/dialogue/close", request, request.request_id)
        return self.state.current.turns[-1].closing_text

    async def evaluate(self):
        self.ensure_saved()
        request = EvaluateRequest(state=self.state, session_id=self.state.current.id, request_id=self.phase_request_id("evaluation"))
        await self.send("/v1/dialogue/evaluate", request, request.request_id)
        return self.state.current.evaluation

    def phase_request_id(self, phase):
        # A saved phase produces the same request ID even after restarting the CLI.
        source = f"{self.state.case_id}/{self.state.current.id}/{self.state.revision}/{phase}"
        return phase + "_" + hashlib.sha256(source.encode()).hexdigest()[:32]

    async def close(self):
        await self.http.aclose()
