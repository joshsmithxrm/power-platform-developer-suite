#!/usr/bin/env python3
"""
PreToolUse hook: guard against broken hook paths in .claude/settings.json.

Prevents the "hook path doubling" problem where a hook command is written as:
  python ".claude/hooks/.claude/hooks/foo.py"
instead of:
  python ".claude/hooks/foo.py"

When Python cannot find the file it exits code 2, which Claude Code interprets
as BLOCK — bricking the session in an infinite stop-hook retry loop.

This hook applies to:
  - Write tool on .claude/settings.json or settings.local.json:
      Parses the full proposed content as JSON, extracts every "command" value
      in the hooks section, and verifies each referenced .py file exists.
  - Edit tool on .claude/settings.json or settings.local.json:
      Checks the new_string fragment for the doubled pattern
      .claude/hooks/.claude/hooks/ (can't reconstruct full file from a diff).

Exit 0 = allow, Exit 2 = block with a message on stderr.
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir

# Regex to extract a path argument from a python "..." command string.
# Matches: python "some/path/to/file.py"  or  python 'some/path/to/file.py'
_PATH_RE = re.compile(r"""python\s+["']([^"']+)["']""")

# The doubled pattern that caused the original bug.
DOUBLED_PATTERN = ".claude/hooks/.claude/hooks/"


def _is_settings_file(file_path: str) -> bool:
    """Return True if the path refers to settings.json or settings.local.json."""
    if not file_path:
        return False
    normalized = file_path.replace("\\", "/")
    basename = normalized.rstrip("/").split("/")[-1]
    return basename in ("settings.json", "settings.local.json")


def _extract_hook_commands(settings: dict) -> list[str]:
    """Walk the hooks section and return every command string found."""
    commands = []
    hooks_section = settings.get("hooks", {})
    for _event, entries in hooks_section.items():
        if not isinstance(entries, list):
            continue
        for entry in entries:
            if not isinstance(entry, dict):
                continue
            for hook in entry.get("hooks", []):
                if not isinstance(hook, dict):
                    continue
                if hook.get("type") == "command":
                    cmd = hook.get("command", "")
                    if cmd:
                        commands.append(cmd)
    return commands


def _check_write(content: str, project_dir: str) -> list[str]:
    """
    Validate all hook command paths in the proposed settings.json content.

    Returns a list of human-readable error strings (empty = OK).
    """
    errors = []

    try:
        settings = json.loads(content)
    except json.JSONDecodeError as exc:
        # Malformed JSON — not our job to block it, let Claude Code handle it.
        return []

    commands = _extract_hook_commands(settings)

    for cmd in commands:
        # Check for the doubled pattern first.
        if DOUBLED_PATTERN in cmd:
            errors.append(
                f"Doubled hook path detected in command: {cmd!r}\n"
                f"  Pattern '{DOUBLED_PATTERN}' found — "
                "correct it to '.claude/hooks/<filename>.py'"
            )
            continue  # No point doing existence check on a bad path.

        # Extract the file path from the command string.
        m = _PATH_RE.search(cmd)
        if not m:
            # Command doesn't look like a python "path" invocation — skip.
            continue

        hook_file = m.group(1)

        # Resolve relative to project root.
        full_path = os.path.normpath(os.path.join(project_dir, hook_file))
        if not os.path.exists(full_path):
            errors.append(
                f"Hook command references a file that does not exist: {hook_file!r}\n"
                f"  Full resolved path: {full_path}\n"
                f"  Command: {cmd!r}"
            )

    return errors


def _check_edit(new_string: str) -> list[str]:
    """
    For Edit operations we only have the changed fragment, not the full file.
    Check for the doubled pattern in the new_string.

    Returns a list of human-readable error strings (empty = OK).
    """
    errors = []
    if DOUBLED_PATTERN in new_string:
        # Find all occurrences to report them.
        lines = new_string.splitlines()
        bad_lines = [
            f"  line: {line.strip()!r}"
            for line in lines
            if DOUBLED_PATTERN in line
        ]
        errors.append(
            "Doubled hook path detected in edit:\n"
            + "\n".join(bad_lines)
            + f"\n  Pattern '{DOUBLED_PATTERN}' found — "
            "correct it to '.claude/hooks/<filename>.py'"
        )
    return errors


def main():
    try:
        hook_input = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        # Can't parse input — fail open.
        sys.exit(0)

    tool_input = hook_input.get("tool_input", {})
    tool_name = hook_input.get("tool_name", "")

    # Determine which file is being operated on.
    file_path = tool_input.get("file_path", "")

    # For Edit, file_path is present. For Write, file_path is also present.
    if not _is_settings_file(file_path):
        sys.exit(0)

    project_dir = get_project_dir()
    errors = []

    if tool_name == "Write":
        content = tool_input.get("content", "")
        errors = _check_write(content, project_dir)

    elif tool_name == "Edit":
        new_string = tool_input.get("new_string", "")
        errors = _check_edit(new_string)

    else:
        # Unknown tool — check both styles by inspecting available keys.
        if "content" in tool_input:
            errors = _check_write(tool_input["content"], project_dir)
        elif "new_string" in tool_input:
            errors = _check_edit(tool_input["new_string"])

    if errors:
        print(
            "BLOCKED by settings-json-guard: invalid hook path(s) detected.\n"
            + "\n".join(f"  ✗ {e}" for e in errors),
            file=sys.stderr,
        )
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
