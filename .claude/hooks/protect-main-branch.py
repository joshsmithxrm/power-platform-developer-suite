"""PreToolUse hook: block Edit/Write on main branch.

Forces worktree workflow — all changes must happen on a feature branch.
Exceptions: temp directories, .worktrees/ and .claude/worktrees/ paths.

Envelope contract (v1-prelaunch fix): Claude Code wraps tool input as
``{"tool_name": "...", "tool_input": {"file_path": "..."}}``. Always read
file_path from the nested ``tool_input`` dict, not the top-level payload.
"""

import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir, normalize_msys_path


def get_current_branch(cwd: str = None) -> str:
    """Resolve the current git branch (HEAD) for *cwd* (defaults to project dir)."""
    cwd = cwd or get_project_dir()
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=cwd,
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            print(
                f"[protect-main-branch] WARNING: git command failed: "
                f"{result.stderr.strip()}",
                file=sys.stderr,
            )
            return ""
        return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError) as e:
        print(
            f"[protect-main-branch] WARNING: Could not determine git branch: {e}",
            file=sys.stderr,
        )
        return ""


def _branch_for_path(file_path: str) -> str:
    """Best-effort: derive the branch of the worktree containing *file_path*.

    Returns "" when unknown. This makes the hook worktree-aware: an Edit on a
    file inside ``.worktrees/<name>/`` (or ``.claude/worktrees/<name>/``) is
    judged against that worktree's branch, not the project root's branch
    (which is usually ``main``).
    """
    if not file_path:
        return ""
    normalized = normalize_msys_path(file_path)
    if os.path.isdir(normalized):
        candidate = normalized
    else:
        candidate = os.path.dirname(normalized)
    # Walk up until we find a directory git can resolve, or hit the filesystem
    # root. Bound the loop to avoid pathological inputs.
    for _ in range(40):
        if not candidate:
            return ""
        if not os.path.isdir(candidate):
            parent = os.path.dirname(candidate)
            if parent == candidate:
                return ""
            candidate = parent
            continue
        try:
            result = subprocess.run(
                ["git", "rev-parse", "--abbrev-ref", "HEAD"],
                cwd=candidate,
                capture_output=True,
                text=True,
                timeout=5,
            )
        except (subprocess.TimeoutExpired, FileNotFoundError):
            return ""
        if result.returncode == 0:
            return result.stdout.strip()
        parent = os.path.dirname(candidate)
        if parent == candidate:
            return ""
        candidate = parent
    return ""


def is_allowed_path(file_path: str) -> bool:
    if not file_path:
        return False
    normalized = normalize_msys_path(file_path).replace("\\", "/").lower()
    project_dir = normalize_msys_path(
        os.environ.get("CLAUDE_PROJECT_DIR", "")
    ).replace("\\", "/").lower().rstrip("/")

    relative = normalized
    if project_dir and normalized.startswith(project_dir + "/"):
        relative = normalized[len(project_dir) + 1:]

    allowed_substrings = [
        "/tmp/",
        "/temp/",
        "appdata/local/temp",
        ".worktrees/",
        ".claude/worktrees/",
    ]
    # Check both absolute and relative forms
    return any(s in normalized for s in allowed_substrings) or any(
        s in relative for s in allowed_substrings
    )


def main() -> None:
    if os.environ.get("PPDS_PIPELINE") or os.environ.get("PPDS_SHAKEDOWN"):
        sys.exit(0)

    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        # If stdin is empty or malformed, allow the operation rather than
        # blocking all edits with an unhelpful traceback.
        sys.exit(0)

    # Claude Code envelope: {"tool_name": "...", "tool_input": {"file_path": "..."}}
    # Reading file_path at the top level was a bug — it was always "" and
    # is_allowed_path("") returned False, so every Edit/Write on main was
    # blocked even for legitimate .worktrees/ paths. Fix: read it from the
    # nested ``tool_input`` dict.
    tool_input = payload.get("tool_input") or {}
    file_path = tool_input.get("file_path", "")

    # Worktree-aware: derive branch from the file's worktree when possible so
    # edits inside ``.worktrees/<name>/`` are judged against that worktree's
    # branch rather than the project root's branch.
    branch = _branch_for_path(file_path) or get_current_branch()
    if branch != "main":
        sys.exit(0)

    if is_allowed_path(file_path):
        sys.exit(0)

    print(
        "BLOCKED: You are on the main branch. "
        "Use /start to create a feature worktree.",
        file=sys.stderr,
    )
    print("  Run /start from your Claude session on main.", file=sys.stderr)
    sys.exit(2)


if __name__ == "__main__":
    main()
