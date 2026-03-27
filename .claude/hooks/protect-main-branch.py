"""PreToolUse hook: block Edit/Write on main branch.

Forces worktree workflow — all changes must happen on a feature branch.
Exceptions: temp directories, .worktrees/ paths.
"""

import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir, normalize_msys_path


def get_current_branch() -> str:
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=get_project_dir(),
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            print(f"[protect-main-branch] WARNING: git command failed: {result.stderr.strip()}", file=sys.stderr)
            return ""
        return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError) as e:
        print(f"[protect-main-branch] WARNING: Could not determine git branch: {e}", file=sys.stderr)
        return ""


def is_allowed_path(file_path: str) -> bool:
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
    ]
    # Check both absolute and relative forms
    return any(s in normalized for s in allowed_substrings) or any(
        s in relative for s in allowed_substrings
    )

def main() -> None:
    branch = get_current_branch()
    if branch != "main":
        sys.exit(0)

    try:
        tool_input = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        # If stdin is empty or malformed, allow the operation rather than
        # blocking all edits with an unhelpful traceback.
        sys.exit(0)

    file_path = tool_input.get("file_path", "")

    if is_allowed_path(file_path):
        sys.exit(0)

    print(
        "BLOCKED: You are on the main branch. "
        "Use /start to create a feature worktree."
    )
    print("  Run /start from your Claude session on main.")
    sys.exit(2)


if __name__ == "__main__":
    main()
