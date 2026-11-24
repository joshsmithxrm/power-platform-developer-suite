"""The devcontainer must install the .NET SDK that global.json pins.

global.json uses rollForward "latestPatch", which only rolls forward within the
pinned feature band (10.0.1xx). The devcontainer base image ships a different
band, so the Dockerfile installs the pinned SDK itself. If the two drift apart,
restore and build fail inside the container while CI stays green.
"""

import json
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent


def test_devcontainer_sdk_matches_global_json():
    global_json = json.loads((REPO_ROOT / "global.json").read_text(encoding="utf-8"))
    pinned = global_json["sdk"]["version"]

    dockerfile = (REPO_ROOT / ".devcontainer" / "Dockerfile").read_text(encoding="utf-8")
    match = re.search(r"^ARG DOTNET_SDK_VERSION=(\S+)\s*$", dockerfile, re.MULTILINE)
    assert match, ".devcontainer/Dockerfile must declare ARG DOTNET_SDK_VERSION"
    assert match.group(1) == pinned, (
        f".devcontainer/Dockerfile installs SDK {match.group(1)} but global.json pins {pinned}"
    )
