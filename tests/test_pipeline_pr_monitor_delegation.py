"""Integration test: pipeline.run_pr_stage delegates to pr_monitor.py subprocess.

v1-prelaunch retro item #4: pipeline.py and pr_monitor.py used to embed two
copies of the same polling loop (CI → Gemini → triage → reconcile → ready →
retro → notify). The duplication was the root drift cause. Now pipeline
delegates the polling phase to pr_monitor.py via subprocess.

These tests mock ``subprocess.run`` and assert that:
  1. After PR creation succeeds, pipeline invokes pr_monitor.py with the
     correct arguments.
  2. The PR stage exit code matches pr_monitor's exit code.
  3. ``--dry-run`` does NOT spawn pr_monitor.
"""
import json
import os
import subprocess
import sys
import unittest.mock as um
from unittest.mock import MagicMock, patch

import pytest

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))

import pipeline


def _make_worktree(tmp_path):
    wt = tmp_path / "wt"
    (wt / ".workflow" / "stages").mkdir(parents=True)
    (wt / ".workflow" / "state.json").write_text(json.dumps({
        "branch": "feat/test", "gates": {"passed": True},
        "verify": {"workflow": "ts"}, "qa": {"workflow": "ts"},
        "review": {"passed": True, "findings": 0},
    }))
    return str(wt)


def _ok(stdout=""):
    return subprocess.CompletedProcess(
        args=[], returncode=0, stdout=stdout, stderr="")


class TestPrStageDelegatesToPrMonitor:
    """v1-prelaunch retro item #4: run_pr_stage MUST invoke pr_monitor.py
    rather than re-implement the polling loop."""

    def test_delegate_invokes_pr_monitor_subprocess(self, tmp_path):
        """After PR creation, run_pr_stage spawns pr_monitor.py with --pr."""
        wt = _make_worktree(tmp_path)
        logger = pipeline.open_logger(str(tmp_path / "pipeline.log"))

        captured_calls = []

        def mock_run(cmd, **kwargs):
            captured_calls.append(list(cmd))
            # Mock all the pre-PR subprocess calls
            if cmd[0] == "git" and "fetch" in cmd:
                return _ok()
            if cmd[0] == "git" and "rebase" in cmd:
                return _ok()
            if cmd[0] == "git" and "rev-parse" in cmd:
                return _ok("feat/test")
            if cmd[0] == "git" and "push" in cmd:
                return _ok()
            if cmd[0] == "python" and "workflow-state.py" in (cmd[1:2] or [""])[0]:
                return _ok("[]")
            if cmd[0] == "python" and any("workflow-state.py" in x for x in cmd):
                return _ok("[]")
            if cmd[0] == "gh" and "create" in cmd:
                return _ok("https://github.com/owner/repo/pull/803\n")
            # The pr_monitor.py invocation — record and return success
            if "pr_monitor.py" in " ".join(cmd):
                return _ok()
            return _ok()

        with patch("subprocess.run", side_effect=mock_run), \
             patch.object(pipeline, "run_claude", return_value=(0, logger)):
            exit_code, _ = pipeline.run_pr_stage(wt, logger, dry_run=False)

        logger.close()

        # Find the pr_monitor invocation
        monitor_calls = [
            c for c in captured_calls
            if any("pr_monitor.py" in str(arg) for arg in c)
        ]
        assert monitor_calls, (
            f"pr_monitor.py was never invoked. captured calls:\n"
            + "\n".join(" ".join(str(a) for a in c) for c in captured_calls)
        )
        cmd = monitor_calls[0]
        # Must pass --worktree and --pr
        assert "--worktree" in cmd
        assert "--pr" in cmd
        pr_idx = cmd.index("--pr")
        assert cmd[pr_idx + 1] == "803"
        # Must invoke via Python interpreter (not just `pr_monitor.py`)
        assert "python" in cmd[0].lower() or cmd[0] == sys.executable
        assert exit_code == 0

    def test_delegate_propagates_pr_monitor_exit_code(self, tmp_path):
        """When pr_monitor exits non-zero (e.g. CI failed), run_pr_stage
        propagates that exit code."""
        wt = _make_worktree(tmp_path)
        logger = pipeline.open_logger(str(tmp_path / "pipeline.log"))

        def mock_run(cmd, **kwargs):
            if cmd[0] == "git" and ("fetch" in cmd or "rebase" in cmd
                                     or "push" in cmd):
                return _ok()
            if cmd[0] == "git" and "rev-parse" in cmd:
                return _ok("feat/test")
            if cmd[0] == "python" and any("workflow-state.py" in x for x in cmd):
                return _ok("[]")
            if cmd[0] == "gh" and "create" in cmd:
                return _ok("https://github.com/owner/repo/pull/42\n")
            if "pr_monitor.py" in " ".join(cmd):
                return subprocess.CompletedProcess(
                    args=cmd, returncode=1, stdout="",
                    stderr="ci_failed",
                )
            return _ok()

        with patch("subprocess.run", side_effect=mock_run), \
             patch.object(pipeline, "run_claude", return_value=(0, logger)):
            exit_code, _ = pipeline.run_pr_stage(wt, logger, dry_run=False)

        logger.close()
        assert exit_code == 1

    def test_dry_run_does_not_spawn_pr_monitor(self, tmp_path):
        """--dry-run short-circuits before any subprocess calls (no monitor)."""
        wt = _make_worktree(tmp_path)
        logger = pipeline.open_logger(str(tmp_path / "pipeline.log"))

        captured_calls = []

        def mock_run(cmd, **kwargs):
            captured_calls.append(list(cmd))
            return _ok()

        with patch("subprocess.run", side_effect=mock_run), \
             patch.object(pipeline, "run_claude", return_value=(0, logger)):
            exit_code, _ = pipeline.run_pr_stage(wt, logger, dry_run=True)

        logger.close()
        monitor_calls = [c for c in captured_calls
                         if any("pr_monitor.py" in str(a) for a in c)]
        assert not monitor_calls, "Dry run must NOT spawn pr_monitor"
        assert exit_code == 0


class TestDelegationHelper:
    """Targeted unit tests for the _delegate_to_pr_monitor helper itself."""

    def test_dry_run_logs_and_skips_subprocess(self, tmp_path):
        """In dry_run mode the helper logs the planned cmd but doesn't exec."""
        wt = str(tmp_path)
        logger = pipeline.open_logger(str(tmp_path / "p.log"))
        with patch("subprocess.run") as mock_run:
            exit_code = pipeline._delegate_to_pr_monitor(
                wt, "42", logger, dry_run=True)
        logger.close()
        assert exit_code == 0
        mock_run.assert_not_called()

    def test_passes_worktree_and_pr_flags(self, tmp_path):
        """Args MUST include --worktree <path> and --pr <number>."""
        wt = str(tmp_path)
        logger = pipeline.open_logger(str(tmp_path / "p.log"))

        called_cmd = []

        def mock_run(cmd, **kwargs):
            called_cmd.extend(cmd)
            return _ok()

        with patch("subprocess.run", side_effect=mock_run):
            pipeline._delegate_to_pr_monitor(wt, "999", logger)
        logger.close()

        assert "--worktree" in called_cmd
        assert wt in called_cmd
        assert "--pr" in called_cmd
        assert "999" in called_cmd
        assert any("pr_monitor.py" in str(c) for c in called_cmd)

    def test_propagates_subprocess_exit_code(self, tmp_path):
        """Exit code from the subprocess is returned verbatim."""
        wt = str(tmp_path)
        logger = pipeline.open_logger(str(tmp_path / "p.log"))

        def mock_run(cmd, **kwargs):
            return subprocess.CompletedProcess(
                args=cmd, returncode=42, stdout="", stderr="boom")

        with patch("subprocess.run", side_effect=mock_run):
            ec = pipeline._delegate_to_pr_monitor(wt, "1", logger)
        logger.close()

        assert ec == 42

    def test_timeout_returns_negative_one(self, tmp_path):
        wt = str(tmp_path)
        logger = pipeline.open_logger(str(tmp_path / "p.log"))

        def mock_run(cmd, **kwargs):
            raise subprocess.TimeoutExpired(cmd, 1)

        with patch("subprocess.run", side_effect=mock_run):
            ec = pipeline._delegate_to_pr_monitor(wt, "1", logger)
        logger.close()
        assert ec == -1
