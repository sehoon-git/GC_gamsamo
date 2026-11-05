"""Optional on-device perceptual tags for comparing generated candidates. Human listening is separate.

In the production virtual environment install ai-edge-litert==2.3.0.
Pass an external directory for the model. No data is sent to an inference service.
The classifier and JSON reports do not belong in Assets.
"""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

import numpy as np
import soundfile as sf
from ai_edge_litert.interpreter import Interpreter
from scipy import signal

MODEL_URL = "https://storage.googleapis.com/mediapipe-models/audio_classifier/yamnet/float32/1/yamnet.tflite"
MODEL_SHA = "4d8b4a53282dc83ef04e3e7dbc4fbc98082e34e44ed798e16c3a0cdd4c584faf"


def classify(model, labels, path):
    audio, sr = sf.read(path)
    if sr != 44100 or audio.ndim != 1:
        raise ValueError(f"Expected a mono 44.1 kHz render: {path}")
    audio = signal.resample_poly(audio, 160, 441)
    audio *= .42 / max(np.max(np.abs(audio)), 1e-9)
    audio = np.pad(audio, (1536, 0))
    audio = np.pad(audio, (0, max(0, 15600 - len(audio))))
    starts = sorted(set(list(range(0, len(audio)-15599, 7800)) + [len(audio)-15600]))
    scores = []
    for start in starts:
        frame = audio[start:start+15600].astype("float32")
        model.set_tensor(model.get_input_details()[0]["index"], frame)
        model.invoke()
        scores.append(model.get_tensor(model.get_output_details()[0]["index"])[0])
    peaks = np.max(scores, axis=0)
    return {labels[i]: round(float(peaks[i]), 5) for i in np.argsort(peaks)[::-1]}


def load_model(cache):
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / "yamnet.tflite"
    if not path.exists():
        with urllib.request.urlopen(MODEL_URL, timeout=60) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != MODEL_SHA:
            raise ValueError("Unexpected model revision")
        path.write_bytes(data)
    if hashlib.sha256(path.read_bytes()).hexdigest() != MODEL_SHA:
        raise ValueError("Model checksum mismatch")
    with zipfile.ZipFile(path) as model_files:
        labels = model_files.read("yamnet_label_list.txt").decode().splitlines()
    model = Interpreter(model_path=str(path), num_threads=2)
    model.allocate_tensors()
    return model, labels


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidates", required=True, type=Path)
    parser.add_argument("--model-cache", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    model, labels = load_model(args.model_cache)
    result = {clip.stem: classify(model, labels, clip) for clip in sorted(args.candidates.glob("sfx_*.wav"))}
    if not result:
        raise ValueError("No SFX WAV files")
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
