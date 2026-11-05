import argparse
import os
from pathlib import Path
import subprocess
import sys

import uvicorn

# An editable Conda install may point at another checkout. Serve this source tree
# so the startup revision describes the code that actually handles requests.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "server"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Start only the local AI API; reuse the configured model service.")
    parser.add_argument("--port", type=int, default=8001, help="Use a separate port for a local candidate, e.g. 8002.")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("port must be between 1 and 65535")
    # Freeze the source revision at process startup, even if the checkout later moves.
    try:
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=Path(__file__).resolve().parents[1], text=True).strip()
        dirty = subprocess.check_output(["git", "status", "--porcelain", "--", "server/app", "server/prompts", "tools/serve_api.py", "ai/configs", "Assets/StreamingAssets/GameData/Patients"],
                                        cwd=Path(__file__).resolve().parents[1], text=True)
        os.environ["AI_CODE_REVISION"] = revision + ("-dirty" if dirty.strip() else "")
    except (OSError, subprocess.CalledProcessError):
        os.environ.setdefault("AI_CODE_REVISION", "unknown")
    uvicorn.run("app.main:app", host="127.0.0.1", port=args.port, workers=1, access_log=False)
