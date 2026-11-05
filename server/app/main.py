from contextlib import asynccontextmanager
import asyncio
import hmac
import os

# All inference and conversation state stay local; no hosted tracing is enabled.
os.environ["LANGSMITH_TRACING"] = "false"
os.environ["LANGCHAIN_TRACING_V2"] = "false"

from fastapi import Depends, FastAPI, Header
from fastapi.responses import JSONResponse

from app.core.config import ExperimentError, Settings
from app.llm.client import VllmClient
from app.schemas.models import EvaluateRequest, StateResponse, TurnRequest
from app.services.catalog import Catalog
from app.services.dialogue import DialogueService
from app.services.evaluation import EvaluationService


def create_app(settings: Settings | None = None, llm=None):
    settings = settings or Settings.from_env()
    engine = llm or VllmClient(settings)
    catalog = Catalog(settings)
    dialogue = DialogueService(engine, catalog)
    evaluator = EvaluationService(engine, catalog)
    gate = asyncio.Lock()

    @asynccontextmanager
    async def lifespan(app):
        yield
        await engine.close()

    app = FastAPI(title="서윤 상담 실험", version="0.1.0", lifespan=lifespan)

    @app.exception_handler(ExperimentError)
    async def experiment_error(request, exc):
        return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": str(exc)}})

    async def authorize(authorization: str = Header(default="")):
        if settings.api_key and not hmac.compare_digest(authorization, "Bearer " + settings.api_key):
            raise ExperimentError("접근 키를 확인하세요.", "unauthorized", 401)

    @app.get("/health")
    async def health():
        status = await engine.health()
        return JSONResponse(status_code=200 if status["ready"] else 503, content={
            "api": "ok", "inference": status, "content_version": catalog.patient["content_version"],
            "model_revision": engine.config["revision"], "prompt_version": catalog.prompt_version, "busy": gate.locked(),
        })

    @app.post("/v1/dialogue/turn", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def turn(request: TurnRequest):
        async with gate:
            return await dialogue.turn(request)

    @app.post("/v1/dialogue/evaluate", response_model=StateResponse, dependencies=[Depends(authorize)])
    async def evaluate(request: EvaluateRequest):
        async with gate:
            return await evaluator.evaluate(request)

    return app


app = create_app()
