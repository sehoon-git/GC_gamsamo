# 팀원 C · 상담 보상, 지갑, 치장 상점

기준은 2026-11-06의 `main/a33c40c`와 작업분담 PDF 10~16쪽이다. PDF 기준 이후 추가된 환자 내보내기는 정산 신호로 사용하지 않는다. 소유권은 C의 경제 모듈·상점·외형·PC 표시·전용 검사까지다.

## 적용 범위

기존 6분류 × 8테마 카탈로그의 ID·순서·썸네일·장착 이미지를 유지했다. 분류 ID는 `window`, `door`, `clipboard`, `pc`, `wallpaper`(PC), `room-wallpaper`(상담실)이다. 테마 ID는 `oak/walnut/sage/lavender/terracotta/midnight/brass/white`이며 상품 키는 `분류.테마`다. 창문 날씨·블라인드와 서류철 확대 이미지도 기존 적용 경로를 사용한다.

| 기준 코드의 상태 | 이번 작업 |
|---|---|
| 구현됨 · 카탈로그와 PC 탭·카드·외형 미리보기 | 기존 카탈로그와 카드를 재사용 |
| 일부 구현 · 세션에만 남는 외형 선택 | 임시 미리보기와 확정 장착 분리. 취소·닫기·탭 이동 때 확정 장착 복원 |
| 미구현 · 보상, 지갑, 승인 가격, 구매/소유/장착 저장 | C 상태·계산·저장 계약·UI 구현. 실제 전체 게임 저장 어댑터는 B 인계 |

신규 경제 코드는 `Assets/_Project/Scripts/Economy/`에 있다. 신규 지갑 파일 경로나 PlayerPrefs 키를 만들지 않는다. 기존 `AiSaveStore`는 환자별 AI 복원용이다. 작업 당시 `Save/`, `Progression/`에는 게임 전체 저장 구현이 없었다.

## A → B → C → B → D 연결

1. A의 `PatientAiAdapter.EvaluationCommitted` 또는 저장된 완료 평가 복원 후, B가 검증된 평가와 `PatientId`, `CaseId`, `SessionId`를 확보한다. B는 활성 게임 진행 ID와 환자·사례·회차가 실제로 일치하는지 확인한다.
2. B가 자신의 전체 저장본·리비전을 읽고 `RewardSettlement.Plan(committedEconomy, policy, runId, patientId, caseId, sessionId, patientScore, counselorScore, cleared, aiEvaluationSaved, progressionConfirmed)`을 호출한다. A가 확정한 두 정수 점수 및 클리어 값을 넘긴다. C는 평가나 클리어를 생성하지 않는다.
3. `Proposed`이면 B가 전체 저장의 예상 리비전을 확인해 `ProposedState`, 회차 진행, 사진 해금(진행 ID + 사진 ID), 다음 환자를 **단일 커밋**으로 저장한다. 예정 금액을 UI에 발행하지 않는다. 실패하면 제안을 버리고, 충돌이면 최신 전체 저장을 다시 읽어 처음부터 확인한다.
4. `AlreadySettled`는 추가 지급 0이다. 같은 키의 점수·클리어가 다른 `ConflictingEvaluation`은 불일치 조사 대상으로 남긴다. 평가 대기·미확정 회차·미승인 정책·손상 저장은 정산 완료로 처리하지 않는다. 0C인 새 회차도 성공 시 원장에 한 번 기록한다.
5. B 저장 성공 뒤 `wallet.Reload()`가 성공하면 D가 PC의 `TryGetWalletTotals(out balance, out totalEarned)`와 `TryGetSettledReward(patientId, caseId, sessionId, out baseCoins, out bonusCoins)`를 읽는다. 후자는 조회한 회차가 실제 확정됐을 때만 true다. 재열람 시 지급 코드를 호출할 필요가 없다.

C의 `EconomyState` 한 개를 B 저장에 포함한다. 회차 키는 상태의 runId와 원장의 patientId/caseId/sessionId를 각각 비교한다. 환자별 최고 기준 및 최초 보너스 여부는 해당 run의 원장에서 `HighestPaidBaseFor`와 `HasFirstClearBonusFor`로 조회한다. 구매 영수증에는 구매 당시 가격을 보관하므로 이후 가격 변경도 이미 지불한 코인에 영향을 주지 않는다.

## B가 제공할 저장 계약

- 대상은 B의 전체 저장 서비스와 상담실 초기화 코드다. `IEconomyStore.TryReadEconomy(runId, out committed, out revision)`는 활성 진행의 확정된 슬라이스 및 **전체 저장**의 비음수 리비전을 반환한다. 다른 run이거나 읽을 수 없으면 false다.
- `TryCommitShopChange(runId, expectedRevision, proposed)`는 활성 run과 리비전을 비교한 뒤 B의 나머지 상태를 함께 보존하며 경제 슬라이스를 원자적으로 교체한다. 디스크 저장 성공 뒤에만 `Committed`를 반환한다. `Conflict`·`Failed`는 기존 저장을 보존한다. 읽기 및 커밋은 Unity 메인 스레드에서 사용할 수 있어야 한다. 리비전은 동시 쓰기 제어용이며 회차 정산 키와 독립적이다.
- B가 새 게임을 만들 때 `EconomyState.NewRun(runId)`를 함께 저장한다. 경제 기능 도입 전 저장은 B가 스키마 버전으로 구별해 처음 한 번만 빈 슬라이스를 추가한다. 이미 존재하는 원장이 유효하지 않으면 복원 오류로 처리하며 자동으로 0C로 덮어쓰지 않는다.
- B가 상담실에서 `new EconomyWallet(runId, wholeGameStore, cosmeticCatalog)` → `wallet.Reload()` → `pcDesktop.BindEconomy(wallet)` 순서로 연결한다. 상담 정산 또는 B 측 저장 변경 후 다시 `Reload()`한다. 게임을 닫거나 슬롯을 교체할 때 `BindEconomy(null)`로 이전 run을 해제한다. 공용 씬·루트 프리팹 변경이 필요한 경우 B가 이 연결만 추가한다.
- 상점 UI는 두 버튼으로 구매와 구매 확정을 분리하고 `Purchase(itemId, quotedPrice)`, `Equip(itemId)`, `Unequip(categoryId)`를 호출한다. 가격 변경·이미 보유·잔액 부족·중복 클릭·저장 실패는 확정 구매로 표시하지 않는다. 돈·영수증·보유권을 같은 제안 상태에 넣는다. 구매 후 장착은 별도 동작이다.

## 출시 보상과 상품 가격

보상과 48개 상품 가격은 2026-11-06 사용자 위임에 따라 아래 수치로 정했다. B는 `RewardPolicy.ForRelease()`가 반환한 설정을 `RewardSettlement.Plan`에 넘긴다. 시작 잔액은 0C이며 두 점수의 합 1점당 1C다. 같은 환자의 이전 최고 지급 기준을 초과한 차액만 지급한다. 진행별·환자별 최초 클리어 보너스는 100C다.

`Assets/_Project/Settings/CosmeticCatalog.asset`의 `coinPrices`가 48개 상품의 판매 기준이다. 아래 숫자의 단위는 C다.

| 분류 | 오크 | 월넛 | 세이지 | 라벤더 | 테라코타 | 미드나이트 | 브라스 | 화이트 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 창문 | 50 | 65 | 55 | 55 | 60 | 75 | 80 | 50 |
| 문 | 40 | 55 | 45 | 45 | 50 | 65 | 70 | 40 |
| 서류철 | 20 | 30 | 25 | 25 | 30 | 40 | 45 | 20 |
| PC | 55 | 70 | 60 | 60 | 65 | 80 | 85 | 55 |
| PC 바탕화면 | 20 | 30 | 25 | 25 | 30 | 40 | 45 | 20 |
| 상담실 벽지 | 30 | 40 | 35 | 35 | 40 | 50 | 55 | 30 |

예를 들어 첫 상담에서 40/40으로 번 80C로 오크 창문(50C)과 세이지 서류철(25C)을 사고 5C를 남길 수 있다. 가격표는 게임 코인으로 직접 정했으며 기존 아트 카탈로그를 재임포트해도 보존된다.

## 검증 기록 및 Unity 재현

2026-11-06 05:10:32 KST (UTC+09:00) 기준. 출시 정책의 정산 예시는 80C, 추가 30C, 추가 130C(기본 30C + 첫 보너스 100C)로 누적 240C다.

- 저장소 루트에서 `& ./tools/economy-checks/Run.ps1`. Unity 번들 컴파일러로 엔진·UI에서 분리된 검사를 빌드했다. 보상 55건, 지갑 33건, 별도 프로세스의 복원 4건 통과. B의 저장 계약 대역은 점수·클리어·다음 환자·사진도 포함하는 임시 전체 저장을 사용한다. 재조회·재구매·동시 수정·저장 예외·가격 변경·저장 실패 후 재시도 포함.
- 48개 원본 상품 ID와 승인 코인 가격 6분류 × 8테마를 대조했다. Unity 6000.3.28f1 격리 프로젝트의 가격 검사에서 48종 승인 로그와 종료 코드 0을 확인했다. 검사에는 누락·중복·미승인 가격을 차단하는 시나리오도 포함한다.
- Unity 6000.3.28f1에서 `Gamsamo > Check Cosmetic Coin Prices` 실행. 현재 정상 결과는 `All 48 cosmetic items have an approved coin price.`다. 중복·누락·잘못된 승인 금액은 오류로 확인한다.
- `Gamsamo > Run Economy Shop UI Checks` 실행. Editor 전용 지갑·가격으로 임시 UI를 띄워 실제 버튼으로 미리보기 취소, 가격 재확인, 구매 실패/성공, 연속 입력, 장착·해제와 재바인딩을 검사한다. Console의 `PASS EconomyShopUiChecks`를 확인한다. 실제 게임의 지갑과 상품 가격에는 샘플을 넣지 않는다.
- 상담실 씬을 열어 Play → PC → 상점 → 상품 선택 → 미리보기 취소 또는 닫기. B 연결 전에도 카드에서 확정 가격과 외형을 볼 수 있다. B 저장 연결 후에는 구매 확인 → 장착 → 재실행하여 동일 슬롯의 잔액·장착 확인, B 쓰기 실패 시 이전 상태 확인 순서로 다시 검사한다.
- 격리 프로젝트의 실제 Unity Play Mode에서 `PASS EconomyShopUiChecks`와 종료 코드 0을 확인했다. 상점은 지갑 연결 전에 승인 가격을 표시하고 결제는 비활성화했다. 실제 서버 상담과 B의 전체 저장소를 거치는 종단 간 검증은 B 연결 후 진행한다.
