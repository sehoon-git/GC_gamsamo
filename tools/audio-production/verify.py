"""Verify the shipped PCM files, their meters and their Unity bindings. Python 3.12+."""
import argparse
import hashlib
import json
import math
import re
import shutil
import subprocess
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(__file__).resolve().parent
LIMITS = {
    "bgm_room": (90, 120), "bgm_ending": (90, 120),
    "sfx_page_turn": (.35, .7), "sfx_clear": (1.2, 2),
    "sfx_ui_confirm": (.08, .18), "sfx_ui_back": (.1, .22),
    "sfx_purchase_success": (.4, .8), "sfx_action_unavailable": (.15, .3),
    "sfx_door_open": (.7, 1.2), "sfx_door_close": (.5, .9),
    "sfx_record_open": (.3, .6), "sfx_photo_open": (.2, .45),
}
SLOTS = {"bgm_room": "roomMusic", "bgm_ending": "endingMusic",
         "sfx_page_turn": "pageSound", "sfx_clear": "clearSound"}


def require(value, message):
    if not value:
        raise ValueError(message)


def resolve_ffmpeg():
    executable = shutil.which("ffmpeg")
    if executable:
        return executable
    try:
        import imageio_ffmpeg
        return imageio_ffmpeg.get_ffmpeg_exe()
    except ImportError:
        raise SystemExit("Install FFmpeg or imageio-ffmpeg==0.6.0 in the audio virtual environment.")


def dbfs(amplitude):
    return round(20 * math.log10(amplitude), 2) if amplitude > 0 else None


def frame_values(raw, width):
    return [int.from_bytes(raw[i:i+width], "little", signed=True) / (1 << (width*8-1))
            for i in range(0, len(raw), width)]


def measure(path, executable):
    with wave.open(str(path), "rb") as wav:
        ch, width, rate, n, compression, _ = wav.getparams()
        require(compression == "NONE" and width in (2, 3), f"{path.name}: expected 16/24-bit PCM")
        require(rate in (44100, 48000) and n > 0, f"{path.name}: invalid sample rate or empty file")
        first = frame_values(wav.readframes(1), width)
        wav.setpos(n - 1)
        last = frame_values(wav.readframes(1), width)
        onset_ms = None
        if path.stem.startswith("sfx_"):
            wav.rewind()
            samples = frame_values(wav.readframes(n), width)
            threshold = 10 ** (-55 / 20)
            onset = next((i // ch for i, sample in enumerate(samples) if abs(sample) > threshold), None)
            require(onset is not None, f"{path.name}: no audible attack")
            onset_ms = round(onset * 1000 / rate, 2)
            require(onset_ms <= 45, f"{path.name}: excessive leading silence ({onset_ms} ms)")
    process = subprocess.run([executable, "-hide_banner", "-nostdin", "-nostats", "-i", str(path),
                              "-af", "loudnorm=print_format=json", "-f", "null", "-"],
                             capture_output=True, text=True, errors="replace", check=True)
    report = json.loads(process.stderr[process.stderr.rindex("{"):process.stderr.rindex("}")+1])
    true_peak = float(report["input_tp"])
    require(math.isfinite(true_peak) and true_peak <= -1, f"{path.name}: true peak {true_peak} dBTP")
    integrated = float(report["input_i"])
    return {"sampleRate": rate, "channels": ch, "pcmBits": width * 8, "frames": n,
            "durationSeconds": round(n / rate, 6), "truePeakDbtp": true_peak,
            "lufsI": integrated if math.isfinite(integrated) else None,
            "boundaryStepDbfs": dbfs(max(abs(a-b) for a, b in zip(first, last))), "onsetMs": onset_ms,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=TOOLS / "manifest.json")
    parser.add_argument("--ffmpeg", help="Path to FFmpeg 7.x")
    parser.add_argument("--report", type=Path, help="Optional JSON output for CI or review")
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    entries = manifest["clips"]
    ids = [clip["id"] for clip in entries]
    require(len(ids) == len(set(ids)) and set(ids) == set(LIMITS), "Expected exactly the twelve requested IDs")
    executable = args.ffmpeg or resolve_ffmpeg()
    prefab = (ROOT / "Assets/_Project/Prefabs/Presentation/PresentationAudio.prefab").read_text(encoding="utf-8")
    delivered = {p.stem for p in (ROOT / "Assets/_Project/Audio/Produced").glob("*.wav")}
    require(delivered == set(ids), "Shipped WAV files and manifest IDs differ")
    report = {}
    seen_guids = set()
    for clip in entries:
        key = clip["id"]
        path = ROOT / clip["file"]
        require(path.resolve() == (ROOT / f"Assets/_Project/Audio/Produced/{key}.wav").resolve(), f"{key}: wrong file")
        meta = Path(str(path) + ".meta").read_text(encoding="utf-8")
        guid = re.search(r"^guid: ([0-9a-f]{32})$", meta, re.M)
        require(guid and guid.group(1) == clip["guid"] and clip["guid"] not in seen_guids, f"{key}: invalid/duplicate GUID")
        seen_guids.add(clip["guid"])
        field = re.escape(clip["prefabField"])
        binding = rf"^  {field}: \{{fileID: 8300000, guid: {clip['guid']}, type: 3\}}$"
        require(re.search(binding, prefab, re.M), f"{key}: missing or incorrect prefab binding")
        require(clip["catalogSlot"] == SLOTS.get(key), f"{key}: wrong catalog slot")
        require(bool(clip["loop"]) == key.startswith("bgm_"), f"{key}: wrong loop mode")
        require(0 < clip["recommendedGain"] <= 1 and clip["event"], f"{key}: missing event or invalid gain")
        numbers = measure(path, executable)
        lo, hi = LIMITS[key]
        require(lo <= numbers["durationSeconds"] <= hi, f"{key}: length outside {lo}..{hi}s")
        require(numbers["channels"] == (2 if clip["loop"] else 1), f"{key}: unexpected channel count")
        if clip["loop"]:
            require(numbers["lufsI"] is not None and -22 <= numbers["lufsI"] <= -18,
                    f"{key}: integrated loudness outside working range")
            seam = numbers["boundaryStepDbfs"]
            require(seam is None or seam <= -32, f"{key}: large waveform jump at loop join ({seam} dBFS)")
        else:
            numbers["lufsI"] = None  # Short one-shots are assessed by peak and audition.
        require(numbers["sha256"] == clip["sha256"], f"{key}: file differs from documented take")
        report[key] = numbers
        print(f"{key:24} {numbers['durationSeconds']:8.3f} s   {numbers['truePeakDbtp']:6.2f} dBTP")
    if args.report:
        args.report.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Verified {len(entries)} clips and their Unity references.")


if __name__ == "__main__":
    main()
