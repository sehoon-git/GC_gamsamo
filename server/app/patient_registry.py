"""Public readiness metadata, separate from private narrative/rubric content."""
import json
from app.core.config import ROOT


def patient_catalog(root=ROOT):
    data = json.loads((root / "Assets/StreamingAssets/GameData/patient-catalog.json").read_text("utf-8"))
    if data["catalog_version"] != 1:
        raise ValueError("지원하지 않는 환자 목록입니다.")
    ids = [p["id"] for p in data["patients"]]
    aliases = [a for p in data["patients"] for a in p["aliases"]]
    if len(set(ids + aliases)) != len(ids + aliases):
        raise ValueError("환자 ID/이전 ID가 중복되었습니다.")
    return data


PATIENT_IDS = tuple(p["id"] for p in patient_catalog()["patients"] if p["ready"])
CONTENT_VERSIONS = {p["id"]: p["content_version"] for p in patient_catalog()["patients"] if p["ready"]}


def content_version(patient_id: str) -> str:
    return CONTENT_VERSIONS.get(patient_id, "unsupported")
