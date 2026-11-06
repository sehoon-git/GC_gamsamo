from contextlib import asynccontextmanager
import hmac
import os
from time import perf_counter

# All inference and conversation state stay local; no hosted tracing is enabled.
os.environ["LANGSMITH_TRACING"] = "false"
os.environ["LANGCHAIN_TRACING_V2"] = "false"

from fastapi import Depends, FastAPI, Header, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from app.core.config import ExperimentError, Settings
from app.core.dialogue_limits import PATIENT_MAX_CHARACTERS, PATIENT_MAX_OUTPUT_TOKENS
from app.core.requests import RequestCoordinator
from app.core.telemetry import emit
from app.llm.client import VllmClient
from app.llm.mock import MockLLM
from app.schemas.models import CONTENT_VERSION, FORMAT_VERSION, CloseRequest, EvaluateRequest, OpenRequest, RevisitRequest, StartRequest, StateResponse, TurnRequest
from app.services.catalog import Catalog
from app.schemas.models import PATIENT_IDS, content_version
from app.services.dialogue import DialogueService
from app.services.evaluation import EvaluationService
from app.patient_registry import patient_catalog
from app.schemas.models import PatientCatalogResponse


def create_app(settings: Settings | None = None, llm=None):
    settings = settings or Settings.from_env()
    engine = llm or (MockLLM(settings) if settings.mock else VllmClient(settings))
    catalogs = {patient_id: Catalog(settings, patient_id) for patient_id in PATIENT_IDS}
    dialogues = {key: DialogueService(engine, catalog) for key, catalog in catalogs.items()}
    evaluators = {key: EvaluationService(engine, catalog) for key, catalog in catalogs.items()}
    catalog = catalogs["seoyun"]  # Preserve the legacy health fields for existing clients.
    roster = patient_catalog(settings.root)

    def service(services, patient_id):
        if patient_id not in services:
            if any(p["id"] == patient_id and not p["ready"] for p in roster["patients"]):
                raise ExperimentError("정식 사연과 평가 기준이 준비되지 않은 환자입니다.", "content_not_ready", 409)
            raise ExperimentError("이 환자의 AI 상담 콘텐츠는 아직 준비되지 않았습니다.", "unsupported_patient", 404)
        return services[patient_id]
    coordinator = RequestCoordinator(settings)

    @asynccontextmanager
    async def lifespan(app):
        emit("ai.runtime", api_version=app.version, code_revision=settings.code_revision,
             model_id=engine.config["model_id"], model_revision=engine.config["revision"],
             tokenizer_revision=engine.config["revision"], max_model_len=engine.config["max_model_len"],
             prompt_version=catalog.prompt_version, mode="mock" if settings.mock else "model")
        yield
        await coordinator.close()
        await engine.close()

    app = FastAPI(title="상담 게임 AI", version="0.4.4", lifespan=lifespan)
    app.state.coordinator = coordinator

    @app.middleware("http")
    async def timings(request, call_next):
        started = perf_counter()
        response = await call_next(request)
        if hasattr(request.state, "request_id"):
            seconds = perf_counter() - started
            trace = getattr(request.state, "trace", None)
            spans = trace.timings() if trace else {}
            response.headers["X-Request-Id"] = request.state.request_id
            response.headers["X-AI-Cache"] = getattr(request.state, "response_cache", "none")
            response.headers["Server-Timing"] = ", ".join(f"{key};dur={value * 1000:.3f}" for key, value in {**spans, "request": seconds}.items())
            emit("ai.request", request_id=request.state.request_id, operation=request.state.action,
                 status=response.status_code, cache=response.headers["X-AI-Cache"], seconds=seconds)
        return response

    @app.exception_handler(ExperimentError)
    async def experiment_error(request, exc):
        retryable = exc.code in {"server_busy", "request_timeout", "model_timeout", "model_unavailable", "invalid_model_output"}
        return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": str(exc), "retryable": retryable}},
                            headers={"Retry-After": "2"} if retryable else None)

    @app.exception_handler(RequestValidationError)
    async def validation_error(request, exc):
        state = exc.body.get("state", {}) if isinstance(exc.body, dict) else {}
        patient_id = state.get("patient_id", "seoyun") if isinstance(state, dict) else "seoyun"
        expected_content = content_version(patient_id) if isinstance(patient_id, str) else CONTENT_VERSION
        legacy = isinstance(state, dict) and (state.get("format_version", FORMAT_VERSION) != FORMAT_VERSION
                 or state.get("content_version", expected_content) != expected_content)
        return JSONResponse(status_code=422, content={"detail": {
            "code": "unsupported_save_version" if legacy else "invalid_request",
            "retryable": False,
            "message": "이전 기록은 보존하고 형식 4의 새 실험을 시작하세요." if legacy else "요청 형식·대화 원문·상담 단계의 일관성을 확인하세요.",
        }})

    async def authorize(authorization: str = Header(default="")):
        if settings.api_key and not hmac.compare_digest(authorization, "Bearer " + settings.api_key):
            raise ExperimentError("접근 키를 확인하세요.", "unauthorized", 401)

    @app.get("/health")
    async def health():
        status = await engine.health()
        return JSONResponse(status_code=200 if status["ready"] else 503, content={
            "api": "ok", "inference": status, "content_version": catalog.patient["content_version"],
            "api_version": app.version, "code_revision": settings.code_revision,
            "model_id": engine.config["model_id"], "model_revision": engine.config["revision"],
            "tokenizer_revision": engine.config["revision"], "max_model_len": engine.config["max_model_len"],
            "prompt_version": catalog.prompt_version, "busy": coordinator.gate.locked(),
            "format_version": FORMAT_VERSION, "mode": "mock" if settings.mock else "model",
            "capabilities": ["game_progress_scope", "patient_catalog_v1", "request_timing_v1", "volatile_request_replay_v1", "single_bubble_reply_v1"],
            "patient_reply_limits": {"max_characters": PATIENT_MAX_CHARACTERS,
                                     "max_output_tokens": min(engine.config["roles"]["patient"]["max_tokens"], PATIENT_MAX_OUTPUT_TOKENS),
                                     "single_paragraph": True},
            "supported_patient_ids": list(catalogs),
            "supported_patients": [{"id": c.patient["id"], "display_name": c.patient["name"],
                                     "content_version": c.patient["content_version"]} for c in catalogs.values()],
        })

    @app.get("/v1/patients", response_model=PatientCatalogResponse, dependencies=[Depends(authorize)])
    async def patients():
        # No private facts, rubric content or original conversation is exposed here.
        return roster

    @app.post("/v1/dialogue/start", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def start(request: StartRequest, http: Request):
        dialogue = service(dialogues, request.patient_id)
        return await coordinator.execute("start", request, await http.json(), http, lambda: dialogue.start(request))

    @app.post("/v1/dialogue/open", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def open_session(request: OpenRequest, http: Request):
        dialogue = service(dialogues, request.state.patient_id)
        return await coordinator.execute("open", request, await http.json(), http, lambda: dialogue.open_session(request),
                                         needs_model=request.state.current.opening is None)

    @app.post("/v1/dialogue/revisit", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def revisit(request: RevisitRequest, http: Request):
        dialogue = service(dialogues, request.state.patient_id)
        replay = request.session_id == request.state.current.id and request.state.current.opening is not None
        return await coordinator.execute("revisit", request, await http.json(), http, lambda: dialogue.revisit(request), needs_model=not replay)

    @app.post("/v1/dialogue/turn", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def turn(request: TurnRequest, http: Request):
        dialogue = service(dialogues, request.state.patient_id)
        replay = any(turn.id == request.turn_id for session in request.state.sessions for turn in session.turns)
        return await coordinator.execute("turn", request, await http.json(), http, lambda: dialogue.turn(request), needs_model=not replay)

    @app.post("/v1/dialogue/evaluate", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def evaluate(request: EvaluateRequest, http: Request):
        evaluator = service(evaluators, request.state.patient_id)
        return await coordinator.execute("evaluate", request, await http.json(), http, lambda: evaluator.evaluate(request),
                                         needs_model=request.state.current.evaluation is None)

    @app.post("/v1/dialogue/close", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def close_session(request: CloseRequest, http: Request):
        dialogue = service(dialogues, request.state.patient_id)
        return await coordinator.execute("close", request, await http.json(), http, lambda: dialogue.close_session(request), needs_model=False)

    return app


app = create_app()
