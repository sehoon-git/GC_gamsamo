"""Fit a small conditional generative model to CC0 foley, then sample a new take.

Windows setup (in a private venv): pip install numpy==2.2.6 scipy==1.15.3 soundfile==0.13.1
Example: python sfx_generate.py --sources D:/audio-cache --output D:/takes --id sfx_page_turn --seed 4913
Sources and candidates belong outside the Unity project. The two checked archives are the
training set. No pretrained network, external inference API, or sample playback is involved.
"""
import argparse
import hashlib
import io
import json
from functools import lru_cache
from pathlib import Path
import urllib.request
import zipfile

import numpy as np
import soundfile as sf
from scipy import interpolate, ndimage, signal

RATE = 44100
FFT = 512
HOP = 128
SEEDS = (4913, 5327, 8753)
MODEL_VERSION = "gc-foley-logspec-pca-1"
PACKS = {
    "rpg": ("rpg-audio", "6dbeaf8544da958d8f2adcb4a4a4b76c1ade34a05f8ab9edccd327da7375f38b"),
    "impact": ("impact-sounds", "029d734af1582474edf3a694d1b0cebc97c1c152f2f39fa34d4c2bafc5de77f8"),
}
CLASSES = {
    "paper": ("rpg", ["bookFlip1", "bookFlip2", "bookFlip3"]),
    "folder": ("rpg", ["bookOpen", "bookFlip2", "bookClose"]),
    "opening": ("rpg", ["doorOpen_1", "doorOpen_2"]),
    "closing": ("rpg", ["doorClose_1", "doorClose_2", "doorClose_3", "doorClose_4"]),
    "wood": ("impact", ["impactWood_light_%03d" % i for i in range(5)]),
}


def fetch_pack(root, name):
    slug, expected = PACKS[name]
    path = root / (name + ".zip")
    if not path.exists():
        root.mkdir(parents=True, exist_ok=True)
        page = "https://kenney.nl/assets/" + slug
        endpoint = "https://kenney.nl/download-link/" + ("assets/" + slug).encode().hex()
        headers = {"Referer": page, "User-Agent": "GamsamoAudioProduction/1"}
        with urllib.request.urlopen(urllib.request.Request(endpoint, headers=headers), timeout=45) as response:
            link = json.load(response)["url"]
        if not link.startswith("https://kenney.nl/download/"):
            raise ValueError("Unexpected download URL for " + slug)
        with urllib.request.urlopen(urllib.request.Request(link, headers=headers), timeout=90) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != expected:
            raise ValueError("New source revision; review license and SHA-256 before use: " + page)
        path.write_bytes(data)
    if hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        raise ValueError("Source digest mismatch: " + str(path))
    return path


def align(features, frames):
    old = np.linspace(0, 1, features.shape[-1])
    return interpolate.interp1d(old, features, axis=-1)(np.linspace(0, 1, frames))


def stft(samples):
    return signal.stft(samples, fs=RATE, nperseg=FFT, noverlap=FFT-HOP)[2]


class FoleyModel:
    def __init__(self, sources):
        self.sources = sources

    @lru_cache(maxsize=None)
    def fit(self, category):
        pack, names = CLASSES[category]
        examples = []
        with zipfile.ZipFile(fetch_pack(self.sources, pack)) as archive:
            for entry in names:
                audio, sr = sf.read(io.BytesIO(archive.read("Audio/" + entry + ".ogg")), always_2d=True)
                mono = audio.mean(axis=1)
                if sr != RATE:
                    divisor = np.gcd(sr, RATE)
                    mono = signal.resample_poly(mono, RATE // divisor, sr // divisor)
                active = np.flatnonzero(np.abs(mono) > .004 * np.max(np.abs(mono)))
                mono = mono[max(0, active[0] - 90):active[-1] + 225]
                spectrum = np.abs(stft(mono))
                spectrum /= np.max(spectrum)
                # Learn timbre and gesture together, on a common relative time axis.
                log_mag = np.log(np.maximum(spectrum, .00012))
                examples.append(align(ndimage.gaussian_filter(log_mag, (.85, .55)), 64))
        samples = np.stack(examples)
        mean = samples.mean(axis=0)
        # Sampling centered example directions with N(0,I) is a low-rank Gaussian
        # whose covariance is the empirical covariance of the training gestures.
        basis = (samples - mean) / np.sqrt(len(samples) - 1)
        return mean, basis

    def gesture(self, category, duration, rng, cutoff):
        mean, basis = self.fit(category)
        latent = np.clip(rng.normal(size=len(basis)), -1.7, 1.7)
        log_magnitude = mean + .78 * np.einsum("i,ift->ft", latent, basis)
        frames = int(round(duration * RATE))
        excitation = stft(rng.normal(size=frames))
        profile = np.exp(np.clip(align(log_magnitude, excitation.shape[1]), -10, .5))
        # Retain sub-frame texture, without reusing a training example's phase.
        excitation /= np.maximum(ndimage.gaussian_filter(np.abs(excitation), (3., 2.)), 1e-8)
        _, audio = signal.istft(profile * excitation, fs=RATE, nperseg=FFT, noverlap=FFT-HOP)
        sos = signal.butter(3, [70, cutoff], btype="bandpass", fs=RATE, output="sos")
        wave = signal.sosfilt(sos, audio[:frames])
        wave /= max(1e-12, np.max(np.abs(wave)))
        return wave


def mix(track, part, when, gain):
    start = int(round(when * RATE))
    size = min(len(part), len(track) - start)
    if size > 0:
        track[start:start + size] += gain * part[:size]


def mallet(model, frequency, duration, rng):
    t = np.arange(int(duration * RATE)) / RATE
    attack = 1 - np.exp(-t / .005)
    wave = np.zeros_like(t)
    # A soft note body plus a newly sampled wooden attack from the impact class.
    for ratio, gain, tau in [(1., 1., .23), (2., .24, .13), (3.01, .065, .081), (4.18, .035, .057)]:
        phase = rng.uniform(-.14, .14)
        wave += gain * np.exp(-t / (tau * rng.uniform(.94, 1.06))) * np.sin(2 * np.pi * frequency * ratio * t + phase)
    wave *= attack
    mix(wave, model.gesture("wood", .046, rng, 2300), .0015, .10)
    return wave


def render(identifier, recipe, model, seed):
    rng = np.random.default_rng(seed)
    duration = recipe["duration"]
    # Build a longer take; crop the single phrase before mastering.
    preroll = .065
    take = np.zeros(int(round((preroll + duration + .09) * RATE)))
    groups = {
        "sfx_page_turn": ("paper", 8500), "sfx_record_open": ("folder", 6100),
        "sfx_photo_open": ("paper", 5550), "sfx_door_open": ("opening", 4750),
        "sfx_door_close": ("closing", 3600), "sfx_ui_confirm": ("wood", 3850),
        "sfx_ui_back": ("wood", 2400),
    }
    if identifier in groups:
        kind, cutoff = groups[identifier]
        gesture = model.gesture(kind, duration, rng, cutoff)
        if identifier == "sfx_page_turn":
            # Fine fingertip pressure ripples make the drawn paper fibers less diffuse.
            t = np.arange(len(gesture)) / RATE
            gesture *= 0.90 + 0.10 * np.sin(2*np.pi*(37*t + 55*t*t))
        mix(take, gesture, preroll, 1.)
        if identifier in ("sfx_record_open", "sfx_door_open", "sfx_door_close"):
            at = {"sfx_record_open": .78, "sfx_door_open": .07, "sfx_door_close": .86}[identifier]
            mix(take, model.gesture("wood", .065, rng, 2900), preroll + duration*at, .16)
    elif identifier == "sfx_action_unavailable":
        for at, level in [(0., 1.), (.098, .62)]:
            mix(take, model.gesture("wood", .125, rng, 1650), preroll + at, level)
    else:
        notes = [(0., 293.665), (.23, 369.994), (.53, 440.00)] if identifier == "sfx_clear" else [(0., 329.628), (.167, 391.995)]
        for index, (at, freq) in enumerate(notes):
            mix(take, mallet(model, freq, duration - at, rng), preroll + at, 1 - index * .1)

    start = int(round(preroll * RATE))
    out = take[start:start + int(round(duration * RATE))].copy()
    out -= np.mean(out)
    attack = int(.0025 * RATE)
    tail = min(len(out)//4, int(.027 * RATE))
    out[:attack] *= np.sin(np.linspace(0, np.pi/2, attack))**2
    out[-tail:] *= np.cos(np.linspace(0, np.pi/2, tail))**2
    peak = np.abs(signal.resample_poly(out, 4, 1)).max()
    return out * (10**(recipe["peak_db"]/20) / peak)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", type=Path, required=True, help="External directory for licensed training packs")
    parser.add_argument("--output", type=Path, required=True, help="External candidate output directory")
    parser.add_argument("--id", help="Generate only one ID")
    parser.add_argument("--seed", type=int, help="Generate just one selected seed")
    args = parser.parse_args()
    recipes = json.loads(Path(__file__).with_name("sfx_recipes.json").read_text(encoding="utf-8"))
    ids = [args.id] if args.id else list(recipes)
    args.output.mkdir(parents=True, exist_ok=True)
    model = FoleyModel(args.sources)
    for identifier in ids:
        for seed in ([args.seed] if args.seed is not None else SEEDS):
            samples = render(identifier, recipes[identifier], model, seed)
            path = args.output / f"{identifier}-{seed}.wav"
            sf.write(path, samples, RATE, subtype="PCM_16")
            print(path.name, f"{len(samples)/RATE:.3f}s", hashlib.sha256(path.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
