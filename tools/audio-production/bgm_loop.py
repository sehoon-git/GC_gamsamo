"""Suggest a room loop cut in an ACE-Step take using overlapping musical context.

This is a numerical aid to audition. It does not judge whether the music fits a scene.
"""
import argparse
import json
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import resample_poly, stft


def recommend(path):
    wav, rate = sf.read(path, always_2d=True, dtype="float32")
    if rate != 48000 or wav.shape[1] != 2:
        raise ValueError("Expected a stereo 48 kHz production take")
    mono = resample_poly(wav.mean(axis=1), 1, 4)
    hz, times, spectrum = stft(mono, 12000, nperseg=2048, noverlap=1808, boundary=None)
    power = np.abs(spectrum) ** 2
    hop = .02
    band_limits = np.geomspace(40, 5400, 19)
    bands = np.array([power[(hz >= a) & (hz < b)].sum(axis=0) for a, b in zip(band_limits, band_limits[1:])])
    log_bands = np.log10(np.maximum(bands, 1e-10))
    note_class = np.rint(69 + 12 * np.log2(np.maximum(hz, 1) / 440)).astype(int) % 12
    chroma = np.sqrt([power[(hz > 65) & (hz < 3500) & (note_class == n)].sum(axis=0) for n in range(12)])
    chroma /= np.maximum(np.linalg.norm(chroma, axis=0), 1e-7)
    level = 10 * np.log10(np.maximum(power.sum(axis=0), 1e-11))
    # Find the quarter-note period over the expected slow tempo range. Rectified
    # spectral flux emphasizes new attacks without counting sustained pad energy.
    flux = np.maximum(np.diff(log_bands, axis=1, prepend=log_bands[:, :1]), 0).mean(axis=0)
    flux = np.maximum(flux - np.median(flux), 0)
    lags = range(round(60 / 82 / hop), round(60 / 58 / hop) + 1)
    def periodicity(lag):
        return sum(float(np.mean(flux[k*lag:] * flux[:-k*lag])) / k for k in (1, 2, 4))
    beat = max(lags, key=periodicity) * hop
    grid = np.exp(2j * np.pi * times / beat)
    # Phase is relative to the proposed splice on each side, not a fixed global
    # metronome. A weak pulse in genuinely sparse music has little decision weight.
    def local_phase(idx):
        lo, hi = max(0, idx - 200), min(len(flux), idx + 200)
        phase = np.sum(flux[lo:hi] * grid[lo:hi]) / max(np.sum(flux[lo:hi]), 1e-12)
        return phase / grid[idx]
    starts = list(range(12, min(350, len(times)), 3))
    ends = list(range(4500, min(5010, len(times)-117), 3))
    phases = {i: local_phase(i) for i in starts + ends}
    middle_level = np.quantile(level, .40)
    offsets = np.arange(0, 110, 4)  # through the complete 2.2s crossfade
    best = None
    for a in starts:
        for b in ends:
            duration = times[b] - times[a]
            if not 90 <= duration <= 120:
                continue
            aa, bb = a + offsets, b + offsets
            harmonic = float(np.sum(chroma[:, aa] * chroma[:, bb], axis=0).mean())
            level_gap = float(np.mean(np.abs(level[aa] - level[bb])))
            color_gap = float(np.abs(log_bands[:, aa] - log_bands[:, bb]).mean())
            pa, pb = phases[a], phases[b]
            phase_gap = abs(np.angle(pa * np.conj(pb))) / np.pi
            pulse = min(abs(pa), abs(pb))
            silence = max(0, middle_level - min(level[aa].mean(), level[bb].mean())) / 10
            cost = 2.7 * (1-harmonic) + .32 * color_gap + .095 * level_gap + 1.5 * pulse * phase_gap + silence
            if best is None or cost < best[0]:
                best = (cost, a, b, harmonic, level_gap, phase_gap, pulse)
    if best is None:
        raise ValueError("Could not find a 90–120s loop with sufficient tail")
    _, a, b, harmonic, level_gap, phase_gap, pulse = best
    # At initial playback there may be no preceding sample. Prefer a near-zero
    # stereo endpoint within 6ms so the first playback and subsequent loops both start gently.
    estimate = round(times[b] * rate)
    neighborhood = wav[estimate-288:estimate+289]
    best_sample = estimate - 288 + np.argmin(np.max(np.abs(neighborhood), axis=1))
    start = round(times[a] * rate) / rate
    return {"start": start, "duration": best_sample / rate - start, "crossfade": 2.2,
            "chromaSimilarity": harmonic, "meanBandLevelGapDb": level_gap,
            "estimatedPulseBpm": 60/beat, "splicePhaseDistanceHalfTurns": float(phase_gap),
            "pulseConfidence": float(pulse)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("take", type=Path)
    print(json.dumps(recommend(parser.parse_args().take), indent=2))
