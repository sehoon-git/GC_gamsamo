# 상담실·진료기록부 UI 구현 인수인계

- 구현·검증 및 인수인계 완료 시각: **2026-11-05 18:37:42 KST (UTC+09:00)**.
- 작업 범위: 상담실 배경, 왼쪽 아래 기록부 클릭, 진료기록부 모달과 배경 블러, 종료 동작, 문서화·커밋 절차.
- 반영 대상: `https://github.com/sehoon-git/GC_gamsamo`, `main` 브랜치. 이 기록과 구현을 같은 커밋으로 관리하며 커밋·원격 반영 결과는 최종 보고와 Git 이력으로 확인한다.

## 완료한 동작

상담실의 왼쪽 아래 클립보드를 누르면 배경이 어두워지고 흐려지며 중앙에 진료기록부가 열린다. 기록부와 내용은 선명하게 유지된다. 닫기 버튼, `Esc`, 기록부 바깥 클릭으로 닫는다. 약 0.2초 동안 투명도와 크기가 전환되며, 닫히는 도중에도 뒤쪽 입력을 막는다. 연속 열기·닫기를 처리하고 닫힌 뒤 원래 버튼으로 키보드 초점을 돌린다.

사용자가 제공한 상담실 이미지와 단독 기록부 이미지를 사용했다. 합성 참고 화면은 에셋으로 추가하지 않았다. 기록부는 외곽과 클립 구멍을 투명하게 처리하고 사진 칸의 인물을 제거했다. 사진 칸은 현재 공백이며 환자 초상화 연결 기능은 없다. 이미지 편집 도구·프롬프트는 [편집 기록](../medical-record-image-edit.txt)에 보관했다.

표 위에 12개 TextMeshPro 필드를 배치했다. 현재 내용은 `MedicalRecordData.CreateSample()`의 가상 환자 예시다. LLM 서버, 환자 원본 데이터, 진행도, 저장과는 아직 연결하지 않았다.

## 실행과 주요 파일

1. Unity `6000.3.28f1`로 저장소 루트 프로젝트를 연다.
2. `Assets/_Project/Scenes/ConsultationRoom.unity`를 열고 Play를 누른다.
3. 왼쪽 아래 기록부를 열어 내용·블러를 확인하고 닫기 버튼, 배경 클릭, `Esc`를 확인한다.

| 위치 | 용도 |
| --- | --- |
| `Assets/_Project/Scenes/ConsultationRoom.unity` | 실행 씬, Build Settings에 등록 |
| `Assets/_Project/Prefabs/ConsultationRoomUI.prefab` | 재사용할 UI 프리팹 |
| `Assets/_Project/Art/Backgrounds/ConsultationRoom.png` | 상담실 원본 이미지 |
| `Assets/_Project/Art/UI/MedicalRecord.png` | 투명 처리한 기록부 이미지 |
| `Assets/_Project/Scripts/Patients/MedicalRecordData.cs` | 화면 표시용 기록 데이터 |
| `Assets/_Project/Scripts/UI/` | 모달·텍스트·블러·클릭 영역·크기 조절 |
| `Assets/_Project/Shaders/ConsultationBackgroundBlur.shader` | 배경 전용 블러 셰이더 |
| `Assets/_Project/Editor/ConsultationRoomSceneBuilder.cs` | 씬과 프리팹 초기 생성 도구 |
| `Assets/_Project/Editor/ConsultationRoomSmokeChecks.cs` | Play 모드 검증 도구 |
| `Assets/_Project/Fonts/` | 한글 원본 폰트, TMP 폰트 에셋, OFL 라이선스 |
| `Assets/TextMesh Pro/` | TMP 필수 리소스와 동봉 라이선스 |

## 함수와 연결 지점

| 클래스·함수 | 역할과 사용 규칙 |
| --- | --- |
| `MedicalRecordData.CreateSample()` | 가상 환자 표시 예시를 반환한다. 실제 환자 로더나 저장소 역할은 하지 않는다. |
| `ConsultationRoomController.SetRecord(MedicalRecordData)` | 현재 기록 데이터를 바꾸고 즉시 화면에 반영한다. 환자 선택·정보 공개 시스템이 연결할 진입점이다. |
| `ConsultationRoomController.OpenRecord()` / `CloseRecord()` | 모달 활성화, 입력 차단, 애니메이션, 초점 복원을 관리한다. `IsRecordOpen`으로 논리적 열림 상태를 확인한다. 닫힘 애니메이션 중에는 이 값이 false여도 잠시 입력을 차단한다. |
| `MedicalRecordView.ShowRecord(MedicalRecordData)` | 12개 필드를 갱신한다. null 또는 빈 문자열은 `—`로 표시하고 기존 환자 내용이 남지 않도록 지운다. |
| `ConsultationBackgroundBlur.Configure(RawImage, Texture, Shader)` | 원본 배경과 셰이더를 연결한다. 배경 변경 시 이 함수로 다시 구성한다. |
| `ConsultationBackgroundBlur.SetAmount(float)` | 0~1 범위로 원본과 캐시된 블러를 혼합한다. 비활성화·파괴 시 RenderTexture와 머티리얼을 정리한다. |
| `PolygonRaycastFilter.IsRaycastLocationValid(Vector2, Camera)` | 클립보드 모양 안의 포인터만 받는다. 좌하단이 (0,0)인 정규화 좌표를 사용한다. |
| `ReferenceRectFitter.Fit()` | 기록부의 1024×1536 좌표계를 유지하며 이미지와 텍스트를 함께 축소·확대한다. 레이아웃 변경 시 자동 호출되는 내부 함수다. |
| `ConsultationRoomSceneBuilder.CreateScene()` | `Gamsamo > Create Consultation Room Scene`. 초기 씬·프리팹·폰트 설정을 생성한다. 기존 씬은 덮어쓰지 않는다. |
| `ConsultationRoomSmokeChecks.Run()` | Play 모드의 `Gamsamo > Run UI Smoke Checks`. 현재 씬을 검사하고 보고서·캡처를 저장한다. |

데이터 필드는 `registrationNumber`, `patientName`, `gender`, `age`, `occupation`, `recordedDate`, `visitReason`, `medicalHistory`, `medication`, `consultationNotes`, `specialNotes`, `counselor`다. 모두 표시용 문자열이며 현재 서버 DTO나 저장 데이터 규격으로 확정한 것이 아니다.

연결 예시:

```csharp
// room: 씬에 연결된 ConsultationRoomController
room.SetRecord(displayRecord);
room.OpenRecord();
// 환자 선택이 해제되었을 때 이전 환자 내용을 지운다.
room.SetRecord(null);
```

## 유지해야 할 규칙과 설정

- 컨트롤러는 항상 활성인 `ConsultationRoomUI` 루트에 둔다. 열고 닫는 모달의 자식으로 옮기면 안 된다.
- 투명 클릭 영역의 `CanvasRenderer.cullTransparentMesh = false`를 유지한다. 완전히 투명한 그래픽이 컬링되면 클릭이 들어오지 않을 수 있다.
- 배경은 1672×941 비율을 유지한다. 다른 화면 비율에서는 여백이 생기며 클릭 영역도 같은 배경 프레임을 기준으로 움직인다.
- 기록부 이미지와 텍스트는 같은 1024×1536 기준 좌표계를 사용한다. 이미지 크기나 내부 표 위치를 바꾸면 텍스트 배치도 맞춰야 한다.
- 배경의 정적 텍스처를 축소하여 블러 결과를 캐시한다. 화면 전체를 캡처하는 방식이 아니므로 추후 추가할 캐릭터·대화 UI는 자동으로 흐려지지 않는다.
- 애니메이션은 `Time.unscaledDeltaTime`을 사용한다. 게임 시간을 멈춰도 기록부 전환이 동작한다.
- 입력은 현재 Legacy Input Manager와 `StandaloneInputModule`을 사용한다. New Input System으로 변경하면 `Esc` 처리와 EventSystem도 함께 수정해야 한다.
- 환자 텍스트의 `richText`는 false다. 데이터에 포함된 태그를 UI 서식으로 해석하지 않는다.
- 정보 공개 조건·진행도·저장 로직은 이 UI에 추가하지 않는다. Unity의 해당 시스템이 공개 가능한 내용만 표시 데이터로 전달한다. LLM의 공개 후보가 곧 확정 상태가 되면 안 된다.
- uGUI `2.0.0`, 패키지 잠금 파일, Unity 프로젝트 설정, 에셋 `.meta`를 함께 커밋한다.
- 현재 씬의 UI는 프리팹과 연결된 인스턴스가 아니다. 생성 도구가 `SaveAsPrefabAsset`으로 별도 프리팹을 저장하므로 프리팹 수정이 씬에 자동 반영되지 않는다. 한쪽을 수정했다면 다른 쪽에도 반영할지 확인한다. 기존 씬이 있으면 생성 메뉴도 이를 갱신하지 않는다.
- 한글 폰트는 Noto Sans CJK KR이며 `Assets/_Project/Fonts/OFL.txt`를 유지한다. 동적 TMP 폰트가 추가 글리프를 만들 수 있도록 원본 `.otf`도 포함한다.

## 검증 결과와 제한

- Unity 에디터에서 스크립트 컴파일, 씬·프리팹 생성과 저장을 확인했다.
- 커밋 전 문서 내부 링크와 신규 에셋의 `.meta` 존재를 검사했다. 직접 작성한 코드·셰이더·문서·패키지 설정의 `git diff --cached --check`는 통과했다. 전체 파일 검사에서는 Unity 직렬화 파일과 TMP 동봉 리소스의 공백 형식 경고가 발생했으며 해당 생성·배포 원본 형식은 유지했다.
- Play 모드 스모크 검사: **52개 통과, 실패 0개**. Unity `6000.3.28f1`, 검사 화면 `2888×1362`.
- 검사 범위: 클립보드 내부/외부 Raycast, 열기·뒤쪽 입력 차단, 블러, 12개 텍스트 연결·한글 글리프·예시 문자열 넘침, null 데이터 초기화, 닫히는 중 재열기, 닫기 버튼·배경 닫기, 원본 배경·초점 복원.
- 보고서와 캡처: `artifacts/ui/smoke-checks.txt`, `record-open.png`, `record-closed.png`. 로컬 검증 산출물이며 Git에는 포함하지 않는다. 위 메뉴로 재생성할 수 있다.
- 실제 에디터 화면에서 기록부와 배경 합성을 확인했고 키보드 Enter로 열기, `Esc`로 닫기를 확인했다.
- 포인터 영역과 버튼 이벤트는 코드로 검사했다. 창 제어 도구 오류로 실제 마우스 조작 검증은 끝까지 수행하지 못했다. 수동 클릭 검증이 남아 있다.
- Windows·macOS 실행 빌드와 목표 해상도별 검증은 아직 수행하지 않았다.
- 긴 텍스트는 현재 말줄임 처리된다. 예시 문자열은 맞지만 임의 길이의 상담 기록 전체를 읽는 스크롤·페이지 기능은 없다.
- 기록부는 읽기 전용이며 텍스트 직접 편집, 환자 사진 교체, 실제 데이터·서버·저장 연동은 미구현이다.

## 다음 추천 작업

1. **실제 환자 표시 데이터 연결:** 환자 JSON과 공개 상태를 정리하고, 선택한 환자의 공개 가능한 필드를 `MedicalRecordData`로 변환하여 `SetRecord()`에 전달한다. 비공개·미확인 값의 표기 규칙도 이 단계에서 정한다.
2. **상담 입력과 응답 UI 연결:** 거의 완료된 LLM 서버의 실제 요청·응답 규격을 확인하고 입력창·전송 중 상태·응답 표시·오류 재시도를 연결한다. 확정된 공개 정보와 상담 기록을 기록부에 갱신한다.
3. **기록 길이·실행 환경 검증:** 긴 상담 기록의 스크롤 또는 페이지 정책을 정하고, 실제 마우스 클릭, 16:9·16:10 등 목표 해상도, Windows·macOS 빌드에서 모달·입력·한글·셰이더를 검증한다.
4. **UI 확장 전 프리팹 연결 정리:** 현재 씬의 UI를 프리팹 인스턴스로 연결하고 생성 도구도 같은 방식으로 저장하도록 정리한다. 변경이 씬과 프리팹에 중복으로 필요한 상태를 해소한다.

이번 작업부터 루트 `AGENTS.md`에 따라 **구현·검증 → docs 인수인계 → 커밋 → GitHub 푸시 확인**을 매 사이클의 완료 조건으로 적용한다.
