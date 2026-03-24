#!/usr/bin/env python3
"""
Canonical workflow state utility — the ONLY way to write .workflow/state.json.
Skills and commands call this script instead of writing JSON by hand.

Usage:
  python scripts/workflow-state.py set gates.passed now
  python scripts/workflow-state.py set gates.commit_ref abc123def
  python scripts/workflow-state.py set review.findings 3
  python scripts/workflow-state.py set pr.gemini_triaged true
  python scripts/workflow-state.py set-null gates.passed
  python scripts/workflow-state.py append issues 602
  python scripts/workflow-state.py get issues
  python scripts/workflow-state.py init feature/my-branch
  python scripts/workflow-state.py show
  python scripts/workflow-state.py delete

Magic values for 'set':
  now   → current UTC ISO 8601 timestamp
  true  → boolean true
  false → boolean false
  digits → integer
"""
import json
import os
import sys
from datetime import datetime, timezone


def get_state_path():
    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())
    state_dir = os.path.join(project_dir, ".workflow")
    os.makedirs(state_dir, exist_ok=True)
    return os.path.join(state_dir, "state.json")


def read_state():
    path = get_state_path()
    if not os.path.exists(path):
        return {}
    try:
        with open(path, "r") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return {}


def write_state(state):
    path = get_state_path()
    with open(path, "w") as f:
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


def main():
    if len(sys.argv) < 2:
        print(
            "Usage: workflow-state.py <command> [args...]\n"
            "Commands: set <key> <value>, set-null <key>, init <branch>, show, delete",
            file=sys.stderr,
        )
        sys.exit(1)

    command = sys.argv[1]

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
        if len(sys.argv) < 4:
            print("Usage: workflow-state.py set <key> <value>", file=sys.stderr)
            sys.exit(1)
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
            print("Usage: workflow-state.py append <key> <value>", file=sys.stderr)
            sys.exit(1)
        key = sys.argv[2]
        value = coerce_value(sys.argv[3])
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

    print(f"Unknown command: {command}", file=sys.stderr)
    sys.exit(1)


if __name__ == "__main__":
    main()
