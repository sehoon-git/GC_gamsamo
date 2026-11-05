"""Download exactly the approved public model revision; no inference or conversion."""
import json
from pathlib import Path

from huggingface_hub import snapshot_download

ROOT = Path(__file__).resolve().parents[1]


def main():
    config = json.loads((ROOT / "ai/configs/seoyun.json").read_text("utf-8"))
    target = ROOT / config["local_directory"]
    snapshot_download(
        repo_id=config["model_id"],
        revision=config["revision"],
        local_dir=target,
    )
    (target / ".seoyun-model.json").write_text(
        json.dumps({"model_id": config["model_id"], "revision": config["revision"]}, indent=2),
        encoding="utf-8",
    )
    print(f"Model downloaded: {target}")


if __name__ == "__main__":
    main()
