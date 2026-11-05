# 상담 게임 AI API · 형식 4

환자가 먼저 이야기하고, 플레이어가 서술형으로 답하며, **플레이어가 `상담 그만하기`를 선택할 때** 회차를 종료한다. 공감·제안·여러 질문을 한 발언에 섞을 수 있고 질문 횟수와 왕복 횟수에 상한이 없다. AI는 환자 발화·공개 후보·평가를 반환하며 **Unity가 공개 확정·자동 저장·게임 진행·보상**을 소유한다.

서버는 상담 상태를 보관하는 DB가 없다. 새 상담을 만드는 `/start`를 제외한 요청에는 클라이언트가 마지막으로 저장한 전체 상태를 보낸다. 계약은 게임 기획 [plan01](../docs/plan01.md)의 서술형 대화와 사용자 종료 기준을 따른다.

## 연결과 지원 환자

Conda 환경에서 저장소 루트 기준:

```bash
conda activate game-ai
python tools/run_experiment.py --mock --server-only
```

GPU·가중치 없이 같은 API 형식으로 연결할 수 있다. 모의 서버는 고정 대사·규칙 기반 공개 후보·0점 평가를 반환하는 연결 검사 도구다. 모의 결과를 실제 AI 품질이나 환자 클리어로 해석하지 않는다. 실제 모델 서버는 `--mock`을 빼고 실행한다. 별도 터미널의 `python tools/chat.py`를 종료해도 서버 전용 프로세스는 유지된다.

`GET /health`에서 `inference.ready`, `mode`, `format_version`, `content_version`, `supported_patient_ids`를 먼저 확인한다. 준비 전 HTTP 503을 반환한다. 화면에 모의 대화인지 실제 AI 대화인지 표시하고, 버전이나 환자 지원 여부가 다르면 해당 상담의 AI 요청을 시작하지 않는다. `busy`는 서버의 직렬 처리 상태다.

최종 지원 환자는 **서윤(`seoyun`), 도현(`dohyeon`), 태준(`taejun`), 연우(`yeonwoo`), 하린(`harin`) 5명**이다. `supported_patients`의 각 항목은 `id`, `display_name`, `content_version`을 포함한다. `GET /v1/patients`는 같은 Bearer 인증으로 정식 ID·이름·이전 ID·콘텐츠 준비 여부를 제공한다. 단일 목록은 `Assets/StreamingAssets/GameData/patient-catalog.json`이다. 민석은 최종 대상에서 제외하며 `/start`는 404 `unsupported_patient`다. B는 별도로 화면·방문 목록에서 민석을 제거해야 한다. `jieun`은 목록의 `aliases`에서 `yeonwoo`로 변환한 뒤 요청하며, AI 상태와 저장에는 정식 ID만 사용한다.

| patient_id | content_version | 이야기 |
|---|---|---|
| `seoyun` | `seoyun-4.0` | 취업 실패와 자기 가치 |
| `dohyeon` | `dohyeon-4.0` | 가상 두 회사 조건·임금 비교와 지역 생활 |
| `taejun` | `taejun-4.0` | 가상 지역·정당의 기대와 개인의 책임 |
| `yeonwoo` | `yeonwoo-4.0` | 합의 이혼 후 아이의 성장과 놓친 순간 |
| `harin` | `harin-4.0` | 다문화가정에서의 애정·부담·소속감 |

상태의 `patient_id`와 `content_version`은 한 실험 내에서 고정한다. 캐릭터를 바꾸면서 기존 실험의 ID·원문·기억·공개 정보·평가를 재사용하지 않는다. `/health.content_version`은 기존 서윤 클라이언트 호환을 위해 `seoyun-4.0`을 유지하므로 **다른 환자는 반드시 supported_patients의 해당 항목에서 콘텐츠 버전을 확인**한다. 같은 모델 프로세스를 공유하되 환자별 프롬프트·저장은 분리된다.

POST 요청은 서버에서 전달받은 `Authorization: Bearer <키>`를 사용한다. 로컬 모의 실행에서 키가 비어 있으면 인증을 생략한다. 외부 접속은 인증이 설정된 API만 HTTPS로 노출하며 모델 포트를 직접 연결하지 않는다. 키·개인 서버 주소는 로컬 설정으로 전달하고 원문·공유 저장 파일·저장소에 넣지 않는다. 서버와 CLI는 환경변수를 사용하며 `.env` 파일을 자동으로 읽지 않는다.

| API | 요청 | 용도 |
|---|---|---|
| `GET /health` | 없음 | 준비 상태·모의/모델 모드·형식/콘텐츠/모델/프롬프트 버전·지원 환자 확인 |
| `GET /v1/patients` | Bearer 인증 | 정식 환자 5명·이전 ID 매핑·콘텐츠 준비 상태 |
| `POST /v1/dialogue/start` | `game_progress_id`, `patient_id`, `case_id`, `session_id`, `request_id`, `created_at` | 새 상담 상태와 첫 환자 발화를 함께 생성 |
| `POST /v1/dialogue/open` | `state`, `session_id`, `request_id` | 첫 발화가 없는 기존 빈 회차의 환자 선발화 생성 |
| `POST /v1/dialogue/revisit` | `state`, **새** `session_id`, `request_id`, `created_at` | 평가 미달 상담의 기억·원문을 유지하며 새 회차와 첫 환자 발화 생성 |
| `POST /v1/dialogue/turn` | `state`, `session_id`, `turn_id`, `text` | 플레이어의 서술형 발언에 대한 환자 응답 |
| `POST /v1/dialogue/close` | `state`, `session_id`, `request_id` | 고정 상담자 종료 안내·종료 시각 기록 |
| `POST /v1/dialogue/evaluate` | `state`, `session_id`, `request_id` | 종료 기록을 평가하고 결과 반환 |

모든 POST 성공 응답은 `StateResponse`다. `case_id`, `session_id`, `request_id`, `base_revision`, `state`, `replayed`, `disclosure_candidates`를 반환한다. `/revisit`만 요청의 `session_id`가 이전 상태의 현재 회차와 다르며 성공 응답은 새 회차 ID를 반환한다.

게임 진행을 연결하는 B·C·D는 [A 담당 연결 계약과 검증 절차](../docs/owner-a-ai-service.md)를 따른다. `/start.game_progress_id`와 `state.game_progress_id`는 동일한 진행에 고정하며, 이후 요청·응답과 저장 원문에도 유지한다. `/health.capabilities`의 `game_progress_scope`를 확인한다. ID는 영문·숫자·`_`·`-`의 1~80자다. 진행 ID 없는 기존 형식 4는 내부적으로 `legacy`에 해당하며 이전 저장 경로·JSON을 유지한다. 기존 기록을 임의의 새 게임 진행에 자동 편입하지 않는다.

**평가 HTTP 응답은 게임 정산 완료 신호가 아니다.** Unity `AiSession.Receive`의 검증과 원문·상태 저장 이후 `PatientAiAdapter.SavedEvaluationAvailable` 또는 `TryGetSavedEvaluation`로 얻은 `AiSavedEvaluation`만 B가 받는다. 진행 ID·환자 ID·사례 ID·회차 ID와 두 점수를 함께 제공하며, 모의/출처 미확인 결과와 저장 실패 중인 결과는 이 경로에서 제공하지 않는다. B의 확인 → C 계산 → B 상태 저장 → D 확정 표시를 별도로 완료해야 한다. `EvaluationCommitted(string)`은 기존 기록 표시용이며 정산용으로 사용하지 않는다.

진행별 예제: [서윤 시작](scoped-seoyun-start.request.example.json), [하린 시작](scoped-harin-start.request.example.json), [대화](scoped-turn.response.example.json), [종료](scoped-close.response.example.json), [평가](scoped-evaluate.response.example.json), [재상담](scoped-revisit.request.example.json). 예제는 테스트용 모의 응답이며 실제 게임의 대체 응답으로 사용하지 않는다.

## 첫 발화와 자유 대화

`format_version=4`, `content_version=<patient_id>-4.0`. 기존 서윤의 형식 4 저장은 계속 호환된다. `sessions`의 마지막 항목이 현재 회차다. `case_id`는 한 환자의 누적 상담 실험, `session_id`는 그 안의 한 회차, `request_id`/`turn_id`는 한 요청을 식별한다. 최초 요청 전에 ID와 생성 시각을 확정하며 재시도할 때 바꾸지 않는다.

1. 환자가 자리에 도착하면 `/start`를 요청한다. 응답의 `base_revision=0`, `state.revision=1`이며 현재 회차의 `opening`에 첫 환자 발화가 들어 있다. 클라이언트에서 [초기 빈 상태](initial-state.example.json)를 먼저 저장한 경우 `/open`을 사용해도 된다.
2. `opening`은 `id`, `patient_text`, `candidate_fact_ids`, `created_at`, `metrics`를 갖는다. 가짜 상담자 인사나 빈 `user_text`를 만들지 않는다. 원문에는 `opening.id + ":patient"`인 환자 기록으로 한 번 저장하며 기억·평가에도 포함한다.
3. 첫 발화를 저장한 뒤 플레이어 입력을 받는다. `/turn`의 `text`는 질문뿐 아니라 공감·경청·해석·제안·복수 질문이 섞인 서술형 발언이다. 서버의 분류는 주제와 정보 공개 조건 확인에만 사용하며 질문 수 때문에 거절하지 않는다. 형식 4에는 `questions_used`, `question_delta`, `needs_split`이 없다.
4. 응답의 실험·회차·요청 ID와 `base_revision`, 기존 원문 보존 여부를 검사한다. `disclosure_candidates`는 이번 첫 발화 또는 턴의 `candidate_fact_ids` 안의 값이어야 한다. 클라이언트가 확정한 후보만 `revealed_fact_ids`에 합친다. 서버는 확정 목록을 추가하지 않는다.
5. 상태와 원문을 같은 revision의 새 스냅샷에 저장한 뒤 현재 스냅샷 포인터를 원자적으로 교체한다. 저장 성공 후 화면에 발화를 확정하고 다음 입력을 허용한다. 긴 대사 페이지·타이핑 표시와 전체 원문 보존은 분리한다.

첫 발화 전 `/turn`은 `opening_required`로 거절한다. 이미 저장된 첫 발화를 같은 ID로 `/open`에 다시 요청하면 기존 결과를 반환한다. 다른 ID로 첫 발화를 덮어쓰는 요청은 거절한다. 채팅에 종료 의사나 종료 안내 문구를 입력해도 버튼 요청으로 해석하지 않는다.

## 사용자 종료·평가·재상담

1. `상담 그만하기` 확인을 받으면 새 입력을 차단한다. 진행 중인 응답·저장이 있으면 종료 의사를 보관하고 **그 요청의 처리와 저장을 먼저 완료**한다. 종료 취소는 종료 의도를 확정·저장하기 전에 처리한다.
2. 종료 의도를 먼저 자동 저장한 뒤 `/close`를 호출한다. Python 참조 구현 `request_closing()`과 `SessionClient.finish()`는 `phase=closing`, `revision + 1`로 저장한다. Unity 어댑터는 대기 중 요청과 함께 로컬 저장 묶음의 `end_requested=true`를 저장하고 서버 상태는 `active`인 채 `/close`를 호출한다. 두 경로 모두 복원 시 새 입력을 차단하고 종료 처리를 이어간다. `finish()`는 진행 중 요청을 자체적으로 대기열에 넣지 않으므로 UI 어댑터가 종료 의사를 보관해야 한다.
3. `/close`는 LLM 호출 없이 **상담은 여기까지 하겠습니다. 오늘 수고하셨습니다!**를 `closing_text`에 기록한다. `closing_speaker=counselor`, `phase=pending_evaluation`이다. 원문에는 `종료요청_ID:closing`, `kind=closing`, `speaker=counselor`로 저장한다. 마지막 환자 발화를 바꾸지 않는다.
4. 종료 안내 저장이 끝나면 상담자/안내 영역에 표시하고 `/evaluate`를 호출한다. 결과와 `phase=evaluated`를 저장하고 책상 서류의 `상담 결과`에 반영한다. 별도의 평가 대화창은 만들지 않는다. 질문이나 답변이 없더라도 종료할 수 있다.
5. 두 평가축 각각 70점 이상인 경우만 클리어한다. 방문 퇴장·여섯 명의 시연 종료·서버 오류를 클리어로 계산하지 않는다. Unity가 결과를 검증하고 게임의 완료 인원·보상 정책을 적용한다.
6. 미달 재상담은 `/revisit`을 호출한다. 이전 원문·기억·확정 공개는 유지하고 새 회차 ID와 첫 환자 발화를 한 응답으로 추가한다. 성공 시 revision은 1 증가한다. CLI의 `next_session()` → 빈 회차 저장 → `/open` 방식도 제공하며 이 경우 각 저장 단계마다 revision이 증가한다. 이미 클리어한 실험은 재상담을 추가하지 않고 별도 case ID로 새 실험을 시작한다.

`/close` 자체는 `active` 또는 `closing`에서 사용할 수 있지만 중단 복원을 위해 종료 의도를 먼저 저장하는 흐름을 사용한다. `/evaluate`는 `pending_evaluation` 또는 `evaluated`에서만 허용한다. 종료 기록 저장 실패 시 평가를 호출하지 않으며 평가 실패를 0점으로 확정하지 않는다.

평가는 각 축 4항목 × 25점이다. 환자 변화는 누적 원문, 상담자 대응은 현재 회차의 원문을 사용한다. 근거 ID는 `opening_ID:patient`, `턴_ID:user`, `턴_ID:patient`이며 인용은 해당 원문의 연속 구절이어야 한다. 양수 점수에는 실제 지지 근거가 필요하다. 고정 종료 안내는 원문에는 보존하지만 기억 요약·평가 근거에서는 제외한다. 첫 발화만 있는 상담은 상담자 근거가 없으므로 상담자 점수가 0점이다. 누적 대화 원문 자체가 없으면 LLM 호출 없이 두 축 0점·미클리어와 근거 부족 이유를 반환한다. 기억 요약이 평가 원문을 대체하지 않는다.

## 자동 저장·복원·재시도

`SaveStore`와 `SessionClient`는 Python 참조 구현이다. 자동 저장만 제공하며 새 루트는 `v4/`다. 형식 1·2·3 기록은 보존하고 자동 변환하지 않는다. 과거 기록에 없었던 환자 첫 발화를 사후에 꾸며 넣거나 예전 자동 종료를 새 사용자 종료로 재해석하지 않는다. 이전 기록 발견 시 CLI는 `--new`를 안내한다.

게임을 닫는 것은 상담 종료가 아니다. 복원 시 `active`는 마지막 저장 대화부터 이어가고 첫 발화가 없으면 `/open`을 호출한다. `closing`은 종료 처리, `pending_evaluation`은 평가만 재시도하며 `evaluated`는 저장된 결과를 재사용한다. 완료 인원·보상은 회차 ID를 기준으로 한 번만 반영한다.

정상 요청은 revision을 1 증가시키고 이미 반영된 요청의 재전달은 `replayed=true`로 현재 revision을 유지한다. 응답 대기 중 중복 전송을 막는다. 불러오기·새 실험·방문 변경 전에 보낸 늦은 응답은 실험 ID·API 회차 ID·Unity 방문 ID·revision으로 거절한다. API 회차 ID와 Unity 방문 ID는 서로 다른 값이다.

네트워크 오류는 **동일 ID·동일 요청 본문**으로 재시도한다. 응답을 받았는데 저장에 실패했다면 응답과 공개 확정 선택을 보관하고 **저장만 재시도**한다. 종료 의도 저장 실패도 새 발언을 받지 않고 저장부터 복구한다. API 0.4.3은 같은 프로세스에서 진행·환자·사례·회차·동작·요청 ID·기준 revision이 같은 요청을 합쳐 처리하며, 완료 응답을 제한된 메모리에 잠시 보관한다. 본문이 바뀐 재전송은 `request_conflict`로 거절한다. 서버 DB나 게임 저장은 아니다. 재시작·만료·용량 초과 후에는 같은 문장을 재생성한다고 보장하지 않는다. 클라이언트는 한 응답만 확정하며 프로세스가 종료되어 미저장 응답을 잃으면 마지막 확정 스냅샷과 보관한 요청부터 재개한다.

참조 클라이언트의 API 제한시간은 추론·검증 재호출을 포함해 180초다. 일반 응답 약 10초는 목표이며 타임아웃 보장은 아니다. 준비 확인·환자 응답·저장·기억 요약·종료·평가·오류 재시도 상태를 구분해 표시한다. 입력 예산 초과 시 초안을 보존하고 길이 조정을 안내하며 상담을 자동 종료하지 않는다.

오류 응답은 `{"detail":{"code":"...","message":"...","retryable":false}}`다. `retryable`은 0.4.3에서 추가한 안내이며 구 서버의 필드 생략도 허용한다. `true`여도 새로운 ID를 만들지 않는다. `false`는 인증·입력·회차·버전부터 확인해야 한다는 뜻이다. 서버 성공 응답과 저장 형식에는 필드를 추가하지 않았다.

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
| `server_busy` | 503 | 대기열 또는 대기 시간 한도. `Retry-After: 2` 이후 원래 요청 재시도 |
| `model_timeout`, `request_timeout` | 504 | 확정하지 않은 입력 보존, 원래 요청 재시도 |
| `request_conflict` | 409 | 동일 ID·기준 revision의 본문이 달라짐. 원래 요청 복원; 새 회차로 우회하지 않음 |

## 생성 계약과 게임 연결

[OpenAPI](dialogue.openapi.json)와 `*.example.json`은 `python tools/export_contracts.py`로 실제 모의 API에서 생성한다. 예제는 작성된 가상 연결 데이터이며 실제 상담 원문이 아니다. `python tools/export_contracts.py --check`로 코드와 생성 계약이 일치하는지 검사한다.

| 예제 접두사 | 확인할 동작 |
|---|---|
| `dohyeon-start`, `taejun-start`, `yeonwoo-start`, `harin-start` | 추가 캐릭터별 ID·콘텐츠 버전·초기 상태·첫 발화의 모의 예제 |
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

Unity는 `PatientVisitController.PatientArrived(visitId, patientId)`에서 지원 환자와 저장 상태를 확인한다. API 상담에서는 `useDemoDialogue=false`로 두고 확정 응답을 `PresentPatientLine(visitId, text)`에 전달한다. `PatientAiInputBridge`가 기존 입력창·전송 버튼을 PR #24의 `PatientAiAdapter`에 연결한다. 종료 확인 시 즉시 퇴장시키지 않고 원문·고정 안내·평가를 저장한 뒤 `MarkConsultationEnded(visitId)`로 체류한다. 사용자 `환자 내보내기` → `DismissPatient()` → `ExternalFinishVisit` → 완료 저장 뒤 `CompleteConsultation(visitId)`로 퇴장한다. 평가는 책상 기록부에 보관하며 결과 대화창을 따로 띄우지 않는다. 이 함수는 그 자체로 클리어·보상·게임 완료를 부여하지 않는다.

진료 기록은 공개가 확정된 정보와 실제 원문만 반영하며 미제공 나이·성별·진단·약물·새 사건을 추측해서 채우지 않는다. 환자별 임상 콘텐츠, 단계별 해금, 승급, 코인 정산, 치장 구매, 최종 엔딩은 각 게임 시스템의 별도 구현 범위다. AI API가 이를 대신 완료한 것으로 취급하지 않는다.


<a id="unity-text-input"></a>

## Unity 텍스트 입력 UI 연결 명세

기존 입력창을 `Gamsamo.Networking.PatientAiInputBridge`로 `PatientAiAdapter`의 공개 함수와 이벤트에 연결했다. Enter는 줄바꿈, Ctrl+Enter 또는 전송 버튼은 전송이며 한글 조합 중에는 보내지 않는다. 지원 5명 모두 입력창을 사용하고 민석은 입력창을 숨긴 로컬 시연이다. 독립 `ConsultationApiClient`나 `UserSettings/CounselingApi.json`은 현재 연결에 사용하지 않는다. UI에서 HTTP 요청·요청 ID·revision을 따로 만들거나 전체 상태 JSON을 일부 필드로 재구성하지 않는다.

### 설정과 데이터 소유권

- 연결 설정은 `Application.persistentDataPath/ai-connection.json`의 `api_url`, `api_key`다. `GAMSAMO_AI_CONNECTION` 환경변수로 다른 로컬 파일을 지정할 수 있다. 지정/기본 파일이 없을 때는 환경변수 `SEOYUN_API_URL`·`SEOYUN_API_KEY`를 사용한다. 실제 주소·키는 저장소·씬·프리팹·Inspector 직렬화·로그에 넣지 않는다.
- 원격 API는 HTTPS와 Bearer 키를 요구한다. 개발용 HTTP는 loopback만 허용한다. `/health`로 준비 상태·형식 4·`supported_patients`에 있는 선택 환자의 콘텐츠 버전·지원 여부를 확인한다. 현재 게임은 담당자가 제공하는 실제 외부 AI 서버 설정을 사용하며, 임시 모의 서버로 연결 성공을 대체하지 않는다. 저장소의 모의 API·합성 예제는 개발 검사용이다.
- `AiProtocol`은 전체 상태 JSON, 기존 원문·회차, 응답 ID/revision, 공개 후보 출처, 평가 합계와 원문 인용을 검증한다. 클라이언트가 이번 응답에서 검증한 후보만 공개 확정 목록에 합친다. 게임의 보상·승급·클리어 인원 증가는 별도 진행 서비스의 책임이다.
- `AiSession`이 요청·초안·종료 의도를 관리하고 `AiSaveStore`가 `Application.persistentDataPath/ai-saves/<patient_id>/`에 상태와 원문 쌍을 저장한 뒤 현재 저장 포인터를 교체한다. Python CLI의 `v4/` 경로와는 별도이며 이전 형식 파일을 자동 변환하지 않는다. 상담 종료와 앱 종료는 다르다. 앱을 다시 열면 저장된 단계부터 복원한다. 초안만 바뀔 때는 입력 후 0.75초 대기해 저장하며 정상 비활성화·일시정지·종료 시 저장을 시도한다. 프로세스 강제 종료 때는 아직 저장되지 않은 초안이 사라질 수 있다. 전송 요청은 네트워크 호출 전에 원문을 즉시 저장한다.

### Editor에서 준비·검증

1. `ProjectSettings/ProjectVersion.txt`의 Unity 버전으로 열고 패키지 복원을 완료한다. JSON 라이브러리는 Unity 공식 `com.unity.nuget.newtonsoft-json` 3.2.2를 사용한다.
2. `Gamsamo > AI > Import Connection JSON`에서 별도 전달받은 설정 파일을 가져온다. 파일은 프로젝트 밖의 기본 연결 경로에 저장된다. 주소가 바뀌면 다시 가져오고 `Retry Pending Operation (Play)`을 실행한다.
3. `ConsultationRoom.unity`를 열고 `Gamsamo > AI > Validate Current Scene`으로 참조·콘텐츠 버전을 확인한다. 이 메뉴는 네트워크를 호출하지 않는다. Play 시작 시 `PatientAiBootstrap`이 기존 방문 컨트롤러에 어댑터를 자동 장착하며 씬·프리팹은 다시 생성하지 않는다.
4. 문을 눌러 지원 환자가 착석한 뒤 첫 발화를 읽고 입력창에 자유롭게 답변한다. Ctrl+Enter 또는 전송 버튼으로 보낸다. API 연결만 별도로 점검하려면 `Gamsamo > AI > Submit Sample Utterance (Play)`을 사용한다. 이 메뉴는 고정 합성 발언으로 `SubmitText`를 호출하는 **Editor 검증 도구**이며 게임 입력 UI가 아니다.
5. 같은 메뉴의 `Request End`, `Confirm End`, `Cancel End`, `Retry Pending Operation`, `Revisit`, `Finish Visit`, `New Experiment`(모두 `Play` 표기)로 해당 공개 함수를 확인한다. `New Experiment`는 별도 실험 생성 확인을 거친다. 게임에서는 종료 확인 후 원문·고정 안내·평가가 저장되면 환자가 머물고 `환자 내보내기`를 기다린다. 책상 서류의 `대화 원문 / 상담 결과` 탭에서 내용을 읽으며 별도의 결과 대화창은 띄우지 않는다. 기록부를 닫아도 머물며 내보내기를 직접 눌러야 퇴장한다.

런타임 어댑터의 Inspector에는 단계·입력 가능·대기·오류만 표시하고 주소·키·원문은 출력하지 않는다. 이전 `Run Patient Visit Checks`는 AI 어댑터가 붙어 있으면 사용자 저장과 외부 API 요청을 보호하기 위해 실행을 차단한다. 이번 AI 검증은 위 메뉴와 아래 수동 체크리스트를 사용한다. 이 도구를 추가했다는 사실은 Unity Editor에서 실제 실행한 결과가 아니다. `Revisit`는 평가된 활성 방문이 있을 때만 가능하며 재상담 선택 UI는 아직 없고 새 회차를 자동 시작하지 않는다.

PR #28에서 서윤을 대상으로 Unity Editor의 입력 브리지·저장/기록부 검사 28개와 실제 서버 CUA의 첫 발화·서술형 2턴·종료·원문/평가 열람을 확인했다. 상세 범위는 plan01 17.25절을 따른다. 완료 사례의 다음 Play 방문도 마지막 대사·기록을 표시한 채 내보내기를 기다리며 재상담/새 실험 게임 UI는 없다. 기록부의 실행 중 색인은 해당 환자 입장 시 저장 AI 상태로 재구성한다. 추가 4명의 Unity 화면 조작은 이번 PR의 체크리스트로 별도 확인한다.

### 공개 연결면

| 함수·속성·이벤트 | 입력 UI의 연결 방법 |
|---|---|
| `SubmitText(string text)` | 전송 버튼에서 현재 입력 원문 전달. `true`는 요청 접수이며 응답·저장 성공을 뜻하지 않음 |
| `UpdateDraft(string text)` | 입력 변경 시 호출. UI를 다시 열 때 `Draft`를 복원. 한글 조합 확정 시에도 최신 문자열 전달 |
| `IsAiVisit`, `PatientId` | AI 지원 환자일 때 입력 패널을 표시하고 현재 환자 ID 조회. `IsSeoyunVisit`은 기존처럼 서윤 방문만 의미한다. 5명 모두의 입력 패널은 `IsAiVisit` 사용 |
| `CanSubmit`, `CanEditDraft` | 전송 버튼·입력창 활성화 조건. UI는 전송 시 공백 여부·IME 조합 여부를 추가로 확인. 대사를 다 읽기 전 초안 편집과 전송 가능 시점은 다름 |
| `IsBusy`, `CurrentPhase`, `EndRequested` | 응답 대기·평가·종료 중 상태 표시. 질문 사용량 UI는 만들지 않음 |
| `Draft` | 초안의 기준값. 전송 직후 임의로 지우지 않고 저장 완료 후 이 값과 동기화 |
| `CanRetry`, `Retry()` | 오류 후 재시도 버튼. 네트워크 실패는 같은 ID/본문, 받은 응답의 저장 실패는 저장만 재시도 |
| `RequestEnd()`, `ConfirmEnd()`, `CancelEnd()` | 종료 확인 열기·확정·취소. 기존 상담 종료 버튼도 같은 흐름을 사용 |
| `Evaluate()` | 종료 안내가 저장된 상태에서 평가 요청. 일반 흐름은 자동 진행하며 실패 재시도는 `Retry()` 사용. 오류를 0점으로 대신 표시하지 않음 |
| `CanRevisit`, `Revisit()` | 평가 미달 후 기억·원문을 잇는 명시적 새 회차 API. 이전 퇴장 권한 해제. 게임 내 재상담 선택 UI는 후속 구현 |
| `CanFinishVisit`, `FinishVisit()` | 평가·기록 저장 후 사용자 내보내기로만 진행. 활성 방문·퇴장 대기·요청/오류 없음·팝업 해제를 요구하며 완료 저장 실패는 체류·재시도. 클리어·보상 부여 없음 |
| `PatientVisitController.MarkConsultationEnded/ClearConsultationEnded`, `IsAwaitingDeparture` | 동일 방문의 종료 후 체류 설정/해제. 완료 사례 복원도 체류하며 새 상담 자동 시작 없음 |
| `PatientVisitController.DismissPatient`, `ExternalFinishVisit` | 명시적 내보내기 진입점과 AI 완료 저장 처리기. 외부 실패·누락을 로컬 퇴장으로 대신하지 않음 |
| `PatientVisitController.CompleteConsultation(visitId)` | 동일 활성 방문·퇴장 대기·팝업 해제를 확인하고 ID 무효화 후 퇴장. 중복·오래된 ID 거절 |
| `PatientDialogueView.SetDepartureAvailable/EnsureDepartureControl`, `DismissRequested` | 기존 종료 버튼 위치에 내보내기 준비·표시. 복제 리스너 제거·중복 구독 방지. 표시·퇴장 가능·확인 없음·일시정지 아님일 때만 이벤트 전달 |
| `NewExperiment()` | 사용자가 별도 새 실험을 선택한 경우에만 호출. 연결 오류·씬 재시작에서 자동 호출하지 않음 |
| `PatientId`, `CaseId`, `SessionId`, `Revision` | 별도 대화 로그 UI의 중복 표시 방지 기준. 복원 시 마지막 확정 이벤트가 다시 올 수 있으므로 같은 기록을 재삽입하지 않음 |
| `LastPatientText`, `EvaluationJson`, `LastError` | 화면 재연결 시 마지막 확정 대사·결과·오류 조회. 오류와 상담 원문을 공개 로그로 남기지 않음 |
| `event Action StateChanged` | 위 속성을 다시 읽어 입력 활성화·처리 상태·초안·재시도 상태 갱신 |
| `event Action<string> PatientLineCommitted` | 응답 검증과 자동 저장이 끝난 환자 대사. 기존 대화창에도 연결되므로 별도 UI가 중복 출력하지 않도록 함 |
| `event Action<string> CounselorNoticeCommitted` | 고정 상담자 종료 안내. 환자 이름 아래에 표시하지 않음 |
| `event Action<string> EvaluationCommitted` | 검증·저장된 평가 JSON. 책상 서류의 상담 결과에 두 축·이유·근거를 표시; 접속 오류와 구분 |
| `event Action<string> ErrorOccurred` | 사용자에게 오류와 가능한 재시도를 안내. 초안·확정 기록을 지우지 않음 |

### 입력과 표시 규칙

1. **첫 환자 발화 저장 이후**에만 전송한다. `CanSubmit`이 거짓이면 버튼과 Ctrl+Enter 전송을 모두 막는다. 접속 준비·진행 중 요청·저장 실패·종료 요청·평가·팝업 차단도 반영한다.
2. 공백만 있는 입력은 전송하지 않는다. 공감·제안·질문·복수 질문·여러 줄 입력을 모두 허용한다. 문자열을 임의로 잘라 보내거나 질문 수로 제한하지 않는다. API에 보낼 원문은 trim·개행 재작성 없이 전달한다.
3. 키보드 전송 정책은 **Enter 줄바꿈, Ctrl+Enter 전송**이다. 한글 IME 조합 중에는 키보드·버튼 전송을 모두 막는다. TMP의 제출 이벤트를 곧바로 `SubmitText`에 연결하지 말고 조합 중인지 확인한다. 마우스 전송도 최종 조합 문자열을 확정한 뒤 실행한다.
4. 요청 접수 뒤 입력 편집·중복 전송을 막고 초안을 유지한다. 실패 시 같은 글을 확인할 수 있어야 한다. `StateChanged`에서 `Draft`를 반영할 때 `SetTextWithoutNotify` 등을 사용해 초안 저장 콜백이 다시 발생하지 않게 한다. 성공 여부를 HTTP 요청 시작 시점으로 판단하지 않는다.
5. `context_overflow` 등 수정이 필요한 입력 오류는 초안을 돌려주며 사용자가 길이를 조정해 새로 전송한다. 네트워크 장애에서는 기존 요청을 그대로 재시도한다. 저장 실패에서 `SubmitText`를 다시 호출하지 않는다.
6. 복원 시 마지막 확정 대사·평가 이벤트가 다시 발생할 수 있다. 별도 로그 UI는 `PatientId`·`CaseId`·`SessionId`·`Revision`으로 중복 삽입을 막거나 `LastPatientText`·`EvaluationJson`으로 화면을 교체한다. 표시용 타이핑·페이지 이동은 원문을 바꾸지 않는다. 출력은 리치 텍스트 해석을 끄고 원문을 그대로 표시한다. 표시 중 PC·기록부를 열어도 상담 상태·입력 초안은 유지한다.
7. 종료 확인 **취소**는 상담을 계속한다. 종료 **확정**은 새 입력을 막고 현재 응답/저장을 먼저 끝낸 뒤 고정 안내 → 저장 → 평가로 진행한다. 사용자가 채팅에 “종료”라고 쓴 것만으로 종료 요청을 만들지 않는다.
8. `pending_evaluation`에서는 “평가 중” 또는 평가 오류·재시도를 표시한다. 두 축의 점수는 저장된 평가에서 읽으며 서버 오류를 임의의 0점·클리어로 바꾸지 않는다.

9. 평가 저장 완료는 퇴장 허용이며 자동 퇴장이 아니다. 완료 사례도 착석 상태에서 기다린다. PC·기록부·확인창, 요청·저장·오류 중에는 내보내기를 거부한다. 서류 닫기·페이지 넘김·다음 프레임은 퇴장 의사가 아니다. 명시적 새 회차는 이전 권한을 해제한다. 저장된 평가 열람·퇴장에 새 서버 응답은 필요 없다.

아래는 `PatientAiInputBridge`의 연결 원리를 줄인 예시다. 실제 브리지는 구독 해제·키 입력·초안 복원·재시도·기록부 열기와 미지원 환자 입력 숨김도 처리한다.

```csharp
// input.onValueChanged -> 이 함수 연결
void OnDraftChanged(string text) => adapter.UpdateDraft(text);

// 전송 버튼 -> 이 함수 연결. Ctrl+Enter 처리에도 동일한 IME 확인이 필요하다.
void OnSendClicked()
{
    if (!adapter.CanSubmit || !string.IsNullOrEmpty(UnityEngine.Input.compositionString)) return;
    if (string.IsNullOrWhiteSpace(input.text)) return;
    adapter.SubmitText(input.text); // 여기서 input.text를 지우지 않는다.
}

// adapter.StateChanged 구독. 연결 해제 시 반드시 이벤트 구독 해제.
void RefreshInput()
{
    input.SetTextWithoutNotify(adapter.Draft);
    input.interactable = adapter.CanEditDraft;
    sendButton.interactable = adapter.CanSubmit && !string.IsNullOrWhiteSpace(input.text);
}
```

### 팀원 실행 검증

명시적 퇴장 수정은 Runtime 29개·Editor 18개 C# 소스 컴파일, Unity 입력 브리지 56개 검사를 통과했다. CUA에서 완료 서윤 복원·체류·문 차단·기록부 열기/닫기·명시적 퇴장·도현 입장·이전 평가 보존을 확인했다. Console 오류·경고는 0개다. 사용자 요청으로 새 대화부터 평가까지의 추가 검증은 중단했으며 자세한 범위는 plan01 17.30절을 따른다.

아래는 Unity Editor와 실제 시연 데스크톱의 확인 기준이다. 서윤 UI의 기존 결과는 plan01 17.25절, 이번 5인 연결 결과는 17.28절을 따르며, 표의 항목을 작성했다는 사실은 검증 통과를 뜻하지 않는다. 이 저장소의 C# 계약 검사와 Unity 대체 타입을 사용하는 어댑터 컴파일 검사는 Unity Editor의 정식 컴파일·실행·한글 IME·화면 배치 검사를 대신하지 않는다. 실제 접속 설정은 별도로 전달하며 결과를 공유할 때 키·주소·개인 상담 원문은 제거한다.

| 방문·종료·기록부 확인 | 기대 결과 |
|---|---|
| 각 지원 환자 착석·첫 발화 | 준비 확인 후 환자만 먼저 한 번 발화. 가짜 상담자 인사 없음 |
| PC·기록부 열기/닫기 | 뒤 입력 차단, 같은 방문·회차·대사 유지 |
| 종료 확인 취소/확정 | 취소는 상담 유지. 확정은 고정 상담자 안내를 저장한 뒤 평가, 이후 방문 종료 가능 |
| 첫 발화만 듣고 종료 | 상담자 근거가 없어 상담자 점수 0, 실제 오류와 구분 |
| 평가 미달 후 재상담 API | 이전 원문·공개 정보·기억 유지, 새 회차의 첫 발화. 이전 퇴장 권한 해제. 게임 내 선택 화면은 미구현 |
| 다음 환자 | 지원 5명은 각자의 입력·이름·기록·평가를 사용. 민석만 로컬 시연이며 다른 환자의 요청·기억을 재사용하지 않음 |

| 입력·복구 확인 | 기대 결과 |
|---|---|
| 한글 조합·Enter·Ctrl+Enter·여러 줄·공백 | 조합 중 오발송 없음, 원문/줄바꿈 유지, 공백 입력 미전송 |
| 공감·복수 질문·6회 이상 대화 | 질문 제한/강제 분리/자동 종료 없이 계속 상담 |
| 전송 연타·응답 대기 중 종료 확정 | 한 요청만 접수, 저장 이후 종료 처리, 누락·중복 없음 |
| 긴 대사·서류 탭·페이지 이동 | 대사와 전체 원문 저장 일치, 원문·결과는 책상 서류에서 열람, 페이지 이동은 새 턴이 아님 |
| 네트워크 끊기·잘못된 키·복구 후 재시도 | 오류와 초안 유지, 같은 요청 재시도, 가짜 성공·0점 평가 없음 |
| 저장 실패 후 재시도 | 받은 응답을 보관하고 저장만 재시도 |
| active/종료 예약/pending_evaluation에서 재시작 | 같은 실험·회차·단계 복원, 첫 발화·종료 안내·평가 중복 없음 |
| 공개 정보와 평가 근거 | 현재 환자의 확정 후보·평가기준·실제 원문만 반영, 미제공 신상 생성 없음 |
| 연우 첫 상담→종료·평가→재상담 | 첫 상담에는 이후의 영상 사건을 미리 말하지 않고, 두 번째 회차부터 고정 영상 사건을 이야기할 수 있음 |

각 테스트에서 수행한 조작, 기대값과 실제 결과, 재현 단계, 오류 코드를 기록한다. Windows/macOS 빌드의 저장 경로·원자적 파일 교체·인증과 네트워크 오류도 대상 PC에서 확인한다.


### 캐릭터별 이야기와 평가 연결

설정 원본은 `Assets/StreamingAssets/GameData/Patients/<patient_id>.json`이다. 공통 두 축/각 70점 규칙은 유지하며 환자 축의 네 기준과 공개 사실은 캐릭터마다 다르다. 상담자 축도 해당 이야기의 현실 맥락에 맞는 label을 사용한다. C#은 `AiProtocol.RegisterPatient`로 원본을 등록한 뒤 상태·평가를 검증한다. 코드에 서윤의 사실 ID/평가 ID를 새 환자용으로 복사하지 않는다.

도현의 회사·지역·급여·근무시간·복리후생·비용은 가상 비교 조건이며 실제 지역 통계나 확정 실수령액으로 취급하지 않는다. 태준의 지역·정당은 가상이다. 하린의 발표 참여 여부, 도현의 이직/잔류, 태준의 정치적 선택, 연우의 재결합 여부를 정답으로 채점하지 않는다.

연우의 `story_events` 중 자전거 영상은 `min_session=2`다. `/revisit`으로 새 회차를 만든 뒤에만 해당 설정을 모델에 제공한다. 단순 앱 재실행·요청 재시도·대화 턴 증가만으로 다음 사건을 열지 않는다. 사전 작성 사건은 평가 근거 원문을 대신하지 않으며 원문에 없는 감정 변화·행동 실행을 평가하지 않는다.

## AI 0.4.3: 계측·재전송·배포 확인

### 호환성과 UI 연결

- `StateResponse`, `CaseState`, 형식 4 원문·저장, 두 축 70/70, Start/Resume/Revisit의 의미는 유지한다. 환자 데이터와 질문 제한을 변경하지 않는다. 서버는 코인·해금·정산을 확정하지 않는다.
- **미저장 응답을 캐시에서 돌려줄 때도 원래 `base_revision`, `revision`, `replayed`를 그대로 보낸다.** `replayed=true`는 요청에 이미 저장된 상태가 들어온 기존 계약에만 해당한다. 전송 캐시 적중 여부로 `replayed`를 바꾸면 Unity의 저장 검증이 깨진다.
- 캐시 키는 진행·환자·사례·회차·API 동작·요청 ID·기준 revision 전체다. 캐시 보존 기간에는 원래 JSON 본문을 키 순서와 무관하게 비교한다. 생성 시각을 생략한 동일 `/start`·`/revisit` 재전송도 같은 응답을 받는다. ID의 유효성, 환자 콘텐츠 버전·공개 후보·평가 근거, 현재 회차 일치 검증은 유지한다. 서버는 클라이언트가 임의로 다시 작성한 전체 게임 이력의 소유권을 인증하는 저장소가 아니다.
- 기본 한도: 실행/대기 16건, 모델 대기 30초, 대기 이후 작업 600초. 단일 모델 호출은 기존 180초 제한을 유지한다. 긴 원문의 여러 근거 추출 호출을 허용하기 위해 전체 작업 한도를 더 길게 두며, Unity의 180초 제한 이후에도 같은 요청으로 진행 중 작업에 합류할 수 있다. 완료 캐시는 최대 10분·256건·직렬화 크기 약 32MiB이고 먼저 도달한 한도에 따라 제거한다. 서버 종료 때 제거한다. 다른 프로세스나 재시작 사이에 캐시가 공유되지 않으므로 **API worker는 1개**로 실행한다.
- 연결이 끊겨도 작업은 한도 안에서 계속되며 같은 요청은 진행 중 작업에 합류한다. 모델 오류·검증 실패는 성공 응답으로 캐시하지 않는다. 종료 안내와 이미 저장된 발화·평가의 조회성 재전송은 모델 대기열을 기다리지 않는다. 입력 응답 저장 → 종료 → 종료 저장 → 평가 → 평가 저장 순서는 클라이언트가 계속 지켜야 한다.
- UI의 기존 첫 발화 준비·답변 생성·저장·평가·완료·오류 상태를 그대로 쓴다. 추가 진행률이나 완료 이벤트는 없다. 캐시의 HTTP 200도 AI 저장 완료를 뜻하지 않으며 B·C는 계속 A의 `TryGetSavedEvaluation()` 이후에만 정산한다. D/UI 담당에게 권장하는 문구: `server_busy`는 “다른 요청 처리 중, 잠시 후 같은 요청 재시도”, `model_timeout`/`request_timeout`은 “응답 대기 실패, 입력은 보존됨”, `request_conflict`는 “보관한 요청과 저장 상태 확인”. 기존 Unity는 추가 필드를 무시하고 503/504/409의 일반 오류와 기존 재시도를 표시해 계속 동작한다. 세부 문구를 구분하는 UI 변경은 이 PR에 포함하지 않는다.

### 지연 측정의 의미

인증·입력 검증을 통과한 상담 요청에는 `X-Request-Id`, `X-AI-Cache`(`miss`/`joined`/`hit`), `Server-Timing` 헤더가 붙는다. 성공 본문에 필드를 넣지 않으므로 구버전 저장 파서를 유지한다.

| 시간 | 측정 범위 |
|---|---|
| `queue` | 이 API 프로세스의 모델 잠금 대기 |
| `model` | 모든 모델 HTTP 호출 합계, 수정 재호출·요약·근거 추출 포함 |
| `postprocess` | 모델 결과 파싱 이후 스키마·근거·대사 검증 |
| `app` | 작업 전체에서 위 시간을 뺀 준비·토큰 계산·상태 조립 등 |
| `request` | HTTP 처리 시작부터 응답 헤더 준비까지 |

단위는 헤더에서 밀리초, `ai.operation`/`ai.request` JSON 로그에서 초다. `model`에는 모델 서버 내부 대기와 통신이 포함된다. GPU 순수 추론과 vLLM 내부 대기 시간은 현재 API만으로 분리하지 못한다. `joined`의 작업 시간은 최초 요청부터의 전체 작업이며 합류한 HTTP 대기 시간과 다를 수 있다. `hit`는 작업 시간을 0으로 표시한다. 응답 전송·Unity 수신·디스크 저장 시간은 별도다. 기본 로그는 요청 ID·환자 ID·동작·오류 코드·호출 수·토큰 수·역할별 시간만 기록하며 원문·키·URL·내부 추론을 넣지 않는다.

실모델 비교 도구는 `ai/evaluation/reliability_benchmark.py`다. `quality_scenarios.json`의 첫 발화만/부적절한 대응/지지적 대응은 테스트용 상담자 발언이며 환자 대사·점수는 실제 모델에서 받는다. `--private-records`를 명시해야 비교용 요청·응답 전문을 별도로 보관한다. 출력은 무시된 `ai/outputs/` 아래 0600 파일로 관리하며 커밋하지 않는다.

```bash
PYTHONPATH=server python ai/evaluation/reliability_benchmark.py \
  --source-root . --profile ai/configs/qwen38-27b-fp8.json \
  --output ai/outputs/quality --label candidate --code-revision REVIEWED_SHA
# 같은 원문으로 비교할 때만 로컬 비공개 baseline.private.json을 --replay에 전달한다.
PYTHONPATH=server python ai/evaluation/compare_runs.py \
  ai/outputs/quality/baseline.json ai/outputs/quality/candidate.json \
  --output ai/outputs/quality/comparison.json
```

측정에는 GPU 제품/메모리, 모델·토크나이저 리비전, 역할별 생성 설정, 문맥 상한/관측 길이, 표본 수를 같이 남긴다. 기준 측정은 고정 코드·프롬프트의 별도 소스 사본과 같은 프로필을 사용한다. 공용 API의 `busy` 관측이 있는 표본은 집계에서 제외한다. API 밖에서 직접 모델을 쓰는 다른 작업은 이 검사로 탐지하지 못한다. 동시 측정은 피한다. 도구는 작업별 성공 표본 20개부터 p50, 100개부터 p95를 출력하며 작은 표본은 평균·범위만 제시한다. 한국어 자연스러움·설정 사실의 의미 검토는 별도로 해야 하며 HTTP 200을 품질 합격으로 삼지 않는다.

`compare_runs.py`는 같은 요청 본문 해시·모델 리비전·문맥·장비 목록을 확인한 쌍만 비교하고 생성 설정 차이와 실패/공용 부하 제외 건수를 같이 기록한다. 캐시 응답은 `evaluate_cached`처럼 별도 집계하며 최초 추론 시간의 평균이나 표본 수에 섞지 않는다. 이번 작업에서 평가 추론 비활성화는 빠르지만 경계 점수 변동이 있어 채택하지 않았으며, 실행 프로필과 평가 기준은 그대로 유지한다.

### 승인 후 배포와 복구

이번 작업은 후보 검증과 PR까지만이다. 공용 서버를 재시작하거나 연결 주소·키를 변경하지 않는다. 배포 담당자는 승인 후 아래 순서로 진행한다.

1. 기존 서비스의 커밋, 모델 프로필/리비전, 실행 명령과 연결 설정을 비공개로 보존한다. `/health`에서 `api_version`, 프로세스 시작 때 고정한 `code_revision`, `model_id`, `model_revision`, `tokenizer_revision`, `max_model_len`, `prompt_version`, `capabilities`, `mode`, `inference.ready`를 확인한다. `-dirty`는 추적 파일 수정 또는 새 서버 파일이 있어 커밋과 다름을 뜻한다. 이전 배포에는 코드 버전 필드가 없을 수 있으므로 실행 기록과 대조한다.
2. 승인된 커밋을 별도 체크아웃에 준비하고 기존 Conda 환경과 모델 프로필을 사용한다. 패키지 업그레이드나 가중치 교체가 필요한 변경은 아니다. `PYTHONPATH=server python tools/serve_api.py --port 8002`로 loopback 후보 API만 실행한다. 모델 경로는 후보 체크아웃에서 유효해야 한다. 공유 모델을 재사용하려면 비공개 프로필에 기존 가중치 절대 경로를 지정할 수 있다.
3. `PYTHONPATH=server python tools/ai_release_smoke.py --url http://127.0.0.1:8002 --expect-revision REVIEWED_SHA --lifecycle`로 준비/모델 버전·5인 목록·인증·첫 발화·대화·종료·평가·미클리어 재방문·동일 본문 재전송을 확인한다. 키는 환경변수로만 제공한다. `--lifecycle`을 빼면 읽기 전용 확인이며 `--patient`로 다른 환자를 선택할 수 있다. 실제 저장·Unity 화면·정산은 별도 확인한다.
4. 실제 접속 대상 전환은 진행 중 요청을 마친 뒤 승인된 운영 절차로 수행한다. 모델과 API를 함께 소유하는 실행 도구를 무심코 종료하면 모델까지 꺼지므로 현재 프로세스 관리 방식을 먼저 확인한다. 전환 뒤 동일 smoke와 팀원 Unity 저장/재시도를 확인한다.
5. 실패하면 이전 소스·프로필의 API로 연결 대상을 되돌리고 health와 smoke를 확인한다. 기존 형식 4 저장을 삭제/환산하지 않는다. 이미 받은 응답은 저장만 재시도하며 미완료 요청은 원래 ID·본문으로 재전송한다. 휘발성 캐시가 사라져 재추론될 수 있지만 한 응답만 확정하는 클라이언트 규칙은 유지한다. 이전 API는 새 capability가 없으므로 복구 확인에는 기존 `tools/check_connection.py` 또는 해당 버전의 검사 도구를 사용한다.
