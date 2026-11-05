"""Start owned foreground services and the CLI; stop both on exit."""
from pathlib import Path
import os
import signal
import socket
import subprocess
import sys
import time

import httpx

ROOT = Path(__file__).resolve().parents[1]


def wait_ready(process, url, logfile):
    started = time.monotonic()
    announced = 0
    with httpx.Client(timeout=2, trust_env=False) as client:
        while time.monotonic() - started < 600:
            if process.poll() is not None:
                raise RuntimeError(f"서버가 종료되었습니다. 로그: {logfile}\n" + "\n".join(logfile.read_text(errors="replace").splitlines()[-16:]))
            try:
                if client.get(url).status_code == 200:
                    return
            except httpx.HTTPError:
                pass
            elapsed = int(time.monotonic() - started)
            if elapsed >= announced:
                print(f"서버 준비 중… {elapsed}초 (로그: {logfile})", flush=True)
                announced += 15
            time.sleep(0.5)
    raise RuntimeError(f"준비 시간이 10분을 넘었습니다. 로그: {logfile}")


def stop(process):
    if process.poll() is not None:
        return
    # Each service owns a new process group, including vLLM's GPU worker children.
    os.killpg(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=20)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait()


def main():
    for port in [8000, 8001]:
        with socket.socket() as connection:
            if connection.connect_ex(("127.0.0.1", port)) == 0:
                raise SystemExit(f"{port} 포트가 사용 중입니다. 기존 두 서버가 준비되어 있다면 python tools/chat.py로 접속하세요.")
    output = ROOT / "ai/outputs"
    output.mkdir(exist_ok=True)
    processes, handles = [], []
    try:
        for script, port, name in [("serve_model.py", 8000, "vllm"), ("serve_api.py", 8001, "api")]:
            log = output / f"{name}.log"
            handle = log.open("w", encoding="utf-8")
            handles.append(handle)
            process = subprocess.Popen([sys.executable, str(ROOT / "tools" / script)], cwd=ROOT,
                                       stdout=handle, stderr=subprocess.STDOUT, start_new_session=True)
            processes.append(process)
            wait_ready(process, f"http://127.0.0.1:{port}/health", log)
        return subprocess.call([sys.executable, str(ROOT / "tools/chat.py")], cwd=ROOT)
    except (RuntimeError, KeyboardInterrupt) as exc:
        print(str(exc))
        return 1
    finally:
        for process in reversed(processes):
            stop(process)
        for handle in handles:
            handle.close()


if __name__ == "__main__":
    raise SystemExit(main())
