"""Bounded, volatile single-flight responses, not a game-state database.

The client remains the commit authority. A restart/expiry can cause recomputation,
but cannot append to a saved conversation. Reuse the original request and snapshot.
"""
import asyncio
from collections import OrderedDict
from dataclasses import dataclass, field
import hashlib
import json
from time import monotonic, perf_counter

from app.core.config import ExperimentError
from app.core.telemetry import Trace, current_trace, emit


@dataclass
class Entry:
    fingerprint: str
    trace: Trace = field(default_factory=Trace)
    task: asyncio.Task | None = None
    finished_at: float = 0.0
    size: int = 0

    @property
    def pending(self):
        return self.task is not None and not self.task.done()


class RequestCoordinator:
    def __init__(self, settings):
        self.settings = settings
        self.gate = asyncio.Lock()
        self.entries = OrderedDict()
        self.expiry_timer = None

    def prune(self):
        if self.expiry_timer is not None:
            self.expiry_timer.cancel()
            self.expiry_timer = None
        for key, entry in list(self.entries.items()):
            if not entry.pending and monotonic() - entry.finished_at >= self.settings.response_cache_seconds:
                del self.entries[key]
        size = sum(entry.size for entry in self.entries.values())
        for key, entry in list(self.entries.items()):
            if len(self.entries) <= self.settings.response_cache_entries and size <= self.settings.response_cache_bytes:
                break
            if not entry.pending:
                size -= entry.size
                del self.entries[key]
        deadlines = [e.finished_at + self.settings.response_cache_seconds for e in self.entries.values() if not e.pending]
        if deadlines:
            # Expire private response data even when no subsequent request arrives.
            self.expiry_timer = asyncio.get_running_loop().call_later(max(.001, min(deadlines) - monotonic()), self.prune)

    def completed(self, task):
        failed = task.cancelled() or task.exception() is not None
        if failed:
            # An exception traceback can retain the entire prompt/HTTP response.
            # Keep only a small fingerprint tombstone, not failed task frames.
            for entry in self.entries.values():
                if entry.task is task:
                    entry.task = None
                    break
        self.prune()

    async def execute(self, action, parsed, raw, http_request, operation, needs_model=True):
        state = getattr(parsed, "state", parsed)
        request_id = getattr(parsed, "request_id", None) or parsed.turn_id
        key = (state.game_progress_id, state.patient_id, state.case_id, parsed.session_id,
               action, request_id, getattr(state, "revision", 0))
        fingerprint = hashlib.sha256(json.dumps(raw, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
        http_request.state.request_id = request_id
        http_request.state.action = action
        if action not in {"start", "revisit"} and parsed.session_id != state.current.id:
            # Reject stale identities before they occupy the inference queue.
            raise ExperimentError("현재 상담 ID가 일치하지 않습니다.", "stale_session", 409)
        self.prune()
        entry = self.entries.get(key)
        if entry and entry.fingerprint != fingerprint:
            raise ExperimentError("같은 요청 ID의 입력 또는 상태가 달라졌습니다. 보관한 원래 요청으로 재시도하세요.", "request_conflict", 409)
        if entry and (entry.task is None or (entry.task.done() and (entry.task.cancelled() or entry.task.exception() is not None))):
            # Failures do not become successful replay responses. Same input may retry.
            del self.entries[key]
            entry = None
        cache = "hit" if entry and not entry.pending else "joined" if entry else "miss"
        if entry is None:
            if sum(e.pending for e in self.entries.values()) >= self.settings.max_pending_requests:
                raise ExperimentError("AI 요청 대기열이 가득 찼습니다. 같은 요청으로 잠시 후 재시도하세요.", "server_busy", 503)
            entry = Entry(fingerprint)
            self.entries[key] = entry
            entry.task = asyncio.create_task(self.run(entry, action, request_id, state.patient_id, operation, needs_model))
            # Consume failures even when every HTTP waiter disconnects.
            entry.task.add_done_callback(self.completed)
        self.entries.move_to_end(key)
        http_request.state.trace = Trace() if cache == "hit" else entry.trace
        http_request.state.response_cache = cache
        # A disconnected waiter cannot cancel an in-flight model request shared by retries.
        result = await asyncio.shield(entry.task)
        self.prune()
        # replayed describes client revision semantics, NOT this transport cache.
        return result.model_copy(deep=True)

    async def run(self, entry, action, request_id, patient_id, operation, needs_model):
        started = perf_counter()
        token = current_trace.set(entry.trace)
        acquired = False
        status = "ok"
        try:
            if needs_model:
                queued = perf_counter()
                try:
                    await asyncio.wait_for(self.gate.acquire(), self.settings.queue_timeout_seconds)
                    acquired = True
                except TimeoutError as exc:
                    raise ExperimentError("AI 요청 대기 시간이 초과되었습니다. 같은 요청으로 재시도하세요.", "server_busy", 503) from exc
                finally:
                    entry.trace.queue_seconds = perf_counter() - queued
            try:
                result = await asyncio.wait_for(operation(), self.settings.operation_timeout_seconds)
            except TimeoutError as exc:
                raise ExperimentError("AI 처리 시간이 초과되었습니다. 상태를 확정하지 말고 같은 요청으로 재시도하세요.", "request_timeout", 504) from exc
            entry.size = len(result.model_dump_json().encode())
            return result
        except ExperimentError as exc:
            status = exc.code
            raise
        except BaseException:
            status = "cancelled_or_internal_error"
            raise
        finally:
            if acquired:
                self.gate.release()
            entry.finished_at = monotonic()
            entry.trace.total_seconds = perf_counter() - started
            current_trace.reset(token)
            emit("ai.operation", request_id=request_id, operation=action, patient_id=patient_id,
                 status=status, seconds=entry.trace.total_seconds, timings=entry.trace.timings(),
                 calls=entry.trace.calls, role_seconds=entry.trace.role_seconds,
                 max_context_tokens=entry.trace.max_context_tokens,
                 prompt_tokens=entry.trace.prompt_tokens, completion_tokens=entry.trace.completion_tokens)

    async def close(self):
        tasks = [e.task for e in self.entries.values() if e.pending]
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        self.entries.clear()
        if self.expiry_timer is not None:
            self.expiry_timer.cancel()
            self.expiry_timer = None
