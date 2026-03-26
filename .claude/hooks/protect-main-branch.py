"""PreToolUse hook: block Edit/Write on main branch.

Forces worktree workflow — all changes must happen on a feature branch.
Exceptions: .plans/ (ephemeral, gitignored), temp directories.
"""

import json
import os
import subprocess
import sys


def get_current_branch() -> str:
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=os.environ.get("CLAUDE_PROJECT_DIR", "."),
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
    normalized = file_path.replace("\\", "/").lower()
    # Strip project dir prefix so absolute paths work the same as relative ones
    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", "").replace("\\", "/").lower().rstrip("/")
    if project_dir and normalized.startswith(project_dir + "/"):
        normalized = normalized[len(project_dir) + 1:]
    allowed_prefixes = []
    allowed_substrings = [
        "/tmp/",
        "/temp/",
        "appdata/local/temp",
        ".worktrees/",
    ]
    return any(normalized.startswith(p) for p in allowed_prefixes) or any(
        s in normalized for s in allowed_substrings
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
        "Create a worktree before making changes."
    )
    print("  git worktree add .worktrees/<name> -b <branch>")
    print("See CLAUDE.md worktree conventions.")
    sys.exit(2)


if __name__ == "__main__":
    main()
