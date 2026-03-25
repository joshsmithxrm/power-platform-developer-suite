"""PreToolUse hook: block git checkout to non-main branches in the main repo folder.

Prevents branch pivoting — use worktrees instead of checking out feature branches
in the main folder.
"""

import json
import os
import re
import subprocess
import sys


def is_main_repo(project_dir: str) -> bool:
    """Check if project_dir is the main repo root (not a worktree)."""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--git-dir", "--git-common-dir"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=5,
            check=True,
        )
        git_dir, git_common = result.stdout.strip().splitlines()
        # In a worktree, git-dir != git-common-dir
        return git_dir.replace("\\", "/") == git_common.replace("\\", "/")
    except (subprocess.TimeoutExpired, FileNotFoundError, subprocess.CalledProcessError):
        return False


def main() -> None:
    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())

    # Only enforce in the main repo folder, not in worktrees
    if not is_main_repo(project_dir):
        sys.exit(0)

    try:
        tool_input = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)

    command = tool_input.get("command", "")

    # Allow: git checkout main, git checkout master
    if re.search(r"git checkout\s+(main|master)\b", command):
        sys.exit(0)

    # Allow: git checkout -- <file> (file restore)
    if "checkout --" in command or "checkout -p" in command:
        sys.exit(0)

    # Allow: git checkout -b (creating a new branch — worktree add is preferred but not blocked)
    if "checkout -b" in command:
        sys.exit(0)

    # Block: git checkout <anything-else> in main folder
    print(
        "BLOCKED: Do not checkout feature branches in the main folder.\n"
        "  Create a worktree instead:\n"
        "  git worktree add .worktrees/<name> -b <branch>",
        file=sys.stderr,
    )
    sys.exit(2)


if __name__ == "__main__":
    main()
