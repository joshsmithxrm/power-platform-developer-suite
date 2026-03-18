#!/usr/bin/env python3
"""
Post-commit hook: invalidates workflow state after each commit.
Clears gates.passed since the codebase has changed.
"""
import json
import os
import subprocess
import sys


def main():
    # Read stdin (Claude Code sends JSON with tool info)
    try:
        json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        pass

    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())
    state_path = os.path.join(project_dir, ".workflow", "state.json")
    os.makedirs(os.path.dirname(state_path), exist_ok=True)

    # Skip if no state file exists
    if not os.path.exists(state_path):
        sys.exit(0)

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        # Corrupted or unreadable — skip silently
        sys.exit(0)

    # Clear gates (codebase changed)
    if isinstance(state.get("gates"), dict):
        state["gates"]["passed"] = None

    # Update last_commit to current HEAD
    try:
        head = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if head.returncode == 0:
            state["last_commit"] = head.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass  # Can't resolve HEAD — skip

    try:
        with open(state_path, "w") as f:
            json.dump(state, f, indent=2)
    except OSError:
        pass  # Can't write — skip silently

    sys.exit(0)


if __name__ == "__main__":
    main()
