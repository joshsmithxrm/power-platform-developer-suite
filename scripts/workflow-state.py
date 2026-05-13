#!/usr/bin/env python3
"""
Canonical workflow state utility — the ONLY way to write .workflow/state.json.
Skills and commands call this script instead of writing JSON by hand.

Usage:
  python scripts/workflow-state.py set gates.passed now
  python scripts/workflow-state.py set gates.commit_ref abc123def
  python scripts/workflow-state.py set review.findings 3
  python scripts/workflow-state.py set pr.gemini_triaged true
  echo 'complex value' | python scripts/workflow-state.py set --value-stdin my.key
  python scripts/workflow-state.py set-null gates.passed
  python scripts/workflow-state.py append issues 602
  python scripts/workflow-state.py get issues
  python scripts/workflow-state.py init feature/my-branch
  python scripts/workflow-state.py show
  python scripts/workflow-state.py delete
  python scripts/workflow-state.py bump routing_gates.backlog.fired_count

Magic values for 'set':
  now   → current UTC ISO 8601 timestamp
  true  → boolean true
  false → boolean false
  digits → integer
"""
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone


def _get_worktree_root():
    """Return the git toplevel for the current working tree.

    In a worktree this returns .worktrees/<name>/, on main it returns
    the repo root.  We use this instead of CLAUDE_PROJECT_DIR so that
    workflow state lands in the worktree, not the main repo.

    Set WORKFLOW_STATE_TEST_SKIP_GIT=1 to skip the git subprocess (tests).
    """
    if os.environ.get("WORKFLOW_STATE_TEST_SKIP_GIT"):
        return os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True, text=True, timeout=5,
        )
        if result.returncode == 0:
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass
    return os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())


def _is_main_branch():
    """Return True if the current branch is main/master.

    Set WORKFLOW_STATE_TEST_SKIP_GIT=1 to skip the git subprocess (tests).
    """
    if os.environ.get("WORKFLOW_STATE_TEST_SKIP_GIT"):
        return False
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            capture_output=True, text=True, timeout=5,
        )
        if result.returncode == 0:
            return result.stdout.strip() in ("main", "master")
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass
    return False


def get_state_path():
    root = _get_worktree_root()
    state_dir = os.path.join(root, ".workflow")
    os.makedirs(state_dir, exist_ok=True)
    return os.path.join(state_dir, "state.json")


def read_state():
    path = get_state_path()
    if not os.path.exists(path):
        return {}
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return {}


def write_state(state):
    path = get_state_path()
    with open(path, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2)
        f.write("\n")
    print(f"Updated {path}", file=sys.stderr)


def coerce_value(raw):
    """Convert string value to appropriate Python type."""
    if raw == "now":
        return datetime.now(timezone.utc).isoformat()
    if raw == "true":
        return True
    if raw == "false":
        return False
    try:
        return int(raw)
    except ValueError:
        return raw


def set_nested(state, key, value):
    """Set a dotted key path (e.g., 'gates.passed') in the state dict."""
    parts = key.split(".")
    current = state
    for part in parts[:-1]:
        if part not in current or not isinstance(current[part], dict):
            current[part] = {}
        current = current[part]
    current[parts[-1]] = value


KEY_PATTERN = re.compile(r"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$")


def bump_nested(state, key):
    """Increment integer at dotted key path, initializing to 1 if absent.

    Raises ValueError if the existing value is not an int, or is a bool
    (bool is a subclass of int and is rejected to prevent silent type
    corruption). Intermediate path segments must be dicts; any non-dict
    intermediate raises ValueError without mutating state.
    Inherits main-branch blocking from the bump command handler's placement
    in write_commands — same guard as set/append/delete.
    """
    parts = key.split(".")
    # Pass 1: validate the full path without mutating state. Reject any
    # intermediate that exists and is not a dict, so a multi-segment key
    # cannot silently destroy a non-dict value.
    cursor = state
    for part in parts[:-1]:
        if part in cursor:
            if not isinstance(cursor[part], dict):
                raise ValueError(key)
            cursor = cursor[part]
        else:
            cursor = None
            break
    final_key = parts[-1]
    if cursor is not None and final_key in cursor:
        existing = cursor[final_key]
        if isinstance(existing, bool) or not isinstance(existing, int):
            # bool is a subclass of int; reject before the int check.
            raise ValueError(key)
    # Pass 2: validation passed; create intermediate dicts as needed and
    # increment (or initialize to 1).
    current = state
    for part in parts[:-1]:
        if part not in current:
            current[part] = {}
        current = current[part]
    existing = current.get(final_key)
    if existing is None:
        current[final_key] = 1
    else:
        current[final_key] = existing + 1


def main():
    if len(sys.argv) < 2:
        print(
            "Usage: workflow-state.py <command> [args...]\n"
            "Commands: set <key> <value>, set-null <key>, init <branch>, show, delete, bump <key>",
            file=sys.stderr,
        )
        sys.exit(1)

    command = sys.argv[1]

    # Read-only commands are fine anywhere; writes are blocked on main
    write_commands = ("set", "set-null", "init", "append", "delete", "bump")
    if command in write_commands and _is_main_branch():
        print(
            "BLOCKED: Cannot write workflow state on main. "
            "Workflow state belongs in a worktree.",
            file=sys.stderr,
        )
        sys.exit(2)

    if command == "show":
        state = read_state()
        print(json.dumps(state, indent=2))
        sys.exit(0)

    if command == "delete":
        path = get_state_path()
        if os.path.exists(path):
            os.remove(path)
            print(f"Deleted {path}", file=sys.stderr)
        else:
            print("No workflow state to clear.", file=sys.stderr)
        sys.exit(0)

    if command == "init":
        if len(sys.argv) < 3:
            print("Usage: workflow-state.py init <branch-name>", file=sys.stderr)
            sys.exit(1)
        branch = sys.argv[2]
        state = {
            "branch": branch,
            "started": datetime.now(timezone.utc).isoformat(),
        }
        write_state(state)
        sys.exit(0)

    if command == "set":
        if len(sys.argv) >= 3 and sys.argv[2] == "--value-stdin":
            if len(sys.argv) < 4:
                print("Usage: workflow-state.py set --value-stdin <key>", file=sys.stderr)
                sys.exit(1)
            key = sys.argv[3]
            value = sys.stdin.buffer.read().decode("utf-8")
        elif len(sys.argv) < 4:
            print("Usage: workflow-state.py set <key> <value>", file=sys.stderr)
            sys.exit(1)
        else:
            key = sys.argv[2]
            value = coerce_value(sys.argv[3])
        state = read_state()
        set_nested(state, key, value)
        write_state(state)
        sys.exit(0)

    if command == "set-null":
        if len(sys.argv) < 3:
            print("Usage: workflow-state.py set-null <key>", file=sys.stderr)
            sys.exit(1)
        key = sys.argv[2]
        state = read_state()
        set_nested(state, key, None)
        write_state(state)
        sys.exit(0)

    if command == "append":
        if len(sys.argv) < 4:
            print("Usage: workflow-state.py append <key> <value1> [<value2> ...]", file=sys.stderr)
            sys.exit(1)
        key = sys.argv[2]
        values = [coerce_value(v) for v in sys.argv[3:]]
        state = read_state()
        # Navigate to parent, get or create list at final key
        parts = key.split(".")
        current = state
        for part in parts[:-1]:
            if part not in current or not isinstance(current[part], dict):
                current[part] = {}
            current = current[part]
        final_key = parts[-1]
        if final_key not in current or not isinstance(current[final_key], list):
            current[final_key] = []
        for value in values:
            if value not in current[final_key]:
                current[final_key].append(value)
        write_state(state)
        sys.exit(0)

    if command == "get":
        if len(sys.argv) < 3:
            print("Usage: workflow-state.py get <key>", file=sys.stderr)
            sys.exit(1)
        key = sys.argv[2]
        state = read_state()
        parts = key.split(".")
        current = state
        for part in parts:
            if isinstance(current, dict) and part in current:
                current = current[part]
            else:
                sys.exit(0)
        print(json.dumps(current))
        sys.exit(0)

    if command == "bump":
        if len(sys.argv) < 3:
            print("Usage: workflow-state.py bump <key>", file=sys.stderr)
            sys.exit(1)
        key = sys.argv[2]
        if not KEY_PATTERN.match(key):
            print(
                f"ERROR: invalid key {key} — keys must use dot-separated alphanumeric segments"
                f" (e.g., routing_gates.backlog.fired_count)",
                file=sys.stderr,
            )
            sys.exit(3)
        state = read_state()
        try:
            bump_nested(state, key)
        except ValueError:
            print(f"ERROR: cannot bump value at {key} (intermediate path is not a dict or value is not an integer)", file=sys.stderr)
            sys.exit(4)
        write_state(state)
        sys.exit(0)

    print(f"Unknown command: {command}", file=sys.stderr)
    sys.exit(1)


if __name__ == "__main__":
    main()
