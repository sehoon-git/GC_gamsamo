"""Compare identical request snapshots, excluding failures and observed shared load.

Reports timing and score differences, not independent human quality approval.
Optional private baseline packets support reports made before request hashes existed.
"""
import argparse
import hashlib
import json

from reliability_benchmark import summary, write_private


def key(row):
    return row["patient_id"], row["scenario"], row["operation"], row["step"]


def compare(before, after, baseline_packets=()):
    for field in ("model_id", "revision", "max_model_len"):
        if before["model"][field] != after["model"][field]:
            raise ValueError("Different model/context: " + field)
    inventory_before = before.get("hardware_inventory", before.get("hardware"))
    if inventory_before != after.get("hardware_inventory", after.get("hardware")):
        raise ValueError("Hardware inventory differs; not a controlled comparison")
    packets = {key(p): p for p in baseline_packets}
    old = {key(row): row for row in before["rows"]}
    left, right, scores, excluded = [], [], [], []
    for row in after["rows"]:
        original = old.get(key(row))
        if original is None:
            continue
        fingerprint = original.get("request_sha256")
        packet = packets.get(key(row))
        if fingerprint is None and packet is not None:
            fingerprint = hashlib.sha256(json.dumps(packet["request"], sort_keys=True, ensure_ascii=False).encode()).hexdigest()
        if not fingerprint or fingerprint != row.get("request_sha256"):
            raise ValueError("Input snapshot differs or its hash is missing: " + str(key(row)))
        if not original["ok"] or not row["ok"] or original["shared_server_busy"] or row["shared_server_busy"]:
            excluded.append({"patient_id": row["patient_id"], "scenario": row["scenario"], "operation": row["operation"],
                             "before_status": original["status"], "after_status": row["status"],
                             "shared_busy": original["shared_server_busy"] or row["shared_server_busy"]})
            continue
        left.append(original); right.append(row)
        if row["operation"] == "evaluate":
            baseline_score = original.get("evaluation")
            if baseline_score is None and packet is not None:
                baseline_score = packet["response"]["state"]["sessions"][-1]["evaluation"]
            fields = ("patient_score", "counselor_score", "cleared")
            scores.append({"patient_id": row["patient_id"], "scenario": row["scenario"],
                           "before": {f: baseline_score[f] for f in fields},
                           "after": {f: row["evaluation"][f] for f in fields}})
    generation_changes = {}
    for role, config in before["model"]["roles"].items():
        other = after["model"]["roles"][role]
        changed = {k: {"before": config.get(k), "after": other.get(k)} for k in config.keys() | other.keys() if config.get(k) != other.get(k)}
        if changed:
            generation_changes[role] = changed
    return {"before_label": before["label"], "after_label": after["label"],
            "paired_requests": len(left), "excluded_pairs": excluded,
            "before": summary(left), "after": summary(right), "scores": scores,
            "changed_generation_settings": generation_changes,
            "scope": "identical request JSON, model revision and hardware inventory; prompts/source may change; API time excludes client disk save",
            "human_quality_approval": False}


def main():
    from pathlib import Path
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--baseline-records", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    packets = json.loads(args.baseline_records.read_text()) if args.baseline_records else []
    result = compare(json.loads(args.before.read_text()), json.loads(args.after.read_text()), packets)
    write_private(args.output, result)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
