"""Expose only the authenticated API; keep connection details in a local file."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import time

import httpx

from app.client import atomic_json
from app.core.config import Settings
from app.schemas.models import CaseState


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cloudflared", type=Path, default=Path.home()/".local/bin/cloudflared")
    args = parser.parse_args()
    settings = Settings.from_env()
    if not settings.api_key:
        raise SystemExit("터널을 열기 전에 SEOYUN_API_KEY를 설정하고 API를 같은 키로 실행하세요.")
    local_url = "http://127.0.0.1:8001"
    with httpx.Client(base_url=local_url, timeout=5, trust_env=False) as client:
        health = client.get("/health")
        health.raise_for_status()
        state = CaseState()
        body = {"state": state.model_dump(), "session_id": state.current.id, "turn_id": "auth_probe", "text": "안녕하세요."}
        denied = client.post("/v1/dialogue/turn", json=body)
        if denied.status_code != 401:
            raise SystemExit("API의 Bearer 인증을 먼저 활성화하세요. 터널은 열지 않았습니다.")
    output = settings.root / "ai/outputs"
    output.mkdir(exist_ok=True)
    connection = output / "demo-connection.json"
    logfile = output / "cloudflared.log"
    with logfile.open("w", encoding="utf-8") as log:
        process = subprocess.Popen([str(args.cloudflared), "--no-autoupdate", "tunnel", "--url", local_url, "--protocol", "http2"], stdout=log, stderr=subprocess.STDOUT)
        try:
            started = time.monotonic()
            while time.monotonic()-started < 90 and process.poll() is None:
                match = re.search(r"https://[a-z0-9-]+\.trycloudflare\.com", logfile.read_text("utf-8"))
                if match:
                    atomic_json(connection, {"api_url": match[0], "api_key": settings.api_key, "mode": health.json()["mode"]})
                    connection.chmod(0o600)
                    print(f"접속 설정을 로컬 파일에 저장했습니다: {connection}", flush=True)
                    print("터널을 종료하면 주소가 만료됩니다. 키와 설정 파일은 저장소에 게시하지 마세요.", flush=True)
                    process.wait()
                    return process.returncode
                time.sleep(.5)
            raise RuntimeError(f"터널 주소를 받지 못했습니다. 로컬 로그를 확인하세요: {logfile}")
        except KeyboardInterrupt:
            return 0
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


if __name__ == "__main__":
    raise SystemExit(main())
