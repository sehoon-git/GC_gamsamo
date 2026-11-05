# B 작업 기록 — 순차 진행·재상담·공용 저장

기록 시각: 2026-11-06 04:32:38 KST (UTC+09:00).

## 기준과 범위

실제 저장소: `C:/Users/test/Desktop/GC_gamsamo/GC_gamsamo`. 처음 브랜치는 `codex/happy-ending-credits`, 미커밋 변경 없음. 추적 브랜치를 ff-only pull한 뒤 최신 origin/main `a33c40c`에서 `codex/b-progression-save` 생성. 루트 AGENTS.md 적용. 사용자의 담당별 문서 분리 지시를 우선해 공통 plan01은 수정하지 않았다. PDF 10~16쪽 담당/연결/소유권과 앞부분 B 상세 요구를 확인했다.

기존 구현: 문 입장/명시적 퇴장, 모달 차단, A의 format 4 검증·원문·미완료 요청 저장.
부분 구현: 실행 중 기록 목록과 시작 화면 진입. CompletedCount는 퇴장 횟수였음.
미구현이었던 B 기능: 진행 ID, 순차 재상담/클리어, 공용 경제·사진·엔딩 저장, 이어하기.

## 변경

- `Progression/GameProgress.cs`: 공용 DTO/인터페이스. 기반 커밋 `aa400a9`로 분리. 계약 상세는 B-progress-contract.md.
- `Progression/ProgressService.cs`: 현재 환자·사례·회차 검증, 두 축 70점 클리어, 고유 회차 정산, 원자 상태 전이, 재상담 의도, distinct 6명 엔딩. 설정 없는 자격증은 임의 승급하지 않는다.
- `Save/GameProgressStore.cs`: revision 충돌 검사, 임시 파일 flush 후 atomic replace, 이전 정상본 .bak 보존. 손상 시 throw하며 새 저장으로 덮어쓰지 않는다.
- `Progression/GameProgressRuntime.cs`: 기존 A 공개 API를 공용 진행에 연결, 저장된 A 스냅샷 복원/최신 저장 재조정, mock 평가 거부, C/D 연결 대기, 새 게임/이어하기, 이전 B 진행까지 환자·회차별 기록 복원, 안전 복귀와 저장 재시도 진입점.
- `PatientVisitController`: 입장 전 진행 확인, 퇴장 전 B 저장, 미달 순서 유지. CompletedCount는 실제 클리어 인원, DepartureCount는 퇴장 횟수. 정식 경로에서 데모 대화 비활성.
- `ConsultationRoomController`: 중첩 IDisposable 입력 잠금, 회차별 기록 목록, 표시 중 모달 상태 보존.
- `StartMenuController`: 새 진행/이어하기 구분, 손상 시 새 게임·불러오기 비활성 및 복구 안내.
- 공용 씬·프리팹은 재생성/재저장하지 않았다. 기존 참조와 meta 보존. 런타임 부착으로 양쪽에 같은 연결을 적용한다.

공용 저장 위치: `Application.persistentDataPath/game-progress/active.txt`, `<파일 ID>.json`, `.bak`. 게임 진행 ID는 JSON의 ProgressId. 새 게임은 별도 파일·새 진행 ID이며 예전 진행과 AI immutable snapshot을 삭제하지 않는다. B는 raw AI 내용을 새로 생성하거나 점수를 계산하지 않는다.

## 검증

- .NET 8 `dotnet run --project tools/b-progression-checks/ProgressChecks.csproj`: **33개 통과**. 90/60, 70/70, 미저장/다른 진행/다른 회차/범위 밖 점수 거부, 중복 정산, 상담/평가/연출 재시작, 정산 저장 실패 rollback 및 재시도, 재방문 의도 해제, distinct 6명, 새 진행 보존, 경제/보유/장착 저장, 손상 파일 보존.
- 기존 `dotnet run --project tools/unity-client-checks/UnityClientChecks.csproj -- .`: **158개 통과**. 실제 서버 인수를 전달하지 않은 계약·저장 회귀 검사.
- Unity **6000.3.28f1** batch 컴파일 및 `-executeMethod BProgressIntegrationChecks.Run`: **Editor 통합 9개 통과**, exit code 0. 공용 프리팹 참조, 중첩 입력 잠금/멱등 해제, Unity Json.NET 저장/복원/중복 방지/다음 환자 확인. 첫 컴파일의 이벤트 해제 오타는 수정 후 재검증했다.
- 실행 로그: git 제외 `artifacts/b-progression/unity-integration.log`. 테스트용 보상 17은 테스트 파일 안에만 있으며 런타임에는 등록하지 않는다.
- 미실행: PlayMode 마우스/IME/GUI 시각 확인, 실제 AI 서버 상담, C 실제 계산기 및 D 실제 연출, Windows/macOS 플레이어 빌드, 강제 OS 종료·전원 차단. Editor 검사를 이 검증의 통과로 간주하지 않는다.
- Unity 배치 import가 자동 변경한 폰트/ProjectSettings는 원래 깨끗했던 상태로 되돌리고 제출에서 제외한다.

## 남은 의존성과 확정 사항

1. C의 실제 IRewardCalculator와 승인 보상/상품 가격표. 미연결 상태에서는 90/60도 정산 대기로 남는다. 임의 0C 정산을 넣지 않았다.
2. D의 정식 patient→photo ID, 클리어/인물 엔딩 presenter, 사진 에셋·최종 엔딩. 70/70도 사진 ID가 없으면 정산 대기. 저장 후 presenter가 없으면 Presenting을 유지한다. D가 PendingResult를 읽고 CompletePresentation을 호출해야 다음 퇴장이 가능하다.
3. A 민석 정식 데이터/지원이 없어서 그 순서에서 입장을 차단한다. 나머지 현재 지원 환자는 기존 서버/데이터를 사용한다.
4. A 진행별 저장 경로 주입 API와 확정된 과거 사례 목록 API 필요. B 도입 이후 모든 진행의 회차는 열람할 수 있지만 도입 전 legacy snapshots의 전체 이력 인덱싱은 남았다. 파일은 보존한다. 확정되지 않은 orphan snapshot을 과거 확정 기록으로 오인하지 않는다.
5. 승인 승급 기준 없음. `Certificate(approvedThresholds)`만 제공하고 2/4명 기준을 설치하지 않았다. 사진·프로필·장착 실제 표시 연결은 D/C가 위 계약으로 연결해야 한다.
6. 실행 중 기본 복귀/오류 UI는 B의 최소 IMGUI 연결이다. D의 최종 시각 UI로 교체할 때 ReturnToStartMenu/RetryProgress API를 호출하면 된다. 현재 저장 모듈은 한 실행의 단일 작성자 모델이며, 다른 실행의 저장 변경은 거부한다.

## Unity 확인 절차

1. Unity 6000.3.28f1로 프로젝트를 열고 `Gamsamo > B > Run Progress Integration Checks` 실행. `B_PROGRESS_CHECKS_PASS 9` 확인. 실제 진행 저장을 쓰지 않는 Editor 검사다.
2. StartMenu 씬 Play → 새 게임. 상담실의 GameProgressRuntime 생성과 game-progress 저장 확인. C/D 공급자는 B-progress-contract.md의 순서로 연결. 실제 게임에 테스트 보상기/가짜 AI를 등록하지 않는다.
3. 실제 A 평가가 90/60이면 C 정산 저장 후 퇴장. 완료 인원은 0이며 문을 다시 클릭해야 같은 환자의 새 회차가 시작되는지 확인.
4. 70/70이면 C/D 연결 시 저장 후 결과 표시. 동일 평가 재수신·재시작에도 잔액/사진/완료 인원이 한 번만 늘고, D의 표시 완료 후 퇴장하면 다음 환자인지 확인.
5. 상담/평가/연출 단계에서 Play 종료 후 이어하기. 같은 진행·AI 사례·회차·대기 상태인지, 기록부를 문 클릭 전에 열어 이전 회차를 탐색할 수 있는지 확인.
6. 별도 테스트용 사용자 데이터에서 저장 실패·손상을 재현. .json/.bak 원본을 복사 보존하고 한 파일을 손상시킨 뒤 시작 화면 복구 안내와 새 게임 덮어쓰기 차단 확인. 복구는 앱을 닫은 상태에서 검토한 .bak을 별도로 복사해 수행(자동 복구 아님).
7. 서로 다른 roster 6명의 실제 정산 완료 후에만 FinalEndingUnlocked가 true인지 확인. 민석 지원 및 C/D 연결 전에는 이 전체 시나리오를 실제로 완료할 수 없다.

브랜치 게시/PR 결과는 마지막 Git 이력과 최종 보고에서 확인한다. main 직접 푸시·병합은 하지 않는다.


## 병합 전 실제 Unity PlayMode 확인 — 2026-11-06 04:42:46 KST (UTC+09:00)

사용자 요청에 따라 PR #33 브랜치에서 실제 Unity 6000.3.28f1 Editor 창을 열고 PlayMode를 실행했다. 기존 다른 프로젝트 Editor와 사용자 저장을 건드리지 않도록 검사 중에만 productName을 고유 테스트 이름으로 변경하고 `Application.persistentDataPath`가 분리되는지 검증했다. 종료/실패/Play 정지 시 원래 productName으로 복원한다. ProjectSettings와 씬은 커밋 변경 없음.

- 추가 메뉴: `Gamsamo > B > Run Isolated PlayMode Checks` (Ctrl+Shift+F10). `Assets/_Project/Tests/Editor/BProgressPlayChecks.cs`에 있으며 Editor 전용이다. PlayMode 시작 전에 저장되지 않은 씬 변경이 있으면 거부한다.
- **실제 PlayMode 검사 19개 통과**: 새 게임/불러오기 가용성, 문 대기, 데모 비활성, 기록부 모달의 입장 차단, 첫 환자 입장, A 사례/회차 저장, 연결 설정 오류를 클리어/퇴장으로 오인하지 않음, 중복 입장 차단, Play 정지·재시작 후 동일 진행/사례/회차 복원, 오류 후 시작 화면 복귀, 새 진행 ID, 이전 진행/AI 이력 보존, 환자 입장 없는 기록부 열기, 빈 방 복귀, 손상 저장 시 새 게임·이어하기 차단 및 손상 파일 보존.
- 검사 방식: 실제 씬/MonoBehaviour/코루틴/도메인 재로드를 실행하고 기존 UI 버튼 이벤트와 공개 API를 호출했다. Editor 검사 메뉴는 실제 창에서 입력했고 실행 화면도 확인했다. 모든 게임 버튼을 물리적 마우스로 클릭한 검증은 아니다.
- **발견·수정한 버그**: `ReturnToStartMenu`가 A.CanRetry를 무조건 차단해, 미완료 요청·저장이 없는 단순 연결 설정 오류에서도 방을 나갈 수 없었다. 이제 IsBusy를 검사하고 저장된 draft 일치 확인·B 저장을 완료한 후 복귀한다. 첫 PlayMode 검사에서 재현, 수정 후 전체 19개 통과.
- .NET 진행/저장 33개도 재실행하여 통과. 이번 변경 후 Unity 스크립트 컴파일 오류 없음.
- 초기 Unity 자체 `UnityEditor.Search.SearchDatabase` 검색 인덱스 예외로 검사 시작이 방해받았다. 게임 코드의 예외와 분리해 기록하며, Editor 준비 후 메뉴에서 다시 실행한 최종 검사는 통과했다.
- 결과: `artifacts/b-progression/playmode-results.txt`의 `B_PLAYMODE_PASS`, 화면: `playmode-room.png`, `playmode-corruption.png`, Editor 로그: `unity-playmode.log` (모두 git 제외).
- **아직 미검증**: 실제 서버 응답/70점 평가, C 실제 보상 계산기, D 클리어·사진·엔딩 연출, 플레이어 빌드/강제 프로세스 종료. 이 검사는 연결 설정이 없는 격리 환경을 사용하며 가짜 AI·점수·보상 공급자를 게임에 연결하지 않는다.
- 검사 후 PlayMode를 정지하고 StartMenu 씬을 열어 두었다. main 병합은 하지 않는다. 수정·검사 추가는 기존 PR #33에 반영한다.
