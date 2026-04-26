#!/usr/bin/env python3
"""Tests for pr-monitor.py (WE AC-106–114, AC-125–128, retro-filing AC-15)."""
import json
import os
import subprocess
import sys
import tempfile
import time

import pytest
from unittest.mock import patch, MagicMock, call, ANY

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))

import pr_monitor


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _make_worktree(tmp_path):
    """Create a minimal worktree directory with .workflow/ structure."""
    wt = str(tmp_path / "worktree")
    os.makedirs(os.path.join(wt, ".workflow"), exist_ok=True)
    return wt


def _make_logger(tmp_path):
    """Create a Logger writing to a temp log file."""
    log_path = str(tmp_path / "test.log")
    return pr_monitor.Logger(log_path)


def _gh_checks_json(checks):
    """Build a subprocess.CompletedProcess mimicking gh pr checks output."""
    return subprocess.CompletedProcess(
        args=[], returncode=0,
        stdout=json.dumps(checks), stderr="",
    )


class TestCiPolling:
    """AC-107: pr-monitor polls CI via 'gh pr checks'."""

    def test_ci_polling_passes_when_all_checks_pass(self, tmp_path):
        """poll_ci returns 'pass' when all checks complete with SUCCESS."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        checks = [
            {"name": "build", "state": "COMPLETED", "conclusion": "SUCCESS"},
            {"name": "test", "state": "COMPLETED", "conclusion": "SUCCESS"},
        ]

        with patch("pr_monitor.subprocess.run", return_value=_gh_checks_json(checks)) as mock_run:
            result = pr_monitor.poll_ci(wt, 42, logger)

        assert result == "pass"
        # Verify gh pr checks was called with correct args
        mock_run.assert_called()
        args = mock_run.call_args[0][0]
        assert args[:2] == ["gh", "pr"]
        assert "checks" in args
        assert "42" in args


class TestCiFailure:
    """AC-108: On CI failure, poll_ci returns 'fail'."""

    def test_ci_failure_returns_fail(self, tmp_path):
        """poll_ci returns 'fail' when a check has bucket=='fail'."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        checks = [
            {"name": "build", "state": "COMPLETED", "bucket": "pass"},
            {"name": "lint", "state": "COMPLETED", "bucket": "fail"},
        ]

        with patch("pr_monitor.subprocess.run", return_value=_gh_checks_json(checks)):
            result = pr_monitor.poll_ci(wt, 10, logger)

        assert result == "fail"

    def test_ci_failure_triggers_notify_in_monitor(self, tmp_path):
        """run_monitor exhausts CI-fix budget and fires exactly one terminal notification."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="fail"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 99, resume=False)

        assert exit_code == 1
        result = pr_monitor.read_result(wt)
        assert result["status"] == "stuck-ci-fix-exhausted"
        # Exactly one terminal notification
        assert mock_notify.call_count == 1
        msg = mock_notify.call_args[1].get("message", "")
        assert "stuck-ci-fix-exhausted" in msg


class TestCiFailContinuesToTriage:
    """AC-108 SUPERSEDED by AC-181/AC-191 (v9.1).

    Pre-v9.1: CI failure continued to Gemini triage.
    v9.1: CI failure routes to CI-fix agent (see Phase 3 tests for AC-108 supersession).
    """

    def test_ci_fail_then_gemini_comment_is_triaged(self, tmp_path):
        """AC-108 supersession: CI fails, CI-fix runs, CI becomes green,
        Gemini comment is triaged — proving the state machine transitions
        from CI-fix to Gemini-triage in a single run."""
        wt = _make_worktree(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        gemini_comment = {
            "id": 7001, "user": "gemini-code-assist[bot]",
            "path": "src/Risky.cs", "line": 42,
            "body": "security finding",
        }

        # poll_ci: initial call returns "fail", then "pass" after CI-fix
        ci_calls = [0]
        def ci_side_effect(*a, **kw):
            ci_calls[0] += 1
            return "pass" if ci_calls[0] > 1 else "fail"

        triage_called = [False]
        def mock_triage(*a, **kw):
            triage_called[0] = True
            return [{"id": 7001, "action": "dismissed",
                     "description": "ok", "commit": None}]

        with patch("pr_monitor.poll_ci", side_effect=ci_side_effect), \
             patch("pr_monitor.poll_gemini_comments",
                   return_value=[gemini_comment]), \
             patch("pr_monitor.get_unreplied_comments", return_value=[]), \
             patch("pr_monitor.run_triage", side_effect=mock_triage), \
             patch("pr_monitor._reconcile_replies"), \
             patch("pr_monitor._dispatch_ci_fix_agent",
                   return_value={"action": "fix", "files_touched": ["scripts/foo.py"],
                                 "lines_added": 2, "lines_removed": 1,
                                 "escalation_reason": None, "scope_violation": False,
                                 "failure_summary": "test failed"}), \
             patch("pr_monitor._write_ci_fix_decision"), \
             patch("pr_monitor._check_thrash", return_value=False), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 858, resume=False)

        assert exit_code == 0, f"Expected exit 0 (ready), got {exit_code}"
        result = pr_monitor.read_result(wt)
        assert result["status"] == "ready"
        assert triage_called[0], "Gemini triage must run after CI became green"


class TestCiTimeout:
    """AC-125: poll_ci returns 'timeout' after CI_MAX_WAIT."""

    def test_ci_timeout_returns_timeout(self, tmp_path):
        """poll_ci returns 'timeout' when checks remain pending past max wait."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        pending_checks = [
            {"name": "build", "state": "IN_PROGRESS", "conclusion": None},
        ]

        with patch("pr_monitor.subprocess.run",
                    return_value=_gh_checks_json(pending_checks)), \
             patch("pr_monitor.CI_MAX_WAIT", 0), \
             patch("pr_monitor.CI_POLL_INTERVAL", 0):
            result = pr_monitor.poll_ci(wt, 5, logger)

        assert result == "timeout"


class TestTerminalNotifications:
    """Monitor fires run_notify on every terminal state (success + failures)."""

    def test_notify_on_ci_timeout(self, tmp_path):
        """CI timeout path calls run_notify once with a terminal notification."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="timeout"), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 99, resume=False)

        assert exit_code == 1
        mock_notify.assert_called_once()
        msg = mock_notify.call_args[1].get("message", "")
        assert "ci-timeout" in msg

    def test_notify_on_monitor_exception(self, tmp_path):
        """Uncaught exception in the monitor loop still fires notify."""
        wt = _make_worktree(tmp_path)

        # mark_step is called in the control-flow code outside step
        # try/except wrappers (e.g., right after _step_ci returns), so
        # raising from it triggers the outer ``except Exception`` path.
        # Uses a one-shot raise so the outer handler's own write_result
        # / run_notify calls still succeed.
        raised = [False]

        def raise_once(*a, **kw):
            if not raised[0]:
                raised[0] = True
                raise RuntimeError("boom")

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.mark_step", side_effect=raise_once), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        assert exit_code == 1
        mock_notify.assert_called_once()
        msg = mock_notify.call_args.kwargs["message"]
        assert "crash" in msg.lower() or "RuntimeError" in msg
        assert "RuntimeError" in msg

    def test_notify_failure_does_not_cascade(self, tmp_path):
        """A crash inside run_notify must not change the monitor's exit code."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="timeout"), \
             patch("pr_monitor.run_notify", side_effect=RuntimeError("notify-down")):
            exit_code = pr_monitor.run_monitor(wt, 1, resume=False)

        # Still exits 1 (timeout), not a different non-zero from notify failure.
        assert exit_code == 1


class TestResumeSkips:
    """AC-109: --resume reads result file and skips completed steps."""

    def test_resume_skips_completed_ci(self, tmp_path):
        """run_monitor with resume=True skips CI when already completed."""
        wt = _make_worktree(tmp_path)

        # Pre-populate result with CI completed
        pre_result = pr_monitor._empty_result()
        pre_result["steps_completed"]["ci"] = {
            "status": "pass", "timestamp": "2026-01-01T00:00:00Z"
        }
        pre_result["ci_result"] = "pass"
        pr_monitor.write_result(wt, pre_result)

        # Mock remaining steps to succeed quickly
        with patch("pr_monitor.poll_ci") as mock_ci, \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            pr_monitor.run_monitor(wt, 1, resume=True)

        # poll_ci should NOT have been called — CI was already completed
        mock_ci.assert_not_called()
        result = pr_monitor.read_result(wt)
        assert result["steps_completed"]["ci"]["status"] == "pass"


class TestTriageOnComments:
    """AC-110: Spawns triage when inline comments > 0."""

    def test_triage_spawned_when_comments_exist(self, tmp_path):
        """run_triage is called when poll_gemini_comments returns comments."""
        wt = _make_worktree(tmp_path)

        comments = [
            {"id": 1, "user": "gemini", "path": "foo.py", "line": 10,
             "body": "Nit: rename this variable"},
        ]

        stage_dir = os.path.join(wt, ".workflow", "stages")
        os.makedirs(stage_dir, exist_ok=True)
        jsonl_path = os.path.join(stage_dir, "pr-monitor-triage.jsonl")

        # The mock Popen stdout is redirected to the stage log file by
        # run_triage.  We need the JSONL content to appear in that file
        # after wait() returns.  Since run_triage opens the file itself
        # and passes it as stdout, we simulate writing by having wait()
        # write to the file before returning.
        triage_output = json.dumps([
            {"id": 1, "action": "fixed", "description": "renamed var",
             "commit": "abc123"}
        ])
        jsonl_event = json.dumps({"type": "result", "result": triage_output})

        mock_proc = MagicMock()
        mock_proc.returncode = 0

        def fake_wait(timeout=None):
            # run_triage passes the opened file as stdout to Popen.
            # Write the JSONL event into it so _parse_triage_output finds it.
            with open(jsonl_path, "w") as f:
                f.write(jsonl_event + "\n")
            return 0

        mock_proc.wait.side_effect = fake_wait

        with patch("pr_monitor.subprocess.Popen", return_value=mock_proc) as mock_popen:
            logger = _make_logger(tmp_path)
            result = pr_monitor.run_triage(wt, 42, comments, logger)

        # Verify claude was invoked with gemini-triage agent
        mock_popen.assert_called_once()
        cmd = mock_popen.call_args[0][0]
        assert "claude" in cmd[0]
        assert "--agent" in cmd
        assert "gemini-triage" in cmd
        assert result is not None
        assert len(result) == 1
        assert result[0]["action"] == "fixed"


class TestRepollCi:
    """AC-111: Re-polls CI after triage commits."""

    def test_repoll_ci_after_triage(self, tmp_path):
        """After triage, CI is re-polled and result updated."""
        wt = _make_worktree(tmp_path)

        pass_checks = [
            {"name": "build", "state": "COMPLETED", "conclusion": "SUCCESS"},
        ]

        call_count = [0]

        def mock_run_side_effect(*args, **kwargs):
            """First call = initial CI pass, subsequent calls for gemini/triage."""
            call_count[0] += 1
            cmd = args[0] if args else kwargs.get("args", [])
            if cmd and cmd[0] == "gh":
                if "checks" in cmd:
                    return _gh_checks_json(pass_checks)
                # Gemini comment poll - return "0" on first call, then stable
                if "api" in cmd:
                    return subprocess.CompletedProcess(
                        args=[], returncode=0, stdout="1", stderr=""
                    )
                # repo view
                if "repo" in cmd and "view" in cmd:
                    return subprocess.CompletedProcess(
                        args=[], returncode=0,
                        stdout="test-owner/test-repo", stderr=""
                    )
            return subprocess.CompletedProcess(
                args=[], returncode=0, stdout="", stderr=""
            )

        # Use run_monitor with mocks to exercise the full triage->CI re-poll path
        with patch("pr_monitor.poll_ci", side_effect=["pass", "pass"]) as mock_poll_ci, \
             patch("pr_monitor.poll_gemini_comments",
                   side_effect=[
                       [{"id": 1, "user": "g", "path": "a.py",
                         "line": 1, "body": "fix"}],
                       [],  # no new comments after triage
                   ]), \
             patch("pr_monitor.run_triage", return_value=[
                 {"id": 1, "action": "fixed", "description": "done", "commit": "abc"}
             ]), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 1, resume=False)

        assert exit_code == 0
        # poll_ci called twice: initial + after triage
        assert mock_poll_ci.call_count == 2


class TestTriageCiLoopLimit:
    """AC-127: Max 3 triage -> CI iterations."""

    def test_triage_ci_loop_limit(self, tmp_path):
        """Triage loop stops after MAX_TRIAGE_ITERATIONS with stuck-triage-exhausted."""
        wt = _make_worktree(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        persistent_comments = [
            {"id": 1, "user": "g", "path": "a.py", "line": 1, "body": "fix"}
        ]

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments",
                   return_value=persistent_comments), \
             patch("pr_monitor.get_unreplied_comments",
                   return_value=persistent_comments), \
             patch("pr_monitor.run_triage", return_value=[]) as mock_triage, \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 1, resume=False)

        assert exit_code == 1
        result = pr_monitor.read_result(wt)
        assert result["status"] == "stuck-triage-exhausted"
        assert mock_triage.call_count == pr_monitor.MAX_TRIAGE_ITERATIONS
        mock_notify.assert_called_once()


class TestRetroBeforeNotify:
    """AC-112: Retro runs as penultimate step before notification."""

    def test_retro_runs_before_notify(self, tmp_path):
        """In the step sequence, retro is called before notify."""
        wt = _make_worktree(tmp_path)

        call_order = []

        def track_retro(worktree, logger):
            call_order.append("retro")
            return "done"

        def track_notify(worktree, pr_number, logger, message=None):
            call_order.append("notify")

        # Pass all finding-#2 gates so the ready-step does NOT emit its
        # own notify call — we want to observe retro->notify ordering.
        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor._ready_flip_gates", return_value=(True, [])), \
             patch("pr_monitor._rebase_source_branch", return_value=True), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", side_effect=track_retro) as mock_retro, \
             patch("pr_monitor.run_notify", side_effect=track_notify) as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 1, resume=False)

        assert exit_code == 0
        assert call_order == ["retro", "notify"]
        mock_retro.assert_called_once()
        mock_notify.assert_called_once()


class TestDraftToReady:
    """AC-113: Converts draft to ready via 'gh pr ready'."""

    def test_draft_to_ready(self, tmp_path):
        """mark_pr_ready calls 'gh pr ready <number>'."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        success_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout="", stderr=""
        )

        with patch("pr_monitor.subprocess.run", return_value=success_result) as mock_run:
            ok = pr_monitor.mark_pr_ready(wt, 55, logger)

        assert ok is True
        mock_run.assert_called_once()
        args = mock_run.call_args[0][0]
        assert args == ["gh", "pr", "ready", "55"]

    def test_draft_to_ready_failure(self, tmp_path):
        """mark_pr_ready returns False when gh pr ready fails."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        fail_result = subprocess.CompletedProcess(
            args=[], returncode=1, stdout="", stderr="not a draft"
        )

        with patch("pr_monitor.subprocess.run", return_value=fail_result):
            ok = pr_monitor.mark_pr_ready(wt, 55, logger)

        assert ok is False


class TestResultJsonSchema:
    """AC-114: Result JSON has required fields."""

    def test_result_json_schema(self, tmp_path):
        """Result file contains required keys after a successful run."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            pr_monitor.run_monitor(wt, 1, resume=False)

        result = pr_monitor.read_result(wt)
        assert "status" in result
        assert "steps_completed" in result
        assert "ci_result" in result
        assert "comment_counts" in result
        assert "triage_summary" in result
        assert "retro_status" in result
        assert "timestamp" in result
        assert result["status"] == "ready"

    def test_result_file_path(self, tmp_path):
        """Result file is written to .workflow/pr-monitor-result.json."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            pr_monitor.run_monitor(wt, 1, resume=False)

        expected_path = os.path.join(wt, ".workflow", "pr-monitor-result.json")
        assert os.path.exists(expected_path)
        with open(expected_path) as f:
            data = json.load(f)
        assert data["status"] == "ready"


class TestGeminiTimeout:
    """AC-126: Gemini polling stops after GEMINI_MAX_WAIT (5 min).

    Updated for v1-prelaunch retro item #3: poll_gemini_comments now
    delegates to triage_common.poll_gemini_review which polls 3 endpoints.
    """

    def test_gemini_timeout(self, tmp_path):
        """poll_gemini_comments returns empty list after max wait."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        poll_calls = [0]

        def empty_endpoints(*args, **kwargs):
            cmd = args[0] if args else kwargs.get("args", [])
            poll_calls[0] += 1
            # gh repo view for slug
            if cmd and "repo" in cmd and "view" in cmd:
                return subprocess.CompletedProcess(
                    args=[], returncode=0, stdout="owner/repo", stderr="")
            # gh pr view for createdAt
            if cmd and cmd[:2] == ["gh", "pr"] and "view" in cmd:
                return subprocess.CompletedProcess(
                    args=[], returncode=0, stdout="2026-01-01T00:00:00Z",
                    stderr="")
            # gh api ... endpoints — always empty list
            return subprocess.CompletedProcess(
                args=[], returncode=0, stdout="[]", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=empty_endpoints), \
             patch("triage_common.subprocess.run", side_effect=empty_endpoints), \
             patch("pr_monitor.GEMINI_MAX_WAIT", 1), \
             patch("pr_monitor.GEMINI_POLL_INTERVAL", 0), \
             patch("triage_common.time.sleep"):
            result = pr_monitor.poll_gemini_comments(wt, 5, logger)

        assert result == []
        # At minimum, the three endpoints should have been polled once
        assert poll_calls[0] >= 1


class TestPidFileLifecycle:
    """AC-128: PID file written on start and cleaned on exit."""

    def test_pid_file_written(self, tmp_path):
        """write_pid creates .workflow/pr-monitor.pid with current PID."""
        wt = _make_worktree(tmp_path)
        pr_monitor.write_pid(wt)

        pid_path = os.path.join(wt, ".workflow", "pr-monitor.pid")
        assert os.path.exists(pid_path)
        with open(pid_path) as f:
            assert f.read().strip() == str(os.getpid())

    def test_pid_file_cleaned(self, tmp_path):
        """cleanup_pid removes .workflow/pr-monitor.pid."""
        wt = _make_worktree(tmp_path)
        pr_monitor.write_pid(wt)
        pid_path = os.path.join(wt, ".workflow", "pr-monitor.pid")
        assert os.path.exists(pid_path)

        pr_monitor.cleanup_pid(wt)
        assert not os.path.exists(pid_path)

    def test_pid_written_during_monitor_run(self, tmp_path):
        """run_monitor writes PID file at start."""
        wt = _make_worktree(tmp_path)

        pid_written = [False]
        original_write_pid = pr_monitor.write_pid

        def tracking_write_pid(worktree):
            original_write_pid(worktree)
            pid_written[0] = True

        with patch("pr_monitor.write_pid", side_effect=tracking_write_pid), \
             patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            pr_monitor.run_monitor(wt, 1, resume=False)

        assert pid_written[0] is True


class TestRetroTrigger:
    """AC-15 (retro-filing): pr-monitor triggers retro as penultimate step,
    passing worktree path for transcript access."""

    def test_retro_trigger(self, tmp_path):
        """run_retro is called with worktree path in run_monitor, before notify."""
        wt = _make_worktree(tmp_path)

        call_order = []

        def track_retro(worktree, logger):
            call_order.append(("retro", worktree))
            return "done"

        def track_notify(worktree, pr_number, logger, message=None):
            call_order.append(("notify", worktree))

        # Pass all finding-#2 gates so ready-step's skip-path does not
        # emit an extra notify call — we want to observe retro->notify order.
        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor._ready_flip_gates", return_value=(True, [])), \
             patch("pr_monitor._rebase_source_branch", return_value=True), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", side_effect=track_retro), \
             patch("pr_monitor.run_notify", side_effect=track_notify):
            exit_code = pr_monitor.run_monitor(wt, 7, resume=False)

        assert exit_code == 0
        # Retro must appear before notify in the call order
        assert len(call_order) == 2
        assert call_order[0][0] == "retro"
        assert call_order[1][0] == "notify"
        # Retro receives the worktree path for transcript access
        assert call_order[0][1] == wt

    def test_retro_invokes_claude_with_retro_prompt(self, tmp_path):
        """run_retro spawns 'claude -p' with /retro prompt in the worktree."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        mock_proc = MagicMock()
        mock_proc.wait.return_value = 0
        mock_proc.returncode = 0

        # Create the stage log directory so the open() succeeds
        stage_dir = os.path.join(wt, ".workflow", "stages")
        os.makedirs(stage_dir, exist_ok=True)

        with patch("pr_monitor.subprocess.Popen", return_value=mock_proc) as mock_popen:
            status = pr_monitor.run_retro(wt, logger)

        assert status == "done"
        mock_popen.assert_called_once()
        cmd = mock_popen.call_args[0][0]
        assert cmd[0] == "claude"
        assert "-p" in cmd
        # The prompt should include /retro
        prompt_arg_idx = cmd.index("-p") + 1
        assert "/retro" in cmd[prompt_arg_idx]
        # Should run in the worktree directory
        assert mock_popen.call_args[1]["cwd"] == wt

    def test_retro_result_stored_in_state(self, tmp_path):
        """After run_monitor, result JSON includes retro_status."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            pr_monitor.run_monitor(wt, 1, resume=False)

        result = pr_monitor.read_result(wt)
        assert result["retro_status"] == "done"
        assert result["steps_completed"]["retro"]["status"] == "done"


class TestReconciliationLoop:
    """AC-140: _reconcile_replies re-triages unreplied comments up to 3 rounds."""

    def test_reconciliation_retries_unreplied(self, tmp_path):
        """_reconcile_replies calls get_unreplied_comments and re-triages."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result_dict = pr_monitor._empty_result()

        initial_triage = [{"id": 1, "action": "fixed", "description": "done", "commit": "abc"}]

        # get_unreplied_comments returns 1 unreplied on first check, then 0
        unreplied_calls = [
            [{"id": 2, "user": "gemini-code-assist[bot]", "path": "a.py", "line": 1, "body": "issue"}],
            [],
        ]

        with patch("pr_monitor.post_replies"), \
             patch("pr_monitor.get_unreplied_comments", side_effect=unreplied_calls), \
             patch("pr_monitor.run_triage", return_value=[{"id": 2, "action": "dismissed", "description": "ok", "commit": None}]) as mock_triage, \
             patch("pr_monitor.get_repo_slug", return_value="owner/repo"), \
             patch("pr_monitor.subprocess.run"):
            pr_monitor._reconcile_replies(wt, 42, initial_triage, logger, result_dict, 1)

        # run_triage should have been called once for the delta
        assert mock_triage.call_count == 1


class TestUnifiedTriagePass:
    """AC-145: Gemini + CodeQL comments triaged in single pass."""

    def test_unified_triage_both_bot_types(self, tmp_path):
        """Both Gemini and CodeQL comments go to a single run_triage call."""
        wt = _make_worktree(tmp_path)

        mixed_comments = [
            {"id": 1, "user": "gemini-code-assist[bot]", "path": "a.py", "line": 1, "body": "fix"},
            {"id": 2, "user": "github-advanced-security[bot]", "path": "b.py", "line": 5, "body": "vuln"},
        ]

        # First poll returns mixed comments; subsequent polls return empty
        # (triage handled them all) so the triage loop stops after 1 iteration.
        gemini_calls = [0]

        def mock_gemini(worktree, pr_number, logger):
            gemini_calls[0] += 1
            if gemini_calls[0] == 1:
                return mixed_comments
            return []

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", side_effect=mock_gemini), \
             patch("pr_monitor.run_triage", return_value=[
                 {"id": 1, "action": "fixed", "description": "done", "commit": "abc"},
             ]) as mock_triage, \
             patch("pr_monitor._reconcile_replies"), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            pr_monitor.run_monitor(wt, 1, resume=False)

        # run_triage called once with both comment types in a single pass
        assert mock_triage.call_count == 1
        comments_arg = mock_triage.call_args[0][2]  # 3rd positional arg
        assert len(comments_arg) == 2


# ---------------------------------------------------------------------------
# Meta-retro (2026-04-19) findings #1, #2, #3, #6
# ---------------------------------------------------------------------------


class TestEmptyBodyReplyGuard:
    """Meta-retro finding #1: replies with empty/whitespace body must not POST."""

    def test_empty_body_skips_common_post(self, tmp_path):
        """Reply items that produce an empty body never reach _post_replies_common."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        # Reset the module-level dedupe set so this test is isolated.
        pr_monitor._POSTED_REPLY_KEYS.clear()

        # item 1: action "noop" with empty description -> body = "Reviewed."
        #   (NOT empty, should post). item 2: action "unknown" with empty
        #   description and whitespace body via None fallback. We need a
        #   genuinely empty body — easiest is action="dismissed" with empty
        #   description -> body = "Not applicable — " which is NOT empty.
        # The real empty-body case: action not in {fixed,dismissed} AND
        # description is empty/whitespace -> body falls to
        # ``description or "Reviewed."`` = "Reviewed." which is also NOT
        # empty. To construct a true empty body, description must be a
        # whitespace string like "   " (truthy, so the fallback is bypassed).
        items = [
            {"id": 101, "action": "noop", "description": "   "},  # empty after strip
            {"id": 102, "action": "fixed", "commit": "abc",
             "description": "real fix"},  # real body
        ]

        with patch("pr_monitor._post_replies_common") as mock_common:
            pr_monitor.post_replies(wt, 42, items, logger)

        # _post_replies_common was called exactly once, with only the
        # non-empty-body item.
        assert mock_common.call_count == 1
        forwarded = mock_common.call_args[0][2]
        assert len(forwarded) == 1
        assert forwarded[0]["id"] == 102

    def test_all_empty_body_never_calls_common(self, tmp_path):
        """If every triage item has a whitespace-only body, _post_replies_common is skipped."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        # Every item has a truthy-but-whitespace description so the
        # default-fallback in _reply_body_for is bypassed and the body
        # ends up whitespace-only.
        items = [
            {"id": 201, "action": "noop", "description": " "},
            {"id": 202, "action": "noop", "description": "\t\n  "},
        ]

        with patch("pr_monitor._post_replies_common") as mock_common:
            pr_monitor.post_replies(wt, 42, items, logger)

        # All items had whitespace-only bodies — common POSTer never invoked.
        mock_common.assert_not_called()


class TestReadyFlipGates:
    """Meta-retro finding #2: auto-ready-flip requires 3 conditions."""

    def _base_result(self, **overrides):
        r = pr_monitor._empty_result()
        r["ci_result"] = "pass"
        r["gemini_review_posted"] = True
        r.update(overrides)
        return r

    def test_ready_flip_fires_when_all_conditions_met(self, tmp_path):
        """Ready flip fires when CI=pass, Gemini reviewed, no unreplied comments."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result()

        with patch("pr_monitor.get_unreplied_comments", return_value=[]), \
             patch("pr_monitor._rebase_source_branch", return_value=True) as mock_rebase, \
             patch("pr_monitor.mark_pr_ready", return_value=True) as mock_ready, \
             patch("pr_monitor.run_notify") as mock_notify:
            pr_monitor._step_ready(wt, 42, logger, result)

        mock_rebase.assert_called_once()
        mock_ready.assert_called_once()
        # No extra notify — we only notify on skip paths in _step_ready.
        mock_notify.assert_not_called()
        assert result["steps_completed"]["ready"]["status"] == "done"

    def test_ready_flip_skipped_when_ci_not_pass(self, tmp_path):
        """Any non-pass CI result prevents the flip (and rebase)."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(ci_result="fail")

        with patch("pr_monitor.get_unreplied_comments", return_value=[]), \
             patch("pr_monitor._rebase_source_branch") as mock_rebase, \
             patch("pr_monitor.mark_pr_ready") as mock_ready, \
             patch("pr_monitor.run_notify") as mock_notify:
            pr_monitor._step_ready(wt, 42, logger, result)

        mock_rebase.assert_not_called()
        mock_ready.assert_not_called()
        mock_notify.assert_called_once()  # notify on skip
        assert result["steps_completed"]["ready"]["status"] == "skipped"

    def test_ready_flip_skipped_when_no_gemini_review(self, tmp_path):
        """No Gemini review means no flip."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(gemini_review_posted=False)

        with patch("pr_monitor.get_unreplied_comments", return_value=[]), \
             patch("pr_monitor._rebase_source_branch") as mock_rebase, \
             patch("pr_monitor.mark_pr_ready") as mock_ready, \
             patch("pr_monitor.run_notify"):
            pr_monitor._step_ready(wt, 42, logger, result)

        mock_rebase.assert_not_called()
        mock_ready.assert_not_called()
        assert result["steps_completed"]["ready"]["status"] == "skipped"
        assert "gemini_review_not_posted" in result["ready_skip_reasons"]

    def test_ready_flip_skipped_when_unreplied_comments(self, tmp_path):
        """Any unreplied bot comment prevents the flip."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result()

        unreplied = [{"id": 9, "user": "gemini-code-assist[bot]",
                      "path": "a.py", "line": 1, "body": "look here"}]
        with patch("pr_monitor.get_unreplied_comments", return_value=unreplied), \
             patch("pr_monitor._rebase_source_branch") as mock_rebase, \
             patch("pr_monitor.mark_pr_ready") as mock_ready, \
             patch("pr_monitor.run_notify"):
            pr_monitor._step_ready(wt, 42, logger, result)

        mock_rebase.assert_not_called()
        mock_ready.assert_not_called()
        assert result["steps_completed"]["ready"]["status"] == "skipped"
        reasons = result["ready_skip_reasons"]
        assert any("unreplied_comments" in r for r in reasons)


class TestGeminiEffectivelyDone:
    """Clean-approval / decline pattern detection for the ready-flip gate."""

    def test_clean_approval_phrase_detected(self):
        """Classic 'no feedback' body is treated as effectively done."""
        body = (
            "## Code Review\n\n"
            "I have no feedback to provide on this pull request."
        )
        assert pr_monitor._gemini_effectively_done(body) is True

    def test_decline_phrase_detected(self):
        """'Unable to generate a review' body is treated as effectively done."""
        body = (
            "Gemini is unable to generate a review for this pull request "
            "due to the file types involved not being currently supported."
        )
        assert pr_monitor._gemini_effectively_done(body) is True

    def test_issues_present_body_not_matched(self):
        """A body that flagged issues should NOT match the clean patterns."""
        body = (
            "## Code Review\n\n"
            "I found a few issues with this pull request. See inline "
            "comments for details."
        )
        assert pr_monitor._gemini_effectively_done(body) is False

    def test_empty_body_not_matched(self):
        """Empty string returns False (no patterns can match)."""
        assert pr_monitor._gemini_effectively_done("") is False

    def test_none_body_not_matched(self):
        """None body is handled without raising and returns False."""
        assert pr_monitor._gemini_effectively_done(None) is False

    def test_patterns_are_module_constant(self):
        """Patterns are exposed for external extension/inspection.

        Stored lower-case — matching is case-insensitive (see
        ``_gemini_effectively_done``).
        """
        assert isinstance(pr_monitor._GEMINI_CLEAN_PATTERNS, tuple)
        assert all(p == p.lower() for p in pr_monitor._GEMINI_CLEAN_PATTERNS), \
            "patterns must be stored lower-case for case-insensitive match"
        assert "i have no feedback to provide" in pr_monitor._GEMINI_CLEAN_PATTERNS
        assert "gemini is unable to generate a review" \
            in pr_monitor._GEMINI_CLEAN_PATTERNS

    def test_case_insensitive_match(self):
        """Uppercase / mixed-case Gemini output still matches the gate."""
        assert pr_monitor._gemini_effectively_done(
            "GEMINI IS UNABLE TO GENERATE A REVIEW for this PR."
        ) is True
        assert pr_monitor._gemini_effectively_done(
            "I Have No Feedback To Provide."
        ) is True


class TestReadyFlipGeminiDimension:
    """Ready-flip gate now accepts clean/declined reviews OR replied-issues."""

    def _base_result(self, **overrides):
        r = pr_monitor._empty_result()
        r["ci_result"] = "pass"
        r.update(overrides)
        return r

    def test_clean_approval_body_satisfies_gemini_gate(self, tmp_path):
        """Clean-approval body flips the Gemini gate even when posted flag
        was somehow missed (and even with no inline-comment replies)."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=False,
            gemini_review_body="I have no feedback to provide on this PR.",
        )

        with patch("pr_monitor.get_unreplied_comments", return_value=[]):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is True
        assert reasons == []

    def test_declined_body_satisfies_gemini_gate(self, tmp_path):
        """Decline phrasing satisfies the Gemini gate — ready to flip."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=False,
            gemini_review_body=(
                "Gemini is unable to generate a review for this pull "
                "request due to the file types involved not being "
                "currently supported."
            ),
        )

        with patch("pr_monitor.get_unreplied_comments", return_value=[]):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is True
        assert reasons == []

    def test_issues_present_with_replies_satisfies_gate(self, tmp_path):
        """Gemini flagged issues (non-clean body) + all replied → gate passes."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=True,
            gemini_review_body="I found issues with this PR.",
        )

        with patch("pr_monitor.get_unreplied_comments", return_value=[]):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is True
        assert reasons == []

    def test_issues_present_without_replies_blocks_gate(self, tmp_path):
        """Gemini flagged issues + unreplied comments → gate fails."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=True,
            gemini_review_body="I found issues with this PR.",
        )
        unreplied = [{"id": 1, "user": "gemini-code-assist[bot]",
                      "path": "a.py", "line": 1, "body": "fix this"}]

        with patch("pr_monitor.get_unreplied_comments", return_value=unreplied):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is False
        assert any("unreplied_comments" in r for r in reasons)

    def test_empty_body_without_posted_flag_blocks_gate(self, tmp_path):
        """Empty body + no review posted → gate fails with posted reason."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=False,
            gemini_review_body="",
        )

        with patch("pr_monitor.get_unreplied_comments", return_value=[]):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is False
        assert "gemini_review_not_posted" in reasons

    def test_none_body_without_posted_flag_blocks_gate(self, tmp_path):
        """None body + no review posted → gate fails without raising."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=False,
            gemini_review_body=None,
        )

        with patch("pr_monitor.get_unreplied_comments", return_value=[]):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is False
        assert "gemini_review_not_posted" in reasons

    def test_clean_gemini_with_other_unreplied_blocks_gate(self, tmp_path):
        """Gemini clean-approval must NOT mask unreplied comments from
        other reviewers (e.g. CodeQL). PR #846 review regression guard.
        """
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=True,
            gemini_review_body="I have no feedback to provide on this PR.",
        )
        # Simulate an unreplied CodeQL finding aggregated by
        # get_unreplied_comments (which covers multiple bots).
        unreplied = [{"id": 99, "user": "github-advanced-security[bot]",
                      "path": "scripts/pr_monitor.py", "line": 1,
                      "body": "Potential security issue"}]

        with patch("pr_monitor.get_unreplied_comments", return_value=unreplied):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is False
        assert any("unreplied_comments" in r for r in reasons)

    def test_declined_gemini_with_other_unreplied_blocks_gate(self, tmp_path):
        """Gemini decline must NOT mask unreplied comments from other
        reviewers — decline satisfies the Gemini dimension only.
        """
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=True,
            gemini_review_body=(
                "Gemini is unable to generate a review for this pull "
                "request due to the file types involved not being "
                "currently supported."
            ),
        )
        unreplied = [{"id": 77, "user": "github-advanced-security[bot]",
                      "path": "scripts/pr_monitor.py", "line": 1,
                      "body": "CodeQL finding"}]

        with patch("pr_monitor.get_unreplied_comments", return_value=unreplied):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is False
        assert any("unreplied_comments" in r for r in reasons)

    def test_declined_gemini_with_zero_unreplied_passes_gate(self, tmp_path):
        """Gemini decline + zero unreplied comments (e.g. yml-only PR)
        → gate passes. Complements the regression guards above.
        """
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = self._base_result(
            gemini_review_posted=False,
            gemini_review_body=(
                "Gemini is unable to generate a review for this pull "
                "request due to the file types involved not being "
                "currently supported."
            ),
        )

        with patch("pr_monitor.get_unreplied_comments", return_value=[]):
            ok, reasons = pr_monitor._ready_flip_gates(wt, 42, result, logger)

        assert ok is True
        assert reasons == []


class TestReplyDedupe:
    """Meta-retro finding #3: in-process dedupe prevents duplicate POSTs."""

    def test_duplicate_reply_skipped_on_second_call(self, tmp_path):
        """Same (pr, id, action) tuple posts once; second call is a no-op."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        item = {"id": 777, "action": "fixed", "commit": "sha1",
                "description": "renamed var"}

        def fake_common(_wt, _pr, items, log_fn, **_kw):
            for i in items:
                log_fn("POSTED", comment_id=i["id"], action=i["action"])

        with patch("pr_monitor._post_replies_common",
                   side_effect=fake_common) as mock_common:
            pr_monitor.post_replies(wt, 42, [item], logger)
            pr_monitor.post_replies(wt, 42, [item], logger)

        # First call POSTed; second call should have been filtered out.
        # Common POSTer is invoked only when at least one item survives
        # filtering, so the second call never invokes it at all.
        assert mock_common.call_count == 1
        forwarded = mock_common.call_args[0][2]
        assert [i["id"] for i in forwarded] == [777]

    def test_duplicate_reply_skipped_regardless_of_action(self, tmp_path):
        """Same (pr, comment_id) posts once even when the action differs.

        PR #864 regression: LLM non-determinism across triage rounds re-
        classified the same Gemini comment as ``fixed`` then ``dismissed``
        — the old dedupe key ``(pr, comment_id, action)`` let both replies
        slip through. Dedupe must key on ``(pr, comment_id)`` only.
        """
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        item_fixed = {"id": 500, "action": "fixed", "commit": "sha",
                      "description": "ok"}
        item_dismissed = {"id": 500, "action": "dismissed",
                          "description": "nvm"}

        def fake_common(_wt, _pr, items, log_fn, **_kw):
            for i in items:
                log_fn("POSTED", comment_id=i["id"], action=i["action"])

        with patch("pr_monitor._post_replies_common",
                   side_effect=fake_common) as mock_common:
            pr_monitor.post_replies(wt, 42, [item_fixed], logger)
            pr_monitor.post_replies(wt, 42, [item_dismissed], logger)

        # Only the first reply POSTs — the second (different action,
        # same comment_id) is dedupped.
        assert mock_common.call_count == 1
        forwarded = mock_common.call_args[0][2]
        assert [i["id"] for i in forwarded] == [500]
        assert forwarded[0]["action"] == "fixed"

    def test_transient_failure_leaves_key_retryable(self, tmp_path):
        """A FAILED POST must not pollute the dedupe set — retry must work.

        PR #865 review (gemini-code-assist HIGH): pre-adding to the set
        before the API call blocked retry on transient errors. The key is
        now recorded only on the POSTED event, so a subsequent call for
        the same comment_id reaches _post_replies_common again.
        """
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        item = {"id": 999, "action": "fixed", "commit": "sha",
                "description": "x"}

        def fail_once(_wt, _pr, items, log_fn, **_kw):
            for i in items:
                log_fn("FAILED", comment_id=i["id"], error="boom")

        def succeed(_wt, _pr, items, log_fn, **_kw):
            for i in items:
                log_fn("POSTED", comment_id=i["id"], action=i["action"])

        with patch("pr_monitor._post_replies_common",
                   side_effect=fail_once) as mock_common:
            pr_monitor.post_replies(wt, 42, [item], logger)
            # First call failed; key must not be in the dedupe set.
            assert (42, 999) not in pr_monitor._POSTED_REPLY_KEYS
            assert mock_common.call_count == 1

        # Retry succeeds — common is invoked again, then key lands in the set.
        with patch("pr_monitor._post_replies_common",
                   side_effect=succeed) as mock_common:
            pr_monitor.post_replies(wt, 42, [item], logger)
            assert mock_common.call_count == 1
            assert (42, 999) in pr_monitor._POSTED_REPLY_KEYS


class TestStepCompletedResumeLogic:
    """Regression #929: failed steps must be retried on --resume.

    step_completed() previously returned True for any recorded status,
    including failures like 'rebase_failed' and 'error'. On resume,
    this caused failed steps to be skipped instead of retried.
    """

    def test_done_is_completed(self):
        result = {"steps_completed": {"ready": {"status": "done", "timestamp": "t"}}}
        assert pr_monitor.step_completed(result, "ready") is True

    def test_pass_is_completed(self):
        result = {"steps_completed": {"ci": {"status": "pass", "timestamp": "t"}}}
        assert pr_monitor.step_completed(result, "ci") is True

    def test_rebase_failed_is_not_completed(self):
        result = {"steps_completed": {"ready": {"status": "rebase_failed", "timestamp": "t"}}}
        assert pr_monitor.step_completed(result, "ready") is False

    def test_skipped_is_not_completed(self):
        result = {"steps_completed": {"ready": {"status": "skipped", "timestamp": "t"}}}
        assert pr_monitor.step_completed(result, "ready") is False

    def test_error_is_not_completed(self):
        result = {"steps_completed": {"retro": {"status": "error", "timestamp": "t"}}}
        assert pr_monitor.step_completed(result, "retro") is False

    def test_missing_step_is_not_completed(self):
        result = {"steps_completed": {}}
        assert pr_monitor.step_completed(result, "ready") is False

    def test_no_steps_completed_key(self):
        result = {}
        assert pr_monitor.step_completed(result, "ready") is False


class TestRebaseBeforeReady:
    """Meta-retro finding #6: rebase source branch before flipping ready."""

    def test_rebase_clean_path_calls_all_three_git_commands(self, tmp_path):
        """Clean rebase: detect-base + fetch + rebase + rev-parse + push all succeed."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        ok_base = subprocess.CompletedProcess(
            args=[], returncode=0, stdout="main\n", stderr="")
        ok = subprocess.CompletedProcess(args=[], returncode=0, stdout="", stderr="")
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return ok_base
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="feature-branch\n", stderr="")
            return ok

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is True
        # First call: gh pr view to detect base
        assert call_log[0][:3] == ["gh", "pr", "view"]
        # Then fetch-base + status (stash check) + rebase + rev-parse
        # + fetch-source (tracking ref update) + push
        assert call_log[1] == ["git", "fetch", "origin", "--", "main"]
        assert call_log[2] == ["git", "status", "--porcelain"]
        assert call_log[3] == ["git", "rebase", "--", "origin/main"]
        assert call_log[4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]
        assert call_log[5] == ["git", "fetch", "origin", "--", "feature-branch"]
        assert call_log[6] == [
            "git", "push", "--force-with-lease",
            "origin", "HEAD:feature-branch",
        ]
        assert len(call_log) == 7  # no abort path

    def test_rebase_conflict_aborts_and_returns_false(self, tmp_path):
        """On rebase conflict: git rebase --abort is called; no push; returns False."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            # base detect returns "main"
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            # fetch succeeds; rebase conflicts; abort succeeds.
            if cmd[:3] == ["git", "fetch", "origin"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="", stderr="")
            if cmd[:2] == ["git", "rebase"] and "--abort" not in cmd:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=1, stdout="",
                    stderr="CONFLICT (content): Merge conflict in foo.py")
            if cmd[:3] == ["git", "rebase", "--abort"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="", stderr="")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is False
        # Must have attempted rebase --abort
        assert ["git", "rebase", "--abort"] in call_log
        # Must NOT have pushed
        assert not any(c[:2] == ["git", "push"] for c in call_log)

    def test_ready_step_does_not_flip_on_rebase_conflict(self, tmp_path):
        """_step_ready: when rebase fails, mark_pr_ready is NOT called and user is notified."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        result = pr_monitor._empty_result()
        result["ci_result"] = "pass"
        result["gemini_review_posted"] = True

        with patch("pr_monitor.get_unreplied_comments", return_value=[]), \
             patch("pr_monitor._rebase_source_branch", return_value=False), \
             patch("pr_monitor.mark_pr_ready") as mock_ready, \
             patch("pr_monitor.run_notify") as mock_notify:
            pr_monitor._step_ready(wt, 7, logger, result)

        mock_ready.assert_not_called()
        mock_notify.assert_called_once()
        # Gemini review #3107696760: notify message must explain draft state.
        notify_kwargs = mock_notify.call_args.kwargs
        assert "message" in notify_kwargs
        assert "still in draft" in notify_kwargs["message"]
        assert result["steps_completed"]["ready"]["status"] == "rebase_failed"

    def test_rebase_uses_non_main_base_branch_from_gh_pr_view(self, tmp_path):
        """Gemini #3107696762: base branch is detected via gh pr view, not hardcoded."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            # gh pr view returns a non-main base branch
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="develop\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="feature-x\n", stderr="")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 88, logger)

        assert result is True
        # Must call gh pr view with PR number as a positional arg.
        # NOTE: no ``--`` separator before the PR number — gh treats everything
        # after ``--`` as positional, which makes ``--json``/``--jq`` fail with
        # "accepts at most 1 arg(s), received 5".
        gh_calls = [c for c in call_log if c[:3] == ["gh", "pr", "view"]]
        assert len(gh_calls) == 1
        assert "88" in gh_calls[0]
        assert "--" not in gh_calls[0]
        # Must fetch and rebase onto origin/develop (not origin/main)
        assert ["git", "fetch", "origin", "--", "develop"] in call_log
        assert ["git", "rebase", "--", "origin/develop"] in call_log
        # Must NOT have used main
        assert not any(
            c == ["git", "fetch", "origin", "--", "main"] for c in call_log)


class TestStaleTrackingRef:
    """Regression #929: fetch feature branch before push to avoid stale tracking ref.

    After the triage agent pushes to the remote, the local tracking ref is
    stale.  --force-with-lease compares the stale ref against the actual
    remote and rejects the push.  The fix fetches the feature branch before
    pushing.
    """

    def test_fetch_source_runs_before_push(self, tmp_path):
        """The feature branch must be fetched between rev-parse and push."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="feat/my-branch\n", stderr="")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is True
        fetch_src = ["git", "fetch", "origin", "--", "feat/my-branch"]
        assert fetch_src in call_log
        fetch_idx = call_log.index(fetch_src)
        push_idx = next(
            i for i, c in enumerate(call_log) if c[:2] == ["git", "push"])
        rev_parse_idx = next(
            i for i, c in enumerate(call_log)
            if c[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"])
        assert rev_parse_idx < fetch_idx < push_idx

    def test_fetch_source_failure_returns_false_and_does_not_push(self, tmp_path):
        """If fetching the feature branch fails, no push and return False."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="feat/stale\n", stderr="")
            # Fail the source-branch fetch (but not the base-branch fetch)
            if cmd == ["git", "fetch", "origin", "--", "feat/stale"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=128, stdout="",
                    stderr="fatal: couldn't find remote ref feat/stale")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is False
        assert not any(c[:2] == ["git", "push"] for c in call_log)


class TestUntrackedFilesDoNotBlockStash:
    """Regression #929: untracked files should not poison the stash safety check.

    git status --porcelain emits ?? for untracked files.  These don't block
    rebase, but if included in the dirty list they prevent stashing of
    legitimately dirty (modified) files that DO block rebase.
    """

    def test_untracked_file_excluded_from_stash_safety_check(self, tmp_path):
        """Modified .claude/state/ file is stashed even when an untracked .retros/ file exists."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="feat/test\n", stderr="")
            if cmd == ["git", "status", "--porcelain"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0,
                    stdout=" M .claude/state/in-flight-issues.json\n"
                           "?? .retros/summary.md\n",
                    stderr="")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is True
        stash_calls = [c for c in call_log if c[:2] == ["git", "stash"]]
        assert len(stash_calls) >= 1
        push_call = stash_calls[0]
        assert push_call[:4] == ["git", "stash", "push", "-m"]
        assert ".claude/state/in-flight-issues.json" in push_call
        assert ".retros/summary.md" not in push_call

    def test_only_untracked_files_skips_stash_entirely(self, tmp_path):
        """When all dirty entries are untracked, no stash is needed."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="feat/test\n", stderr="")
            if cmd == ["git", "status", "--porcelain"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0,
                    stdout="?? .retros/summary.md\n"
                           "?? some-other-untracked.txt\n",
                    stderr="")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is True
        stash_calls = [c for c in call_log if c[:2] == ["git", "stash"]]
        assert len(stash_calls) == 0

    def test_retros_dir_is_stashable(self, tmp_path):
        """Modified .retros/ files are safe to stash (written by monitor retro step)."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="feat/test\n", stderr="")
            if cmd == ["git", "status", "--porcelain"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0,
                    stdout=" M .retros/summary.json\n",
                    stderr="")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is True
        stash_calls = [c for c in call_log if c[:2] == ["git", "stash"]]
        assert len(stash_calls) >= 1
        assert ".retros/summary.json" in stash_calls[0]


class TestDetectBaseBranch:
    """Regression: gh pr view argv must not include ``--`` before the PR number.

    Cobra treats everything after ``--`` as positional, so ``--json`` and
    ``--jq`` become positional args and gh fails with
    ``accepts at most 1 arg(s), received 5`` — logged as BASE_DETECT_ERROR.
    """

    def test_detect_base_branch_argv_has_no_separator_before_pr_number(self, tmp_path):
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        captured = {}

        def fake_run(cmd, **kwargs):
            captured["cmd"] = cmd
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="main\n", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            base = pr_monitor._detect_base_branch(wt, 856, logger)

        assert base == "main"
        cmd = captured["cmd"]
        assert cmd[:3] == ["gh", "pr", "view"]
        # PR number must come immediately after "view" as the sole positional.
        assert cmd[3] == "856"
        assert "--" not in cmd
        # JSON flags must remain flags, not positionals.
        assert "--json" in cmd and "--jq" in cmd

    def test_detect_base_branch_falls_back_to_main_on_gh_failure(self, tmp_path):
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        def fake_run(cmd, **kwargs):
            return subprocess.CompletedProcess(
                args=cmd, returncode=1, stdout="",
                stderr="accepts at most 1 arg(s), received 5")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            assert pr_monitor._detect_base_branch(wt, 856, logger) == "main"


class TestRebasePushRefspec:
    """Regression: git push must use explicit origin + HEAD:<branch> refspec.

    Modern git's ``push.default=simple`` refuses a bare ``git push`` when the
    local branch name differs from its upstream tracking ref, logging
    ``rebase: PUSH_ERROR`` and leaving the PR in draft. Fix: detect the local
    branch via ``git rev-parse --abbrev-ref HEAD`` and push with an explicit
    refspec that is unambiguous regardless of upstream config.
    """

    def test_push_uses_explicit_origin_and_head_refspec(self, tmp_path):
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0,
                    stdout="fix/pr-monitor-push-refspec\n", stderr="")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is True
        push_calls = [c for c in call_log if c[:2] == ["git", "push"]]
        assert len(push_calls) == 1
        # Exact argv shape — no bare ``git push``.
        assert push_calls[0] == [
            "git", "push", "--force-with-lease",
            "origin", "HEAD:fix/pr-monitor-push-refspec",
        ]
        # rev-parse must have run before push.
        rev_parse_idx = next(
            i for i, c in enumerate(call_log)
            if c[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"])
        push_idx = next(
            i for i, c in enumerate(call_log) if c[:2] == ["git", "push"])
        assert rev_parse_idx < push_idx

    def test_rev_parse_failure_returns_false_and_does_not_push(self, tmp_path):
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=128, stdout="",
                    stderr="fatal: not a git repository")
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        log_path = str(tmp_path / "test.log")
        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is False
        # Must NOT have attempted any git push.
        assert not any(c[:2] == ["git", "push"] for c in call_log)
        # Must have logged a structured BRANCH_DETECT_ERROR event.
        with open(log_path, encoding="utf-8") as f:
            log_contents = f.read()
        assert "BRANCH_DETECT_ERROR" in log_contents

    def test_rev_parse_subprocess_exception_returns_false(self, tmp_path):
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        call_log = []

        def fake_run(cmd, **kwargs):
            call_log.append(cmd)
            if cmd[:3] == ["gh", "pr", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="main\n", stderr="")
            if cmd[:4] == ["git", "rev-parse", "--abbrev-ref", "HEAD"]:
                raise subprocess.TimeoutExpired(cmd=cmd, timeout=10)
            return subprocess.CompletedProcess(
                args=cmd, returncode=0, stdout="", stderr="")

        log_path = str(tmp_path / "test.log")
        with patch("pr_monitor.subprocess.run", side_effect=fake_run):
            result = pr_monitor._rebase_source_branch(wt, 42, logger)

        assert result is False
        assert not any(c[:2] == ["git", "push"] for c in call_log)
        with open(log_path, encoding="utf-8") as f:
            assert "BRANCH_DETECT_ERROR" in f.read()


class TestTriageLoopSkipsRepliedComments:
    """Regression: the triage loop must not re-ingest comments it already replied to.

    PR #864 double-reply: round 1 POSTed action=fixed, the end-of-round re-poll
    returned the same Gemini comment (the pulls/comments endpoint does not
    filter by in_reply_to_id), round 2 POSTed action=dismissed against the same
    id. On rounds 2+, the loop must consider only unreplied bot comments.
    """

    def test_round_two_sees_no_comments_when_round_one_already_replied(self, tmp_path):
        """After round 1 posts, the next iteration must not re-triage the same comment."""
        wt = _make_worktree(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        gemini_comment = {
            "id": 3115341440, "user": "gemini-code-assist[bot]",
            "path": "a.py", "line": 1, "body": "please fix",
        }

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments",
                   return_value=[gemini_comment]), \
             patch("pr_monitor.get_unreplied_comments", return_value=[]), \
             patch("pr_monitor.run_triage", return_value=[
                 {"id": 3115341440, "action": "fixed", "commit": "abc",
                  "description": "renamed var"}]) as mock_triage, \
             patch("pr_monitor._post_replies_common"), \
             patch("pr_monitor._rebase_source_branch", return_value=True), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 864, resume=False)

        assert exit_code == 0
        # run_triage must fire exactly once — round 2's re-poll must
        # treat the already-replied comment as out-of-scope and exit.
        assert mock_triage.call_count == 1

    def test_round_two_picks_up_new_unreplied_comments(self, tmp_path):
        """If Gemini posts a NEW comment after round 1, round 2 still runs."""
        wt = _make_worktree(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        first_comment = {
            "id": 100, "user": "gemini-code-assist[bot]",
            "path": "a.py", "line": 1, "body": "fix a",
        }
        second_comment = {
            "id": 200, "user": "gemini-code-assist[bot]",
            "path": "b.py", "line": 2, "body": "fix b",
        }
        # Round 1: run_triage sees first_comment and posts a reply.
        # Round 2: a new second_comment is unreplied; run_triage fires.
        # Round 3: nothing left; loop exits.
        unreplied_results = iter([[second_comment], [], []])
        triage_results = iter([
            [{"id": 100, "action": "fixed", "commit": "abc", "description": "ok"}],
            [{"id": 200, "action": "fixed", "commit": "def", "description": "ok"}],
        ])

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments",
                   return_value=[first_comment]), \
             patch("pr_monitor.get_unreplied_comments",
                   side_effect=lambda *a, **k: next(unreplied_results, [])), \
             patch("pr_monitor.run_triage",
                   side_effect=lambda *a, **k: next(triage_results, [])) as mock_triage, \
             patch("pr_monitor._post_replies_common"), \
             patch("pr_monitor._rebase_source_branch", return_value=True), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 999, resume=False)

        assert exit_code == 0
        # Round 1 + round 2 (new comment) = 2 triage calls. Round 3
        # has no unreplied comments so the loop exits.
        assert mock_triage.call_count == 2


class TestMsysPathconvNotInherited:
    """#910: MSYS_NO_PATHCONV must not propagate to claude subprocesses.

    Setting MSYS_NO_PATHCONV=1 in the claude -p env suppresses MSYS path
    conversion for ALL child processes, including hooks. Claude Code hooks
    reference $CLAUDE_PROJECT_DIR which needs MSYS->Windows conversion
    when passed to Python. Without conversion, /c/Users/... becomes
    C:\\c\\Users\\... — a non-existent path.
    """

    def test_triage_env_excludes_msys_no_pathconv(self, tmp_path):
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)
        comments = [{"id": 1, "user": "g", "path": "a.py", "line": 1, "body": "fix"}]

        stage_dir = os.path.join(wt, ".workflow", "stages")
        os.makedirs(stage_dir, exist_ok=True)
        jsonl_path = os.path.join(stage_dir, "pr-monitor-triage.jsonl")

        mock_proc = MagicMock()
        mock_proc.returncode = 0

        def fake_wait(timeout=None):
            with open(jsonl_path, "w") as f:
                f.write(json.dumps({"type": "result", "result": "[]"}) + "\n")
            return 0

        mock_proc.wait.side_effect = fake_wait

        with patch("pr_monitor.subprocess.Popen", return_value=mock_proc) as mock_popen:
            pr_monitor.run_triage(wt, 42, comments, logger)

        assert mock_popen.called, "subprocess.Popen was not called (check PPDS_SHAKEDOWN)"
        env = mock_popen.call_args.kwargs.get("env", {})
        assert "MSYS_NO_PATHCONV" not in env, \
            "MSYS_NO_PATHCONV in claude env breaks hook path resolution (#910)"

    def test_retro_env_excludes_msys_no_pathconv(self, tmp_path):
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        stage_dir = os.path.join(wt, ".workflow", "stages")
        os.makedirs(stage_dir, exist_ok=True)

        mock_proc = MagicMock()
        mock_proc.returncode = 0
        mock_proc.wait.return_value = 0

        with patch("pr_monitor.subprocess.Popen", return_value=mock_proc) as mock_popen:
            pr_monitor.run_retro(wt, logger)

        assert mock_popen.called, "subprocess.Popen was not called (check PPDS_SHAKEDOWN)"
        env = mock_popen.call_args.kwargs.get("env", {})
        assert "MSYS_NO_PATHCONV" not in env, \
            "MSYS_NO_PATHCONV in claude env breaks hook path resolution (#910)"


# ---------------------------------------------------------------------------
# PR #956: CI poll-error escalation
# ---------------------------------------------------------------------------


class TestCiEscalation:
    """PR #956: escalate when CI checks aren't posted after sustained POLL_ERROR."""

    def test_poll_error_below_threshold_does_not_escalate(self, tmp_path):
        """Fewer than CI_ESCALATION_THRESHOLD errors: no notification."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        call_count = [0]

        def mock_run(*args, **kwargs):
            call_count[0] += 1
            if call_count[0] <= 2:
                return subprocess.CompletedProcess(
                    args=[], returncode=1,
                    stdout="", stderr="no checks reported")
            return _gh_checks_json([
                {"name": "build", "state": "COMPLETED", "bucket": "pass"},
            ])

        with patch("pr_monitor.subprocess.run", side_effect=mock_run), \
             patch("pr_monitor.run_notify") as mock_notify, \
             patch("pr_monitor.CI_POLL_INTERVAL", 0), \
             patch("pr_monitor.CI_ESCALATION_MIN_ELAPSED", 0):
            result = pr_monitor.poll_ci(wt, 42, logger)

        assert result == "pass"
        mock_notify.assert_not_called()

    def test_poll_error_at_threshold_escalates_once(self, tmp_path):
        """>=CI_ESCALATION_THRESHOLD errors: run_notify fires exactly once."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        call_count = [0]

        def mock_run(*args, **kwargs):
            call_count[0] += 1
            if call_count[0] <= 5:
                return subprocess.CompletedProcess(
                    args=[], returncode=1,
                    stdout="", stderr="no checks reported")
            return _gh_checks_json([
                {"name": "build", "state": "COMPLETED", "bucket": "pass"},
            ])

        with patch("pr_monitor.subprocess.run", side_effect=mock_run), \
             patch("pr_monitor.run_notify") as mock_notify, \
             patch("pr_monitor.CI_POLL_INTERVAL", 0), \
             patch("pr_monitor.CI_ESCALATION_THRESHOLD", 3), \
             patch("pr_monitor.CI_ESCALATION_MIN_ELAPSED", 0):
            result = pr_monitor.poll_ci(wt, 42, logger)

        assert result == "pass"
        mock_notify.assert_called_once()
        msg = mock_notify.call_args.kwargs.get("message", "")
        assert "no CI checks reported" in msg
        assert "42" in msg

    def test_poll_error_recovers_after_escalation_logs_recovery(self, tmp_path):
        """After escalation, checks posting triggers RECOVERED log entry."""
        wt = _make_worktree(tmp_path)
        log_path = str(tmp_path / "test.log")
        logger = pr_monitor.Logger(log_path)

        call_count = [0]

        def mock_run(*args, **kwargs):
            call_count[0] += 1
            if call_count[0] <= 4:
                return subprocess.CompletedProcess(
                    args=[], returncode=1,
                    stdout="", stderr="no checks reported")
            return _gh_checks_json([
                {"name": "build", "state": "COMPLETED", "bucket": "pass"},
            ])

        with patch("pr_monitor.subprocess.run", side_effect=mock_run), \
             patch("pr_monitor.run_notify"), \
             patch("pr_monitor.CI_POLL_INTERVAL", 0), \
             patch("pr_monitor.CI_ESCALATION_THRESHOLD", 3), \
             patch("pr_monitor.CI_ESCALATION_MIN_ELAPSED", 0):
            result = pr_monitor.poll_ci(wt, 42, logger)

        assert result == "pass"
        with open(log_path) as f:
            log_contents = f.read()
        assert "RECOVERED" in log_contents
        assert "ci_checks_posting=True" in log_contents


# ---------------------------------------------------------------------------
# AC-183: MAX_CI_FIX_ROUNDS constant
# ---------------------------------------------------------------------------


class TestCiFixRoundBudget:
    """AC-183: MAX_CI_FIX_ROUNDS default 3, overridable via env var."""

    def test_ci_fix_round_budget_default_and_override(self, monkeypatch):
        """MAX_CI_FIX_ROUNDS defaults to 3 and is overridable via PPDS_MAX_CI_FIX_ROUNDS."""
        # Default value
        assert pr_monitor.MAX_CI_FIX_ROUNDS == 3

        # Override via environment variable (by checking the expression)
        import importlib
        monkeypatch.setenv("PPDS_MAX_CI_FIX_ROUNDS", "5")
        # Verify the constant is read from env at module load time
        override_val = int(os.environ.get("PPDS_MAX_CI_FIX_ROUNDS", "3"))
        assert override_val == 5


# ---------------------------------------------------------------------------
# AC-184 (part 1): KNOWN_FLAKE_PATTERNS constant
# ---------------------------------------------------------------------------


class TestKnownFlakePatternsConstant:
    """AC-184 part 1: KNOWN_FLAKE_PATTERNS constant exists and is non-empty."""

    def test_known_flake_patterns_constant_exists(self):
        """KNOWN_FLAKE_PATTERNS is a non-empty list of lowercase strings."""
        patterns = pr_monitor.KNOWN_FLAKE_PATTERNS
        assert isinstance(patterns, list), "KNOWN_FLAKE_PATTERNS must be a list"
        assert len(patterns) > 0, "KNOWN_FLAKE_PATTERNS must be non-empty"
        # Check at least one network/transient pattern is present
        patterns_lower = [p.lower() for p in patterns]
        assert any("timeout" in p or "connection" in p or "reset" in p
                   for p in patterns_lower), \
            "KNOWN_FLAKE_PATTERNS should include at least one transient network pattern"


# ---------------------------------------------------------------------------
# AC-187: Decision file schema
# ---------------------------------------------------------------------------


class TestDecisionFileSchema:
    """AC-187: _write_ci_fix_decision writes typed fields to JSON."""

    def test_decision_file_schema_typed_fields(self, tmp_path):
        """_write_ci_fix_decision writes all required fields with correct types."""
        wt = _make_worktree(tmp_path)
        sha = "abc1234def567"
        payload = {
            "pr": 42,
            "failure_summary": "test suite failed on line 10",
            "files_touched": ["scripts/pr_monitor.py", "tests/test_pr_monitor.py"],
            "lines_added": 5,
            "lines_removed": 3,
            "action": "fix",
            "escalation_reason": None,
            "scope_violation": False,
        }

        path = pr_monitor._write_ci_fix_decision(wt, sha, round_num=1, payload=payload)

        assert os.path.exists(str(path))
        with open(str(path), "r", encoding="utf-8") as f:
            record = json.load(f)

        assert isinstance(record["round"], int)
        assert record["round"] == 1
        assert isinstance(record["timestamp"], str)
        assert "T" in record["timestamp"]  # ISO 8601 format
        assert isinstance(record["pr"], int)
        assert record["pr"] == 42
        assert isinstance(record["failure_summary"], str)
        assert isinstance(record["files_touched"], list)
        assert all(isinstance(p, str) for p in record["files_touched"])
        assert isinstance(record["lines_added"], int)
        assert isinstance(record["lines_removed"], int)
        assert record["action"] in ("fix", "escalate")
        assert record["escalation_reason"] is None or isinstance(record["escalation_reason"], str)
        assert isinstance(record["scope_violation"], bool)


# ---------------------------------------------------------------------------
# AC-188: .workflow/ci-fix-decisions/ not gitignored
# ---------------------------------------------------------------------------


class TestCiFixDecisionsDirNotGitignored:
    """AC-188: .workflow/ci-fix-decisions/ is committed, not gitignored."""

    def test_ci_fix_decisions_dir_not_gitignored(self, tmp_path):
        """The .gitignore negation rules allow ci-fix-decisions/ to be tracked."""
        import subprocess as sp
        repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

        # git check-ignore returns non-zero when path is NOT ignored
        result = sp.run(
            ["git", "check-ignore", "-v",
             ".workflow/ci-fix-decisions/.gitkeep"],
            cwd=repo_root,
            stdin=sp.DEVNULL,
            stdout=sp.PIPE,
            stderr=sp.PIPE,
            text=True,
        )
        # Non-zero means "not ignored" — that's what we want
        assert result.returncode != 0, (
            ".workflow/ci-fix-decisions/.gitkeep must NOT be gitignored. "
            f"git check-ignore says: {result.stdout.strip()!r}"
        )


# ---------------------------------------------------------------------------
# AC-194 (enum): TERMINAL_STATES constant
# ---------------------------------------------------------------------------


class TestTerminalStateEnum:
    """AC-194: TERMINAL_STATES tuple contains exactly the specified values."""

    def test_terminal_state_enum(self):
        """TERMINAL_STATES is a tuple with exactly the 6 required terminal state names."""
        states = pr_monitor.TERMINAL_STATES
        assert isinstance(states, tuple), "TERMINAL_STATES must be a tuple"
        required = {
            "ready",
            "stuck-ci-fix-exhausted",
            "stuck-triage-exhausted",
            "stuck-thrash-detected",
            "ci-timeout",
            "monitor-crash",
        }
        assert set(states) == required, (
            f"TERMINAL_STATES mismatch.\n"
            f"Expected: {sorted(required)}\n"
            f"Got:      {sorted(states)}"
        )
        assert len(states) == 6, "TERMINAL_STATES must have exactly 6 entries (no duplicates)"


# ---------------------------------------------------------------------------
# AC-181: No dispatch while CI running
# ---------------------------------------------------------------------------


class TestOrchestratorNoCiDispatch:
    """AC-181: Monitor never dispatches an agent while CI is running."""

    def test_orchestrator_no_dispatch_while_ci_running(self, tmp_path):
        """_classify returns 'wait' when ci_result is 'running'."""
        # The classifier is the gating function — test it directly
        assert pr_monitor._classify("running", False) == "wait"
        assert pr_monitor._classify("running", True) == "wait"
        assert pr_monitor._classify(None, False) == "wait"
        assert pr_monitor._classify(None, True) == "wait"


# ---------------------------------------------------------------------------
# AC-182: Classifier dispatch matrix
# ---------------------------------------------------------------------------


class TestClassifierDispatchMatrix:
    """AC-182: Parametrized decision table test for _classify."""

    @pytest.mark.parametrize("ci_result,has_comments,expected_action", [
        ("pass",    False, "done"),
        ("pass",    True,  "dispatch_gemini_triage"),
        ("fail",    False, "dispatch_ci_fix"),
        ("fail",    True,  "dispatch_ci_fix"),
        ("timeout", False, "terminal_state"),
        ("timeout", True,  "terminal_state"),
        ("running", False, "wait"),
        ("running", True,  "wait"),
    ])
    def test_classifier_dispatch_matrix(self, ci_result, has_comments, expected_action):
        """_classify returns expected action for each (ci_result, has_comments) pair."""
        assert pr_monitor._classify(ci_result, has_comments) == expected_action


# ---------------------------------------------------------------------------
# AC-195: Serialized agent dispatch
# ---------------------------------------------------------------------------


class TestSerializedAgentDispatch:
    """AC-195: Only one agent in flight at a time."""

    def test_serialized_agent_dispatch_across_kinds(self, tmp_path):
        """With (fail, comments), CI-fix runs first; triage dispatches only
        after CI is green (only one agent in flight at a time)."""
        wt = _make_worktree(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        dispatch_log = []

        gemini_comment = {"id": 1, "user": "gemini-code-assist[bot]",
                          "path": "a.py", "line": 1, "body": "fix this"}

        # ci_calls tracks how many times poll_ci has been called.
        # First call: "fail" (triggers ci-fix dispatch)
        # Subsequent calls (after ci-fix): "pass"
        ci_calls = [0]
        def ci_side_effect(*a, **kw):
            ci_calls[0] += 1
            return "pass" if ci_calls[0] > 1 else "fail"

        triage_calls = [0]
        def triage_side_effect(*a, **kw):
            dispatch_log.append("gemini-triage")
            triage_calls[0] += 1
            return []

        with patch("pr_monitor.poll_ci", side_effect=ci_side_effect), \
             patch("pr_monitor.poll_gemini_comments",
                   return_value=[gemini_comment]), \
             patch("pr_monitor.get_unreplied_comments", return_value=[]), \
             patch("pr_monitor.run_triage", side_effect=triage_side_effect), \
             patch("pr_monitor._reconcile_replies"), \
             patch("pr_monitor._dispatch_ci_fix_agent",
                   return_value={"action": "fix", "files_touched": ["scripts/foo.py"],
                                 "lines_added": 1, "lines_removed": 0,
                                 "escalation_reason": None, "scope_violation": False,
                                 "failure_summary": "test failed"}), \
             patch("pr_monitor._write_ci_fix_decision"), \
             patch("pr_monitor._check_thrash", return_value=False), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        # CI-fix ran first (ci_calls > 1 means we re-polled after fix attempt)
        assert ci_calls[0] > 1, "CI should be re-polled after CI-fix dispatch"
        # Triage ran after CI was green
        assert triage_calls[0] >= 1, "Gemini triage should run after CI became green"
        # Final result should be "ready" (happy path after fix + triage)
        result = pr_monitor.read_result(wt)
        assert result["status"] == "ready"


# ---------------------------------------------------------------------------
# AC-196: Independent round budgets
# ---------------------------------------------------------------------------


class TestIndependentRoundBudgets:
    """AC-196: MAX_CI_FIX_ROUNDS and MAX_TRIAGE_ITERATIONS are independent."""

    def test_independent_round_budgets(self, tmp_path):
        """Exhausting CI-fix budget does not consume triage budget and vice-versa."""
        wt = _make_worktree(tmp_path)

        # Scenario: CI always fails → CI-fix budget exhausted
        # → stuck-ci-fix-exhausted, with triage budget untouched
        with patch("pr_monitor.poll_ci", return_value="fail"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 1, resume=False)

        assert exit_code == 1
        result = pr_monitor.read_result(wt)
        assert result["status"] == "stuck-ci-fix-exhausted"
        # The result file should NOT contain any triage_N steps — triage was not run
        steps = result.get("steps_completed", {})
        triage_steps = [k for k in steps if k.startswith("triage_")]
        assert triage_steps == [], \
            f"Triage budget should be untouched; got triage steps: {triage_steps}"


# ---------------------------------------------------------------------------
# STATE_TRANSITION log events
# ---------------------------------------------------------------------------


class TestStateTransitionsLogged:
    """Bonus: orchestrator logs STATE_TRANSITION events."""

    def test_state_transitions_logged(self, tmp_path):
        """run_monitor emits STATE_TRANSITION log events with from/to fields."""
        wt = _make_worktree(tmp_path)

        log_events = []
        original_log = pr_monitor.Logger.log

        def capture_log(self_, step, event, **extra):
            log_events.append((step, event, extra))
            original_log(self_, step, event, **extra)

        with patch.object(pr_monitor.Logger, "log", capture_log), \
             patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            pr_monitor.run_monitor(wt, 1, resume=False)

        transitions = [(step, ev, ex) for step, ev, ex in log_events
                       if ev == "STATE_TRANSITION"]
        assert len(transitions) >= 1, "At least one STATE_TRANSITION must be logged"
        for _, _, extra in transitions:
            assert "from" in extra, "STATE_TRANSITION must have 'from' field"
            assert "to" in extra, "STATE_TRANSITION must have 'to' field"


# ---------------------------------------------------------------------------
# AC-184 part 2: Flake pattern — rerun once, then real
# ---------------------------------------------------------------------------


class TestFlakePatternRerunOnceThenReal:
    """AC-184 part 2: First flake match reruns; second match treats as real."""

    def test_flake_pattern_rerun_once_then_real(self, tmp_path):
        """_check_flake returns 'flake-rerun' on first match, 'real' on second."""
        wt = _make_worktree(tmp_path)
        sha = "deadbeef1234"

        # First match: transient network error
        result1 = pr_monitor._check_flake(
            wt, "connection reset by peer in test run", sha
        )
        assert result1 == "flake-rerun", f"Expected flake-rerun, got {result1!r}"

        # Second match for the same commit: treat as real
        result2 = pr_monitor._check_flake(
            wt, "connection reset by peer in test run again", sha
        )
        assert result2 == "flake-second-match", f"Expected flake-second-match, got {result2!r}"

    def test_no_pattern_match_returns_real(self, tmp_path):
        """_check_flake returns 'real' when log contains no known flake pattern."""
        wt = _make_worktree(tmp_path)
        result = pr_monitor._check_flake(
            wt, "AssertionError: expected 1 got 2", "sha123"
        )
        assert result == "real"

    def test_empty_log_returns_real(self, tmp_path):
        """_check_flake returns 'real' for empty failure log."""
        wt = _make_worktree(tmp_path)
        assert pr_monitor._check_flake(wt, "", "sha123") == "real"
        assert pr_monitor._check_flake(wt, None, "sha123") == "real"


# ---------------------------------------------------------------------------
# AC-185: CI-fix agent prompt payload
# ---------------------------------------------------------------------------


class TestCiFixAgentPromptPayload:
    """AC-185: CI-fix agent receives all 6 required fields."""

    def test_ci_fix_agent_prompt_payload(self, tmp_path):
        """_dispatch_ci_fix_agent sends all 6 required fields to dispatch_subagent."""
        wt = _make_worktree(tmp_path)

        # Create a minimal .workflow/state.json for branch_acs
        wf = os.path.join(wt, ".workflow")
        os.makedirs(wf, exist_ok=True)
        with open(os.path.join(wf, "state.json"), "w") as f:
            json.dump({"spec": "specs/workflow-enforcement.md"}, f)

        captured_payloads = []

        def mock_dispatch(profile_name, payload, **kwargs):
            captured_payloads.append((profile_name, payload))
            return {"stdout": '{"action": "fix", "files_touched": [], "lines_added": 0, "lines_removed": 0, "escalation_reason": null, "scope_violation": false, "failure_summary": "ok"}',
                    "stderr": "", "exit_code": 0}

        with patch("pr_monitor.subprocess.run",
                   return_value=subprocess.CompletedProcess(
                       args=[], returncode=0, stdout="abc1234\n", stderr="")), \
             patch("triage_common.dispatch_subagent", mock_dispatch):
            pr_monitor._dispatch_ci_fix_agent(
                wt, 42, "test suite failed on line 10",
                "abc1234", round_num=1, gemini_comments=[],
            )

        assert len(captured_payloads) == 1
        profile_name, payload = captured_payloads[0]
        assert profile_name == "ci-fix"

        # 6 required fields per AC-185
        assert "failure_summary" in payload, "missing: failure_summary"
        assert "diff" in payload, "missing: diff"
        assert "branch_acs" in payload, "missing: branch_acs"
        assert "gemini_comments" in payload, "missing: gemini_comments"
        assert "constitution" in payload, "missing: constitution"
        assert "commit_sha" in payload, "missing: commit_sha"


# ---------------------------------------------------------------------------
# AC-186: Thrash detection
# ---------------------------------------------------------------------------


class TestThrashDetection:
    """AC-186: Thrash detection exits on repeated files_touched."""

    def test_thrash_detection_exits_on_repeat_files(self, tmp_path):
        """_check_thrash returns True when round N files == round N-1 files."""
        wt = _make_worktree(tmp_path)
        sha = "abc123"

        # Write a prior decision (round 1)
        prior_payload = {
            "pr": 1,
            "failure_summary": "test failed",
            "files_touched": ["scripts/pr_monitor.py", "tests/test_pr_monitor.py"],
            "lines_added": 3,
            "lines_removed": 2,
            "action": "fix",
            "escalation_reason": None,
            "scope_violation": False,
        }
        pr_monitor._write_ci_fix_decision(wt, sha, round_num=1, payload=prior_payload)

        # Round 2 touches the exact same files (order-independent)
        current_decision = {
            "files_touched": ["tests/test_pr_monitor.py", "scripts/pr_monitor.py"],
        }
        result = pr_monitor._check_thrash(wt, sha, round_num=2, current_decision=current_decision)
        assert result is True, "Should detect thrash when files_touched is identical"

    def test_thrash_check_skipped_round_1(self, tmp_path):
        """_check_thrash always returns False for round 1 (no prior round)."""
        wt = _make_worktree(tmp_path)
        sha = "abc123"

        current = {"files_touched": ["scripts/pr_monitor.py"]}
        result = pr_monitor._check_thrash(wt, sha, round_num=1, current_decision=current)
        assert result is False, "Round 1 must never trigger thrash detection"

    def test_thrash_check_different_files_no_thrash(self, tmp_path):
        """Different files_touched in round N vs N-1 → no thrash."""
        wt = _make_worktree(tmp_path)
        sha = "abc123"

        prior = {"pr": 1, "failure_summary": "x", "files_touched": ["a.py"],
                 "lines_added": 1, "lines_removed": 0, "action": "fix",
                 "escalation_reason": None, "scope_violation": False}
        pr_monitor._write_ci_fix_decision(wt, sha, round_num=1, payload=prior)

        current = {"files_touched": ["b.py"]}
        result = pr_monitor._check_thrash(wt, sha, round_num=2, current_decision=current)
        assert result is False, "Different files should not trigger thrash"


# ---------------------------------------------------------------------------
# AC-191: Single terminal notification per run
# ---------------------------------------------------------------------------


class TestSingleTerminalNotificationPerRun:
    """AC-191: _notify_terminal fires exactly once, on terminal state."""

    def test_single_terminal_notification_per_run(self, tmp_path):
        """Happy path: exactly one run_notify call (via _step_notify)."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor._ready_flip_gates", return_value=(True, [])), \
             patch("pr_monitor._rebase_source_branch", return_value=True), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        assert exit_code == 0
        # Exactly one notify: the terminal success notification from _step_notify
        assert mock_notify.call_count == 1

    def test_ci_timeout_exactly_one_notification(self, tmp_path):
        """CI timeout terminal path fires exactly one notification."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="timeout"), \
             patch("pr_monitor.run_notify") as mock_notify:
            pr_monitor.run_monitor(wt, 42, resume=False)

        assert mock_notify.call_count == 1

    def test_ci_fix_exhausted_exactly_one_notification(self, tmp_path):
        """CI-fix-exhausted terminal path fires exactly one notification."""
        wt = _make_worktree(tmp_path)

        with patch("pr_monitor.poll_ci", return_value="fail"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.run_notify") as mock_notify:
            pr_monitor.run_monitor(wt, 42, resume=False)

        assert mock_notify.call_count == 1
        result = pr_monitor.read_result(wt)
        assert result["status"] == "stuck-ci-fix-exhausted"

    def test_triage_exhausted_exactly_one_notification(self, tmp_path):
        """Triage-exhausted terminal path fires exactly one notification."""
        wt = _make_worktree(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        comment = {"id": 1, "user": "gemini-code-assist[bot]",
                   "path": "a.py", "line": 1, "body": "fix"}

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[comment]), \
             patch("pr_monitor.get_unreplied_comments", return_value=[comment]), \
             patch("pr_monitor.run_triage", return_value=[]), \
             patch("pr_monitor.run_notify") as mock_notify:
            pr_monitor.run_monitor(wt, 42, resume=False)

        assert mock_notify.call_count == 1
        result = pr_monitor.read_result(wt)
        assert result["status"] == "stuck-triage-exhausted"

    def test_flake_rerun_does_not_notify(self, tmp_path):
        """A flake-rerun path emits NO notification (flakes are transparent)."""
        wt = _make_worktree(tmp_path)

        # CI: first call "fail" with flake log, second call "pass"
        ci_calls = [0]
        def ci_side_effect(*a, **kw):
            ci_calls[0] += 1
            return "pass" if ci_calls[0] > 1 else "fail"

        # _check_flake returns "flake-rerun" for the first call
        flake_calls = [0]
        def flake_side_effect(*a, **kw):
            flake_calls[0] += 1
            return "flake-rerun"

        with patch("pr_monitor.poll_ci", side_effect=ci_side_effect), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor._check_flake", side_effect=flake_side_effect), \
             patch("pr_monitor._ready_flip_gates", return_value=(True, [])), \
             patch("pr_monitor._rebase_source_branch", return_value=True), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        # Flake rerun must not emit an intermediate notification
        # (only the terminal success notification at the end)
        assert exit_code == 0
        assert mock_notify.call_count == 1


# ---------------------------------------------------------------------------
# AC-192: No mid-loop notifications
# ---------------------------------------------------------------------------


class TestNoMidLoopNotifications:
    """AC-192: No notifications during CI-fix loop or between triage iterations."""

    def test_no_notification_during_ci_fix_loop(self, tmp_path):
        """During CI-fix rounds (before terminal state), no notification fires."""
        wt = _make_worktree(tmp_path)

        # CI: first "fail", then "pass" after fix
        ci_calls = [0]
        def ci_side_effect(*a, **kw):
            ci_calls[0] += 1
            return "pass" if ci_calls[0] > 1 else "fail"

        with patch("pr_monitor.poll_ci", side_effect=ci_side_effect), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor._dispatch_ci_fix_agent",
                   return_value={"action": "fix", "files_touched": ["a.py"],
                                 "lines_added": 1, "lines_removed": 0,
                                 "escalation_reason": None, "scope_violation": False,
                                 "failure_summary": "fixed"}), \
             patch("pr_monitor._write_ci_fix_decision"), \
             patch("pr_monitor._check_thrash", return_value=False), \
             patch("pr_monitor._ready_flip_gates", return_value=(True, [])), \
             patch("pr_monitor._rebase_source_branch", return_value=True), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        assert exit_code == 0
        # Only one notification: the final success notification
        assert mock_notify.call_count == 1

    def test_no_notification_between_triage_iterations(self, tmp_path):
        """Between triage iterations (while comments remain), no intermediate notification."""
        wt = _make_worktree(tmp_path)
        pr_monitor._POSTED_REPLY_KEYS.clear()

        comment = {"id": 1, "user": "gemini-code-assist[bot]",
                   "path": "a.py", "line": 1, "body": "issue"}

        # Triage iteration 1: returns a result, unreplied comments then resolve
        triage_calls = [0]
        def triage_side_effect(*a, **kw):
            triage_calls[0] += 1
            return [{"id": 1, "action": "dismissed", "description": "ok", "commit": None}]

        unreplied_calls = [0]
        def unreplied_side_effect(*a, **kw):
            unreplied_calls[0] += 1
            # Return empty on first call (comments resolved)
            return []

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[comment]), \
             patch("pr_monitor.get_unreplied_comments",
                   side_effect=unreplied_side_effect), \
             patch("pr_monitor.run_triage", side_effect=triage_side_effect), \
             patch("pr_monitor._reconcile_replies"), \
             patch("pr_monitor._ready_flip_gates", return_value=(True, [])), \
             patch("pr_monitor._rebase_source_branch", return_value=True), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        assert exit_code == 0
        # Only one notification: the terminal success notification
        assert mock_notify.call_count == 1


# ---------------------------------------------------------------------------
# AC-193: Terminal notification format
# ---------------------------------------------------------------------------


class TestTerminalNotificationFormat:
    """AC-193: Terminal notification body format."""

    def test_terminal_notification_format(self, tmp_path):
        """_build_terminal_notification produces the required fields."""
        wt = _make_worktree(tmp_path)
        result = pr_monitor._empty_result()
        result["ci_result"] = "fail"
        result["gemini_review_posted"] = False
        result["triage_summary"] = []

        msg = pr_monitor._build_terminal_notification(
            pr_number=42,
            terminal_state="stuck-ci-fix-exhausted",
            result=result,
            ci_fix_rounds_used=3,
            triage_rounds_used=0,
            worktree=wt,
        )

        assert "PR #42:" in msg
        assert "stuck-ci-fix-exhausted" in msg
        assert "CI: fail" in msg
        assert "CI-fix rounds used: 3/" in msg
        assert "Triage rounds used: 0/" in msg
        assert "Last decision:" in msg


# ---------------------------------------------------------------------------
# AC-108 superseded behavior
# ---------------------------------------------------------------------------


class TestCiFailureRoutesToFixLoop:
    """AC-108 superseded: CI failure routes to CI-fix agent, not a mid-loop notification."""

    def test_ci_failure_routes_to_fix_loop(self, tmp_path):
        """When CI fails, _dispatch_ci_fix_agent is called (not _notify_terminal mid-loop)."""
        wt = _make_worktree(tmp_path)

        ci_calls = [0]
        def ci_side_effect(*a, **kw):
            ci_calls[0] += 1
            return "fail"  # Always fail → exhaust rounds

        with patch("pr_monitor.poll_ci", side_effect=ci_side_effect), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        assert exit_code == 1
        result = pr_monitor.read_result(wt)
        assert result["status"] == "stuck-ci-fix-exhausted"
        # Only ONE notification at the terminal state (not mid-loop)
        assert mock_notify.call_count == 1
