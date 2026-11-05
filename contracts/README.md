# 상담 게임 AI API · 형식 4

환자가 먼저 이야기하고, 플레이어가 서술형으로 답하며, **플레이어가 `상담 마치기`를 선택할 때** 회차를 종료한다. 공감·제안·여러 질문을 한 발언에 섞을 수 있고 질문 횟수와 왕복 횟수에 상한이 없다. AI는 환자 발화·공개 후보·평가를 반환하며 **Unity가 공개 확정·자동 저장·게임 진행·보상**을 소유한다.

서버는 상담 상태를 보관하는 DB가 없다. 새 상담을 만드는 `/start`를 제외한 요청에는 클라이언트가 마지막으로 저장한 전체 상태를 보낸다. 계약은 게임 기획 [plan01](../docs/plan01.md)의 서술형 대화와 사용자 종료 기준을 따른다.

## 연결과 지원 환자

Conda 환경에서 저장소 루트 기준:

```bash
conda activate game-ai
python tools/run_experiment.py --mock --server-only
```

GPU·가중치 없이 같은 API 형식으로 연결할 수 있다. 모의 서버는 고정 대사·규칙 기반 공개 후보·0점 평가를 반환하는 연결 검사 도구다. 모의 결과를 실제 AI 품질이나 환자 클리어로 해석하지 않는다. 실제 모델 서버는 `--mock`을 빼고 실행한다. 별도 터미널의 `python tools/chat.py`를 종료해도 서버 전용 프로세스는 유지된다.

`GET /health`에서 `inference.ready`, `mode`, `format_version`, `content_version`, `supported_patient_ids`를 먼저 확인한다. 준비 전 HTTP 503을 반환한다. 화면에 모의 대화인지 실제 AI 대화인지 표시하고, 버전이나 환자 지원 여부가 다르면 해당 상담의 AI 요청을 시작하지 않는다. `busy`는 서버의 직렬 처리 상태다.

현재 지원 콘텐츠는 **서윤(`seoyun`) 한 명**이다. `supported_patient_ids`는 `["seoyun"]`, `supported_patients`는 `[{"id":"seoyun","display_name":"서윤"}]`이다. 게임의 도현·민석·태준·지은·하린은 이름·초상·임시 대사만 있으며 각색 사연·공개 조건·평가기준은 아직 제공되지 않았다. 이 다섯 명을 서윤 ID로 보내거나 서윤의 기억·평가를 재사용하지 않는다. 게임에서는 로컬 시연임을 표시하고 실제 AI 결과와 구분한다.

POST 요청은 서버에서 전달받은 `Authorization: Bearer <키>`를 사용한다. 로컬 모의 실행에서 키가 비어 있으면 인증을 생략한다. 외부 접속은 인증이 설정된 API만 HTTPS로 노출하며 모델 포트를 직접 연결하지 않는다. 키·개인 서버 주소는 로컬 설정으로 전달하고 원문·공유 저장 파일·저장소에 넣지 않는다. 서버와 CLI는 환경변수를 사용하며 `.env` 파일을 자동으로 읽지 않는다.

| API | 요청 | 용도 |
|---|---|---|
| `GET /health` | 없음 | 준비 상태·모의/모델 모드·형식/콘텐츠/모델/프롬프트 버전·지원 환자 확인 |
| `POST /v1/dialogue/start` | `patient_id`, `case_id`, `session_id`, `request_id`, `created_at` | 새 상담 상태와 첫 환자 발화를 함께 생성 |
| `POST /v1/dialogue/open` | `state`, `session_id`, `request_id` | 첫 발화가 없는 기존 빈 회차의 환자 선발화 생성 |
| `POST /v1/dialogue/revisit` | `state`, **새** `session_id`, `request_id`, `created_at` | 평가 미달 상담의 기억·원문을 유지하며 새 회차와 첫 환자 발화 생성 |
| `POST /v1/dialogue/turn` | `state`, `session_id`, `turn_id`, `text` | 플레이어의 서술형 발언에 대한 환자 응답 |
| `POST /v1/dialogue/close` | `state`, `session_id`, `request_id` | 고정 상담자 종료 안내·종료 시각 기록 |
| `POST /v1/dialogue/evaluate` | `state`, `session_id`, `request_id` | 종료 기록을 평가하고 결과 반환 |

모든 POST 성공 응답은 `StateResponse`다. `case_id`, `session_id`, `request_id`, `base_revision`, `state`, `replayed`, `disclosure_candidates`를 반환한다. `/revisit`만 요청의 `session_id`가 이전 상태의 현재 회차와 다르며 성공 응답은 새 회차 ID를 반환한다.

## 첫 발화와 자유 대화

`format_version=4`, `content_version=seoyun-4.0`. `sessions`의 마지막 항목이 현재 회차다. `case_id`는 한 환자의 누적 상담 실험, `session_id`는 그 안의 한 회차, `request_id`/`turn_id`는 한 요청을 식별한다. 최초 요청 전에 ID와 생성 시각을 확정하며 재시도할 때 바꾸지 않는다.

1. 환자가 자리에 도착하면 `/start`를 요청한다. 응답의 `base_revision=0`, `state.revision=1`이며 현재 회차의 `opening`에 첫 환자 발화가 들어 있다. 클라이언트에서 [초기 빈 상태](initial-state.example.json)를 먼저 저장한 경우 `/open`을 사용해도 된다.
2. `opening`은 `id`, `patient_text`, `candidate_fact_ids`, `created_at`, `metrics`를 갖는다. 가짜 상담자 인사나 빈 `user_text`를 만들지 않는다. 원문에는 `opening.id + ":patient"`인 환자 기록으로 한 번 저장하며 기억·평가에도 포함한다.
3. 첫 발화를 저장한 뒤 플레이어 입력을 받는다. `/turn`의 `text`는 질문뿐 아니라 공감·경청·해석·제안·복수 질문이 섞인 서술형 발언이다. 서버의 분류는 주제와 정보 공개 조건 확인에만 사용하며 질문 수 때문에 거절하지 않는다. 형식 4에는 `questions_used`, `question_delta`, `needs_split`이 없다.
4. 응답의 실험·회차·요청 ID와 `base_revision`, 기존 원문 보존 여부를 검사한다. `disclosure_candidates`는 이번 첫 발화 또는 턴의 `candidate_fact_ids` 안의 값이어야 한다. 클라이언트가 확정한 후보만 `revealed_fact_ids`에 합친다. 서버는 확정 목록을 추가하지 않는다.
5. 상태와 원문을 같은 revision의 새 스냅샷에 저장한 뒤 현재 스냅샷 포인터를 원자적으로 교체한다. 저장 성공 후 화면에 발화를 확정하고 다음 입력을 허용한다. 긴 대사 페이지·타이핑 표시와 전체 원문 보존은 분리한다.

첫 발화 전 `/turn`은 `opening_required`로 거절한다. 이미 저장된 첫 발화를 같은 ID로 `/open`에 다시 요청하면 기존 결과를 반환한다. 다른 ID로 첫 발화를 덮어쓰는 요청은 거절한다. 채팅에 종료 의사나 종료 안내 문구를 입력해도 버튼 요청으로 해석하지 않는다.

## 사용자 종료·평가·재상담

1. `상담 마치기` 확인을 받으면 새 입력을 차단한다. 진행 중인 응답·저장이 있으면 종료 의사를 보관하고 **그 요청의 처리와 저장을 먼저 완료**한다. 종료 취소는 종료 의도를 확정·저장하기 전에 처리한다.
2. 종료 의도를 먼저 자동 저장한 뒤 `/close`를 호출한다. Python 참조 구현 `request_closing()`과 `SessionClient.finish()`는 `phase=closing`, `revision + 1`로 저장한다. Unity 어댑터는 대기 중 요청과 함께 로컬 저장 묶음의 `endRequested=true`를 저장하고 서버 상태는 `active`인 채 `/close`를 호출한다. 두 경로 모두 복원 시 새 입력을 차단하고 종료 처리를 이어간다. `finish()`는 진행 중 요청을 자체적으로 대기열에 넣지 않으므로 UI 어댑터가 종료 의사를 보관해야 한다.
3. `/close`는 LLM 호출 없이 **상담은 여기까지 하겠습니다. 오늘 수고하셨습니다!**를 `closing_text`에 기록한다. `closing_speaker=counselor`, `phase=pending_evaluation`이다. 원문에는 `종료요청_ID:closing`, `kind=closing`, `speaker=counselor`로 저장한다. 마지막 환자 발화를 바꾸지 않는다.
4. 종료 안내 저장이 끝나면 상담자/안내 영역에 표시하고 `/evaluate`를 호출한다. 결과와 `phase=evaluated`를 저장한 뒤 결과 화면을 보여준다. 질문이나 답변이 없더라도 종료할 수 있다.
5. 두 평가축 각각 70점 이상인 경우만 클리어한다. 방문 퇴장·여섯 명의 시연 종료·서버 오류를 클리어로 계산하지 않는다. Unity가 결과를 검증하고 게임의 완료 인원·보상 정책을 적용한다.
6. 미달 재상담은 `/revisit`을 호출한다. 이전 원문·기억·확정 공개는 유지하고 새 회차 ID와 첫 환자 발화를 한 응답으로 추가한다. 성공 시 revision은 1 증가한다. CLI의 `next_session()` → 빈 회차 저장 → `/open` 방식도 제공하며 이 경우 각 저장 단계마다 revision이 증가한다. 이미 클리어한 실험은 재상담을 추가하지 않고 별도 case ID로 새 실험을 시작한다.

`/close` 자체는 `active` 또는 `closing`에서 사용할 수 있지만 중단 복원을 위해 종료 의도를 먼저 저장하는 흐름을 사용한다. `/evaluate`는 `pending_evaluation` 또는 `evaluated`에서만 허용한다. 종료 기록 저장 실패 시 평가를 호출하지 않으며 평가 실패를 0점으로 확정하지 않는다.

평가는 각 축 4항목 × 25점이다. 환자 변화는 누적 원문, 상담자 대응은 현재 회차의 원문을 사용한다. 근거 ID는 `opening_ID:patient`, `턴_ID:user`, `턴_ID:patient`이며 인용은 해당 원문의 연속 구절이어야 한다. 양수 점수에는 실제 지지 근거가 필요하다. 고정 종료 안내는 원문에는 보존하지만 기억 요약·평가 근거에서는 제외한다. 첫 발화만 있는 상담은 상담자 근거가 없으므로 상담자 점수가 0점이다. 누적 대화 원문 자체가 없으면 LLM 호출 없이 두 축 0점·미클리어와 근거 부족 이유를 반환한다. 기억 요약이 평가 원문을 대체하지 않는다.

## 자동 저장·복원·재시도

`SaveStore`와 `SessionClient`는 Python 참조 구현이다. 자동 저장만 제공하며 새 루트는 `v4/`다. 형식 1·2·3 기록은 보존하고 자동 변환하지 않는다. 과거 기록에 없었던 환자 첫 발화를 사후에 꾸며 넣거나 예전 자동 종료를 새 사용자 종료로 재해석하지 않는다. 이전 기록 발견 시 CLI는 `--new`를 안내한다.

게임을 닫는 것은 상담 종료가 아니다. 복원 시 `active`는 마지막 저장 대화부터 이어가고 첫 발화가 없으면 `/open`을 호출한다. `closing`은 종료 처리, `pending_evaluation`은 평가만 재시도하며 `evaluated`는 저장된 결과를 재사용한다. 완료 인원·보상은 회차 ID를 기준으로 한 번만 반영한다.

정상 요청은 revision을 1 증가시키고 이미 반영된 요청의 재전달은 `replayed=true`로 현재 revision을 유지한다. 응답 대기 중 중복 전송을 막는다. 불러오기·새 실험·방문 변경 전에 보낸 늦은 응답은 실험 ID·API 회차 ID·Unity 방문 ID·revision으로 거절한다. API 회차 ID와 Unity 방문 ID는 서로 다른 값이다.

네트워크 오류는 **동일 ID·동일 요청 본문**으로 재시도한다. 응답을 받았는데 저장에 실패했다면 응답과 공개 확정 선택을 보관하고 **저장만 재시도**한다. 종료 의도 저장 실패도 새 발언을 받지 않고 저장부터 복구한다. 서버는 무상태이므로 과거 스냅샷과 `/start`의 재전송이 같은 LLM 문장을 다시 생성한다고 보장하지 않는다. 클라이언트는 한 응답만 확정하며 프로세스가 종료되어 미저장 응답을 잃으면 마지막 확정 스냅샷부터 재개한다.

참조 클라이언트의 API 제한시간은 추론·검증 재호출을 포함해 180초다. 일반 응답 약 10초는 목표이며 타임아웃 보장은 아니다. 준비 확인·환자 응답·저장·기억 요약·종료·평가·오류 재시도 상태를 구분해 표시한다. 입력 예산 초과 시 초안을 보존하고 길이 조정을 안내하며 상담을 자동 종료하지 않는다.

오류 응답은 `{"detail":{"code":"...","message":"..."}}`다.

| 코드 | HTTP | 처리 |
|---|---|---|
| `unsupported_patient` | 404 | 지원 환자 목록 확인; 다른 환자의 상태로 대체하지 않음 |
| `unsupported_save_version` | 422 | 이전 기록 보존 후 새 형식으로 시작 |
| `invalid_request` | 422 | 요청 형식·원문·단계 일관성 확인 |
| `context_overflow` | 422 | 확정 원문과 초안을 유지하고 입력 길이 조정 |
| `unauthorized` | 401 | 로컬 연결 설정의 Bearer 키 확인 |
| `stale_session`, `duplicate_turn`, `opening_exists` | 409 | 현재 회차·요청 ID와 저장 상태 확인 |
| `opening_required` | 409 | 환자 첫 발화 생성·저장을 먼저 완료 |
| `session_closed`, `case_cleared` | 409 | 추가 입력 차단; 저장된 종료·평가 또는 새 실험 흐름 사용 |
| `session_open` | 409 | 종료 안내 저장·평가를 먼저 완료 |
| `model_unavailable`, `invalid_model_output` | 503/502 | 기존 확정 상태 유지, 같은 요청 재시도 |

## 생성 계약과 게임 연결

[OpenAPI](dialogue.openapi.json)와 `*.example.json`은 `python tools/export_contracts.py`로 실제 모의 API에서 생성한다. 예제는 작성된 가상 연결 데이터이며 실제 상담 원문이 아니다. `python tools/export_contracts.py --check`로 코드와 생성 계약이 일치하는지 검사한다.

| 예제 접두사 | 확인할 동작 |
|---|---|
| `health`, `start`, `open-retry` | 지원 환자·버전, 환자 선발화, 저장된 첫 발화 재사용 |
| `unsupported-patient`, `opening-required` | 미지원 인물과 첫 발화 없는 답변 거절 |
| `greeting`, `compound`, `turn`, `question-2`~`question-4` | 공감·복수 질문·일반 답변을 모두 정상 수락 |
| `fifth`, `sixth` | 이전 질문 수와 무관하게 대화 계속 |
| `closing-intent`, `closed-turn` | 종료 의도 저장과 종료 후 새 입력 차단 |
| `early-close`, `opening-only-close`, `opening-only-evaluate` | 짧은 상담·첫 환자 발화만 있는 상담의 사용자 종료와 평가 |
| `empty-close`, `empty-evaluate` | 첫 발화조차 없는 빈 상태의 종료 및 근거 없는 0점 평가 |
| `close`, `evaluate`, `revisit` | 종료 안내 저장·평가·누적 기억을 잇는 새 회차 |
| `retry`, `close-retry`, `evaluate-retry` | 확정된 요청 재전달 |
| `stale-session`, `unauthorized`, `legacy` | 회차·인증·저장 형식 오류 |

Unity는 `PatientVisitController.PatientArrived(visitId, patientId)`에서 지원 환자와 저장 상태를 확인한다. API 상담에서는 `useDemoDialogue=false`로 두고 확정 응답을 `PresentPatientLine(visitId, text)`에 전달한다. `PatientDialogueView`의 서술형 입력·전송과 처리 상태를 API 어댑터에 연결하고 종료 확인 시 즉시 퇴장시키지 않는다. 저장된 결과를 보여준 뒤 `CompleteConsultation(visitId)`로 해당 방문을 마친다. 이 함수는 그 자체로 클리어·보상·게임 완료를 부여하지 않는다.

진료 기록은 공개가 확정된 정보와 실제 원문만 반영하며 미제공 나이·성별·진단·약물·새 사건을 추측해서 채우지 않는다. 환자별 임상 콘텐츠, 단계별 해금, 승급, 코인 정산, 치장 구매, 최종 엔딩은 각 게임 시스템의 별도 구현 범위다. AI API가 이를 대신 완료한 것으로 취급하지 않는다.
