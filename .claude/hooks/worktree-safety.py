#!/usr/bin/env python3
"""PreToolUse hook: protect against catastrophic worktree removals.

Blocks:
- ``git worktree remove`` targeting the main repo root
- Concurrent worktree removals (lock file with live PID)

Triggers on Bash matching ``git worktree remove*``.
Exit 0: allow. Exit 2: block.
"""
import json
import os
import shlex
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir, normalize_msys_path  # noqa: E402

LOCK_REL = ".workflow/worktree-remove.lock"


def _pid_alive(pid):
    if pid <= 0:
        return False
    try:
        if sys.platform.startswith("win"):
            r = subprocess.run(
                ["tasklist", "/FI", f"PID eq {pid}", "/NH"],
                capture_output=True, text=True, timeout=5,
            )
            return str(pid) in r.stdout
        os.kill(pid, 0)
        return True
    except (ProcessLookupError, PermissionError, OSError, subprocess.TimeoutExpired):
        return False


def _parse_target(command):
    try:
        toks = shlex.split(command, posix=False)
    except ValueError:
        return None
    # Expect: git worktree remove [--force] <path>
    if "worktree" in toks and "remove" in toks:
        ridx = toks.index("remove")
        for tok in toks[ridx + 1:]:
            if not tok.startswith("-"):
                return tok.strip("'\"")
    return None


def _is_main_root(target, project_dir):
    # Resolve both, compare. project_dir may be a worktree; the "main root"
    # is its common parent or itself if invoked from main. We compare normalized
    # absolute paths.
    try:
        t = os.path.abspath(target)
        p = os.path.abspath(project_dir)
        return os.path.normcase(t) == os.path.normcase(p)
    except Exception:
        return False


def main():
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)
    tool_input = payload.get("tool_input", {}) or {}
    command = tool_input.get("command", "") or ""
    if "worktree" not in command or "remove" not in command:
        sys.exit(0)

    project_dir = normalize_msys_path(get_project_dir())
    target = _parse_target(command)

    if target and _is_main_root(target, project_dir):
        print("BLOCKED: cannot remove the main worktree.", file=sys.stderr)
        sys.exit(2)

    # Concurrent removal lock
    lock_path = os.path.join(project_dir, LOCK_REL)
    os.makedirs(os.path.dirname(lock_path), exist_ok=True)
    if os.path.exists(lock_path):
        try:
            with open(lock_path, "r") as f:
                other_pid = int(f.read().strip() or "0")
        except (OSError, ValueError):
            other_pid = 0
        if other_pid and _pid_alive(other_pid):
            print(
                f"BLOCKED: another worktree removal is in progress (PID {other_pid}). "
                f"Wait for it to finish.",
                file=sys.stderr,
            )
            sys.exit(2)
    # Write current PID; cleanup is best-effort (next invocation detects stale).
    try:
        with open(lock_path, "w") as f:
            f.write(str(os.getpid()))
    except OSError:
        pass
    sys.exit(0)


if __name__ == "__main__":
    main()
