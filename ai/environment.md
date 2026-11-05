# 서윤 AI 환경변수와 A6000 이전 안내

2026-11-05 기준 현재 코드를 실행하기 위한 안내다. 명령은 **Linux·Bash, 저장소 루트, Conda `game-ai`**를 기준으로 한다. 현재 확인한 장비는 RTX 5070 Ti이며 A6000에서의 호환성과 속도는 아직 검증하지 않았다. 모델과 질문 규칙을 변경하는 절차는 이 문서의 범위가 아니다.

## 확인한 실행 환경

| 항목 | 현재 서버에서 확인한 값 |
|---|---|
| Python | 3.11.18 (`server/pyproject.toml` 허용 범위: 3.11) |
| GPU | NVIDIA GeForce RTX 5070 Ti, 16,303 MiB |
| NVIDIA 드라이버 | 595.99.02 |
| PyTorch | 배포 버전 2.14.0, 실행 시 `2.14.0+cu130`, CUDA 빌드 13.0 |
| vLLM | 0.33.0 |
| Transformers | 5.20.2 |
| FastAPI / Uvicorn | 0.136.3 / 0.54.0 |
| LangGraph | 1.2.20 |
| Pydantic / HTTPX | 2.14.2 / 0.28.1 |
| Hugging Face Hub | 2.4.0 |
| FlashInfer Python / cubin | 0.7.2.post1 / 0.7.2.post1 |
| FlashInfer JIT cache | 0.7.2.post1+cu130 |
| NVIDIA NVSHMEM | `nvidia-nvshmem-cu13` 3.4.5 |
| OpenCV | `opencv-python-headless` 5.0.0.93 |
| pytest / pytest-asyncio | 9.2.0 / 1.4.1.post1 |

직접 의존성은 [server/pyproject.toml](../server/pyproject.toml)에 고정되어 있다. 위 표는 현재 환경의 관측값이며 모든 전이 의존성을 고정한 lock 파일은 아니다. 이전 환경 백업·전체 목록은 Git에서 제외된 `ai/outputs/env-backups/`, `ai/outputs/verified-pip-freeze.txt`에 있다.

이전 설치에서 PyTorch import 오류는 NVSHMEM 의존성을 정리하며 해결했고, Conda OpenCV와 pip 설치 충돌도 해결했다. 이는 기존 서버에서 수행한 조치이며, 새 서버에서 같은 오류가 없는 상태로 OpenCV나 NVIDIA 패키지를 임의 삭제할 필요는 없다.

## 애플리케이션 환경변수

설정은 [Settings.from_env()](../server/app/core/config.py)와 [SaveStore](../server/app/client.py)가 읽는다. **`.env` 자동 로딩은 구현되어 있지 않다.** `server/.env.example`을 복사하기만 해서는 적용되지 않으며 실행하는 셸에 export해야 한다.

| 변수 | 미설정 시 기본값 | 읽는 곳·의미 |
|---|---|---|
| `SEOYUN_VLLM_URL` | `http://127.0.0.1:8000` | API → vLLM 연결. `/v1`을 붙이지 않은 서버 루트 주소 |
| `SEOYUN_API_URL` | `http://127.0.0.1:8001` | CLI → FastAPI 연결. 서버 루트 주소 |
| `SEOYUN_API_KEY` | 빈 값: 인증 비활성 | FastAPI의 두 POST API와 CLI의 Bearer 키. 서버·클라이언트에 동일하게 설정 |
| `SEOYUN_VLLM_API_KEY` | 빈 값 | vLLM 실행 키와 API → vLLM 요청의 Bearer 키. `serve_model.py`가 `VLLM_API_KEY`로 전달 |
| `SEOYUN_DATA_DIR` | 아래 저장 경로 규칙 | CLI 저장 디렉토리. 저장소 밖의 절대 경로 권장; 저장소 내부는 코드가 거부 |
| `XDG_DATA_HOME` | `$HOME/.local/share` | `SEOYUN_DATA_DIR`이 없을 때 사용할 사용자 데이터 루트 |

저장 위치 우선순위는 `SEOYUN_DATA_DIR` → `$XDG_DATA_HOME/gc-gamsamo/seoyun` → `$HOME/.local/share/gc-gamsamo/seoyun`이다. SSH로 서버에서 CLI를 실행하면 해당 서버 사용자의 경로에 저장한다. API 서버에는 별도 상담 저장 디렉토리나 데이터베이스가 없다.

이번 확인 셸에서는 위 변수와 API 키가 모두 미설정 또는 빈 값이었다. 따라서 기본 localhost 주소·기본 저장 경로를 사용하는 구성이며, 실행 중인 다른 셸의 환경까지 확인한 것은 아니다. 실제 접근 키·토큰은 이 문서와 Git에 기록하지 않는다.

### 기본 설정 적용

```bash
conda activate game-ai
# 저장소 루트에서 실행. 기존 .env가 있다면 덮어쓰지 않는다.
cp -n server/.env.example server/.env
# 필요하면 server/.env의 값만 로컬에서 편집한다.
set -a
source server/.env
set +a
```

예제의 키는 비어 있다. 키를 사용할 때에는 같은 셸에서 통합 실행하거나, 개별 서버와 CLI를 실행하는 각 셸에 같은 값을 적용한다. `.env`는 Git 제외 대상이다. `VLLM_API_KEY`가 다른 작업 때문에 설정되어 있다면 `SEOYUN_VLLM_API_KEY`와 일치시키거나 미사용 설정을 해제한다. `serve_model.py`는 `SEOYUN_VLLM_API_KEY`가 비어 있다고 기존 `VLLM_API_KEY`를 지우지는 않는다.

**URL 변수는 접속 주소만 바꾼다.** 현재 `serve_model.py`의 bind는 `127.0.0.1:8000`, `serve_api.py`는 `127.0.0.1:8001`로 고정되어 있다. `run_experiment.py`도 이 두 포트를 확인한다. URL을 바꾸어 포트나 공개 범위까지 변경할 수 있는 런처는 아니다. 다른 PC에서는 SSH로 서버 터미널에 접속하는 현재 방식을 사용한다.

## GPU·라이브러리 환경변수

| 변수 | 현재 코드의 처리·용도 |
|---|---|
| `CUDA_VISIBLE_DEVICES` | 코드가 설정하지 않는다. GPU 선택이 필요한 서버에서 실행 셸에 설정; 선택한 GPU 번호를 `nvidia-smi`로 먼저 확인 |
| `HF_HOME`, `HF_HUB_CACHE` | 코드가 설정하지 않는다. Hugging Face 캐시를 다른 디스크로 옮길 때 선택적으로 사용. 모델의 실제 다운로드 경로는 JSON의 `local_directory` |
| `HF_TOKEN` | 코드가 설정하지 않는다. 현재 공개 모델용 필수 설정은 아님. 개인 인증이 필요해도 Git에 넣지 않음 |
| `HF_HUB_OFFLINE=1` | `serve_model.py`가 모델 서버 프로세스에 설정. 다운로드 전에 셸에서 이 값을 설정하면 다운로드가 막힐 수 있음 |
| `VLLM_NO_USAGE_STATS=1`, `DO_NOT_TRACK=1` | `serve_model.py`가 모델 서버 프로세스에 설정 |
| `VLLM_API_KEY` | 위 애플리케이션 키를 vLLM에 전달하는 변수; 값은 문서에 남기지 않음 |
| `LANGSMITH_TRACING=false`, `LANGCHAIN_TRACING_V2=false` | `server/app/main.py`가 API 프로세스에서 설정하여 외부 추적 비활성화 |

런처가 자식 프로세스에서 설정한 값은 부모 셸의 환경을 변경하지 않는다. 예를 들어 다운로드 단계에서 `HF_HUB_OFFLINE`을 해제해도, 모델 실행 시에는 스크립트가 다시 `1`로 설정한다. API의 토크나이저도 로컬 파일만 읽는다.

## 환경변수가 아닌 모델 설정

[configs/seoyun.json](configs/seoyun.json)이 원본이다. `MODEL_NAME`, `CONTEXT_LENGTH` 같은 임의 환경변수로 덮어쓰는 기능은 없다.

| 설정 | 현재 값 |
|---|---|
| `model_id` | `RedHatAI/Qwen3.5-9B-quantized.w4a16` |
| `revision` | `a398088c4228b0ae0c8c78df88fd1e4bf445f068` |
| `served_model_name` | `seoyun` |
| `local_directory` | `ai/models/seoyun` — 저장소 루트 기준 |
| `max_model_len` | 16,384: 입력과 출력 합산 한도 |
| `summary_threshold` / `recent_turns` | 약 12,000 입력 토큰 / 최근 6왕복 보존 우선 |
| `gpu_memory_utilization` / `max_num_seqs` | 0.9 / 1 |

| 모델 요청 역할 | Temperature | 출력 상한 | Thinking |
|---|---:|---:|---|
| 환자 대사·마무리 | 0.7 | 512 | 끔 |
| 분류·종료 판단 | 0.1 | 1,024 | 끔 |
| 기억 요약·평가 근거 추출 | 0.1 | 2,048 | 끔 |
| 최종 평가 | 0.2 | 4,096 | 켬, thinking 예산 최대 1,024 |

vLLM에는 `--language-model-only`, `--reasoning-parser qwen3`, 기본 `enable_thinking=false`를 전달하고 역할별 요청에서 thinking 설정을 지정한다. `top_p`, `top_k`, 반복 관련 설정은 현재 클라이언트가 명시하지 않는다. 모델을 바꿀 때에는 토크나이저·채팅 템플릿·reasoning parser·JSON Schema 출력 지원도 함께 확인해야 하며 모델 이름만 교체해 호환된다고 간주하지 않는다.

## A6000 서버에서 준비하는 순서

아래는 실행할 절차이며 A6000 검증 완료 기록이 아니다. 드라이버 설치나 모델 변경을 자동으로 수행하지 않는다.

1. PR 브랜치 `codex/seoyun-vllm`을 가져온다. 병합 전에는 `main`만 받아서는 이 AI 구현이 포함되지 않는다. 저장소를 처음 받는 경우:

   ```bash
   git clone --branch codex/seoyun-vllm https://github.com/sehoon-git/GC_gamsamo.git
   cd GC_gamsamo
   ```

2. `nvidia-smi`로 실제 GPU 이름·메모리·드라이버·사용 중인 작업을 확인한다. 기존 Conda 환경이 있다면 `conda activate game-ai`를 사용한다. **해당 서버에 `game-ai`가 없을 때만** 다음과 같이 만든다.

   ```bash
   conda create -n game-ai python=3.11 pip
   conda activate game-ai
   ```

3. 저장소 루트에서 의존성을 설치한다. 기존 환경이면 변경 전 패키지 목록을 별도로 보관한다.

   ```bash
   python -m pip install -e './server[serve,test]'
   python -m pip check
   python -c "import torch; print(torch.__version__, torch.version.cuda, torch.cuda.is_available())"
   ```

   기존 서버에서 확인한 PyTorch는 CUDA 13.0 빌드다. A6000 서버의 드라이버가 이 빌드를 실행할 수 있는지는 대상 서버에서 확인해야 한다. 실패하면 오류와 버전을 먼저 기록하고 호환 조합을 결정한다. 이 문서의 기존 드라이버 버전은 대상 서버에 그대로 설치하라는 지시가 아니다.

4. 고정 모델과 GPU 커널을 준비한다. 이 단계에는 다운로드를 위한 네트워크가 필요하다. 기존 장비의 GPU 커널 캐시를 복사해서 대체하지 않는다.

   ```bash
   unset HF_HUB_OFFLINE
   python -m vllm.entrypoints.cli.main download-kernels
   python tools/download_model.py
   ```

   다운로드가 끝나면 `ai/models/seoyun/.seoyun-model.json`이 생성된다. 모델 실행과 API 토크나이저는 이 파일의 리비전을 확인한다. 모델 가중치·환경 백업·실행 로그는 PR에 포함되어 있지 않다.

5. 앞의 환경변수 설정을 적용하고 필요하면 실제 장비 번호로 GPU를 선택한다. 현재 설정은 실험 동안 GPU를 전용으로 사용하고 상담 1개를 처리하는 조건이다. VRAM이 늘어도 우선 현재 모델·16,384 문맥·0.9 설정으로 비교하며 자동 확대하지 않는다.

   ```bash
   # GPU 선택이 필요한 경우에만, 실제 장비 번호로 설정한다.
   export CUDA_VISIBLE_DEVICES=0
   python -m pytest server/tests -q
   python tools/run_experiment.py
   ```

6. 별도 SSH 셸에서 `curl --fail http://127.0.0.1:8001/health`로 `inference.ready`를 확인한다. 상담자 입력창에서 한국어로 대화하고 `x`로 종료한다. 통합 실행이 시작한 서버가 종료되는지, 다시 실행하면 확정 대화가 복원되는지 확인한다. 별도 실행 방식은 [AI README](README.md)를 따른다.

7. 실제 모델 품질과 성능을 비교하려면 API·모델을 실행한 상태에서 아래 검사를 **하나씩** 실행한다. 개인 저장 슬롯을 사용하지 않지만, 합성 결과 파일은 `ai/outputs/`에서 같은 이름으로 갱신된다. 이전 측정값을 비교할 경우 먼저 별도 보관한다.

   ```bash
   python ai/evaluation/terminal_smoke.py
   python ai/evaluation/long_context_smoke.py
   ```

   이 스크립트는 기존 주질문 규칙용이다. 새로운 총 질문 5개 규칙이나 한국어 자연스러움의 자동 합격 검사로 사용하지 않는다.

## 저장 기록을 함께 이전할 때

코드만 옮기면 기존 상담 기록은 따라오지 않는다. 현재 기본 저장 루트는 `$HOME/.local/share/gc-gamsamo/seoyun/`이며, 사용자 지정 환경변수가 있었다면 그 위치를 사용한다.

- CLI가 종료된 상태에서 저장 루트 전체를 별도 보관·이전한다. `active.json`, `case_.../auto.json`, 존재하는 `manual.json`을 함께 옮겨야 기존 실험 선택과 슬롯을 유지할 수 있다.
- A6000 서버의 저장소 밖 경로에 두고 `SEOYUN_DATA_DIR`로 지정하거나, 그 서버 사용자의 기본 저장 위치에 둔다. 새 서버의 `$HOME`은 기존 서버와 다를 수 있다.
- 동일 코드·콘텐츠 버전에서 복원부터 확인한다. 이 저장 형식에는 주제 기반 질문 이력이 들어 있으며 새 질문 규칙으로 자동 변환하는 기능은 없다.
- 모델을 비교할 때 기존 기록을 남기고 `/new`로 새 실험을 시작한다. 기존 대화 기억이 첫 응답 비교에 섞이지 않게 한다.
- 개인 원문, 접근 키, `.env`, 모델 가중치와 GPU 로그는 Git에 올리지 않는다.

## 자주 확인할 오류

| 증상 | 확인할 사항 |
|---|---|
| `No module named app` | 활성화한 환경에서 `python -m pip install -e './server[serve,test]'`를 실행했는지 |
| 고정 리비전 모델 다운로드 안내 | 가중치와 `.seoyun-model.json`, JSON의 리비전 일치 여부 |
| `8000/8001 포트가 사용 중` | 기존 서버 두 개가 준비되었다면 `python tools/chat.py`; 런처는 기존 프로세스를 인수하거나 종료하지 않음 |
| `/health` 503 | vLLM 준비·모델 별칭 `seoyun`·URL·vLLM 키, `ai/outputs/vllm.log`와 `api.log` |
| POST API 401 | CLI와 API의 `SEOYUN_API_KEY` 일치 여부 |
| 환경 파일을 바꿔도 값이 그대로임 | 실행 셸에 다시 export하고 관련 프로세스를 재시작했는지 |
| CUDA 오류·메모리 부족 | 실제 GPU 선택·다른 작업 점유·드라이버와 PyTorch 빌드. 설정을 바꿨다면 기존 기준과 구분해 기록 |
