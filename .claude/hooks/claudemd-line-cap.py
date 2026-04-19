"""PreToolUse hook: enforce 100-line cap on CLAUDE.md edits.

CLAUDE.md is loaded into every session. A bloated file degrades Claude's
ability to follow its actual instructions (Anthropic best-practices). This
hook fails fast at edit time so authors get feedback before commit.

Triggers on Edit/Write where ``payload["tool_input"]["file_path"]`` ends in
``CLAUDE.md`` (case-sensitive — the convention is uppercase).

For Write: counts lines in ``payload["tool_input"]["content"]``.
For Edit: simulates the edit, then counts lines in the projected file.

Exit codes:
- 0: allow (file does not match, or post-edit line count <= 100)
- 2: block (post-edit line count > 100)

See docs/CLAUDE-MD-GOVERNANCE.md for the rationale and the 4-question test.
"""

from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import normalize_msys_path  # noqa: E402

LINE_CAP = 100


def is_claude_md(file_path: str) -> bool:
    """True if file_path targets a CLAUDE.md file (any directory)."""
    if not file_path:
        return False
    normalized = normalize_msys_path(file_path).replace("\\", "/")
    return normalized.endswith("/CLAUDE.md") or normalized == "CLAUDE.md"


def count_lines(content: str) -> int:
    """Match GNU ``wc -l`` semantics: count newline characters.

    A trailing newline counts; missing trailing newline does not. This matches
    what the commit-time gate does, so behavior is consistent across the chain.
    """
    return content.count("\n")


def project_edit(original: str, old_string: str, new_string: str, replace_all: bool) -> str | None:
    """Simulate an Edit operation. Returns projected content or None on failure."""
    if replace_all:
        return original.replace(old_string, new_string)
    # Single-replacement Edit requires old_string to be unique. If not present
    # or not unique, the underlying Edit tool would have failed — return None
    # to signal "can't project" and fall through to allow (the Edit tool itself
    # will produce the right error).
    occurrences = original.count(old_string)
    if occurrences != 1:
        return None
    return original.replace(old_string, new_string, 1)


def main() -> None:
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)

    tool_name = payload.get("tool_name", "")
    tool_input = payload.get("tool_input", {}) or {}
    file_path = tool_input.get("file_path") or payload.get("file_path", "")

    if not is_claude_md(file_path):
        sys.exit(0)

    # Resolve the absolute path so we can read the current file content for
    # Edit projection. Use CLAUDE_PROJECT_DIR-anchored path if relative.
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
            # If we can't read it, let Claude attempt the edit; the Edit tool
            # will surface a useful error.
            sys.exit(0)
        projected = project_edit(
            original,
            tool_input.get("old_string", ""),
            tool_input.get("new_string", ""),
            tool_input.get("replace_all", False),
        )
        if projected is None:
            # Couldn't simulate (non-unique or absent old_string) — allow,
            # the Edit tool will report the real problem.
            sys.exit(0)
    else:
        # Unknown tool name (older envelope or something weird) — be lenient.
        sys.exit(0)

    line_count = count_lines(projected)
    if line_count <= LINE_CAP:
        sys.exit(0)

    print(
        f"BLOCKED: this edit would push CLAUDE.md to {line_count} lines "
        f"(cap is {LINE_CAP}).",
        file=sys.stderr,
    )
    print(
        "  Apply the 4-question test from docs/CLAUDE-MD-GOVERNANCE.md "
        "before adding to CLAUDE.md.",
        file=sys.stderr,
    )
    print(
        "  If the line truly belongs in CLAUDE.md, find something to remove "
        "or move into a skill / README / hook.",
        file=sys.stderr,
    )
    sys.exit(2)


if __name__ == "__main__":
    main()
