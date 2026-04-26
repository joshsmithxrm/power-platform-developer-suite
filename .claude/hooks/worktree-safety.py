#!/usr/bin/env python3
"""Hook: protect against catastrophic worktree removals.

PreToolUse on ``git worktree remove*``:
- Block removals targeting the main repo root.
- Block concurrent removals (lock file with live PID).

PostToolUse on the same matcher:
- Clean up the PID lock so a follow-up removal is not falsely blocked.

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
    if "worktree" not in toks:
        return None
    wt_idx = toks.index("worktree")
    # `remove` must follow `worktree` directly (skip stray "remove" tokens elsewhere,
    # e.g. inside a path argument). Search starting at wt_idx + 1.
    try:
        rm_idx = toks.index("remove", wt_idx + 1)
    except ValueError:
        return None
    for tok in toks[rm_idx + 1:]:
        if not tok.startswith("-"):
            return tok.strip("'\"")
    return None


def _is_main_root(target, project_dir):
    # Compare normalized absolute paths. project_dir may itself be a worktree;
    # this hook only blocks when the target equals the active root.
    try:
        t = os.path.abspath(target)
        p = os.path.abspath(project_dir)
        return os.path.normcase(t) == os.path.normcase(p)
    except OSError:
        return False


def main():
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)
    hook_event = payload.get("hook_event_name", "")
    tool_input = payload.get("tool_input", {}) or {}
    command = tool_input.get("command", "") or ""
    if "worktree" not in command or "remove" not in command:
        sys.exit(0)

    project_dir = normalize_msys_path(get_project_dir())
    lock_path = os.path.join(project_dir, LOCK_REL)

    if hook_event == "PostToolUse":
        # AC-164: release the PID lock once the removal completes (success or failure).
        try:
            with open(lock_path, "r") as f:
                owner = int(f.read().strip() or "0")
        except (OSError, ValueError):
            owner = 0
        if owner == os.getppid() or owner == os.getpid():
            try:
                os.unlink(lock_path)
            except OSError:
                pass
        sys.exit(0)

    target = _parse_target(command)

    if target and _is_main_root(target, project_dir):
        print("BLOCKED: cannot remove the main worktree.", file=sys.stderr)
        sys.exit(2)

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
    # Write current parent PID (Bash invoker) so the PostToolUse cleanup matches.
    try:
        with open(lock_path, "w") as f:
            f.write(str(os.getppid()))
    except OSError:
        pass
    sys.exit(0)


if __name__ == "__main__":
    main()
