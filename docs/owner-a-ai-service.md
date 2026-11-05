# A 담당: 환자별 AI 상담 서비스

## 기준과 담당 범위

첨부 「GC_gamsamo_미구현_항목_및_4인_작업분담.pdf」 10~16페이지의 소유권·연결 순서와 앞부분의 AI 요구사항을 확인했다. PDF 기준 `main/c260a0d` 이후 변경을 포함한 `main/a33c40c`에서 시작했다. 사용자의 최신 결정에 따라 최종 대상은 **서윤·도현·태준·연우·하린 5명**이다. 지은의 기존 ID `jieun`은 승인된 연우(`yeonwoo`)에 연결한다. 민석은 개발 대기 인물이 아니라 최종 대상에서 제외한다.

사용자가 담당자별 작업 기록을 요청했으므로 이 파일에 A 기록을 분리한다. 공용 진행 문서, 씬, 루트 프리팹, 환자 순서·클리어 집계·코인·화면 파일은 이번 작업에서 수정하지 않는다.

착수 시 확인한 상태:

| 범위 | 기존 상태 | 이번 변경 |
|---|---|---|
| 정식 환자별 설정·대사·공개 조건·두 축 평가 | 5명 구현 | 기존 콘텐츠 재사용, 단일 목록과 이전 ID 연결 제공 |
| 환자별 저장·종료 의도·미완료 요청·응답 저장 재시도 | 구현 | 기존 형식 4·수동 퇴장을 보존하고 진행/사례 범위 검증 추가 |
| 새 사례·같은 회차 이어하기·새 재상담 | 내부 동작 일부 구현 | B가 서로 구분해서 호출하는 ID 기반 API 추가 |
| 게임 진행별 저장 격리·확정 평가 인계 | 미구현 | 진행별 저장 경로, 전체 정산 식별자, 저장 완료 모델 평가 전달 |
| 원문·발언 위치·공개 상태 조회 | JSON/화면 표시 일부 구현 | 변경 불가능한 조회 모델과 회차별 원문·정확한 인용 위치 제공 |

## 정식 콘텐츠 연결

`Assets/StreamingAssets/GameData/patient-catalog.json`이 목록의 기준이다. `Patients/*.json`은 기존의 정식 사실, 말투, 공개 조건, 변화/상담자 평가 기준을 유지한다. 첫 발화는 공개 사실과 해당 회차에 허용된 사건에서 실제 모델이 생성한다. 미공개 사실을 첫 발화에 미리 넣지 않는다.

| 정식 ID | 이름 | 기존 정식 자료 | 주제 |
|---|---|---|---|
| `seoyun` | 서윤 | `Patients/seoyun.json` | 취업 실패·자기 가치 |
| `dohyeon` | 도현 | `Patients/dohyeon.json` | 두 가상 회사의 조건·지역 생활·비교 |
| `taejun` | 태준 | `Patients/taejun.json` | 가상 지역·정당의 기대와 개인의 책임 |
| `yeonwoo` | 연우 | `Patients/yeonwoo.json` | 합의 이혼 뒤 양육과 놓친 성장 순간 |
| `harin` | 하린 | `Patients/harin.json` | 다문화가정의 애정·부담·소속감 |

서버 `GET /v1/patients`와 Unity `PatientAiAdapter.PatientCatalog.Patients`는 정식 ID·이름·aliases·ready·content_version·unavailable_reason을 제공한다. 비공개 사연과 원문은 목록 API에 포함하지 않는다. Unity의 `PatientCatalog.Get("jieun").Id`는 `yeonwoo`지만 서버에는 정식 ID만 보낸다. `minseok` 요청은 404 `unsupported_patient`다. 정식 콘텐츠 파일을 읽지 못하거나 버전이 맞지 않으면 오류로 중단한다. 임시 대사·임의 점수로 대체하지 않는다.

## B가 연결할 수명주기

`AiConversationKey`는 `GameProgressId`, `PatientId`, `CaseId`, `SessionId`의 변경 불가능한 묶음이다. ID는 영문·숫자·`_`·`-`, 1~80자로 제한한다. `SettlementId`는 이 네 값을 `/`로 연결한다. B는 **호출 전에** 선택한 환자와 작업 종류, 이 ID들을 자신의 진행 저장에 기록한다. 실패/재시작 시 같은 ID로 다시 호출한다. 화면의 `PatientVisitController.CurrentSessionId`는 애니메이션/방문 식별자이며 AI 회차 ID를 대체하지 않는다.

| 공개 함수 | 입력 | 결과와 시점 |
|---|---|---|
| `adapter.StartNewCase(key)` | 새 사례 ID와 첫 회차 ID | 빈 상태를 원자적으로 저장. 같은 ID 재호출은 기존 첫 회차를 재사용하며 덮어쓰지 않음 |
| `adapter.ResumeSession(key)` | 저장된 사례·현재 회차 ID | 생성 없이 같은 회차/미완료 요청을 복원. 저장 없으면 false/오류 |
| `adapter.StartRevisit(previousKey, newSessionId)` | 이전 회차 전체 키와 새 회차 ID | 종료·평가된 미클리어 사례에서만 요청을 저장. 기억·공개 정보·원문·평가 보존 |
| `adapter.CanChangeConversation` | 없음 | 요청·저장·초안 처리가 끝나 안전하게 선택을 바꿀 수 있는지 |
| `adapter.SubmitText(text)` | 원문 그대로의 서술형 입력 | 요청을 먼저 저장한 뒤 전송. 한글 IME 조합 중에는 호출하지 않음 |
| `adapter.RequestEnd/ConfirmEnd/CancelEnd` | 기존 종료 확인 UI | 사용자의 종료 의도를 저장하고, 진행 중 답변 저장 후 종료로 연결 |
| `adapter.Retry()` | 없음 | 받은 응답의 저장부터 재시도, 필요할 때만 같은 미완료 요청을 재전송 |

`StartNewCase/ResumeSession/StartRevisit`는 bool을 반환한다. false이면 환자를 입장시키거나 진행을 완료 처리하지 말고 `LastError`/`ServiceStatus`를 확인한다. 현재 다른 환자가 상담 중이면 먼저 B가 방문을 정리한다. **선택 API를 성공시킨 다음** 기존 B의 입장 동작을 호출하면 `PatientArrived`에서 저장된 세션으로 자동 연결된다. 같은 환자가 착석한 상태에서는 선택 성공 직후 새 흐름을 진행할 수도 있다. 선택은 AI 저장만 소유하며 환자 순서를 결정하지 않는다.

```csharp
// B 영역 예시: 실제 진행 ID와 사례/회차 ID를 B 저장에 먼저 확정한다.
string patientId = PatientAiAdapter.PatientCatalog.Get(selectedPatientId).Id;
var key = new AiConversationKey(gameProgressId, patientId, caseId, sessionId);
bool accepted = adapter.StartNewCase(key);  // 새 사례
// 재실행: adapter.ResumeSession(key)
// 재상담: adapter.StartRevisit(key, persistedNextSessionId)
// accepted == true일 때만 B의 입장 흐름을 시작한다.
```

네트워크 없는 저장 작업에는 `PatientAiAdapter.CreateCaseRepository()`가 반환하는 `AiCaseRepository`를 사용할 수 있다. `StartNewCase/Resume/StartRevisit`가 `AiSession`을 반환하며 같은 계약을 적용한다. 실제 Unity 화면/네트워크를 실행할 때는 어댑터 연결을 사용한다. 저장소 API와 어댑터를 동시에 같은 사례의 쓰기 소유자로 사용하지 않는다.

기존 화면이 새 API를 아직 호출하지 않으면 환자별 `legacy` 독립 상담은 계속 작동한다. 그 기록은 새 게임 진행으로 자동 이관하지 않고 **정산용 평가를 발행하지 않는다**. 이전 `Revisit()`/`NewExperiment()`는 legacy 시연용이며 진행이 지정된 게임에서는 false를 반환한다. B는 명시적 키를 받는 새 API를 사용한다.

## 상태·오류·복원

`StateChanged`를 구독하고 `ServiceStatus`를 읽는다. 문자열 대사를 분석해서 완료 여부를 추측하지 않는다.

| `AiServicePhase` | 의미 |
|---|---|
| `Idle` | 상담 선택 전 |
| `PreparingOpening` | 첫 발화 또는 재상담 첫 발화 준비 |
| `Ready` | 대화 입력 가능 단계; 실제 버튼 활성화는 `CanEditDraft/CanSubmit` 확인 |
| `GeneratingReply` | 저장한 사용자 발언에 대한 응답 요청 |
| `Saving` | 요청/답변/원문/초안/종료 의도를 저장 중 또는 저장 재시도 필요 |
| `Closing` | 답변 뒤 사용자 종료 요청 처리 |
| `Evaluating` | 종료 원문 저장 이후 평가 요청 |
| `SavingEvaluation` | 평가 응답 검증 이후 원문·상태 저장 |
| `Completed` | AI 회차 저장 완료. B/C의 게임 정산 완료를 뜻하지 않음 |
| `Error` | 연결/검증/저장 실패. 완료/0점으로 표시하지 않음 |

상태는 `Key`, `PendingAction`, `CanRetry`, `Error`, `HasSavedEvaluation`을 포함한다. 재상담 요청 중 `ServiceStatus.Key`는 새 목표 회차, `ConversationKey`와 원문은 마지막으로 저장된 현재 회차다. 원자적 저장은 동기식이므로 `Saving*` 알림을 받을 수 있어도 화면 프레임에 보일 시간은 짧을 수 있다. 실제 시간의 퍼센트나 남은 초를 꾸미지 않는다.

요청 ID·본문을 전송 전에 저장한다. 네트워크 끊김·평가 실패는 같은 요청을 유지한다. 응답을 받았으나 저장 실패이면 `AiSession`이 응답을 보관하고 `RetrySave`로 디스크 쓰기만 재시도한다. 재시도 전에 추가 발언/재상담은 금지한다. 앱 자체가 강제 종료되면 저장된 요청을 재전송할 수 있다. 서버는 DB가 없으므로 아직 저장하지 못한 요청을 재추론할 수 있지만 클라이언트는 한 응답만 확정하여 원문·회차 중복을 막는다. 종료 의도는 대기 중 답변과 별도로 저장하여 답변 확정 후 자동 종료를 유지한다.

## 평가 인계 순서

`SavedEvaluationAvailable` 이벤트 또는 `TryGetSavedEvaluation(out result)`는 아래 조건을 모두 만족하는 `AiSavedEvaluation`만 제공한다.

- 원문 ID·발언자·회차·정확한 인용·8개 평가 항목·점수 합산·두 축 각각 70점 판정을 검증했다.
- AI 원문과 상태를 같은 저장 버전으로 작성하고 현재 스냅샷 포인터 교체까지 완료했다.
- 해당 흐름에 대기 중 저장/요청이 없고 게임 진행 ID가 지정되었다.
- 모델 모드를 확인한 응답만 누적한 기록이며 평가 출처도 `model`이다. 모의 응답과 출처 미확인 기존 기록은 이 경로에 나오지 않는다.

결과의 `Key`, `PatientScore`, `CounselorScore`, `Cleared`, `EvaluationJson`, `SourceRevision`을 읽을 수 있다. SourceRevision은 읽은 AI 스냅샷 버전이며 중복 정산 키가 아니다. 같은 결과를 재실행/재구독으로 다시 받을 수 있으므로 B는 전체 `Key.SettlementId`로 중복 정산을 막는다.

**A 검증·AI 저장 → B 진행·회차 확인 → C 보상 계산 → B 진행·보상·사진·다음 환자 상태 저장 → D 확정 표시** 순서를 지킨다. `Cleared=true`는 이 회차의 두 평가 축 기준 충족이며 전체 게임 클리어, 코인 지급, 사진 해금 완료가 아니다. `EvaluationCommitted(string)`과 `EvaluationJson`은 기존 기록 표시용이다. 정산에는 사용하지 않는다. 최초 클리어 보너스는 진행 ID+환자 ID, 사진 해금은 진행 ID+사진 ID로 B/C가 별도 관리한다.

B가 정산 저장에 실패했으면 AI 평가를 다시 생성하지 말고 `repository.TryReadSavedEvaluation(key, out result)`로 동일 결과를 읽어 B 저장을 재시도한다. `FinishVisit()`은 기존 수동 퇴장 동작을 유지하며 자동 퇴장·다음 환자 결정·보상 처리를 추가하지 않았다.

## 읽기와 저장 경로

- `adapter.ReadCommittedRecord()` / `repository.Read(key)`: 저장된 원문·공개 정보의 읽기 전용 `AiCaseRecord`.
- `record.SessionIds`, `record.SessionTranscript(sessionId)`: 특정 회차의 원문. 사례 전체 원문은 `Transcript`.
- 각 발언의 `Key`, `MessageId`, `Position`(사례 전체 0부터 시작), `Speaker`, `Kind`, `Text`로 근거 위치를 표시한다. `QuoteOffsets(quote)`는 원문에서 대소문자·공백을 그대로 비교한 UTF-16 시작 위치이며, 없으면 빈 목록을 반환한다. 반복 인용은 모든 위치를 반환한다.
- `Disclosures`: 사실 ID와 `Undisclosed/Candidate/Confirmed`. 사실의 Text는 Confirmed일 때만 제공한다. 현재 시연의 정책은 검증된 공개 후보를 수용한다. 서버 후보와 클라이언트 확정은 계속 별도 필드다.
- `repository.ListCases(progressId, patientId)`는 해당 진행·환자의 현재 사례 키만 반환한다. 완료된 과거 회차의 평가는 `TryReadSavedEvaluation`에 해당 회차 키로 조회한다.

Unity 새 경로: `Application.persistentDataPath/ai-saves/progress/<progress>/patients/<patient>/cases/<case>/`. 사례 안의 상태/원문에는 모든 회차가 각각의 회차 ID로 보존되므로 누적 기억·평가의 근거를 잃지 않는다. 원문에는 진행 ID·환자 ID·사례 ID·revision과 회차별 기록이 함께 들어간다. 상태/원문 쌍을 불변 스냅샷에 먼저 쓰고 `current.json`을 마지막에 원자 교체한다. 경로와 파일의 진행·환자·사례가 다르면 읽기/쓰기를 거부한다.

Python `SaveStore(directory, patient_id, game_progress_id)`도 진행·환자별 경로와 사례 인덱스를 사용한다. 기존 진행 ID 없는 Unity/Python 저장은 기존 위치에서 보존한다. 새 게임과 이전 시연 기록 사이에 자동 복사/환산은 하지 않는다.

CLI에서는 `python tools/chat.py --patient harin --game-progress-id test_game`으로 같은 진행 범위를 명시할 수 있다. `/new`도 지정한 진행 안에 새 사례를 만들며 `/cases`, `/open`은 그 진행·환자의 사례만 조회한다.

## 다른 담당자의 연결 작업

| 담당 / 대상 파일 | 필요한 작업·입출력 | 이유 |
|---|---|---|
| B: 게임 진행 저장 모듈, `PatientVisitController`, 시작/이어하기 호출부 | 방문 전에 작업 종류와 전체 키 저장 → A의 선택 API 성공 확인 → 입장. 정산 시 전체 키 검증·중복 방지 | 현재 화면 방문 ID만으로 게임 진행과 AI 사례를 식별할 수 없음 |
| B: `PatientVisitController` 및 환자 방문 목록/공용 씬·프리팹 | 민석 제거, 최종 5명 순서·재상담 선택 반영 | A는 순서/공용 화면 소유자가 아님 |
| B/C: 진행·보상 모듈 | `AiSavedEvaluation` → 회차 확인 → 보상 계산 → 진행·보상·사진·다음 환자 상태의 저장 | A 평가 이벤트 자체로 코인/사진/전체 클리어 처리 금지 |
| D: 결과/기록/로딩 화면 | `ServiceStatus`, `AiCaseRecord` 사용. 확정 결과는 B 저장 완료 알림에서만 표시 | 저장 실패·평가 대기를 완료로 보이지 않게 함 |

보상 수치, 가격, 승급 기준, 새 사연/엔딩은 이번 A 범위에서 정하거나 추가하지 않았다. 새 공용 게임 DTO나 보상 서비스를 따로 만들지 않았으며 A 조회/평가 타입만 Networking에 제공했다.

## 검증 및 Unity 재현

서버와 정적 검사 실행:

```bash
conda activate game-ai
PYTHONPATH=server python -m pytest server/tests -q
PYTHONPATH=server python tools/export_contracts.py --check
dotnet run --project tools/unity-client-checks/UnityClientChecks.csproj -- .
dotnet build tools/unity-client-checks/AdapterCompileChecks.csproj --no-restore
```

C# 실제 API 검사에는 세 번째 인자로 `seoyun,harin,dohyeon,taejun,yeonwoo`를 지정할 수 있다. 예: `dotnet run --project tools/unity-client-checks/UnityClientChecks.csproj -- . <로컬연결JSON> seoyun,harin,dohyeon,taejun,yeonwoo`. 필요하면 `GAMSAMO_AI_CHECK_REPORT_DIR`를 Git 제외 로컬 디렉터리로 지정해 합성 검증 원문을 보존한다. 실제 사용자 저장은 건드리지 않는다. 모의 서버 검사는 모델 품질 검증으로 기록하지 않는다.

Unity 확인 절차(B/D 담당 호스트):

1. PR을 반영하고 Unity 정식 컴파일을 확인한다. 공유 씬 생성 도구는 실행하지 않는다. 로컬 연결 JSON을 가져오고 `/health`의 `mode=model`, `game_progress_scope`, 목록 5명을 확인한다.
2. B 저장에 진행 A·서윤·사례 A·회차 1 키를 확정하고 `StartNewCase` 성공 후 입장한다. 첫 발화 준비 → 입력 → 답변/저장 상태를 확인한다. 여섯 번째 질문 이후에도 사용자가 종료할 때까지 계속 대화한다.
3. 같은 진행의 하린 사례에 다른 키로 연결해 대화하고 서윤 키로 `ResumeSession`한다. 서윤의 원문/공개 정보와 회차가 그대로이고 하린의 기록이 섞이지 않아야 한다. B의 허용된 전환 시점에서 진행한다.
4. API 연결을 일시 끊고 발언/평가를 요청한다. 오류를 완료나 0점으로 표시하지 않고 연결 복원 뒤 같은 요청을 한 번만 저장해야 한다. 답변 대기 중 종료 요청도 재시작 후 유지한다.
5. 사용자 종료 후 고정 문구 `상담은 여기까지 하겠습니다. 오늘 수고하셨습니다!`를 확인한다. 평가 전/저장 실패 시 `SavedEvaluationAvailable`이 없고 B 정산이 실행되지 않아야 한다.
6. 테스트용 저장 위치에 쓰기 실패를 유도해 저장 재시도와 평가 전달 차단을 확인한다. 원래 사용자 저장을 삭제/편집하지 않는다. 쓰기 복구 후 받은 응답을 재추론하지 않고 저장하는지 확인한다.
7. 미클리어 평가 저장 후 B가 새 회차 ID를 저장하고 `StartRevisit`한다. 첫 회차 원문·평가·기억·확정 정보가 남고 새 회차만 하나 생겨야 한다. 첫 발화 대기 중 종료/재실행해도 두 번 생성하지 않는다. `ResumeSession`은 회차 수를 늘리지 않아야 한다.
8. 진행 B의 새 사례를 시작해 같은 환자의 진행 A 기억이 넘어오지 않는지 확인한다. 두 점수와 네 ID를 B/C에 전달하고 B 저장 실패 시 동일 키로 정산만 재시도한다. D는 B 저장 완료 뒤에만 확정 결과를 표시한다.
9. 나머지 도현·태준·연우도 첫 발화/자유 입력/사용자 종료/평가를 확인한다. 기존 기록부의 인용을 `MessageId`와 `QuoteOffsets`로 원문과 대조한다. 수동 내보내기 버튼을 누르기 전 자동 퇴장하지 않아야 한다.

### A 작업 기록

구현·검증 완료 시각: **2026-11-06 05:08:44 KST (UTC+09:00)**. 작업 브랜치 `codex/a-ai-service-lifecycle`, 기준 `main/a33c40c`. PR에서는 개인 장비/경로/접속 정보·측정 로그·합성 검증 원문을 제외한다.

주요 변경: `AiCaseRepository`, `AiConversationKey`, `AiCaseRecord`, `AiSavedEvaluation`, `AiServiceStatus` 추가; 기존 `AiSession/AiSaveStore/AiProtocol/PatientAiAdapter`에 연결; Python 상태/저장에 진행 ID 추가; 단일 환자 목록과 `/v1/patients`; 진행별 계약 예제·CLI·평가 도구 확장. 재상담에서는 설정에 있는 사건과 실제 이전 대화에서 말한 사건을 구분하도록 지침을 보완했다. OpenAPI 생성기가 날짜/seconds 스키마 객체까지 예제 값으로 치환하던 기존 오류도 수정했다.

| 구분 | 실행 결과 | 범위와 한계 |
|---|---|---|
| Python 단위/API 대역 검사 | **118개 통과** | 5인 콘텐츠, 진행·환자·사례 격리, 요청/평가/저장 복구, 정확한 근거, 69/70 경계 등 기존 회귀와 추가 검사 |
| C# 계약·저장 검사 | **249개 통과** | 두 환자 교대와 두 진행의 동일 사례 ID, 새 사례/이어하기/재상담, 저장 실패 중 평가 전달 차단, 모의/출처 미확인 배제, 늦은 응답·저장 콜백 재진입, 읽기 전용 원문과 UTF-16 위치 |
| C# 어댑터 정적 컴파일 | 오류 0·경고 0 | Unity API 대역 사용. 실제 Editor 컴파일/Play 검증이 아님. 최신 main의 수동 퇴장 버튼 호출에 맞춰 대역만 보완 |
| API 계약 | 생성 결과 일치, OpenAPI 모델 검증 통과 | 실제 서버 스키마와 합성 예제. 게임용 대체 응답이 아님 |
| 실제 모델 · C# HTTP/저장 | **5명 모두 통과** | 각 1회차의 첫 발화·1개 서술형 발언·사용자 종료·복원·두 축 평가·원문 근거·새 재상담·모델 평가 전달. 짧은 표본이며 충분한 자연스러움/전체 클리어 시험은 아님 |
| 실제 모델 · 지침 보완 후 추가 검사 | **하린·연우 통과** | 각 2개 서술형 발언·복원·종료·평가·재상담 저장. 하린의 요리/대표 역할 정보, 연우의 친구 반응/아이 부담 정보가 해당 공개 조건에서 확정되고 재상담에 보존됨 |
| Unity Editor/Play · 팀원 PC | **미실행** | 해당 호스트에서 위 절차로 확인 필요 |
| B/C/D 진행·보상·결과 화면 통합 | **미연결** | 다른 담당자 소유. 가짜 서비스/결과를 실제 게임 경로에 추가하지 않음 |

실제 원문 검토에서 일부 첫 발화/답변 반복과 조사·문장 어색함을 확인했다. 연우의 재상담이 이전에 말하지 않은 자전거 사건을 이미 나눈 이야기로 표현한 표본에 대해 지침을 보완했고, 추가 표본에서는 새 이야기로 표현했다. 이 지침 보완 후 전체 5인 실험을 다시 수행한 것은 아니며 하린·연우를 추가 확인했다. 장기 대화의 의미적 기억 정확도와 한국어 품질은 추가 사람 검토가 필요하다. 긴 기록 요약/원문 보존은 단위 회귀로 확인했으며 이번 실제 모델 검사는 짧은 회차에 한정한다.

기존 공개 API·모델은 계속 실행 중이며 **이번 변경의 진행별 연결은 아직 공개 API에 배포하지 않았다**. 별도의 로컬 검증 API로 실제 모델을 재사용했고 검증 후 그 임시 API를 종료했다. 병합 뒤 서버 실행 버전을 갱신하고 `/health.capabilities`에 `game_progress_scope`가 있는지 확인해야 한다. 기존 공개 API에 새 진행 연결을 시도하면 지원 검사에서 중단되는 것이 정상이다. 이번 작업에서 공개 주소/접근 키를 새로 게시하지 않았다.

다음 순서: B의 진행 ID/사례/회차 선택·저장 연결과 민석 방문 목록 제거 → B/C의 중복 없는 정산·상태 저장 → D 확정 표시 연결 → 서버 버전 갱신 후 팀원 Unity 검증. 최종 병합은 사용자가 결정한다.
