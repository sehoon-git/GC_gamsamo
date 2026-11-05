"""Read a local connection JSON; never print its URL or key.

Uses only Python's standard library, including on teammate desktops.
"""
import argparse
import json
from pathlib import Path
import urllib.error
import urllib.request
from uuid import uuid4


def main():
    parser = argparse.ArgumentParser(description="외부 PC에서 HTTPS·Bearer·AI 응답 확인")
    parser.add_argument("connection", type=Path, help="공유받은 로컬 demo-connection.json 경로")
    args = parser.parse_args()
    config = json.loads(args.connection.read_text("utf-8"))
    url, key = config["api_url"].rstrip("/"), config["api_key"]
    if not url.startswith("https://") or not key:
        raise SystemExit("HTTPS 주소와 접근 키가 필요합니다.")
    with urllib.request.urlopen(url+"/health", timeout=20) as response:
        health = json.load(response)
    assert health["inference"]["ready"] and health["format_version"] == 2
    state = {"format_version": 2, "content_version": "seoyun-2.0", "case_id": "probe_"+uuid4().hex,
             "sessions": [{"id": "session_"+uuid4().hex}]}
    body = json.dumps({"state": state, "session_id": state["sessions"][0]["id"], "turn_id": "hello_"+uuid4().hex,
                       "text": "안녕하세요. 와주셔서 감사합니다."}, ensure_ascii=False).encode()
    request = urllib.request.Request(url+"/v1/dialogue/turn", data=body, headers={"Content-Type": "application/json"})
    try:
        urllib.request.urlopen(request, timeout=20)
        raise AssertionError("Unauthenticated request was accepted")
    except urllib.error.HTTPError as error:
        assert error.code == 401, f"Expected 401, got {error.code}"
    request.add_header("Authorization", "Bearer "+key)
    with urllib.request.urlopen(request, timeout=180) as response:
        result = json.load(response)
    session = result["state"]["sessions"][-1]
    assert session["questions_used"] == 0 and session["phase"] == "active" and len(session["turns"]) == 1
    assert session["turns"][0]["patient_text"]
    print("HTTPS·인증 거부·인증 후 인사 응답 확인 완료. mode="+health["mode"])
    print("이 결과는 실행한 PC의 접속 검사입니다. Unity 완주·복원 검사는 별도로 진행합니다.")


if __name__ == "__main__":
    main()
