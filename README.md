# 상담 커피톡 · GC_gamsamo

2026 게임인재원 AI 해커톤 팀 공용 저장소입니다.

작업 시작 전 [저장소 작업 지침](AGENTS.md)과 [최신 인수인계](docs/README.md)를 확인합니다. 매 작업은 검증 후 `docs/`에 완료 시각·함수·규칙·다음 작업을 기록하고, 커밋과 GitHub 푸시 확인까지 마무리합니다.

현재 저장소에는 **상담실과 진료기록부 UI 프로토타입**이 구현되어 있습니다. 상담실 왼쪽 아래 기록부를 누르면 배경이 흐려지고 환자 기록이 표시됩니다. 표시 데이터는 가상 환자 예시이며, 대화 서버와 게임 진행 시스템은 아직 이 화면에 연결하지 않았습니다.

## 상담실 UI 실행

1. `ProjectSettings/ProjectVersion.txt`에 기록된 Unity `6000.3.28f1`로 프로젝트를 엽니다.
2. `Assets/_Project/Scenes/ConsultationRoom.unity`를 열고 Play를 누릅니다.
3. 책상 왼쪽 아래 기록부를 클릭합니다. 닫기 버튼, `Esc`, 기록부 바깥 배경 클릭으로 닫을 수 있습니다.

UI 프리팹은 `Assets/_Project/Prefabs/ConsultationRoomUI.prefab`입니다. 씬의 `ConsultationRoomUI`에 있는 `ConsultationRoomController`의 `record` 필드에서 예시 내용을 수정할 수 있습니다. 런타임에는 `SetRecord(MedicalRecordData)`로 내용을 교체합니다. 기록부 사진 칸은 항상 공백입니다. 배경과 팝업은 화면 비율을 유지하며, 화면 비율이 다르면 상담실 좌우 또는 상하에 여백이 생깁니다.

Play 모드에서 `Gamsamo > Run UI Smoke Checks`로 클릭 영역, 모달 차단, 블러, 한글 표시, 텍스트 넘침, 닫기와 재열기를 검사합니다. 결과와 화면 캡처는 Git에서 제외된 `artifacts/ui/`에 저장됩니다. 씬을 처음 생성하는 도구는 `Gamsamo > Create Consultation Room Scene`이며, 기존 씬이 있으면 덮어쓰지 않습니다.

### 화면 에셋과 폰트

- 상담실: `Assets/_Project/Art/Backgrounds/ConsultationRoom.png` — 사용자 제공 상담실 원본.
- 기록부: `Assets/_Project/Art/UI/MedicalRecord.png` — 사용자 제공 단독 기록부에서 외곽 배경을 투명하게 처리하고 사진 칸을 비운 이미지. 합성 참고 화면은 프로젝트에 포함하지 않았습니다.
- 기록부 편집에는 기본 제공 ImageGen을 사용했습니다. 편집 지시는 원본의 1024 × 1536 구도, 클립보드·종이 질감, 한글 제목·항목명·표 선을 유지하면서 외곽과 클립 구멍만 투명하게 만들고, 사진 속 인물만 제거해 테두리 안을 종이색 공백으로 채우는 내용입니다. [최종 편집 프롬프트](docs/medical-record-image-edit.txt)를 함께 보관합니다.
- 한글은 [Noto Sans CJK KR](https://github.com/notofonts/noto-cjk/tree/main/Sans/OTF/Korean)의 프로젝트 포함 폰트를 사용합니다. 라이선스는 `Assets/_Project/Fonts/OFL.txt`에 있습니다. TextMeshPro 필수 리소스는 `Assets/TextMesh Pro/`에 포함되어 있습니다.

## 개발 방향

| 항목 | 합의 내용 |
| --- | --- |
| Unity | 6.3 LTS, 현재 프로젝트 `6000.3.28f1` |
| 화면 구성 | 2D Built-In 렌더러, uGUI·TextMeshPro |
| 지원 환경 | Windows·macOS |
| 대화 | 자유 텍스트 입력, 완성 응답 수신 후 타이핑 효과 |
| 환자 | 미리 작성한 고정 설정, 환자별 상담 기록 저장 |
| 정보 공개 | LLM이 공개 후보를 제안하고 Unity가 조건을 확인하여 항목별 공개 |
| 성장 | 누적 완료 환자 수에 따라 자동 승급, 전체 환자 완료 시 엔딩 |
| 저장 | Unity의 로컬 자동 저장 1개 + 수동 저장 1개 |
| 서버 | Python·FastAPI, 별도로 서빙하는 양자화 로컬 LLM 연결 |
| 외부 연결 | 임시 HTTPS 터널, 접속 주소·접근 키는 로컬 설정으로 관리 |

## 디렉토리 구조

Unity 프로젝트는 저장소 루트에 배치합니다. 아래는 각 담당자가 구현을 채워 넣을 위치입니다.

```text
GC_gamsamo/
├── Assets/
│   ├── _Project/
│   │   ├── Scenes/                # 게임 씬
│   │   ├── Scripts/
│   │   │   ├── Core/              # 게임 상태와 전체 흐름
│   │   │   ├── Dialogue/          # 대화 진행과 상담 기록
│   │   │   ├── Patients/          # 환자 정보와 공개 규칙
│   │   │   ├── Progression/       # 완료, 승급, 환자 해금, 엔딩
│   │   │   ├── Economy/           # 재화 지급과 차감
│   │   │   ├── Save/              # 저장과 불러오기
│   │   │   ├── Networking/        # 서버 통신과 데이터 타입
│   │   │   └── UI/                # 화면 표시와 입력
│   │   ├── Prefabs/               # 재사용 오브젝트와 UI
│   │   ├── Art/
│   │   │   ├── Characters/        # 캐릭터 이미지
│   │   │   ├── Backgrounds/       # 배경 이미지
│   │   │   └── UI/                # UI 이미지
│   │   ├── Audio/                 # 배경음과 효과음
│   │   ├── Fonts/                 # 한글 표시용 폰트
│   │   ├── Materials/             # 머티리얼
│   │   ├── Shaders/               # 블러 등 표현 효과
│   │   ├── Settings/              # 비밀값이 없는 게임 설정
│   │   └── Tests/
│   │       ├── EditMode/          # 게임 규칙 등의 테스트
│   │       └── PlayMode/          # 실행 중 동작 테스트
│   └── StreamingAssets/
│       └── GameData/
│           └── Patients/          # Unity와 서버가 함께 읽을 환자 JSON
├── Packages/                      # Unity 패키지 설정
├── ProjectSettings/               # Unity 버전과 프로젝트 설정
├── server/
│   ├── app/
│   │   ├── api/                   # 대화·연결 확인 API
│   │   ├── schemas/               # 요청·응답 데이터 규격
│   │   ├── services/              # 환자 로딩과 대화 처리
│   │   ├── llm/                   # 추론 엔진 연결부
│   │   └── core/                  # 서버 설정과 공통 처리
│   ├── prompts/                   # 환자 연기·판정 프롬프트
│   └── tests/                     # 서버 테스트
├── ai/
│   ├── configs/                   # 모델·양자화 설정
│   ├── quantization/              # 양자화 작업 코드
│   ├── evaluation/                # 품질·속도 평가
│   ├── models/                    # 모델 가중치: 내용은 Git 제외
│   └── outputs/                   # AI 작업 산출물: 내용은 Git 제외
├── contracts/                     # 공용 API·데이터 규격과 예제
├── docs/                          # 기획, 설계, 협업, AI 활용 기록
├── tools/                         # 추후 추가할 개발·제출 도구
├── Builds/                        # 실행 빌드: 내용은 Git 제외
├── .gitignore
└── README.md
```

빈 폴더는 `.gitkeep`으로 유지합니다. 파일이 추가되면 해당 폴더의 `.gitkeep`은 제거해도 됩니다.

## 역할과 작업 경계

| 담당 | 역할 | 주 작업 위치 |
| --- | --- | --- |
| 김세훈 | PM · 환자 설정, 상담 목표, 성장 기준, 제출 자료 | `docs/`, `Assets/StreamingAssets/GameData/` |
| 김민규 | 프론트 · Unity 초기화, 게임 진행, 화면, 저장, 빌드 | `Assets/`, `Packages/`, `ProjectSettings/` |
| 강태훈 | 백엔드 · 대화 API, 통신 규격, 추론 연결, 서버 접속 구성 | `server/`, `contracts/`, `tools/` |
| 전제오 | AI·아트 · 캐릭터·배경·UI 에셋, 환자 표현 검토, AI 활용 기록 | `Assets/_Project/Art/`, `server/prompts/`, `docs/` |

모델 선택·양자화는 별도로 정한 담당자가 `ai/`에서 진행합니다.

- **Unity:** 진행도, 정보 공개 확정, 보상, 승급, 엔딩과 저장을 담당합니다.
- **대화 서버:** 환자 설정·기존 대화로 프롬프트를 구성하고, LLM의 대사와 판정 후보를 반환합니다.
- **추론 엔진:** 모델을 실행합니다. 모델·서빙 도구가 바뀌어도 게임의 API 규격을 유지하도록 연결부를 분리합니다.
- **공용 데이터:** 환자 JSON 원본은 `Assets/StreamingAssets/GameData/Patients/` 한 곳에서 관리합니다. API·데이터 규격은 프론트와 백엔드가 `contracts/`에서 맞춥니다.

## 협업 기준

1. Unity 담당자가 프로젝트 버전과 렌더러를 관리하고 기본 설정을 공유합니다. 현재 버전은 `ProjectSettings/ProjectVersion.txt`를 기준으로 하며, uGUI와 TextMeshPro를 사용합니다.
2. 환자 데이터 형식과 대화 API 요청·응답 규격을 먼저 합의한 뒤 각자 구현합니다.
3. Unity 에셋과 연결된 `.meta`는 함께 커밋합니다. 씬을 동시에 수정하기보다 담당자를 정하고 프리팹·스크립트·데이터로 작업을 나눕니다.
4. 모델 가중치, 빌드, 캐시, 가상환경과 실제 접근 키는 Git에 넣지 않습니다. Unity가 추후 생성하는 `.meta`와 패키지 잠금 파일은 공유합니다.
5. 상담 기록과 사용자 설정은 실행 환경의 로컬 저장 공간에 보관하도록 구현합니다.

## 후속 결정 사항

- 재화 사용처: 상담 힌트 제공 또는 미공개 정보 구매. 현재 미정입니다.
- 최종 환자 수·사연·상담 목표·보상액·승급 기준 인원.
- 모델 종류·양자화 방식·추론 엔진과 실행 설정.

이번 범위에서 음료 제조 기능은 제외합니다. 상세 구현은 각 팀원이 진행합니다.

## 제출 참고

첨부 OT 기준 제출 마감은 **2026년 11월 6일 11:30**입니다. 게임 실행 빌드·프로젝트 원본·실행 방법과 AI 활용 내역을 적은 README를 하나의 ZIP으로 제출합니다. 구현 완료 후 이 README에 실제 실행 방법과 검증 결과를 추가합니다.
