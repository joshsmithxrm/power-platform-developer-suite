"""Tests for claudemd-line-cap hook — block CLAUDE.md edits over 100 lines."""
import importlib.util
import json
import os
import subprocess
import sys
import tempfile

import pytest


def _load_hook():
    hook_path = os.path.normpath(
        os.path.join(
            os.path.dirname(__file__),
            os.pardir,
            ".claude",
            "hooks",
            "claudemd-line-cap.py",
        )
    )
    spec = importlib.util.spec_from_file_location("claudemd_line_cap", hook_path)
    mod = importlib.util.module_from_spec(spec)
    hooks_dir = os.path.dirname(hook_path)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()

HOOK_PATH = os.path.normpath(
    os.path.join(
        os.path.dirname(__file__),
        os.pardir,
        ".claude",
        "hooks",
        "claudemd-line-cap.py",
    )
)


def _run_hook(payload: dict) -> tuple[int, str]:
    result = subprocess.run(
        [sys.executable, HOOK_PATH],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=5,
    )
    return result.returncode, result.stderr


class TestIsClaudeMd:
    def test_root_claude_md(self):
        assert hook.is_claude_md("CLAUDE.md")

    def test_repo_claude_md(self):
        assert hook.is_claude_md("/c/repo/CLAUDE.md")

    def test_windows_claude_md(self):
        assert hook.is_claude_md("C:\\repo\\CLAUDE.md")

    def test_nested_claude_md(self):
        assert hook.is_claude_md("src/foo/CLAUDE.md")

    def test_lowercase_not_matched(self):
        # CLAUDE.md is uppercase by convention; lowercase variants are
        # unrelated.
        assert not hook.is_claude_md("src/claude.md")

    def test_other_md_allowed(self):
        assert not hook.is_claude_md("README.md")
        assert not hook.is_claude_md("docs/foo.md")


class TestProjectEdit:
    def test_unique_replacement(self):
        original = "line1\nold\nline3\n"
        result = hook.project_edit(original, "old", "new", False)
        assert result == "line1\nnew\nline3\n"

    def test_replace_all(self):
        original = "old\nold\nold\n"
        result = hook.project_edit(original, "old", "X", True)
        assert result == "X\nX\nX\n"

    def test_non_unique_returns_none(self):
        original = "old\nold\n"
        result = hook.project_edit(original, "old", "X", False)
        assert result is None

    def test_missing_returns_none(self):
        original = "line1\n"
        result = hook.project_edit(original, "missing", "X", False)
        assert result is None


class TestCountLines:
    def test_counts_newlines(self):
        assert hook.count_lines("a\nb\nc\n") == 3

    def test_no_trailing_newline_doesnt_count(self):
        assert hook.count_lines("a\nb\nc") == 2

    def test_empty(self):
        assert hook.count_lines("") == 0


class TestHookSubprocess:
    def test_short_write_allowed(self):
        code, _ = _run_hook({
            "tool_name": "Write",
            "tool_input": {
                "file_path": "C:/repo/CLAUDE.md",
                "content": "short\n",
            },
        })
        assert code == 0

    def test_at_cap_allowed(self):
        # Exactly 100 lines must pass (cap is inclusive).
        content = "\n".join(["x"] * 100) + "\n"
        code, _ = _run_hook({
            "tool_name": "Write",
            "tool_input": {
                "file_path": "C:/repo/CLAUDE.md",
                "content": content,
            },
        })
        assert code == 0

    def test_over_cap_blocked(self):
        content = "\n".join(["x"] * 110) + "\n"
        code, stderr = _run_hook({
            "tool_name": "Write",
            "tool_input": {
                "file_path": "C:/repo/CLAUDE.md",
                "content": content,
            },
        })
        assert code == 2
        assert "BLOCKED" in stderr
        assert "110" in stderr

    def test_non_claude_md_allowed(self):
        big = "\n".join(["x"] * 500) + "\n"
        code, _ = _run_hook({
            "tool_name": "Write",
            "tool_input": {
                "file_path": "C:/repo/README.md",
                "content": big,
            },
        })
        assert code == 0

    def test_edit_under_cap_allowed(self, tmp_path):
        target = tmp_path / "CLAUDE.md"
        target.write_text("\n".join(["x"] * 50) + "\n", encoding="utf-8")
        code, _ = _run_hook({
            "tool_name": "Edit",
            "tool_input": {
                "file_path": str(target),
                "old_string": "x\nx\nx\n",
                "new_string": "y\ny\ny\n",
                "replace_all": False,
            },
        })
        # old_string isn't unique in this content — projection returns None,
        # we allow and let Edit tool surface the real error.
        assert code == 0

    def test_edit_pushing_over_cap_blocked(self, tmp_path):
        target = tmp_path / "CLAUDE.md"
        # Start at 95 lines.
        original = "\n".join([f"line{i}" for i in range(95)]) + "\n"
        target.write_text(original, encoding="utf-8")
        # Replace one unique line with a 20-line block — pushes to 114.
        new_block = "\n".join([f"new{i}" for i in range(20)])
        code, stderr = _run_hook({
            "tool_name": "Edit",
            "tool_input": {
                "file_path": str(target),
                "old_string": "line50",
                "new_string": new_block,
                "replace_all": False,
            },
        })
        assert code == 2
        assert "BLOCKED" in stderr

    def test_handles_empty_stdin(self):
        result = subprocess.run(
            [sys.executable, HOOK_PATH],
            input="",
            capture_output=True,
            text=True,
            timeout=5,
        )
        assert result.returncode == 0
