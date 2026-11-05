# BGM 재생성

두 BGM은 ACE-Step 1.5 Turbo를 로컬 CPU에서 실행해 만든다. 텍스트 인코더, 양자화된 DiT와 VAE만 사용한다. 별도 가사·음악 토큰 생성 LM은 필요하지 않다. 각 후보는 독립된 시드와 103초 길이 요청을 사용한다. 프롬프트와 시드는 `bgm_generate.py`의 `TRACKS`에 있다.

## 모델과 라이선스

- [원본 ACE-Step 1.5 공식 가중치](https://huggingface.co/ACE-Step/Ace-Step1.5/tree/19671f406d603126926c1b7e2adc169acbcade22). 모델 카드가 MIT와 상업적 생성 음원 사용을 명시한다.
- [실제 사용한 GGUF](https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF/tree/666ac70204440867d8c01ba4b119cc79c95b370a). 변환 저장소의 모델 카드는 MIT다. 세 파일의 SHA-256은 생성 스크립트에 고정했다. Qwen 텍스트 인코더 원본은 [Apache-2.0](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B/tree/97b0c614be4d77ee51c0cef4e5f07c00f9eb65b3)이다.
- [CPU 추론 엔진과 MIT 라이선스](https://github.com/ServeurpersoCom/acestep.cpp/tree/de858d8e1a94f0e1939cf9bdd8ff7d9631953077). ggml 서브모듈은 `cc5df49a6b878db2b68b9a7b45976e279594627d`이다. 최종 게임은 음원만 배포한다.
- 상담실 전사는 [Spotify Basic Pitch ICASSP 2022 체크포인트](https://github.com/spotify/basic-pitch/tree/fa5997af0a8210982619003269994a1be25eddf3/basic_pitch/saved_models/icassp_2022)와 해당 저장소의 [Apache-2.0 라이선스](https://github.com/spotify/basic-pitch/blob/fa5997af0a8210982619003269994a1be25eddf3/LICENSE)를 따른다. 피아노는 [FreePats Upright Piano KW 2022-02-21](https://freepats.zenvoid.org/Piano/acoustic-grand-piano.html#UprightKW)의 약한 타건 층이다. Gonzalo·Roberto가 2017년에 녹음하고 Roberto가 편집한 [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) 녹음을 사용했다.
- 제작 기기는 Intel Core Ultra 7 155H, 시스템 RAM 약 32GiB, Intel Arc 내장 그래픽(Windows 전용 VRAM 보고 약 2GiB)이다. 게임·AI 서버 GPU와 분리하기 위해 CPU 전용 커널, Q4_K_M Turbo DiT와 청크 VAE 디코딩을 선택했다.

## Windows 빌드 및 후보 생성

Visual Studio 2022의 C++ 도구와 Python 3.12가 필요하다. 아래 `$workspace`는 Unity 프로젝트 밖의 별도 경로로 정한다. 명령은 Unity 저장소 루트에서 실행한다.

```powershell
$workspace = Join-Path $env:LOCALAPPDATA 'GC-AudioProduction/bgm'
$toolsDir = (Resolve-Path 'tools/audio-production').Path
New-Item -ItemType Directory -Force -Path $workspace | Out-Null
py -3.12 -m venv "$workspace/venv"
$py = "$workspace/venv/Scripts/python.exe"
& $py -m pip install cmake numpy==2.5.4 scipy==1.18.1 soundfile==0.14.0 pyloudnorm==0.2.0
git clone https://github.com/ServeurpersoCom/acestep.cpp.git "$workspace/acestep.cpp"
git -C "$workspace/acestep.cpp" checkout de858d8e1a94f0e1939cf9bdd8ff7d9631953077
git -C "$workspace/acestep.cpp" submodule update --init --recursive
$cmake = "$workspace/venv/Scripts/cmake.exe"
& $cmake -S "$workspace/acestep.cpp" -B "$workspace/build" -G "Visual Studio 17 2022" -A x64 -DGGML_OPENMP=ON -DGGML_NATIVE=OFF -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON -DGGML_VULKAN=OFF -DGGML_CUDA=OFF -DBUILD_SHARED_LIBS=OFF
& $cmake --build "$workspace/build" --config Release --target ace-synth -j 6
& $py "$toolsDir/bgm_generate.py" download --models "$workspace/models"
$opts = @('--exe', "$workspace/build/Release/ace-synth.exe", '--models', "$workspace/models", '--out', "$workspace/takes")
foreach ($seed in 268104,268125,268176) {
    & $py "$toolsDir/bgm_generate.py" render @opts --track bgm_room --seed $seed
}
foreach ($seed in 276103,276144,276189) {
    & $py "$toolsDir/bgm_generate.py" render @opts --track bgm_ending --seed $seed
}
```

ACE-Step CLI는 요청 파일 이름에 배치 인덱스 0을 붙인다. 예를 들어 `bgm_room-268104.json`으로 생성한 원본 파형은 `bgm_room-2681040.wav`다. 동일 시드의 샘플도 GGML 빌드나 CPU 커널이 달라지면 달라질 수 있다.

상담실 후보를 비교할 때 `python tools/audio-production/bgm_loop.py <take.wav>`로 접합 구간을 찾을 수 있다. 2.2초 겹침 구간의 크로마·주파수대별 에너지·어택에서 검출한 박 위상을 비교한 뒤 양 채널 진폭이 낮은 샘플로 끝점을 미세 조정한다. 원하는 엔딩 후보에서 허밍과 악기 구분이 모호하면 동일한 생성 명령에 `--track bgm_ending --seed 276247`을 지정한다. 이 보강 테이크는 피아노와 나일론 기타만 명시한 별도 프롬프트를 사용한다.

## 상담실 악기 재연주

상담실 C의 음표·어택·길이를 Basic Pitch로 추출했다. 시간 격자 스냅을 적용하지 않는다. 새 버퍼에는 CC0 피아노, 저역 전자피아노, 짧은 기타 발현과 희미한 사인파 패드만 더한다. 전자 악기는 코드에 정의된 결정적 합성이다. ACE 생성 파형은 전사용 입력에만 사용한다. 엔딩은 선택된 D 테이크를 바로 마스터한다.

```powershell
$revoice = Join-Path $workspace 'revoice'
New-Item -ItemType Directory -Force -Path $revoice | Out-Null
& $py -m pip install onnxruntime==1.31.1
Invoke-WebRequest 'https://raw.githubusercontent.com/spotify/basic-pitch/fa5997af0a8210982619003269994a1be25eddf3/basic_pitch/saved_models/icassp_2022/nmp.onnx' -OutFile "$revoice/nmp.onnx"
Invoke-WebRequest 'https://freepats.zenvoid.org/Piano/UprightPianoKW/UprightPianoKW-SFZ+FLAC-20220221.7z' -OutFile "$revoice/piano.7z"
if ((Get-FileHash "$revoice/nmp.onnx").Hash -ne '2c3c1d144bfa61ad236e92e169c13535c880469a12a047d4e73451f2c059a0ec') { throw 'Wrong Basic Pitch weights' }
if ((Get-FileHash "$revoice/piano.7z").Hash -ne '3a66da3b25a2fd9499d90bcf09f1aee4431825796e249361b48af59b1201e703') { throw 'Wrong piano archive' }
tar -xf "$revoice/piano.7z" -C $revoice
$sfz = "$revoice/UprightPianoKW-SFZ+FLAC-20220221/UprightPianoKW-20220221.sfz"
& $py "$toolsDir/bgm_revoice.py" "$workspace/takes/bgm_room-2681760.wav" "$revoice/nmp.onnx" $sfz "$revoice/bgm_room-268176-instrumental.wav" --score-out "$revoice/room-score.json"
& $py "$toolsDir/bgm_loop.py" "$revoice/bgm_room-268176-instrumental.wav"
```

최종 마스터에는 manifest의 시작·길이·크로스페이드 값을 사용한다. 상담실은 **재연주 WAV**의 마지막 샘플 이후 파형을 시작 구간으로 크로스페이드한다. 엔딩은 **ACE 생성 WAV**에 짧은 어택 보호 페이드와 끝 감쇠를 적용한다. 둘 다 네이티브 48kHz 스테레오 16-bit PCM으로 내보낸다. 게인은 -20 LUFS-I를 겨냥하되 4배 보간 피크가 -1.15 dBTP를 넘지 않게 제한한다.
