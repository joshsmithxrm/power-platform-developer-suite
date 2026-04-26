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
        with open(state_path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return {}


def _changed_src_files(project_dir: str) -> list[str]:
    """Return local src/ files that are modified, staged, or untracked.

    Scope is deliberately the *working-tree + index* state, not a branch
    comparison: shakedown is a session-level read-only claim, so we only
    care about changes introduced during this session, not commits that
    predate it on the feature branch. Using a branch-range would falsely
    block sessions on branches that legitimately already contain committed
    src/ changes.

    Implemented with a single ``git status --porcelain -- src/`` call —
    one subprocess (staged + unstaged + untracked in one shot) instead of
    four. The ``--`` separator ensures ``src/`` is parsed as a pathspec,
    not an option, even if the repo layout ever gains a file starting
    with ``-``.
    """
    try:
        r = subprocess.run(
            # --untracked-files=all expands new untracked directories into
            # individual file entries (default "normal" collapses them to
            # the dir name, which would under-report the violation set).
            ["git", "status", "--porcelain", "--untracked-files=all", "--", "src/"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return []
    if r.returncode != 0:
        return []
    changed: set[str] = set()
    for line in r.stdout.splitlines():
        # Porcelain v1 format: XY<space>path — X=index, Y=worktree status,
        # then a single space, then the path. Rename entries are "R  a -> b"
        # — we keep the destination path (after "-> ") since that's the
        # current src/ file.
        if len(line) < 4:
            continue
        path = line[3:]
        if " -> " in path:
            path = path.split(" -> ", 1)[1]
        path = path.strip().strip('"')
        if path.startswith("src/"):
            changed.add(path)
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
