# 서윤 AI API · 형식 2

팀장은 PR을 `main`에 병합하고 팀원들은 그 `main`에서 각자 데스크톱으로 연결·복원·재시도를 검증한다. AI는 대사·공개 후보·평가를 반환하고 **Unity가 공개 확정·진행·자동 저장·보상**을 소유한다. 서버 DB는 없으며 모든 요청에 확정된 전체 상태를 보낸다.

## 빠른 연결

Conda 환경에서 저장소 루트 기준:

```bash
conda activate game-ai
python tools/run_experiment.py --mock --server-only
```

GPU·가중치 없이 같은 API 형식으로 연결할 수 있다. 모의 분류는 예제용 문자열 규칙이며 모의 대사·0점 평가를 실제 AI 품질로 해석하지 않는다. `/health`의 `mode`가 `mock`인지 `model`인지 화면 또는 개발 로그에 표시한다. 실제 서버는 `--mock`을 빼고 실행한다. 별도 터미널의 `python tools/chat.py` 종료는 서버 전용 프로세스를 종료하지 않는다.

| API | 요청 | 응답 |
|---|---|---|
| `GET /health` | 없음 | 준비 상태, 모드, 콘텐츠/모델/프롬프트 버전. 준비 전 503 |
| `POST /v1/dialogue/turn` | `state`, `session_id`, `turn_id`, `text` | `StateResponse` |
| `POST /v1/dialogue/close` | `state`, `session_id`, `request_id` | `StateResponse` |
| `POST /v1/dialogue/evaluate` | 위와 동일 | `StateResponse` |

POST에는 서버에서 전달받은 `Authorization: Bearer <키>`를 넣는다. 로컬 모의 실행에서 키가 비어 있으면 인증을 생략한다. 외부 접속은 Bearer 인증을 설정한 API만 HTTPS 터널로 노출한다. 모델 포트를 직접 연결하지 않는다. 키·터널 주소는 로컬 설정으로 전달하며 저장 파일이나 저장소에 넣지 않는다.

## 상태와 저장 순서

`format_version=2`, `content_version=seoyun-2.0`. [초기 상태 예제](initial-state.example.json)의 실험/상담 ID와 시각은 새 실험마다 새로 생성한다. `sessions`의 마지막 항목이 현재 회차다.

1. `active`: 인사·질문 없는 공감은 `question_delta=0`. 후속·반복·간접 질문도 하나면 `1`. 사용량은 `questions_used`로 표시한다. 복합 질문은 `needs_split`이며 입력 분리를 안내하고 원문·사용량·revision을 변경하지 않는다.
2. 응답의 실험·회차·요청 ID와 `base_revision`을 확인한다. `disclosure_candidates` 중 클라이언트가 확정할 항목만 `revealed_fact_ids`에 합친다. 후보는 마지막 턴의 `candidate_fact_ids`에도 기록된다. 서버는 확정 ID를 추가하지 않는다.
3. 상태와 원문을 같은 revision의 새 스냅샷에 쓴다. 둘 다 완료한 뒤 현재 스냅샷 포인터를 원자적으로 교체한다. 저장 성공 후 UI 상태를 확정한다. 기존 원문 스냅샷은 보존한다.
4. 다섯 번째 답변의 `phase=closing`을 저장하면 입력을 차단하고 `/close`를 호출한다. 이 상태에는 아직 마무리 원문이 없다. `/close` 응답의 마지막 턴 `closing_text`를 표시하고 `pending_evaluation` 상태를 저장한다.
5. 마무리 저장이 끝나면 `/evaluate`를 호출한다. 평가 결과와 `phase=evaluated`를 저장한다. 두 축 각각 70점 이상이어야 클리어한다.
6. 재실행은 저장된 단계부터 이어간다. `closing`이면 마무리, `pending_evaluation`이면 평가만 재시도한다. 평가 미달 재상담은 이전 원문·기억·확정 공개를 유지하고 새 회차 ID, 빈 턴, 질문 0개, `active`를 추가하며 revision을 1 증가시킨다. 새 실험은 별도 case ID로 완전히 분리한다.

`SaveStore`와 `SessionClient`는 Python 참조 구현이다. 자동 저장만 제공한다. 형식 1 기록은 보존하며 주제 수를 질문 수로 자동 환산하지 않는다. 이전 기록 발견 시 CLI는 `--new`를 안내한다.

## 재시도와 중복 처리

정상 응답은 revision을 1 증가시킨다. 이미 확정된 요청의 재전달은 `replayed=true`이며 현재 revision을 유지한다. 응답 대기 중 새 요청을 막는다. 불러오기·새 실험·상태 변경 전에 보낸 늦은 응답과 이미 반영한 응답은 확정하지 않는다.

네트워크 오류는 **동일 ID·동일 요청 본문**으로 재시도한다. 마무리·평가 ID는 저장된 실험·회차·revision·단계에서 결정하므로 프로그램 재실행 후에도 유지한다. 응답을 받았는데 저장에 실패했다면 응답과 공개 확정 선택을 메모리에 보관하고 **저장만 재시도**한다. 저장이 끝나기 전 마무리·평가 요청을 보내지 않는다. 프로세스가 종료되어 미저장 응답을 잃은 경우 마지막 확정 스냅샷부터 재개한다. 서버는 무상태이므로 과거 스냅샷 요청의 생성 결과까지 동일하게 보장하지 않는다.

API 요청 제한시간은 추론과 검증 재호출을 포함해 충분히 둔다(참조 클라이언트 180초). 약 10초는 일반 응답의 목표이며 타임아웃 보장은 아니다. 로딩·기억 요약·마무리·평가는 별도로 처리 상태를 표시한다.

오류 형식은 항상 `{"detail":{"code":"...","message":"..."}}`이다.

| 코드 | HTTP | 처리 |
|---|---|---|
| `needs_split` | 422 | 질문을 나누어 새 요청 ID로 보냄; 기존 상태 유지 |
| `unsupported_save_version` | 422 | 이전 기록 보존, 새 형식의 실험 생성 |
| `invalid_request` | 422 | 형식과 단계·사용량 일관성 확인 |
| `unauthorized` | 401 | 로컬 설정의 Bearer 키 확인 |
| `stale_session`, `duplicate_turn` | 409 | 현재 실험·회차 및 요청 ID 확인 |
| `session_closed` | 409 | 추가 입력 차단, 저장된 단계 이어가기 |
| `session_open` | 409 | 다섯 번째 답변·마무리 저장 순서 확인 |
| `model_unavailable`, `invalid_model_output` | 503/502 | 기존 확정 상태 유지, 같은 요청 재시도 |

## 예제와 평가 근거

[OpenAPI](dialogue.openapi.json)와 `*.example.json`은 `python tools/export_contracts.py`로 실제 모의 API에서 생성한다. 모두 작성된 가상 연결 예제이며 실험 원문이 아니다. `python tools/export_contracts.py --check`로 코드와 계약 일치를 검사한다.

| 예제 접두사 | 상황 |
|---|---|
| `initial-state` | 초기 상태 |
| `greeting`, `turn`, `question-2`~`question-4` | 인사 0회·일반/후속 질문 1회 |
| `compound` | 복합 질문 422 |
| `fifth`, `sixth` | 다섯 번째 답변과 추가 입력 차단 |
| `close`, `evaluate` | 마무리와 평가 저장 단계 |
| `retry`, `evaluate-retry` | 확정된 요청 재전달 |
| `stale-session`, `unauthorized`, `legacy` | 회차·인증·버전 오류 |

평가는 각 축 4항목×25점이다. 환자 변화는 누적 원문, 상담자 대응은 현재 회차를 사용한다. 근거 ID는 `턴_ID:user` 또는 `턴_ID:patient`이며 인용은 실제 원문의 연속 구절이어야 한다. 마지막 환자 기록에는 마무리 대사가 덧붙는다. 원래 답변은 `patient_text`에 보존한다. 기억 요약은 평가 원문을 대체하지 않는다. 보상·자격증·게임 완료 상태는 Unity에서 결정한다.
