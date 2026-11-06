"""Static font-advance estimate for private benchmark replies; NOT a TMP/Unity test.

Uses the checked-in font and body dimensions (790 x 76, 26 pt, optional 24 pt).
Run DialogueBubbleChecks in Unity for actual wrapping, masking and glyph bounds.
Only hashes, counts and estimates are written; conversation text is not exported.
Requires fontTools in the measurement environment, not in the serving environment.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re

from fontTools.ttLib import TTFont

ROOT = Path(__file__).resolve().parents[2]


def estimate(text, font, size, width=790):
    cmap, metrics = font.getBestCmap(), font["hmtx"].metrics
    units = font["head"].unitsPerEm
    advance = lambda c: metrics[cmap.get(ord(c), ".notdef")][0] * size / units
    lines, used = 1, 0.0
    for paragraph in text.splitlines() or [""]:
        if used: lines += 1; used = 0
        for word in re.findall(r"\S+\s*", paragraph):
            word_width = sum(advance(c) for c in word)
            if used and used + word_width > width:
                lines += 1; used = 0
            for c in word:
                value = advance(c)
                if used and used + value > width:
                    lines += 1; used = 0
                used += value
    # TMP font asset lineHeight 57.92 at pointSize 40; kerning/spacing are zero.
    height = lines * 57.92 * size / 40
    return {"lines": lines, "estimated_height": round(height, 3), "fits": height <= 76,
            "missing_glyphs": sum(ord(c) not in cmap for c in text if not c.isspace())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("records", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    font_path = ROOT / "Assets/_Project/Fonts/NotoSansCJKkr-Regular.otf"
    font = TTFont(font_path)
    rows = []
    for packet in json.loads(args.records.read_text()):
        state = packet["response"].get("state")
        if not state or packet["operation"] not in ("start", "open", "turn", "revisit"): continue
        session = state["sessions"][-1]
        text = session["turns"][-1]["patient_text"] if packet["operation"] == "turn" else session["opening"]["patient_text"]
        rows.append({"patient_id": packet["patient_id"], "operation": packet["operation"], "step": packet["step"],
            "text_sha256": hashlib.sha256(text.encode()).hexdigest(), "characters": len(text),
            "at_26pt": estimate(text, font, 26), "at_24pt": estimate(text, font, 24)})
    report = {"method": "font advance / whitespace word-wrap estimate; not Unity TMP rendering",
        "font_sha256": hashlib.sha256(font_path.read_bytes()).hexdigest(), "body_size": [790, 76],
        "n": len(rows), "over_56_characters": sum(row["characters"] > 56 for row in rows),
        "estimated_overflow_at_24pt": sum(not row["at_24pt"]["fits"] for row in rows), "rows": rows}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({key: value for key, value in report.items() if key != "rows"}))


if __name__ == "__main__": main()
