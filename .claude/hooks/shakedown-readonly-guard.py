#!/usr/bin/env python3
"""Stop hook: enforce the read-only claim of the shakedown phase.

Shakedown is documented as a validation-only phase — it must not modify
source code. Until now that rule was on the honor system. This hook closes
the gap: when ``.workflow/state.json`` shows ``phase == "shakedown"`` and
any files under ``src/`` have changed, the session is blocked from stopping
with a message listing the offending files.

Distinct from ``shakedown-readonly.py`` (PreToolUse, blocks ppds CLI
mutations via ``PPDS_SHAKEDOWN=1``). That hook guards Dataverse writes;
this hook guards source-code writes. Two different boundaries, two hooks.

Envelope: reads Claude's Stop event JSON on stdin. Honors the
``stop_hook_active`` re-entry guard. Outputs a ``decision: block`` JSON
object and exits 2 when violations are found; exits 0 otherwise.

Failure modes (git unavailable, state file missing, corrupt JSON): exit 0.
The hook must never block legitimate stops on infrastructure failure.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir


def _read_state(project_dir: str) -> dict:
    state_path = os.path.join(project_dir, ".workflow", "state.json")
    if not os.path.exists(state_path):
        return {}
    try:
        with open(state_path, "r") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return {}


def _changed_src_files(project_dir: str) -> list[str]:
    """Return tracked src/ files that differ from origin/main (staged or not).

    Falls back to working-tree diff if origin/main isn't resolvable — a
    stricter-than-necessary check is fine; a silently-missed mutation is not.
    """
    commands = [
        ["git", "diff", "--name-only", "origin/main...HEAD"],
        ["git", "diff", "--name-only", "HEAD"],
        ["git", "diff", "--name-only", "--cached"],
        # Untracked files (e.g., newly added src/ not yet staged) — also a violation.
        ["git", "ls-files", "--others", "--exclude-standard"],
    ]
    changed: set[str] = set()
    for cmd in commands:
        try:
            r = subprocess.run(
                cmd,
                cwd=project_dir,
                capture_output=True,
                text=True,
                timeout=10,
            )
        except (subprocess.TimeoutExpired, FileNotFoundError):
            continue
        if r.returncode != 0:
            continue
        for line in r.stdout.splitlines():
            line = line.strip()
            if line.startswith("src/"):
                changed.add(line)
    return sorted(changed)


def main() -> None:
    # Read stdin (Claude's Stop event envelope)
    try:
        hook_input = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError, ValueError):
        hook_input = {}

    # Re-entry guard: don't recursively block when this hook is itself
    # triggering a stop.
    if hook_input.get("stop_hook_active"):
        sys.exit(0)

    project_dir = get_project_dir()
    state = _read_state(project_dir)
    phase = state.get("phase")

    if phase != "shakedown":
        sys.exit(0)

    changed = _changed_src_files(project_dir)
    if not changed:
        sys.exit(0)

    # Truncate the list so the block message stays readable.
    preview = changed[:10]
    more = len(changed) - len(preview)
    files_line = ", ".join(preview)
    if more > 0:
        files_line += f" (+{more} more)"

    reason = (
        "Shakedown phase must not modify src/. "
        f"Detected changes: {files_line}. Revert or explain."
    )

    # Stop-hook block envelope (matches session-stop-workflow.py).
    print(json.dumps({"decision": "block", "reason": reason}))
    sys.exit(2)


if __name__ == "__main__":
    main()
