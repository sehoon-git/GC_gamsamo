# D — 대기·클리어·엔딩·사진·안내 표시

작업·검증 기록: **2026-11-06 05:03:56 KST (UTC+09:00)**. 기준은 사용자의 요청으로 `main a33c40c`를 `pull --ff-only`한 `codex/d-presentation-flow`이며, PR 대상도 사용자 확인에 따라 `main`이다. 첨부 「GC_gamsamo_미구현_항목_및_4인_작업분담.pdf」 10–16쪽의 소유권·연결 규칙과 앞부분의 해당 기능 요구를 확인했다. PDF의 `c260a0d`보다 실제 코드를 우선한다.

이번 요청의 **담당자별 문서 분리·공통 진행 문서 동시 수정 금지**에 따라 이 파일에 기록한다. README와 plan01, 공용 씬·루트 프리팹, A/B/C 소유 파일은 수정하지 않았다. 기존 사용자 파일 `ConsultationRoomUI 2.prefab` 및 `.meta`도 보존했다.

## 시작 시 확인한 구현 상태

| 상태 | D 관련 실제 코드 |
|---|---|
| 구현됨 | A의 AI 상태·초안·재시도·종료 의도, 입력 중복 방지·IME 처리, 대사 페이지, 수동 환자 내보내기, 기록부 원문·평가·페이지, PC의 프로필·6칸 사진·설정 UI, GameSettings의 오디오 소스 등록·음량 적용 |
| 일부 구현 | 대기·오류가 문자열 상태 안내로 표시됨. 기록부의 상태별 정보 표현, 프로필 진행, 사진 데이터 공급과 상세 보기 연결은 부족함 |
| 미구현/외부 의존 | B의 게임 전체 진행·정산·해금·재생 상태 저장, 공용 모달 잠금, 클리어·엔딩·안내서, 실제 해금 사진·정식 후일담·크레딧·음원 연결 |

현재 AI 환자는 서윤·도현·태준·연우·하린 5명이며 민석은 로컬 시연이다. 퇴장 횟수, AI 평가 저장, 화면을 끝까지 본 횟수는 클리어·해금 근거가 아니다. 이번 구현은 기존 A 서비스와 입력 브리지를 재구현하지 않는다.

## 완료한 D 구현과 파일

경로는 저장소 루트 기준이다.

| 파일 | 동작 |
|---|---|
| `Assets/_Project/Scripts/Dialogue/PatientDialogueView.cs` | `DialogueProcessingState`, `SetProcessingState`로 첫 발화 준비/답변 대기/기록 저장/평가 대기/결과 저장/종료 의도/저장 완료/실제 오류 구분. 상태는 환자 대사와 분리하며 대화·초안을 변경하지 않음. 장시간 대기도 대기이고 가짜 진행률·남은 시간·0점을 만들지 않음. 소유자별 `SetPresentationBlock`으로 입력 차단 중첩을 보존 |
| `Assets/_Project/Scripts/UI/Presentation/DialogueProcessingBinding.cs` | 기존 A `StateChanged`를 읽기 전용으로 연결. B 결과 저장과 오류 재시도는 별도 경로로 표시하며 AI 재전송을 호출하지 않음 |
| `Presentation/PresentationModal.cs`, `GuidebookView.cs` | B 모달 잠금이 없으면 열지 않음. 배경 Raycast 차단, 대화 입력 차단, 포커스 복원·Tab 제한. 우측 상단 책, 6쪽 안내, 이전/다음/쪽 번호/첫·마지막 처리/닫기/Esc/방향키 |
| `Presentation/EndingPresentationView.cs` | B가 저장 완료한 클리어·해금·재생 상태만 수신. 획득 C·사진 안내, 인물 엔딩 페이지·계속/건너뛰기/다시 보기, 최종 크레딧과 6개 사진·설명 슬롯. 상세 점수는 서류에 유지 |
| `Presentation/EndingContentCatalog.cs` | 승인된 인물별 후일담·사진·크레딧·음원 에셋 연결. 승인되지 않은 글과 사진을 정식 콘텐츠로 표시하지 않음. 인물 사진은 B의 사진 해금 여부도 필요 |
| `Assets/_Project/Scripts/UI/PhotoGalleryView.cs`, `Presentation/PhotoViewer.cs` | B의 저장된 사진 ID 목록으로 교체·중복 제거·다른 게임 진행 사진 제거. 썸네일 클릭 상세 사진·설명, 긴 설명 페이지, 닫기/Esc/방향키. 기록부 사진 칸은 변경하지 않음 |
| `Assets/_Project/Scripts/UI/MedicalRecordView.cs` | 미확인/해당 없음/미공개/공개됨 구분. 미공개 값은 캐시에도 보관하지 않음. 확정 정산 내역은 평가 서류에 추가. 게임·사례 전환 시 표시 캐시 초기화 |
| `Assets/_Project/Scripts/UI/ProfileView.cs` | 제공된 자격증·현재 내담자·진행 문구·완료 인원 표시. 승급이나 클리어를 계산하지 않음 |
| `Presentation/PresentationAudio.cs` | 별도 영속 오디오 루트 1개, 동일 BGM 중복 재생 방지, 기존 GameSettings에 BGM/효과음 등록. 없는 음원은 무음 |
| `Assets/_Project/Editor/Presentation/` | D 전용 프리팹 생성기, 독립 검사, Editor 전용 합성 미리보기. 공용 씬 생성 도구를 실행하지 않음 |

위 표의 `Presentation/` 단축 경로는 `Assets/_Project/Scripts/UI/Presentation/`이다. 전용 프리팹 5개는 `Assets/_Project/Prefabs/Presentation/`, 빈 콘텐츠 카탈로그는 `Assets/_Project/Settings/Presentation/EndingContent.asset`에 있다. 새 에셋과 `.meta`를 함께 관리한다. 생성기는 **없는 D 전용 파일만 생성**하며 기존 파일·GUID·공용 씬을 덮어쓰지 않는다.

## B에게 필요한 연결 계약

게임 저장용으로 합의된 B DTO·서비스는 현재 기준에서 발견하지 못했다. 아래는 **D의 표시 API와 B에게 제안하는 연결 계약**이다. D는 별도 공용 저장 DTO·보상 서비스·사진 해금 서비스를 만들지 않았다. B가 확정한 공용 형식에서 아래 인자로 투영한다.

### 1. 공용 씬 배치와 모달 차단

대상 소유 파일: B의 `ConsultationRoomController` 및 공용 씬/루트 프리팹. 현재 `IsOverlayBlocking`은 기존 PC·서류만 검사하므로 D의 Raycast 차단만으로는 문·키보드 진입까지 보장할 수 없다.

- `Guidebook`, `ClearEnding`, `PhotoViewer`, `ProcessingFeedback` 프리팹을 **상담실의 별도 루트**로 배치한다. 각자 Screen Space Overlay Canvas, 기준 1920×1080이다. 모달 sorting order는 200, ProcessingFeedback은 모달 뒤에 가려지도록 190이다. 1280×720도 같은 16:9 비율로 축소된다. 기존 루트 Canvas 아래 중첩 배치는 피한다.
- 기존 EventSystem을 재사용한다. `PresentationAudio`는 UI 자식이 아닌 전용 루트에 배치한다.
- 각 `PresentationModal.dialogue`에 기존 `PatientDialogueView`를 연결한다.
- B가 `bool TryAcquirePresentationInput()` / `void ReleasePresentationInput()`에 해당하는 소유권 기반 잠금을 제공하고 `view.BindModalGate(acquire, release)`에 연결한다. 성공 시 문 입장, PC/기록부 전환, 대화 전송·종료·퇴장과 관련 키보드 진입을 막아야 한다. 이미 진행 중인 A 요청을 취소하거나 다시 보내지 않는다.
- PhotoViewer는 PC 사진첩 안에서 열리므로 **PC가 열린 상태에서도 상세 모달 잠금 획득**이 가능해야 한다. 닫으면 기존 PC/사진첩 상태로 돌아가고 PC의 기존 잠금은 유지한다.
- 같은 잠금은 둘 이상의 D 화면을 동시에 열지 않게 해야 한다. 닫기/비활성화 시 D는 자신이 획득한 잠금만 한 번 해제한다. D 자체도 대화 입력 차단 소유자를 분리한다.
- B 잠금이 연결되기 전에는 안내서·사진 확대·클리어·엔딩이 열리지 않는 것이 의도된 동작이다. 잠금 실패로 `Restore…`가 false이면 해당 알림을 소비하지 말고 기존 모달 종료 후 B의 확정 상태로 다시 시도한다.

### 2. A 대기 상태와 B 결과 저장

`ProcessingFeedback`의 `DialogueProcessingBinding.Bind(existingPatientAiAdapter, existingPatientDialogueView)`를 호출한다. A의 `PatientAiInputBridge`는 기존대로 전송/재시도/종료 의도를 담당한다. D 모달 열기·닫기로 시작/답변/평가를 다시 요청하지 않는다.

A는 아직 typed 저장 단계를 공개하지 않아 기존 `SetStatus` 문구, `CurrentPhase`, `IsBusy`, `EndRequested`, `LastError`를 호환 투영한다. A에게 요청할 개선 API는 **첫 발화/답변 요청/기록 저장/평가 요청/평가 저장/완료/오류를 나타내는 읽기 전용 상태**이며 출력은 상태·안전한 오류 안내, 입력/서버 재호출은 필요 없다. A 문구 변경 시 호환 매핑도 같이 확인해야 한다.

B는 게임 정산 저장을 시작할 때 `BeginGameResultSave()`, 실패 시 `FailGameResultSave(safeMessage)`, 실제 커밋 성공 시 `CompleteGameResultSave()`를 호출한다. `GameResultRetryRequested`에는 **B의 같은 정산 저장 재시도**를 연결한다. AI의 `Retry`/`SubmitText`에 연결하지 않는다. 별도 결과 저장 재시도 버튼은 중복 호출을 막고 B 실패 응답 후에만 다시 활성화된다. 오류 문구에는 비밀값·미공개 설정·서버 전체 스택을 전달하지 않는다.

### 3. 클리어·정산·재생 복원

반드시 유지할 순서:

**A 평가 검증·AI 저장 완료 → B 진행·회차 확인 → C 보상 계산 → B 진행·보상·사진·다음 환자 상태 저장 → D 표시**

```csharp
ending.PlaybackSaveRequested += PersistPlaybackOnly; // B 소유
ending.RestoreCommittedClear(gameId, patientId, caseId, sessionId,
    gameSaveCommitted, cleared, displayName, coinsAwarded, photoWasUnlocked, savedStage);
```

- `gameSaveCommitted && cleared`가 참이고 4개 ID가 모두 있어야 열린다. 인물 이름·획득량·사진 여부도 B의 저장 결과에서 가져온다. A의 `EvaluationCommitted`만으로 호출하면 안 된다.
- 회차 정산 키는 `게임 진행 ID + 환자 ID + 사례 ID + 회차 ID`. D도 4개를 모두 사용하며 요청 번호나 응답 갱신 번호로 대체하지 않는다.
- 최초 클리어 보너스 키 `게임 진행 ID + 환자 ID`, 사진 해금 키 `게임 진행 ID + 사진 ID`의 중복 방지와 저장은 B/C 소유다. D는 지급 함수를 갖지 않는다.
- 인물 재생 단계는 `clear`, `ending`, `completed`, `skipped`. 최종 엔딩은 `credits`, `completed`, `skipped`다. 문자열은 D 표시 계약이며 B의 저장 enum으로 대응 가능하다.
- `PlaybackSaveRequested(game, patient, case, session, targetStage)`에서 B는 **재생 상태만** 저장한 뒤 `AcknowledgePlaybackSaved(game, patient, case, session, targetStage, committed, safeError)`로 응답한다. 다른 식별자/단계의 늦은 응답은 무시한다. B는 재시도에서도 같은 상태 저장을 멱등 처리해야 한다.
- 성공 전에는 다음 단계·닫기로 진행하지 않는다. 실패하면 현재 확정 단계를 유지하고 저장 재시도 버튼을 제공한다. 재생/건너뛰기에서 정산·사진 지급을 호출하지 않는다.
- `completed/skipped` 복원은 자동으로 화면을 열지 않는다. B 또는 C의 다시 보기 진입에서 해당 인물의 확정 상태를 복원한 뒤 `Replay()`를 호출한다. 다시 보기는 로컬 표시만 하며 저장·보상·사진 콜백이 없다.
- 비활성화/씬 종료로 대기 중 화면이 없어져도 실제 쓰기는 B가 소유한다. 재생성 시 B의 마지막 확정 스냅샷을 복원한다. 미완료 저장을 D가 성공으로 추정하지 않는다.
- B가 확정한 최종 엔딩 해금은 `RestoreFinalEnding(gameId, gameSaveCommitted, finalUnlocked, savedStage)`에 전달한다. D는 인원수를 세어 해금하지 않는다. 최종 재생 저장 콜백의 patient/case/session은 빈 문자열이고 game ID로 해당 최종 엔딩 상태를 구분한다.
- 최종 화면 복원 후 `SetCreditsPhotos(gameId, savedUnlockedPhotoEntries)`를 호출한다. B가 검증한 사진/설명만 넣는다. 중앙 크레딧은 위에서 아래로 흐르고 양쪽 6칸은 제공된 사진·제목만 표시한다.

### 4. C의 PC 사진첩, 프로필, 기록부

대상 소유 파일: C의 `PcDesktopView` 또는 C/B 초기 연결 스크립트. **PcDesktopView는 이번 PR에서 수정하지 않았다.**

- 기존 `PhotoGalleryView.viewer`에 D `PhotoViewer`를 지정하고 잠금을 연결한다. B 로드/커밋 후 `SetUnlockedPhotos(gameId, savedPhotoIds, catalog)`를 호출한다. 기존 `PhotoGalleryEntry`에 photoId/description/fullSize를 확장했다. thumbnail은 필수, fullSize는 선택이다. 아직 이미지가 없는 해금 항목은 준비 중 표시이며 잠긴 항목을 임시로 노출하지 않는다.
- `SetPhotos`도 기존 호출 호환을 유지한다. 이 API에 전달하는 항목은 C/B가 이미 해금을 확인한 것으로 간주한다. 해금 여부를 모르면 빈 목록을 전달한다.
- `ProfileView.SetProgress(certificationLabel, currentPatientName, stateLabel, clearedCount, totalCount)`에 B의 확정 값을 전달한다. 기존 `SetProfile`은 유지한다. progressText가 연결되어 있지 않으면 기존 프로필 안에 표시용 자식만 추가한다.
- 기록부는 B가 현재 게임/사례를 선택할 때 `BeginRecordContext(gameId, caseId)` → 기존 `ShowRecord(sanitizedRecord)` → `SetInformationState(recordId, field, state, disclosedValue)` 순서로 갱신한다. field는 gender/age/occupation/visitReason/medicalHistory/medication/specialNotes다. recordId는 현재 MedicalRecordData.registrationNumber와 일치해야 한다.
- 원본 미공개 설정을 `ShowRecord`에 넣지 않는다. `Undisclosed`에 실수로 전달된 값은 D가 버리지만, A/B가 제공하는 원문/기록 자체의 공개 검증은 별도 책임이다. 같은 환자의 이전 회차 콜백은 B가 현재 game/case/session과 비교해 걸러낸 후 화면 API에 전달한다.
- `SetCommittedSettlement(recordId, committed, detailText)`는 저장된 점수·평가 근거와 함께 표시할 확정 정산 문자열을 받는다. 사례를 바꿀 때 반드시 BeginRecordContext를 먼저 호출한다. 상세 점수/보상 규칙은 D가 계산하지 않는다.

### 5. 오디오와 설정

`EndingContent.asset`에 roomMusic/endingMusic/pageSound/clearSound를 연결하고, B가 상담실 진입 시 `PresentationAudio.Instance.PlayRoomMusic()`을 호출한다. 안내서 페이지 전환·클리어·엔딩·엔딩 종료는 연결된 소리를 사용한다. GameSettings의 기존 Music/Effects 등록 API를 통해 음량을 받으며 master를 이중 곱하지 않는다. 설정 UI나 저장 형식은 재구현하지 않았다.

영속 오디오 프리팹은 첫 씬부터 하나만 사용한다. 다른 씬에 같은 프리팹이 있어도 중복 객체가 제거되고 같은 트랙 재요청은 재시작하지 않는다. 프로젝트에 별도의 BGM 재생기가 추가되면 B가 재생 소유자를 이 컴포넌트로 통일해야 한다.

## 미연결·미확정·자료 부족

- **실제 상담실 공용 씬 연결은 미완료**: B 모달 게이트, B/C 정산·전체 저장·재생 복원 서비스, C 사진 확대/다시 보기 진입 배치가 필요하다. 새 컴포넌트가 있다는 사실을 전체 게임 연동 완료로 해석하지 않는다.
- **정식 콘텐츠는 미완료**: 추적된 Assets에서 승인된 인물 후일담/웃는 사진/최종 크레딧/음원을 찾지 못했다. 카탈로그는 빈 상태로 제출한다. 기존 초상화를 웃는 사진으로 전용하거나 임의 사연을 만들지 않았다. 미준비 안내는 정식 후일담이 아니다.
- 필요한 자료: 환자별 patientId↔photoId 매핑과 승인된 제목·후일담·사진 설명·웃는 사진 6종, 최종 크레딧 전체 명단·표기 순서·승인, 상담실/엔딩 BGM과 페이지/클리어 효과음 및 사용 권한. 민석을 포함한 최종 콘텐츠 범위는 기획 담당 확인이 필요하다.
- 권장 납품 규격(확정 기획 아님): 사진 PNG/JPG 원본 장변 1600px 이상, Sprite(2D and UI), 전체 이미지와 축소 이미지(같은 Sprite도 가능); 후일담 UTF-8 텍스트; 반복 가능한 BGM WAV/OGG와 짧은 효과음 WAV/OGG. 실제 화면은 비율을 유지하며 긴 본문은 페이지로 나눈다. 긴 사진 제목은 별도 길이 검수가 필요하다.
- 승인 후 카탈로그 contentVersion, 각 인물 approved, creditsApproved를 설정한다. 승인 여부는 콘텐츠 검수이고 게임의 해금 상태와는 별개다.
- 보상 수치·가격·승급 기준을 D가 확정하지 않는다. 화면은 B/C에서 제공한 값만 표시한다. 미리보기의 7 C는 **Editor 전용 검사 값**이며 런타임 기본 보상이 아니다.
- 안내서의 순차 재상담·게임 전체 자동 저장 설명은 이번 통합 계약에 맞춘 내용이다. B 기능이 연결되기 전 안내서만 먼저 실제 게임에 노출하지 않는다. 질문 횟수 제한은 넣지 않았다.

## 실행한 검증과 한계

사용자가 요청한 관련 기능 위주의 검증을 수행했다. Unity 버전은 `ProjectSettings/ProjectVersion.txt`의 **6000.3.28f1**이다.

| 구분 | 실제 결과 |
|---|---|
| 정적 검사 | `git diff --check` 통과, D 신규 에셋의 meta 확인, 공용 씬·루트 프리팹 diff 없음 확인 |
| Unity 컴파일·독립 컴포넌트 검사 | 최소 검증 프로젝트에서 실제 Unity C# 컴파일 성공, `PresentationChecks.RunAndBuild`의 D 검사 **77개 통과**. 대기/초안·상태 분리·중복 입력·모달·클리어 저장 게이트·재생 실패/재시도·복원·사진 페이지·미해금 사진 비노출·기록 공개 상태·프로필 검사 |
| 기존 입력 연동 회귀 | 같은 Unity 실행에서 기존 `PatientAiInputChecks` **56개 통과**. 서비스 재구현 없이 기존 이벤트·초안·입력 규칙 확인 |
| Unity Play 자동 검사 | 합성 전용 씬에서 **9개 통과**. 실제 GraphicRaycaster가 문보다 모달을 우선, 잠금/대화 유지, 실제 AudioSource 재생·중복 제거·음량 .7/.3/.2 적용·음원 없음 무음. 설정 검사 후 기존 PlayerPrefs 원문/존재 여부 복원 |
| 기존 .NET 어댑터 스텁 컴파일 | `AdapterCompileChecks.csproj`는 **실패**. main에도 있는 Instantiate, Button.transform, GetComponentInChildren(bool)을 기존 UnityApiStubs가 제공하지 않아 3개 오류. A 소유 스텁 파일은 수정하지 않음. 위 실제 Unity 컴파일 성공과 구분 |
| 실제 서버/전체 게임 저장 | **미실행**. D는 서버 요청·실제 게임 재화/사진 저장을 만들지 않음. B의 디스크 재실행 복원/정산 트랜잭션·실제 서버 오류 복구는 연결 후 검증 필요 |
| 수동 화면·입력/빌드 | 주 프로젝트 실제 상담 중 Esc/방향키·문 차단·HD/FHD 시각 확인, 실제 음원 청감, Windows/macOS 빌드는 **미검증**. 자동 상태 검사와 같다고 보고하지 않음 |

검증용 프로젝트는 무시되는 `artifacts/d-presentation/validation`에 런타임 소스·D Editor 도구·폰트·필요한 패키지만 복사했다. 공용 씬·루트 프리팹을 복제하거나 생성하지 않았다. `artifacts/d-presentation/validation-final.log`, `play-validation-final.log`, 검증 프로젝트의 `artifacts/d-presentation/{edit-checks,play-checks}.txt`에서 결과를 확인할 수 있다. 프리팹 생성은 이 격리 프로젝트에서만 실행하고 생성된 D 전용 에셋을 가져왔다.

Play 시작 시 UnityEditor.Search.SearchDatabase의 `ArgumentOutOfRangeException`이 별도로 기록되었다. D Play 검사 9개는 완료했지만 **Console 전체 오류 0개라고 주장하지 않는다**. 이 에디터 검색 인덱스 오류를 이유로 범위 밖 파일을 변경하지 않았다.

사용자가 첨부한 “StartMenu.unity modified externally” 창은 main 갱신으로 디스크 씬이 바뀔 때의 재로드 확인이다. 이번 D 변경의 씬 수정은 없다. Unity 미저장 편집이 없다면 Reload로 최신 디스크 씬을 읽는다. 미저장 편집이 있다면 먼저 별도 사본으로 보존해야 한다. 도구에 확인창이 노출되지 않아 Reload 클릭 자체는 확인하지 못했다.

## Unity 확인 절차와 다음 작업

1. 기존 씬의 사용자 편집을 저장/보존한 Edit 모드에서 `Gamsamo > Presentation > Run Isolated D Checks`를 실행한다. 실제 AI나 플레이어 저장을 사용하지 않는다.
2. `Gamsamo > Presentation > Open D Preview (Synthetic Only)`를 실행한다. 기존 씬이 dirty이거나 Play 중이면 실행을 거부한다. 새 **미저장 격리 씬**에서 개발용 표시를 띄우고 Play에 진입한다. 개발용 7 C·클리어는 Editor 코드에만 있다.
3. 안내서 첫/마지막 페이지, Esc/좌우/Tab, 뒤 문 버튼·대화 버튼 차단과 닫은 뒤 원래 대화를 확인한다. 개발용 클리어/최종 엔딩 버튼으로 화면·미준비 콘텐츠 안내를 본다. 미리보기는 즉시 성공하는 B 저장 대역이므로 실제 저장 완료 검증이 아니다.
4. Play를 종료하고 **미리보기 씬을 저장하지 않는다**. 원래 StartMenu 또는 ConsultationRoom을 다시 연다. 정식 씬 생성 메뉴는 실행할 필요 없다.
5. B가 위 연결을 마친 뒤 실제 상담에서 대기 중 초안·서류/안내서 왕복, 중복 전송/종료 의도, 평가 지연·게임 저장 실패에서 클리어 미표시를 확인한다. 저장 성공 후 획득량·상세 서류·사진 해금을 대조한다.
6. 인물/최종 엔딩 중 종료 후 재실행해 마지막 확정 재생 단계와 사진을 복원한다. 건너뛰기·다시 보기 전후 지갑/사진 키 개수가 불변인지 B 저장 데이터로 확인한다. HD/FHD 화면 배치, 실제 음원·설정 음량·씬 왕복 BGM 1개를 직접 확인한다.

우선순위: **B 모달·저장 계약 확정 및 공용 씬 연결 → C PC 사진/다시 보기 연결 → 승인 콘텐츠/음원 납품 → 연결된 실제 진행·복원·수동 입력 검증 → 장식 연출 다듬기**. 커밋·작업 브랜치 게시·PR 결과는 Git 이력과 최종 보고에서 확인하며, 병합은 사용자가 결정한다.
