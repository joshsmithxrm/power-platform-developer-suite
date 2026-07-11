"""Envelope-contract tests for the remaining PreToolUse hooks.

v1-prelaunch retro item #2: every hook that reads a Bash command from stdin
must read it from ``payload["tool_input"]["command"]`` (the Claude Code
envelope), not ``payload["command"]``. Reading at the top level was the same
bug as protect-main-branch.py — command was always "" so the hook either
silently allowed everything (rm-guard) or block-failed cryptically.

These tests invoke each hook as a subprocess with a properly-nested envelope
and assert the expected exit code.
"""
import json
import os
import subprocess
import sys

import pytest


HOOKS_DIR = os.path.normpath(
    os.path.join(os.path.dirname(__file__), os.pardir, ".claude", "hooks")
)


def _run(hook_name: str, payload: dict, env_extra: dict = None,
         cwd: str = None) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    if env_extra:
        env.update(env_extra)
    return subprocess.run(
        [sys.executable, os.path.join(HOOKS_DIR, hook_name)],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=15,
        env=env,
        cwd=cwd,
    )


# ---------------------------------------------------------------------------
# rm-guard.py
# ---------------------------------------------------------------------------


class TestRmGuardEnvelope:
    def test_nested_envelope_blocks_outside_project(self, tmp_path):
        """Delete outside CLAUDE_PROJECT_DIR is blocked (nested envelope)."""
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": "rm /etc/passwd"},
        }
        env = {"CLAUDE_PROJECT_DIR": str(tmp_path)}
        result = _run("rm-guard.py", payload, env_extra=env)
        # /etc/passwd is outside tmp_path → block
        assert result.returncode == 2, (
            f"Delete outside project should be blocked; stderr={result.stderr!r}"
        )

    def test_nested_envelope_allows_inside_project(self, tmp_path):
        """Delete inside CLAUDE_PROJECT_DIR is allowed (nested envelope)."""
        target = tmp_path / "scratch.txt"
        target.write_text("x")
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": f"rm {target}"},
        }
        env = {"CLAUDE_PROJECT_DIR": str(tmp_path)}
        result = _run("rm-guard.py", payload, env_extra=env)
        assert result.returncode == 0, result.stderr

    def test_top_level_envelope_no_crash(self, tmp_path):
        """Old-style envelope (no tool_input) → no crash."""
        payload = {"command": "rm /etc/passwd"}
        env = {"CLAUDE_PROJECT_DIR": str(tmp_path)}
        result = _run("rm-guard.py", payload, env_extra=env)
        # Old style has command="" after the fix → no path extracted → exit 0
        assert result.returncode == 0
