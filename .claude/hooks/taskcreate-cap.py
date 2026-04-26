#!/usr/bin/env python3
"""PreToolUse hook: cap TaskCreate at 3 in-flight background jobs.

CLAUDE.md ALWAYS rule: <=3 simultaneous background TaskCreate jobs.
Reads the in-flight registry to count active tasks.
Exit 0: allow. Exit 2: block.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir  # noqa: E402

CAP = 3
TERMINAL_STATUSES = {"completed", "cancelled", "failed", "succeeded"}


def _count_active(project_dir):
    state_path = os.path.join(project_dir, ".claude", "state", "in-flight-issues.json")
    if not os.path.exists(state_path):
        return 0
    try:
        with open(state_path, "r", encoding="utf-8") as f:
            state = json.load(f)
    except (OSError, json.JSONDecodeError):
        return 0
    entries = state.get("open_work", []) if isinstance(state, dict) else []
    count = 0
    for entry in entries:
        status = (entry.get("status") or "active").lower()
        if status in TERMINAL_STATUSES:
            continue
        count += 1
    return count


def main():
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)
    tool_name = payload.get("tool_name", "")
    # In this codebase the public tool is "Agent"; "TaskCreate"/"Task" are
    # accepted defensively in case the envelope name shifts.
    if tool_name not in ("TaskCreate", "Task", "Agent"):
        sys.exit(0)
    project_dir = get_project_dir()
    active = _count_active(project_dir)
    if active >= CAP:
        print(
            f"BLOCKED: TaskCreate cap reached ({active}/{CAP}).\n"
            f"  CLAUDE.md ALWAYS: hard cap on simultaneous background TaskCreate jobs <=3.\n"
            f"  Wait for a task to complete before starting a 4th.",
            file=sys.stderr,
        )
        sys.exit(2)
    sys.exit(0)


if __name__ == "__main__":
    main()
