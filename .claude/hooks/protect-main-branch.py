"""PreToolUse hook: block Edit/Write on main branch.

Forces worktree workflow — all implementation changes must happen on a feature branch.
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
        return result.stdout.strip()
    except Exception:
        return ""


def is_allowed_path(file_path: str) -> bool:
    normalized = file_path.replace("\\", "/").lower()
    allowed_prefixes = [
        ".plans/",
        ".plans\\",
    ]
    allowed_substrings = [
        "/tmp/",
        "\\tmp\\",
        "/temp/",
        "\\temp\\",
        "appdata/local/temp",
    ]
    return any(normalized.startswith(p) for p in allowed_prefixes) or any(
        s in normalized for s in allowed_substrings
    )


def main() -> None:
    branch = get_current_branch()
    if branch != "main":
        sys.exit(0)

    tool_input = json.loads(sys.stdin.read())
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
