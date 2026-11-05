"""Reproduce the six ACE-Step instrumental takes using a local ace-synth executable.

Checkout/build ServeurpersoCom/acestep.cpp at ENGINE_REV with its pinned ggml submodule.
Use a separate Python 3.12 venv. Model files and takes belong outside the Unity project.
"""

import argparse
import hashlib
import json
import subprocess
import urllib.request
from pathlib import Path

ENGINE_REV = "de858d8e1a94f0e1939cf9bdd8ff7d9631953077"
MODEL_REPO = "Serveurperso/ACE-Step-1.5-GGUF"
MODEL_REV = "666ac70204440867d8c01ba4b119cc79c95b370a"
DIT = "acestep-v15-turbo-Q4_K_M.gguf"
FILES = {
    DIT: "55b4d8514850f3d0f82536f37e99673aaf48df802b5ae5b153eea32a2e2daa5e",
    "Qwen3-Embedding-0.6B-Q8_0.gguf": "972f23255e46adfe744a0eb9a0039f3c63988f65753b0968d776e8b27168c321",
    "vae-BF16.gguf": "0599862ac5d15cd308e1d2e368373aea6c02e25ebd1737ad4a4562a0901b0ef8",
}

TRACKS = {
    "bgm_room": {
        "caption": "Instrumental background music for a warm, quiet counseling-room visual novel. Soft felt piano, gentle warm electric piano, sparse acoustic guitar textures, subtle ambient pad. Around 68 BPM, restrained dynamics, spacious arrangement, reassuring and attentive, unobtrusive beneath long reading. No vocals, no humming, no speech, no drums dominating the mix, no dramatic climax, no sudden transitions. A steady harmonic texture suitable for seamless looping.",
        "bpm": 68, "keyscale": "D major", "duration": 103,
        "seeds": [268104, 268125, 268176],
    },
    "bgm_ending": {
        "caption": "Instrumental epilogue music for a gentle counseling visual novel. Warm felt piano and soft acoustic guitar, restrained strings, quiet optimism and a sense of taking a small step forward. Around 76 BPM. Intimate, reflective and hopeful, never triumphant or sentimental to excess. No vocals, no humming, no speech, no epic drums, no cinematic impact, no abrupt loud climax. Gentle transitions and a soft sustained ending.",
        "bpm": 76, "keyscale": "G major", "duration": 103,
        "seeds": [276103, 276144, 276189],
    },
}

# A fourth epilogue take is available if the softer sustained timbres in the
# first three are ambiguous during vocal screening. All sources are plucked or struck.
ENDING_DUO_SEED = 276247
ENDING_DUO_PROMPT = (
    "Intimate instrumental chamber duet, only two acoustic instruments, close-miked felt piano "
    "and lightly fingerpicked nylon-string guitar. A quiet epilogue to a warm counseling visual novel. "
    "At 76 BPM, small hopeful answering phrases between plucked strings and struck piano keys, "
    "clear gentle attacks, brief woody decays and rests between notes. Measured arpeggios, "
    "low stimulation, restrained register and dynamics. End on a softly struck resolving piano chord."
)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def prepare_weights(models):
    models.mkdir(parents=True, exist_ok=True)
    for name, expected in FILES.items():
        path = models / name
        if path.is_file() and sha256(path) == expected:
            continue
        url = f"https://huggingface.co/{MODEL_REPO}/resolve/{MODEL_REV}/{name}?download=true"
        temp = path.with_suffix(".download")
        with urllib.request.urlopen(url, timeout=180) as response, temp.open("wb") as out:
            while block := response.read(1024 * 1024):
                out.write(block)
        if sha256(temp) != expected:
            raise RuntimeError(f"Checkpoint digest mismatch: {name}")
        temp.replace(path)


def render(executable, models, out, track_id, seed):
    track = TRACKS[track_id]
    duo = track_id == "bgm_ending" and seed == ENDING_DUO_SEED
    if seed not in track["seeds"] and not duo:
        raise ValueError(f"Seed must be one of {track['seeds']}")
    out.mkdir(parents=True, exist_ok=True)
    path = (out / f"{track_id}-{seed}.json").resolve()
    request = {key: value for key, value in track.items() if key != "seeds"}
    if duo:
        request["caption"] = ENDING_DUO_PROMPT
    request.update(synth_model=DIT, lyrics="[Instrumental]", timesignature="4", seed=seed,
                   inference_steps=8, guidance_scale=1.0, shift=3.0, output_format="wav32")
    path.write_text(json.dumps(request, ensure_ascii=False, indent=2), encoding="utf-8")
    command = [str(executable.resolve()), "--models", str(models.resolve()), "--device", "CPU",
               "--vae-chunk", "192", "--vae-overlap", "32", "--request", str(path)]
    with path.with_suffix(".log").open("wb") as log:
        subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
    take = path.with_name(path.stem + "0.wav")
    if not take.is_file():
        raise RuntimeError(f"Missing generated WAV; inspect {path.with_suffix('.log')}")
    print(take, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=["download", "render"])
    parser.add_argument("--models", required=True, type=Path)
    parser.add_argument("--exe", type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--track", choices=list(TRACKS))
    parser.add_argument("--seed", type=int)
    args = parser.parse_args()
    if args.operation == "download":
        prepare_weights(args.models)
        return
    if not all([args.exe, args.out, args.track]) or args.seed is None:
        parser.error("render requires --exe, --out, --track and --seed")
    render(args.exe, args.models, args.out, args.track, args.seed)


if __name__ == "__main__":
    main()
