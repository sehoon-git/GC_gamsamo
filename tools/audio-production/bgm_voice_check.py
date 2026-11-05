"""Screen complete BGM takes for voice-like passages using the offline YAMNet auditor.

The scores are indicators for take selection, not a guarantee that a track is vocal-free.
Usage: python bgm_voice_check.py --model-cache D:/audio-cache --report D:/tags.json <wav> [...]
"""
import argparse
import json
from pathlib import Path

import numpy as np
from scipy import signal
import soundfile as sf

from sfx_audit import load_model


def scan(path, model, labels):
    stereo, rate = sf.read(path, dtype="float32", always_2d=True)
    if rate != 48000 or stereo.shape[1] != 2:
        raise ValueError(f"Expected native 48 kHz stereo take: {path}")
    mono = signal.resample_poly(stereo.mean(axis=1), 1, 3)
    mono *= 0.42 / max(float(np.max(np.abs(mono))), 1e-9)
    names = ["Speech", "Singing", "Humming", "Choir", "Vocal music", "Music"]
    indices = [labels.index(name) for name in names]
    start_frames = list(range(0, max(1, len(mono)-15600+1), 7800))
    if start_frames[-1] != len(mono)-15600:
        start_frames.append(len(mono)-15600)
    scores = []
    for start in start_frames:
        model.set_tensor(model.get_input_details()[0]["index"], mono[start:start+15600].astype("float32"))
        model.invoke()
        scores.append(model.get_tensor(model.get_output_details()[0]["index"])[0, indices])
    matrix = np.array(scores)
    result = {}
    for i, name in enumerate(names):
        column = matrix[:, i]
        result[name] = {"max": round(float(column.max()), 4), "p95": round(float(np.quantile(column, .95)), 4),
                        "mean": round(float(column.mean()), 4),
                        "maxAtSecond": round(start_frames[int(column.argmax())]/16000, 2)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-cache", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("takes", type=Path, nargs="+")
    args = parser.parse_args()
    model, labels = load_model(args.model_cache)
    report = {p.stem: scan(p, model, labels) for p in args.takes}
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
