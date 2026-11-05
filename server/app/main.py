from contextlib import asynccontextmanager
import asyncio
import hmac
import os

# All inference and conversation state stay local; no hosted tracing is enabled.
os.environ["LANGSMITH_TRACING"] = "false"
os.environ["LANGCHAIN_TRACING_V2"] = "false"

from fastapi import Depends, FastAPI, Header
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from app.core.config import ExperimentError, Settings
from app.llm.client import VllmClient
from app.llm.mock import MockLLM
from app.schemas.models import CONTENT_VERSION, FORMAT_VERSION, CloseRequest, EvaluateRequest, OpenRequest, RevisitRequest, StartRequest, StateResponse, TurnRequest
from app.services.catalog import Catalog
from app.schemas.models import PATIENT_IDS, content_version
from app.services.dialogue import DialogueService
from app.services.evaluation import EvaluationService


def create_app(settings: Settings | None = None, llm=None):
    settings = settings or Settings.from_env()
    engine = llm or (MockLLM(settings) if settings.mock else VllmClient(settings))
    catalogs = {patient_id: Catalog(settings, patient_id) for patient_id in PATIENT_IDS}
    dialogues = {key: DialogueService(engine, catalog) for key, catalog in catalogs.items()}
    evaluators = {key: EvaluationService(engine, catalog) for key, catalog in catalogs.items()}
    catalog = catalogs["seoyun"]  # Preserve the legacy health fields for existing clients.

    def service(services, patient_id):
        if patient_id not in services:
            raise ExperimentError("이 환자의 AI 상담 콘텐츠는 아직 준비되지 않았습니다.", "unsupported_patient", 404)
        return services[patient_id]
    gate = asyncio.Lock()

    @asynccontextmanager
    async def lifespan(app):
        yield
        await engine.close()

    app = FastAPI(title="상담 게임 AI", version="0.4.1", lifespan=lifespan)

    @app.exception_handler(ExperimentError)
    async def experiment_error(request, exc):
        return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": str(exc)}})

    @app.exception_handler(RequestValidationError)
    async def validation_error(request, exc):
        state = exc.body.get("state", {}) if isinstance(exc.body, dict) else {}
        patient_id = state.get("patient_id", "seoyun") if isinstance(state, dict) else "seoyun"
        expected_content = content_version(patient_id) if isinstance(patient_id, str) else CONTENT_VERSION
        legacy = isinstance(state, dict) and (state.get("format_version", FORMAT_VERSION) != FORMAT_VERSION
                 or state.get("content_version", expected_content) != expected_content)
        return JSONResponse(status_code=422, content={"detail": {
            "code": "unsupported_save_version" if legacy else "invalid_request",
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
            "model_revision": engine.config["revision"], "prompt_version": catalog.prompt_version, "busy": gate.locked(),
            "format_version": FORMAT_VERSION, "mode": "mock" if settings.mock else "model",
            "supported_patient_ids": list(catalogs),
            "supported_patients": [{"id": c.patient["id"], "display_name": c.patient["name"],
                                     "content_version": c.patient["content_version"]} for c in catalogs.values()],
        })

    @app.post("/v1/dialogue/start", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def start(request: StartRequest):
        async with gate:
            return await service(dialogues, request.patient_id).start(request)

    @app.post("/v1/dialogue/open", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def open_session(request: OpenRequest):
        async with gate:
            return await service(dialogues, request.state.patient_id).open_session(request)

    @app.post("/v1/dialogue/revisit", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def revisit(request: RevisitRequest):
        async with gate:
            return await service(dialogues, request.state.patient_id).revisit(request)

    @app.post("/v1/dialogue/turn", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def turn(request: TurnRequest):
        async with gate:
            return await service(dialogues, request.state.patient_id).turn(request)

    @app.post("/v1/dialogue/evaluate", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def evaluate(request: EvaluateRequest):
        async with gate:
            return await service(evaluators, request.state.patient_id).evaluate(request)

    @app.post("/v1/dialogue/close", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def close_session(request: CloseRequest):
        async with gate:
            return await service(dialogues, request.state.patient_id).close_session(request)

    return app


app = create_app()
