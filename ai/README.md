# 환자별 AI 베타 실행

서윤·도현·태준·연우·하린 5명의 환자 첫 발화·한국어 서술형 대화·사용자 종료·기억·자동 저장·두 축 평가 API를 제공한다. Unity 연결 계층과 [텍스트 입력 UI 연결 명세](../contracts/README.md#unity-text-input)는 같은 계약을 사용한다. 입력창·전송 버튼은 팀원이 별도로 구현한다. `/health`의 `supported_patient_ids`에 있는 환자만 실제 AI 상담 대상이며, 민석은 아직 AI 설정이 없는 로컬 시연이다.

## Conda 실행

설치는 [환경 안내](environment.md)를 따른다. 모든 설치·가중치·캐시는 홈 아래에 둔다. 저장소 루트에서:

```bash
conda activate game-ai
python tools/run_experiment.py --server-only
# 별도 터미널
python tools/chat.py --patient seoyun
# 선택 가능: seoyun, dohyeon, taejun, yeonwoo, harin
```

서버 전용 모드는 CLI가 종료되어도 유지된다. 서버 터미널의 Ctrl+C로 자신이 시작한 모델·API를 종료한다. 함께 실행하고 함께 종료하려면 `python tools/run_experiment.py`를 사용한다. 모델 포트 8000과 API 포트 8001은 localhost에만 바인딩한다.

GPU 없이 팀원 연결을 시작할 때는 `python tools/run_experiment.py --mock --server-only`. 모의 대사·분류·점수는 연결용 고정 데이터이며 `/health`에 `mode=mock`으로 표시한다.

## 모델 프로필

`SEOYUN_MODEL_CONFIG`는 다운로드·모델 실행·API에 **같은 값**을 적용한다. 기본값은 기존 `ai/configs/seoyun.json`이며 검증 없이 자동 교체하지 않는다.

```bash
export SEOYUN_MODEL_CONFIG=ai/configs/qwen38-27b-fp8.json
python tools/download_model.py
python tools/run_experiment.py --server-only
```

`qwen38-27b-fp8.json`·`exaone45-33b-awq.json`·예비 `gemma4-31b-qat.json`에 공식 배포본의 고정 리비전·16k 문맥·단일 상담·역할별 생성 설정을 명시했다. EXAONE은 행사 이용 조건을 확인한 뒤에만 사용한다. 프로필이 있다는 사실은 기동·품질 채택 완료를 뜻하지 않는다. 환자 대사는 thinking을 끄며 최종 평가만 제한된 thinking을 사용한다. 가중치·실행 로그는 커밋하지 않는다.

## 대화·저장·이어하기

환자가 먼저 이야기하고 플레이어는 공감·요약·질문을 자유로운 서술형으로 답한다. 여러 질문을 한 번에 보내도 정상적으로 대화를 이어간다. 질문 분류·차감·사용량 표시·횟수 제한은 없다. 게임의 상담 종료 버튼 또는 CLI의 `/finish`로 종료를 요청하면 **상담은 여기까지 하겠습니다. 오늘 수고하셨습니다!**를 상담자 안내로 저장하고 평가한다. 종료 안내에는 LLM을 호출하지 않는다.

`/v1/dialogue/start`는 서버가 새 상태와 첫 발화를 함께 만들고, `/revisit`는 평가가 끝난 미완료 환자의 기억을 이어 새 회차를 시작한다. `/open`은 저장된 빈 회차에 첫 발화를 만든다. 첫 발화 요청·응답도 저장 실패 시 재시도 대상으로 관리한다.

`active → closing → pending_evaluation → evaluated`를 저장한다. 종료 버튼을 누르기 전에는 질문 수에 관계없이 대화를 이어간다. 버튼을 누른 뒤 중단하면 저장된 종료 의도 또는 평가 단계부터 이어간다. 자동 저장은 원문·상태의 같은 revision 쌍을 작성하고 포인터를 마지막에 교체한다. 서윤의 기존 기본 위치는 `~/.local/share/gc-gamsamo/seoyun/v4/`이며 다른 환자는 동일 데이터 루트의 `patients/<patient_id>/v4/`로 분리한다. 데이터 루트는 프로젝트 밖의 `SEOYUN_DATA_DIR`로 바꿀 수 있다. 기존 형식 1·2·3 기록은 보존하고 `python tools/chat.py --patient <patient_id> --new`로 새 실험을 시작한다.

| 명령 | 동작 |
|---|---|
| `/retry` | 같은 ID·본문의 요청 재시도. 응답 수신 후 저장 실패이면 저장만 재시도 |
| `/finish` | 원하는 시점에 종료 안내 저장·평가 (상담자 발언이 없어도 가능) |
| `/evaluate` | 마무리 저장 이후 평가 재시도 |
| `/next` | 기준 미달 후 원문·기억을 유지한 새 회차 |
| `/new` | 선택한 환자의 이전 기록을 보존한 새 실험 |
| `/cases`, `/open 실험_ID` | 보관된 실험 확인·자동 저장 복원 |
| `/status`, `/help` | 상담 회차·단계·도움말 |
| `x`, `/quit` | 확정 상태를 보존하고 CLI 종료 |

LLM은 공개 후보를 반환하고 클라이언트가 확정한다. Python CLI와 Unity 시연 어댑터는 현재 응답의 출처·허용 ID를 검증한 후보만 수용한다. 실제 대화에서의 의미적 공개 조건은 별도 검토한다. 서버 DB는 없다. 고정 종료 안내는 평가·기억 근거에서 제외하며 원문에는 별도 보존한다. 기억 요약은 원문을 대체하지 않는다. 약 12k 토큰부터 출처를 가진 기억으로 압축하고 최근 6왕복의 원문을 보존한다. 환자의 첫 발화는 왕복 수에 포함하지 않으며 원문·기억·평가에서 환자 발언으로 다룬다. 오래된 기록을 압축해도 실제 문맥 한도를 넘으면 최근 원문을 버리지 않고 입력 길이 조정을 안내한다.

평가는 환자 누적 원문과 상담자 현재 회차를 각각 4항목×25점으로 분석한다. 두 축 모두 70점 이상이어야 클리어하며 근거 ID·정확한 원문 인용·발언자·회차를 검증한다.

## 외부 PC 연결

로컬 `server/.env`에 API 키를 설정하고 셸에 적용한다. 예제 파일은 자동으로 읽지 않는다. 이 파일은 Git에서 제외된다.

```bash
set -a
source server/.env
set +a
python tools/run_experiment.py --server-only
# 같은 환경을 적용한 별도 터미널
python tools/run_tunnel.py
```

`run_tunnel.py`는 Bearer 인증이 실제로 활성화된 API만 연결하며 기본적으로 홈의 `.local/bin/cloudflared`를 사용한다. 공식 [설치 안내](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/downloads/)에서 운영체제별 실행 파일과 무결성을 확인한다. `--cloudflared`로 위치를 지정할 수 있다.

접속 설정은 `ai/outputs/demo-connection.json`에 저장된다. 팀원에게 이 파일을 안전한 경로로 전달하고 저장소에는 올리지 않는다. 팀원 PC에서는 표준 Python만으로 `python tools/check_connection.py <설정파일>`을 실행해 HTTPS·인증·환자 첫 발화·복수 질문 응답을 검사할 수 있다. Unity는 이 파일의 `api_url`, `api_key`를 로컬 설정에 적용한다. 설정 위치와 공개 함수·이벤트는 [공용 계약](../contracts/README.md#unity-text-input)을 따른다. 연결 주소와 키를 씬이나 Inspector 직렬화 필드에 넣지 않는다.

평가는 일반 대화보다 오래 걸릴 수 있다. Unity의 요청 제한시간 안에서도 외부 프록시가 먼저 연결을 종료할 수 있으므로 오류 상태와 같은 요청 재시도를 제공한다. 실패를 0점으로 저장하지 않는다.

Quick Tunnel은 실행을 종료하거나 다시 생성하면 주소가 바뀐다. 시연 전에 주소를 전달하고 프로세스를 유지한다. 완성 JSON 응답을 사용하며 SSE는 사용하지 않는다. [공식 Quick Tunnel 제한](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/)

## 검증

```bash
python -m pytest server/tests -q
python tools/export_contracts.py --check
python ai/evaluation/terminal_smoke.py --label chosen
python ai/evaluation/long_context_smoke.py --label chosen
python ai/evaluation/compare_models.py --label chosen
python ai/evaluation/patient_smoke.py --label characters
```

Unity와 공유하는 C# 계약·저장 코드는 .NET 8 개발 환경에서 다음과 같이 검사한다. 두 번째 명령은 지정한 API에 합성 상담을 생성해 종료·평가·재상담까지 확인한다. 실제 설정 파일은 Git에 넣지 않는다.

```bash
dotnet run --project tools/unity-client-checks/UnityClientChecks.csproj -- .
dotnet run --project tools/unity-client-checks/UnityClientChecks.csproj -- . <로컬연결설정.json>
```

선택적으로 `dotnet build tools/unity-client-checks/AdapterCompileChecks.csproj`로 Unity 대체 타입을 사용하는 어댑터 구문·타입 검사를 할 수 있다. C# 검사는 Unity Editor의 정식 컴파일·실행이나 입력 UI 검사가 아니다. 팀원의 화면 연결·한글 IME·복원 검증은 [입력 UI 명세](../contracts/README.md#unity-text-input)와 PR 체크리스트를 따른다.

첫 두 검사는 모델 없이 규칙·장애·계약을 확인한다. 나머지는 실제 모델을 하나씩 실행하며 사용자 저장을 건드리지 않는다. `terminal_smoke`는 환자 첫 발화·공감과 복수 질문을 포함한 8턴 대화·명시적 종료·단계 복원·평가 재시도, `long_context_smoke`는 합성 긴 원문·요약·근거 검사를 수행한다. `compare_models`는 기존 서윤의 같은 20개 상황×2를 측정한다. `patient_smoke`는 추가 네 명의 실제 첫 발화·대화·복원·종료·평가·재상담을 확인한다. 결과는 Git 제외 영역에 보존한다.

[공용 계약과 예제](../contracts/README.md), [현재 진행·한계](progress.md), [통합 기획서](../docs/plan01.md)를 함께 확인한다. 자동 기능 검사는 한국어 자연스러움이나 팀원 데스크톱의 Unity 통합 검증을 대신하지 않는다.

모델별 비교 결과가 준비되면 다음 도구로 모델 이름을 가린 검토 화면과 평가표를 만든다. HTML과 mapping·평가 결과는 로컬 산출물이며 공개 PR에 넣지 않는다. mapping은 평가 완료 후에만 확인한다.

```bash
python ai/evaluation/blind_review.py ai/outputs/comparison-baseline.json ai/outputs/comparison-candidate.json --output ai/outputs/blind-review.html
python ai/evaluation/grade_review.py ai/outputs/blind-review.mapping.json <검토자가 저장한 JSON>
```

두 비교의 대화용 프롬프트(분류·대사·기억)와 상황 해시가 같아야 한다. 평가 프롬프트 보완은 대화 비교와 별도로 기록한다. 사람 점수 평균 4/5·선호 60%·위반 없음과 실제 기동·자유 대화 규칙·지연·완주 기준을 함께 확인한 뒤 모델을 채택한다.
