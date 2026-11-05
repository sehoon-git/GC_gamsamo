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
    assert health["inference"]["ready"] and health["format_version"] == 4
    assert "seoyun" in health["supported_patient_ids"]
    body = json.dumps({"patient_id": "seoyun", "case_id": "probe_"+uuid4().hex,
                       "session_id": "session_"+uuid4().hex, "request_id": "opening_"+uuid4().hex}, ensure_ascii=False).encode()
    request = urllib.request.Request(url+"/v1/dialogue/start", data=body, headers={"Content-Type": "application/json"})
    try:
        urllib.request.urlopen(request, timeout=20)
        raise AssertionError("Unauthenticated request was accepted")
    except urllib.error.HTTPError as error:
        assert error.code == 401, f"Expected 401, got {error.code}"
    request.add_header("Authorization", "Bearer "+key)
    with urllib.request.urlopen(request, timeout=180) as response:
        result = json.load(response)
    session = result["state"]["sessions"][-1]
    assert session["phase"] == "active" and not session["turns"]
    assert session["opening"]["patient_text"]
    followup = {"state": result["state"], "session_id": session["id"], "turn_id": "turn_"+uuid4().hex,
                "text": "많이 긴장되셨겠어요. 지금 마음은 어떠세요? 어떤 이야기를 하고 싶으세요?"}
    request = urllib.request.Request(url+"/v1/dialogue/turn", data=json.dumps(followup, ensure_ascii=False).encode(),
        headers={"Content-Type": "application/json", "Authorization": "Bearer "+key})
    with urllib.request.urlopen(request, timeout=180) as response:
        session = json.load(response)["state"]["sessions"][-1]
    assert session["phase"] == "active" and len(session["turns"]) == 1 and session["turns"][0]["patient_text"]
    print("HTTPS·인증 거부·환자 첫 발화·서술형 복수 질문 응답 확인 완료. mode="+health["mode"])
    print("이 결과는 실행한 PC의 접속 검사입니다. Unity 완주·복원 검사는 별도로 진행합니다.")


if __name__ == "__main__":
    main()
