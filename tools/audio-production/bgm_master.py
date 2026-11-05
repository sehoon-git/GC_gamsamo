"""Trim and master one ACE-Step take for the counseling game (48 kHz stereo).
Install numpy, scipy, soundfile, pyloudnorm in the isolated production venv.
The room's first crossfade samples are a continuation of its last samples.
"""
import argparse
import math
from pathlib import Path

import numpy as np
import pyloudnorm as pyln
import soundfile as sf
from scipy.signal import resample_poly


def peak_4x(samples):
    peak = 0.0
    # Bound memory for a full-length stereo track. Guard samples preserve the FIR
    # context at internal block boundaries, including the last partial block.
    for start in range(0, len(samples), 48000):
        end = min(start + 48000, len(samples))
        left = max(start - 32, 0)
        right = min(end + 32, len(samples))
        interpolated = resample_poly(samples[left:right], 4, 1, axis=0)
        core = interpolated[4 * (start - left):4 * (end - left)]
        peak = max(peak, float(np.max(np.abs(core))))
    return peak


def master(take, target, track, start, duration, crossfade):
    raw, rate = sf.read(take, dtype="float32", always_2d=True)
    if rate != 48000 or raw.shape[1] != 2 or not np.isfinite(raw).all():
        raise ValueError("Expected stereo, 48000 Hz, finite-valued local ACE-Step waveform")
    if not (90 <= duration <= 120) or start < 0 or not (0 < crossfade <= 4):
        raise ValueError("Expected 90–120s duration, nonnegative start and <=4s crossfade")
    first, count, blend = [round(value * rate) for value in (start, duration, crossfade)]
    end = first + count
    # Ending can breathe to silence between replays. Only the room must loop
    # continuously without a fade-out interrupting a long reading session.
    required = end + blend if track == "bgm_room" else end
    if required > len(raw):
        raise ValueError(f"Window exceeds the {len(raw) / rate:.3f}s take")
    wav = raw[first:end].copy()
    if track == "bgm_room":
        phase = np.linspace(0, math.pi / 2, blend, dtype=np.float32)[:, None]
        wav[:blend] = np.cos(phase)**2 * raw[end:end + blend] + np.sin(phase)**2 * wav[:blend]
    else:
        fade_in = round(rate * .05)
        wav[:fade_in] *= np.linspace(0, 1, fade_in, dtype=np.float32)[:, None]
        wav[-blend:] *= np.cos(np.linspace(0, math.pi / 2, blend, dtype=np.float32)[:, None])**2
    meter = pyln.Meter(rate)
    loudness = meter.integrated_loudness(wav)
    if not np.isfinite(loudness):
        raise ValueError("Silent or invalid candidate")
    peak = peak_4x(wav)
    # Keep 0.15 dB extra margin on the oversampled peak before PCM quantization.
    gain_db = min(-20 - loudness, -1.15 - 20 * math.log10(peak))
    wav *= 10 ** (gain_db / 20)
    target.parent.mkdir(parents=True, exist_ok=True)
    sf.write(target, wav, rate, subtype="PCM_16")
    print(f"{target}: {len(wav)/rate:.3f}s, gain {gain_db:+.2f} dB, "
          f"{loudness+gain_db:.2f} LUFS-I, {20*math.log10(peak)+gain_db:.2f} dBTP (4x)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("track", choices=["bgm_room", "bgm_ending"])
    parser.add_argument("take", type=Path)
    parser.add_argument("target", type=Path)
    parser.add_argument("--start", type=float, required=True)
    parser.add_argument("--duration", type=float, required=True)
    parser.add_argument("--crossfade", type=float, required=True)
    args = parser.parse_args()
    master(args.take, args.target, args.track, args.start, args.duration, args.crossfade)
