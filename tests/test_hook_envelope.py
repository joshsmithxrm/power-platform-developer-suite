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
# checkout-guard.py
# ---------------------------------------------------------------------------


class TestCheckoutGuardEnvelope:
    """The guard only enforces in the main repo. We verify the nested envelope
    parse by running in a tmp dir (not a worktree) and confirming the parse
    branch is taken (no crash; the rest of the hook makes its own decision)."""

    def test_nested_envelope_parsed_without_crash(self, tmp_path):
        """checkout-guard reads command from tool_input — no crash on nested."""
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": "git checkout main"},
        }
        result = _run("checkout-guard.py", payload, cwd=str(tmp_path))
        # tmp_path is not a git repo, so is_main_repo() returns False and the
        # hook exits 0 without ever inspecting the command. The point of this
        # test is that the json envelope parse doesn't crash.
        assert result.returncode == 0

    def test_nested_envelope_allow_main_in_main_repo(self, tmp_path):
        """``git checkout main`` is allowed in the main repo (envelope nested)."""
        # Create a real bare-equivalent git repo directory (init counts as main)
        subprocess.run(["git", "init", "-q", str(tmp_path)], check=True)
        # Make sure HEAD is on main/master so is_main_repo returns True
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": "git checkout main"},
        }
        result = _run("checkout-guard.py", payload, cwd=str(tmp_path))
        assert result.returncode == 0, result.stderr

    def test_nested_envelope_block_feature_branch_in_main_repo(self, tmp_path):
        """``git checkout feature/foo`` is blocked when in the main repo and
        the envelope is correctly nested."""
        subprocess.run(["git", "init", "-q", str(tmp_path)], check=True)
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": "git checkout feature/foo"},
        }
        env = {"CLAUDE_PROJECT_DIR": str(tmp_path)}
        result = _run("checkout-guard.py", payload, env_extra=env, cwd=str(tmp_path))
        assert result.returncode == 2, (
            f"Expected block (exit 2); got {result.returncode}\n"
            f"stderr={result.stderr!r}"
        )

    def test_top_level_envelope_does_not_crash(self):
        """Old-style top-level envelope (no tool_input) → still parses OK."""
        payload = {"command": "git checkout main"}
        result = _run("checkout-guard.py", payload)
        # Without nesting the new code reads command="" and proceeds; with the
        # tmp cwd it isn't a main repo so this exits 0. The key assertion is
        # no crash.
        assert result.returncode == 0


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


# ---------------------------------------------------------------------------
# review-guard.py
# ---------------------------------------------------------------------------


class TestReviewGuardEnvelope:
    def test_nested_envelope_passes_when_no_state(self, tmp_path):
        """No .workflow/state.json → guard exits 0 regardless of command."""
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": "gh issue create -t 'bug'"},
        }
        env = {"CLAUDE_PROJECT_DIR": str(tmp_path)}
        result = _run("review-guard.py", payload, env_extra=env)
        assert result.returncode == 0

    def test_nested_envelope_blocks_in_active_review(self, tmp_path):
        """Active review (findings present + not passed) blocks gh issue create."""
        wf = tmp_path / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({
            "review": {"findings": 3, "passed": False},
        }))
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": "gh issue create -t 'finding'"},
        }
        env = {"CLAUDE_PROJECT_DIR": str(tmp_path)}
        result = _run("review-guard.py", payload, env_extra=env)
        assert result.returncode == 2, (
            f"Issue creation during review should be blocked; "
            f"stderr={result.stderr!r}"
        )

    def test_top_level_envelope_no_crash(self, tmp_path):
        """Old-style envelope (no tool_input) → no crash."""
        payload = {"command": "gh issue create -t 'x'"}
        env = {"CLAUDE_PROJECT_DIR": str(tmp_path)}
        result = _run("review-guard.py", payload, env_extra=env)
        assert result.returncode == 0
