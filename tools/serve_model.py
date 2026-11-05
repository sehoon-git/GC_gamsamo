"""Run the approved model in the foreground; Ctrl-C stops this service."""
import json
import os
import sys

from app.core.config import Settings


def main():
    settings = Settings.from_env()
    config = settings.model
    manifest = settings.model_path / ".seoyun-model.json"
    if not manifest.exists() or json.loads(manifest.read_text("utf-8"))["revision"] != config["revision"]:
        raise SystemExit("먼저 python tools/download_model.py를 실행하세요.")
    os.environ["VLLM_NO_USAGE_STATS"] = "1"
    os.environ["DO_NOT_TRACK"] = "1"
    os.environ["HF_HUB_OFFLINE"] = "1"
    arguments = [
        sys.executable, "-m", "vllm.entrypoints.cli.main", "serve", str(settings.model_path),
        "--served-model-name", config["served_model_name"],
        "--host", "127.0.0.1", "--port", "8000",
        "--max-model-len", str(config["max_model_len"]),
        "--max-num-seqs", str(config["max_num_seqs"]),
        "--gpu-memory-utilization", str(config["gpu_memory_utilization"]),
        "--language-model-only", "--reasoning-parser", config.get("reasoning_parser", "qwen3"),
        "--default-chat-template-kwargs", json.dumps({**config.get("chat_template_kwargs", {}), "enable_thinking": False}),
    ]
    if settings.vllm_api_key:
        # vLLM reads its own key from the environment, keeping it out of process arguments.
        os.environ["VLLM_API_KEY"] = settings.vllm_api_key
    os.execv(sys.executable, arguments)


if __name__ == "__main__":
    main()
