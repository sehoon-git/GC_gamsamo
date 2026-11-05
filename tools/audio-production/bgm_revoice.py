"""Render the room score with acoustic keys and deterministic non-vocal synthesis.

Use Python 3.12 with numpy, scipy, soundfile and onnxruntime. The source waveform is
used only for note transcription. The output buffer starts at zero and is filled
exclusively with FreePats low-velocity piano samples and the oscillators below.
"""
import argparse
import hashlib
import json
import re
from functools import lru_cache
from pathlib import Path

import numpy as np
import onnxruntime as ort
import soundfile as sf
from scipy.io import wavfile
from scipy.ndimage import median_filter
from scipy.signal import butter, find_peaks, resample_poly, sosfilt

MODEL_SHA = "2c3c1d144bfa61ad236e92e169c13535c880469a12a047d4e73451f2c059a0ec"
RATE = 48000


def transcribe(wave, model):
    if hashlib.sha256(model.read_bytes()).hexdigest() != MODEL_SHA:
        raise ValueError("Unexpected Spotify Basic Pitch ICASSP 2022 checkpoint")
    options = ort.SessionOptions()
    options.intra_op_num_threads = 3
    session = ort.InferenceSession(str(model), options, providers=["CPUExecutionProvider"])
    mono = resample_poly(wave.mean(axis=1), 147, 320).astype("float32")
    # The published ONNX input is 43844 samples at 22.05 kHz. Drop 15 edge
    # predictions on each side and concatenate the 142 valid note frames.
    window_size = 43844
    hop = window_size - 30 * 256
    padded = np.pad(mono, (15 * 256, window_size))
    note_frames = []
    onset_frames = []
    for start in range(0, len(padded) - window_size + 1, hop):
        window = padded[start:start + window_size].reshape(1, -1, 1)
        pitch, onset = session.run(["StatefulPartitionedCall:1", "StatefulPartitionedCall:2"],
                                   {"serving_default_input_2:0": window})
        note_frames.append(pitch[0, 15:-15])
        onset_frames.append(onset[0, 15:-15])
    pitch = np.concatenate(note_frames)
    onset = np.concatenate(onset_frames)
    seconds_per_frame = hop / 22050 / 142
    events = []
    for key in range(24, 88):
        bin_id = key - 21
        strength = median_filter(pitch[:, bin_id], size=3)
        edges = np.diff(np.r_[False, strength >= .31, False].astype(int))
        for begin, end in zip(np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)):
            attacks, _ = find_peaks(onset[begin:end, bin_id], height=.50, distance=14, prominence=.17)
            cuts = [begin] + [begin + offset for offset in attacks if offset > 12 and end-begin-offset > 10] + [end]
            for left, right in zip(cuts, cuts[1:]):
                duration = (right - left) * seconds_per_frame
                confidence = float(np.quantile(strength[left:right], .8))
                if duration >= .135 and confidence >= .43:
                    events.append((left * seconds_per_frame, min(5., duration), key, confidence))
    if not events:
        raise ValueError("No note events transcribed")
    return sorted(events)


def piano_regions(sfz):
    rows = []
    for group in sfz.read_text(encoding="utf-8").split("<group>")[1:]:
        if "hivel=80" not in group.split("<region>")[0]:
            continue
        for region in group.split("<region>")[1:]:
            keys = [int(re.search(r"\b" + name + r"=(\d+)", region).group(1))
                    for name in ("lokey", "hikey", "pitch_keycenter")]
            file = re.search(r"sample=([^\r\n]+)", region).group(1).strip()
            rows.append((*keys, sfz.parent / file))
    if not rows:
        raise ValueError("Expected FreePats Upright Piano KW low-velocity regions")
    return rows


@lru_cache(100)
def sample(path):
    return sf.read(path, dtype="float32", always_2d=True)


def piano_note(rows, key, duration):
    _, _, center, path = next(row for row in rows if row[0] <= key <= row[1])
    recording, sample_rate = sample(path)
    speed = 2 ** ((key - center) / 12) * sample_rate / RATE
    positions = np.arange(round(RATE * (duration + .9))) * speed
    positions = positions[positions < len(recording)-1]
    idx = positions.astype(int)
    fraction = (positions - idx)[:, None]
    result = (1-fraction) * recording[idx] + fraction * recording[idx+1]
    time = np.arange(len(result)) / RATE
    return result * np.exp(-6.4 * np.maximum(time-duration, 0))[:, None]


def string_note(key, duration):
    frequency = 440 * 2 ** ((key - 69) / 12)
    time = np.arange(round((min(2.6, duration) + .22) * RATE)) / RATE
    wave = np.zeros_like(time)
    for harmonic in range(1, 10):
        amplitude = np.sin(np.pi * .19 * harmonic) / harmonic**2
        wave += amplitude * np.sin(2*np.pi*harmonic*frequency*time) * np.exp(-(1.55+.46*harmonic)*time)
    return wave * np.minimum(time/.008, 1) * np.exp(-16*np.maximum(time-duration, 0)) * .30


def electric_keys(key, duration):
    hz = 440 * 2 ** ((key - 69) / 12)
    time = np.arange(round((duration + .37) * RATE)) / RATE
    phase = 2 * np.pi * hz * time
    # A pitched tine with a decaying overtone; no speech/formant generator.
    tone = np.sin(phase + .48 * np.exp(-3.4*time) * np.sin(phase*2.003))
    return .071 * tone * np.minimum(time/.014, 1) * np.exp(-.83*time - 11*np.maximum(time-duration, 0))


def pad(key, duration):
    hz = 440 * 2 ** ((key - 69) / 12)
    time = np.arange(round((duration + .62) * RATE)) / RATE
    phase = 2 * np.pi * hz * time
    envelope = np.minimum(time/.31, 1) * np.exp(-6*np.maximum(time-duration, 0))
    return .012 * envelope * (.86*np.sin(phase) + .14*np.sin(2*phase))


def add_note(mix, start, note, gain):
    length = min(len(note), len(mix) - start)
    if length > 0:
        mix[start:start+length] += note[:length] * gain


def render(source, model, sfz, out, score_out=None):
    reference, rate = sf.read(source, dtype="float32", always_2d=True)
    if rate != RATE or reference.shape[1] != 2:
        raise ValueError("Expected a 48 kHz stereo ACE-Step take")
    events = transcribe(reference, model)
    frame_count = len(reference)
    del reference
    if score_out is not None:
        score_out.write_text(json.dumps({"columns": ["start_s", "duration_s", "midi", "confidence"],
                                         "events": events}, indent=2), encoding="utf-8")
    mix = np.zeros((frame_count, 2), dtype="float32")
    regions = piano_regions(sfz)
    for i, (start, duration, key, confidence) in enumerate(events):
        pos = round(start * RATE)
        velocity = np.clip(.33 + (confidence-.43)*.92, .36, .85)
        add_note(mix, pos, piano_note(regions, key, duration), velocity)
        if key <= 60:
            add_note(mix, pos, electric_keys(key, duration)[:, None], velocity * np.array([.79, .87]))
        if 45 <= key <= 68 and duration > .25 and i % 3 == 0:
            add_note(mix, pos, string_note(key, duration)[:, None], velocity * np.array([.53, .77]))
        if 40 <= key <= 58 and duration > .95:
            add_note(mix, pos, pad(key, duration)[:, None], velocity * np.array([.70, .85]))
    soft = sosfilt(butter(2, 5500, fs=RATE, output="sos"), mix, axis=0).astype("float32")
    # Preserve floating-point headroom until mastering. The deterministic header
    # also lets the production manifest identify this intermediate by file hash.
    wavfile.write(out, RATE, soft)
    print(f"Revoiced {len(events)} events across {frame_count/RATE:.2f}s")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("model", type=Path)
    parser.add_argument("piano_sfz", type=Path)
    parser.add_argument("out", type=Path)
    parser.add_argument("--score-out", type=Path)
    args = parser.parse_args()
    render(args.source, args.model, args.piano_sfz, args.out, args.score_out)
