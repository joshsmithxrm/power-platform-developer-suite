"""PreToolUse hook: block rm/del/Remove-Item outside the project directory.

Allows delete operations within the project worktree but blocks anything
that resolves outside it. Parses the command string from tool input to
extract target paths.
"""

import json
import os
import re
import shlex
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir, normalize_msys_path


def extract_paths_from_command(command: str) -> list[str]:
    """Extract file/directory paths from rm, del, rmdir, Remove-Item commands."""
    # Strip the command prefix and flags to get target paths
    # Remove common rm flags: -r, -f, -rf, -fr, -v, -i, --recursive, --force, etc.
    # Remove PowerShell flags: -Recurse, -Force, -Path, etc.
    try:
        tokens = shlex.split(command)
    except ValueError:
        # Malformed quoting — fall back to simple split
        tokens = command.split()
    if not tokens:
        return []

    paths = []
    skip_next = False
    for i, token in enumerate(tokens):
        if skip_next:
            skip_next = False
            continue
        # Skip the command itself
        if i == 0:
            continue
        # Skip flags
        if token.startswith("-"):
            # PowerShell -Path takes a value
            if token.lower() in ("-path", "-literalpath", "-include", "-exclude", "-filter"):
                skip_next = True
            continue
        # Everything else is a path argument (shlex already stripped quotes)
        if token:
            paths.append(token)

    return paths


def is_within_project(path: str, project_dir: str) -> bool:
    """Check if a path resolves within the project directory."""
    # Normalize the path
    path = normalize_msys_path(path)

    # If relative, resolve against project dir
    if not os.path.isabs(path):
        resolved = os.path.normpath(os.path.join(project_dir, path))
    else:
        resolved = os.path.normpath(path)

    project_norm = os.path.normpath(project_dir)

    # Case-insensitive comparison on Windows
    if sys.platform == "win32":
        resolved = resolved.lower()
        project_norm = project_norm.lower()

    # Must be within or equal to the project directory
    return resolved.startswith(project_norm + os.sep) or resolved == project_norm


def main() -> None:
    project_dir = get_project_dir()

    try:
        tool_input = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)

    command = tool_input.get("command", "")
    if not command:
        sys.exit(0)

    paths = extract_paths_from_command(command)

    # If no paths found, allow (could be a flag-only invocation like --help)
    if not paths:
        sys.exit(0)

    for path in paths:
        if not is_within_project(path, project_dir):
            print(
                f"BLOCKED: delete target '{path}' resolves outside the project directory.\n"
                f"  Project: {project_dir}\n"
                f"  Only deletions within the project worktree are allowed."
            )
            sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
