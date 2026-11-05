# B 공통 진행·저장 계약 v1

기준: main a33c40c, PDF 10~16쪽, 2026-11-06 사용자 B 요청. 다른 담당 파일을 수정하지 않는다. 이 계약과 GameProgress.cs는 기반 커밋으로 먼저 분리한다. 기존 AI format 4와 AiSaveStore를 사용하며 AI DTO·점수 계산기를 복제하지 않는다.

| 제공자 → 사용자 | 계약 | 규칙 |
| --- | --- | --- |
| B → A/C/D | GameProgress, PatientProgress, RoundIdentity | 진행/환자/사례/회차 ID. State는 복사본. 현재 순서와 distinct 클리어 인원은 B 소유 |
| A → B/C | SavedEvaluation | 검증 및 AI 저장이 완료된 evaluated 상태, 두 점수 0~100. B는 두 축 각각 70 이상 판정 |
| A → B/D | 기존 PatientAiAdapter.CurrentPhase, IsBusy, LastError, CaseId, SessionId; AiSaveStore snapshot | active/closing/pending_evaluation/evaluated와 미완료 요청·초안은 A 소유. B가 새 AI 응답을 생성하지 않음 |
| C → B | IRewardCalculator.Calculate(evaluation, previousEconomy, firstClear) → RewardCalculation | 순수 계산. 입력은 복사본. SettlementKey 일치, Granted 비음수, 새 Balance=이전+Granted. 저장/표시 부작용 금지 |
| C ↔ B | EconomyState; ProgressService.CommitEconomy(economy, expectedRevision) | 잔액/최고 보상/최초 보너스/보유/장착을 진행과 함께 원자 저장. C가 상품·경제 유효성 검증. 독자 지갑 파일 금지 |
| B → D | ConfirmedResult, PendingPresentation, Photos, PlayedEndings, FinalEndingUnlocked/Played | 저장 성공 후에만 제공. 결과 수신 자체가 표시 완료는 아님 |
| D → B | CompletePresentation(settlementKey), CompleteFinalEnding() | 연출 종료/건너뛰기 확인을 저장. 재생으로 보상 재지급 금지 |
| B → D/A | ConsultationRoomController.AcquireInputBlock() → IDisposable | 중첩 가능. Dispose는 멱등. 기존 기록부/PC 모달 상태를 지우지 않음 |

## 식별자와 처리 순서

- 정산 키: JSON 문자열 배열 `[progressId, patientId, caseId, roundId]` (`RoundIdentity.Key`). 구분자 충돌 방지.
- 최초 보너스 키: `[progressId, patientId]` (`FirstClearKey`). C는 Economy.FirstClearBonuses에 이 키를 기록한다.
- 사진: 진행별 Photos 목록에 photoId 한 번. 실질 식별자는 progressId+photoId.
- 요청 번호/응답 revision은 정산 식별자가 아니다. 파일 Revision은 동시 저장 충돌 감지에만 사용한다.
- A 검증·저장 → B 환자/사례/회차 확인 → C 계산 → B 단일 저장 → D 표시.
- 실패 시 진행·지갑·사진을 메모리에도 확정하지 않는다. 같은 평가로 재시도한다. 결과는 저장 성공 이후 반환한다.

## 상태 전이

Waiting → Consulting → Evaluating → AwaitingSettlement → (클리어: Presenting → AwaitingDeparture / 미달: AwaitingDeparture) → Waiting 또는 Finished.

미달은 순서를 유지하고 NeedsRevisit를 저장한다. 다음 문 클릭에서 A.Revisit로 새 회차를 요청한다. 이전 회차 ID가 그대로면 새 상담으로 확정하지 않는다. 클리어는 저장된 Current.Cleared로 다음 대상을 예약하고, 명시적 퇴장 시 CurrentIndex를 옮긴다. 재시작 시 Presenting도 그대로 복원한다.

## 실제 씬 연결

PatientVisitController.Start가 GameProgressRuntime을 자동 부착한다. 기존 A의 PatientAiBootstrap과 함께 동작하며 씬/프리팹 재생성은 필요 없다.

C/D 연결 컴포넌트는 GameProgressRuntime.Progress가 준비된 뒤 아래 순서로 연결한다(초기화 순서를 가정하지 말고 코루틴으로 기다릴 것).

```csharp
runtime.Rewards = yourApprovedRewardCalculator;       // C
runtime.PhotoForPatient = patientId => catalog.PhotoId(patientId); // D: 정식 ID
runtime.ResultCommitted += receipt => presenter.Show(receipt);    // D
runtime.RetryProgress(); // 이미 저장된 평가도 정산/표시 재개
// 표시 완료 또는 건너뛰기:
runtime.CompletePresentation(receipt.Identity.Key);
```

D는 늦게 연결되거나 다시 열 때 `runtime.PendingResult`를 읽는다. 서비스의 FinalEndingUnlocked는 서로 다른 정식 roster 6명이 모두 클리어일 때만 true다. 자격증은 `Certificate(approvedThresholds)`로 승인된 설정만 전달한다. 기본 승급 기준은 설치하지 않았다. 보상·사진 공급자가 없으면 정산 대기, D 연출이 없으면 Presenting을 유지한다.

## A에게 필요한 연결 개선(요청안, A 파일 수정 없음)

1. `PatientAiAdapter`/`AiSaveStore`: 진행 ID 또는 저장 경로를 주입하는 세션 열기 API. 입력 progressId/patientId/caseId/roundId, 출력 기존 AiSession 또는 실패. 현재 B는 기존 단일 환자 저장 포인터를 공개 Save API로 선택하고, B에 저장한 A 스냅샷을 복원한다. 이전 immutable snapshots는 삭제하지 않는다. 동시 여러 게임 실행은 금지하고 충돌은 revision으로 거부한다.
2. `PatientAiAdapter`: B 승인 없이 공개 Revisit/NewExperiment를 호출하지 않도록 연결. 현재 UI는 이 API들을 직접 노출하지 않으며 B가 다음 문 입장에서 Revisit를 호출한다. 향후 버튼 연결 시 우회하지 말 것.
3. `AiSaveStore`: 확정된 과거 사례/회차 목록을 읽는 read-only API. 현재 B 진행에 연결된 모든 진행/환자/회차는 입장 없이 복원하지만, B 도입 이전 snapshot 폴더의 모든 파일이 실제 commit됐는지는 포인터만으로 증명할 수 없다. 기존 원문은 보존하고 이를 임의로 확정 이력으로 가져오지 않는다.
4. `PatientAiAdapter`: 초안 flush 성공/실패를 반환하는 API가 있으면 즉시 복귀 UX 개선 가능. 현재 ReturnToStartMenu는 busy/retry 상태를 차단하고, ai.Draft가 실제 저장 snapshot의 draft와 같을 때만 B 저장 후 이동한다. 아직 저장되지 않은 초안은 기존 0.75초 debounce를 기다린 뒤 재시도한다.
5. 민석 정식 데이터/평가 기준 및 UsesPatient 지원. 현재 roster의 여섯 번째 종류 중 minseok은 미지원이므로 입장을 차단한다.

보상 공식/가격은 C, 사연/평가 기준은 A, 사진·엔딩 자산은 D가 제공한다. B가 승인으로 간주하거나 만들어 넣지 않는다.
