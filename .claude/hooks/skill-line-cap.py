"""PreToolUse hook: enforce 150-line cap on SKILL.md edits.

Skills are loaded selectively. Each SKILL.md is the entry point - rationale
and worked examples belong in REFERENCE.md (see .claude/skills/TWO-FILE-PATTERN.md).
This hook fails fast at edit time so authors get feedback before commit.
"""

from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import normalize_msys_path  # noqa: E402

LINE_CAP = 150


def is_skill_md(file_path: str) -> bool:
    """True if file_path targets a SKILL.md file (any directory)."""
    if not file_path:
        return False
    normalized = normalize_msys_path(file_path).replace("\\", "/")
    return normalized.endswith("/SKILL.md") or normalized == "SKILL.md"


def count_lines(content: str) -> int:
    """Count physical lines, including a final unterminated line."""
    return len(content.splitlines())


def project_edit(original, old_string, new_string, replace_all):
    """Simulate an Edit operation. Returns projected content or None on failure."""
    if replace_all:
        return original.replace(old_string, new_string)
    occurrences = original.count(old_string)
    if occurrences != 1:
        return None
    return original.replace(old_string, new_string, 1)


def main():
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)

    tool_name = payload.get("tool_name", "")
    tool_input = payload.get("tool_input", {}) or {}
    file_path = tool_input.get("file_path", "")

    if not is_skill_md(file_path):
        sys.exit(0)

    abs_path = file_path
    if not os.path.isabs(abs_path):
        project_dir = normalize_msys_path(os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd()))
        abs_path = os.path.join(project_dir, abs_path)

    if tool_name == "Write":
        projected = tool_input.get("content", "")
    elif tool_name == "Edit":
        try:
            with open(abs_path, "r", encoding="utf-8") as f:
                original = f.read()
        except OSError:
            sys.exit(0)
        projected = project_edit(
            original,
            tool_input.get("old_string", ""),
            tool_input.get("new_string", ""),
            tool_input.get("replace_all", False),
        )
        if projected is None:
            sys.exit(0)
    else:
        sys.exit(0)

    line_count = count_lines(projected)
    if line_count <= LINE_CAP:
        sys.exit(0)

    print(
        f"BLOCKED: SKILL.md would exceed {LINE_CAP} lines ({line_count}). "
        f"Move rationale/examples to REFERENCE.md. "
        f"See .claude/skills/TWO-FILE-PATTERN.md.",
        file=sys.stderr,
    )
    sys.exit(2)


if __name__ == "__main__":
    main()
