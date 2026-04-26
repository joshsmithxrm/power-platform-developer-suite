"""Tests for settings-json-guard hook.

Verifies that the hook:
  1. Blocks a Write with a doubled hook path (.claude/hooks/.claude/hooks/)
  2. Allows a Write with correct (existing) hook paths
  3. Blocks a Write that references a non-existent hook file
  4. Blocks an Edit that introduces the doubled pattern
  5. Allows an Edit that does not introduce the doubled pattern
  6. Ignores files that are not settings.json / settings.local.json
"""
import json
import os
import subprocess
import sys

import pytest

HOOK_PATH = os.path.normpath(
    os.path.join(
        os.path.dirname(__file__),
        os.pardir,
        ".claude",
        "hooks",
        "settings-json-guard.py",
    )
)

# The project root (parent of tests/).
PROJECT_DIR = os.path.normpath(os.path.join(os.path.dirname(__file__), os.pardir))

# A real hook file that we know exists (used for "valid path" tests).
_REAL_HOOK = ".claude/hooks/pr-gate.py"
_REAL_HOOK_FULL = os.path.join(PROJECT_DIR, _REAL_HOOK)
assert os.path.exists(_REAL_HOOK_FULL), f"Reference hook not found: {_REAL_HOOK_FULL}"


def _run(payload: dict) -> tuple[int, str]:
    """Run the hook as a subprocess with the given JSON payload on stdin."""
    env = os.environ.copy()
    env["CLAUDE_PROJECT_DIR"] = PROJECT_DIR
    result = subprocess.run(
        [sys.executable, HOOK_PATH],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=10,
        env=env,
    )
    return result.returncode, result.stderr


def _write_payload(file_path: str, content: str) -> dict:
    return {
        "tool_name": "Write",
        "tool_input": {
            "file_path": file_path,
            "content": content,
        },
    }


def _edit_payload(file_path: str, old_string: str, new_string: str) -> dict:
    return {
        "tool_name": "Edit",
        "tool_input": {
            "file_path": file_path,
            "old_string": old_string,
            "new_string": new_string,
        },
    }


def _settings_with_command(command: str) -> str:
    """Build a minimal settings.json string containing a single hook command."""
    settings = {
        "hooks": {
            "PreToolUse": [
                {
                    "matcher": "Write",
                    "hooks": [
                        {
                            "type": "command",
                            "command": command,
                        }
                    ],
                }
            ]
        }
    }
    return json.dumps(settings, indent=2)


# ---------------------------------------------------------------------------
# Write tool — full content validation
# ---------------------------------------------------------------------------

class TestWriteDoubledPath:
    """Hook must block a Write that contains a doubled hook path."""

    def test_blocks_doubled_path(self):
        command = 'python ".claude/hooks/.claude/hooks/pr-gate.py"'
        payload = _write_payload(".claude/settings.json", _settings_with_command(command))
        code, stderr = _run(payload)
        assert code == 2
        assert "BLOCKED" in stderr
        assert ".claude/hooks/.claude/hooks/" in stderr

    def test_blocks_doubled_path_in_settings_local(self):
        command = 'python ".claude/hooks/.claude/hooks/pr-gate.py"'
        payload = _write_payload("settings.local.json", _settings_with_command(command))
        code, stderr = _run(payload)
        assert code == 2
        assert "BLOCKED" in stderr


class TestWriteValidPath:
    """Hook must allow a Write with correct, existing hook paths."""

    def test_allows_correct_existing_path(self):
        command = f'python "{_REAL_HOOK}"'
        payload = _write_payload(".claude/settings.json", _settings_with_command(command))
        code, _ = _run(payload)
        assert code == 0

    def test_allows_settings_local_with_correct_path(self):
        command = f'python "{_REAL_HOOK}"'
        payload = _write_payload("settings.local.json", _settings_with_command(command))
        code, _ = _run(payload)
        assert code == 0


class TestWriteNonExistentFile:
    """Hook must block a Write that references a non-existent hook file."""

    def test_blocks_nonexistent_hook(self):
        command = 'python ".claude/hooks/does-not-exist-xyz.py"'
        payload = _write_payload(".claude/settings.json", _settings_with_command(command))
        code, stderr = _run(payload)
        assert code == 2
        assert "BLOCKED" in stderr
        assert "does not exist" in stderr.lower() or "does-not-exist" in stderr

    def test_blocks_invented_path(self):
        command = 'python ".claude/hooks/ghost-hook.py"'
        payload = _write_payload(".claude/settings.json", _settings_with_command(command))
        code, stderr = _run(payload)
        assert code == 2


# ---------------------------------------------------------------------------
# Edit tool — fragment validation (doubled pattern only)
# ---------------------------------------------------------------------------

class TestEditDoubledPath:
    """Hook must block an Edit whose new_string introduces the doubled pattern."""

    def test_blocks_edit_with_doubled_pattern(self):
        new_string = 'python ".claude/hooks/.claude/hooks/pr-gate.py"'
        payload = _edit_payload(
            ".claude/settings.json",
            'python ".claude/hooks/pr-gate.py"',
            new_string,
        )
        code, stderr = _run(payload)
        assert code == 2
        assert "BLOCKED" in stderr
        assert ".claude/hooks/.claude/hooks/" in stderr


class TestEditAllowedPath:
    """Hook must allow an Edit whose new_string does not contain the doubled pattern."""

    def test_allows_correct_edit(self):
        new_string = 'python ".claude/hooks/pr-gate.py"'
        payload = _edit_payload(
            ".claude/settings.json",
            'python ".claude/hooks/old-hook.py"',
            new_string,
        )
        code, _ = _run(payload)
        assert code == 0


# ---------------------------------------------------------------------------
# Non-settings files must be ignored
# ---------------------------------------------------------------------------

class TestNonSettingsFilesIgnored:
    def test_ignores_other_json_files(self):
        # Even if the content has a doubled path, non-settings files pass through.
        command = 'python ".claude/hooks/.claude/hooks/bad.py"'
        payload = _write_payload("some/other/config.json", _settings_with_command(command))
        code, _ = _run(payload)
        assert code == 0

    def test_ignores_python_files(self):
        payload = _write_payload("src/foo.py", "# not settings")
        code, _ = _run(payload)
        assert code == 0


# ---------------------------------------------------------------------------
# Robustness: malformed inputs must fail open (exit 0)
# ---------------------------------------------------------------------------

class TestRobustness:
    def test_empty_stdin_allows(self):
        env = os.environ.copy()
        env["CLAUDE_PROJECT_DIR"] = PROJECT_DIR
        result = subprocess.run(
            [sys.executable, HOOK_PATH],
            input="",
            capture_output=True,
            text=True,
            timeout=10,
            env=env,
        )
        assert result.returncode == 0

    def test_garbled_json_allows(self):
        env = os.environ.copy()
        env["CLAUDE_PROJECT_DIR"] = PROJECT_DIR
        result = subprocess.run(
            [sys.executable, HOOK_PATH],
            input="{not json",
            capture_output=True,
            text=True,
            timeout=10,
            env=env,
        )
        assert result.returncode == 0

    def test_malformed_settings_json_allows(self):
        # Malformed settings content (invalid JSON in Write body) — fail open.
        payload = _write_payload(".claude/settings.json", "{invalid json content")
        code, _ = _run(payload)
        assert code == 0
