#!/usr/bin/env python3
"""Hook: enforce /debug after test/build failure.

PostToolUse on test/build commands: record non-zero exit to .workflow/last_failure.
PreToolUse on the same commands: block if last_failure exists and debug.last_run is older.
Exit 0: allow. Exit 2: block.
"""
import json
import os
import sys
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir  # noqa: E402

LAST_FAILURE_REL = ".workflow/last_failure"
STATE_REL = ".workflow/state.json"


def _now_iso():
    return datetime.now(timezone.utc).isoformat()


def _is_test_or_build(command):
    cmd = command.lower()
    return (
        "dotnet test" in cmd
        or "dotnet build" in cmd
        or "npm test" in cmd
        or "npm run test" in cmd
        or "npm run build" in cmd
        or (" test " in cmd and "dotnet" in cmd)
    )


def _load_state(project_dir):
    p = os.path.join(project_dir, STATE_REL)
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f) or {}
    except (OSError, json.JSONDecodeError):
        return {}


def _record_failure(project_dir, command):
    p = os.path.join(project_dir, LAST_FAILURE_REL)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8") as f:
        json.dump({"timestamp": _now_iso(), "command": command}, f)


def _clear_failure(project_dir):
    p = os.path.join(project_dir, LAST_FAILURE_REL)
    try:
        os.unlink(p)
    except OSError:
        pass


def _read_failure(project_dir):
    p = os.path.join(project_dir, LAST_FAILURE_REL)
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError):
        return None


def main():
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)
    hook_event = payload.get("hook_event_name", "")
    tool_input = payload.get("tool_input", {}) or {}
    command = tool_input.get("command", "") or ""
    if not _is_test_or_build(command):
        sys.exit(0)
    project_dir = get_project_dir()

    if hook_event == "PostToolUse":
        resp = payload.get("tool_response", {}) or {}
        rc = resp.get("returncode")
        ok = resp.get("success")
        failed = (rc is not None and rc != 0) or (ok is False)
        if failed:
            _record_failure(project_dir, command)
        else:
            _clear_failure(project_dir)
        sys.exit(0)

    if hook_event == "PreToolUse":
        last = _read_failure(project_dir)
        if not last:
            sys.exit(0)
        state = _load_state(project_dir)
        debug_last = (state.get("debug") or {}).get("last_run")
        if debug_last and debug_last > last.get("timestamp", ""):
            sys.exit(0)
        ts = last.get("timestamp")
        msg = (
            f"BLOCKED: test/build re-invocation blocked. A prior failure was recorded at {ts}.\n"
            "  Run /debug to investigate before retrying.\n"
            "  See CLAUDE.md: 'For any test/build failure, invoke /debug first.'"
        )
        print(msg, file=sys.stderr)
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
