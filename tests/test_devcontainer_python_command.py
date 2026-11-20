"""The devcontainer must provide the python command that the project hooks call.

The hooks in .claude/settings.json and the documented test commands run
`python`, but the Ubuntu base image only ships `python3`. The Dockerfile
installs python-is-python3 so those commands are found inside the container.
"""

import json
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent


def _hook_commands(settings):
    for entries in settings.get("hooks", {}).values():
        for entry in entries:
            for hook in entry.get("hooks", []):
                if "command" in hook:
                    yield hook["command"]


def _instructions(dockerfile):
    """Yield Dockerfile instructions with comments dropped and continuations joined."""
    lines = [line for line in dockerfile.splitlines() if not line.lstrip().startswith("#")]
    yield from re.sub(r"\\\n", " ", "\n".join(lines)).splitlines()


def test_devcontainer_provides_python_command_for_hooks():
    settings = json.loads((REPO_ROOT / ".claude" / "settings.json").read_text(encoding="utf-8"))
    if not any(cmd.split()[:1] == ["python"] for cmd in _hook_commands(settings)):
        return

    dockerfile = (REPO_ROOT / ".devcontainer" / "Dockerfile").read_text(encoding="utf-8")
    assert any(
        "apt-get install" in line and "python-is-python3" in line.split()
        for line in _instructions(dockerfile)
    ), ".devcontainer/Dockerfile must apt-get install python-is-python3 (hooks call `python`)"
