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
# Fixture: auto-mock _poll_codeql and detect_gemini_overload for run_monitor tests
# These were added in v8.0 and most existing tests don't need to exercise them.
# ---------------------------------------------------------------------------

_real_poll_codeql = pr_monitor._poll_codeql
_real_detect_gemini_overload = pr_monitor.detect_gemini_overload


@pytest.fixture(autouse=True)
def _mock_v8_helpers(monkeypatch):
    """Prevent real git/gh calls from _poll_codeql and detect_gemini_overload.

    Tests that exercise these functions directly should call the _real_*
    module-level references instead of ``pr_monitor._poll_codeql``.
    """
    monkeypatch.setattr(pr_monitor, "_poll_codeql", lambda *a, **kw: None)
    monkeypatch.setattr(pr_monitor, "detect_gemini_overload", lambda *a, **kw: False)


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
        """poll_ci returns 'fail' when a check has FAILURE conclusion."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        checks = [
            {"name": "build", "state": "COMPLETED", "conclusion": "SUCCESS"},
            {"name": "lint", "state": "COMPLETED", "conclusion": "FAILURE"},
        ]

        with patch("pr_monitor.subprocess.run", return_value=_gh_checks_json(checks)):
            result = pr_monitor.poll_ci(wt, 10, logger)

        assert result == "fail"

    def test_ci_failure_triggers_notify_in_monitor(self, tmp_path):
        """run_monitor writes status=ci_failed and calls notify on CI failure."""
        wt = _make_worktree(tmp_path)

        checks_fail = [
            {"name": "build", "state": "COMPLETED", "conclusion": "FAILURE"},
        ]

        with patch("pr_monitor.subprocess.run", return_value=_gh_checks_json(checks_fail)), \
             patch("pr_monitor.run_notify") as mock_notify:
            exit_code = pr_monitor.run_monitor(wt, 99, resume=False)

        assert exit_code == 1
        result = pr_monitor.read_result(wt)
        assert result["status"] == "ci_failed"
        mock_notify.assert_called_once()
        # Verify worktree and PR number are passed to notification
        notify_args = mock_notify.call_args
        assert notify_args[0][0] == wt, "run_notify must receive worktree path"
        assert notify_args[0][1] == 99, "run_notify must receive PR number"


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
        """Triage loop stops after MAX_TRIAGE_ITERATIONS even if comments persist."""
        wt = _make_worktree(tmp_path)

        persistent_comments = [
            {"id": 1, "user": "g", "path": "a.py", "line": 1, "body": "fix"}
        ]

        # poll_ci: initial + 3 re-checks = 4 calls
        # poll_gemini: initial + 3 re-polls = 4 calls (always returns comments)
        # run_triage: 3 calls
        with patch("pr_monitor.poll_ci", return_value="pass") as mock_ci, \
             patch("pr_monitor.poll_gemini_comments",
                   return_value=persistent_comments) as mock_gemini, \
             patch("pr_monitor.run_triage", return_value=[]) as mock_triage, \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 1, resume=False)

        assert exit_code == 0
        # Triage should be called exactly MAX_TRIAGE_ITERATIONS (3) times
        assert mock_triage.call_count == pr_monitor.MAX_TRIAGE_ITERATIONS


class TestRetroBeforeNotify:
    """AC-112: Retro runs as penultimate step before notification."""

    def test_retro_runs_before_notify(self, tmp_path):
        """In the step sequence, retro is called before notify."""
        wt = _make_worktree(tmp_path)

        call_order = []

        def track_retro(worktree, logger):
            call_order.append("retro")
            return "done"

        def track_notify(worktree, pr_number, logger):
            call_order.append("notify")

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
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
        assert result["status"] == "complete"

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
        assert data["status"] == "complete"


class TestGeminiTimeout:
    """AC-126: Gemini polling stops after GEMINI_MAX_WAIT (5 min)."""

    def test_gemini_timeout(self, tmp_path):
        """poll_gemini_comments returns empty list after max wait with no stable count."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        # Return incrementing counts so it never stabilizes
        poll_count = [0]

        def varying_counts(*args, **kwargs):
            cmd = args[0] if args else kwargs.get("args", [])
            if cmd and "view" in cmd:
                return subprocess.CompletedProcess(
                    args=[], returncode=0, stdout="owner/repo", stderr=""
                )
            if cmd and "--jq" in cmd and "length" in cmd[cmd.index("--jq") + 1]:
                # Count query — return incrementing counts to prevent stabilization
                poll_count[0] += 1
                return subprocess.CompletedProcess(
                    args=[], returncode=0, stdout=str(poll_count[0]), stderr=""
                )
            # Full fetch query — return JSON array
            return subprocess.CompletedProcess(
                args=[], returncode=0, stdout="[]", stderr=""
            )

        # Use a small positive max_wait so the loop body actually executes
        time_calls = [0]

        def advancing_time():
            time_calls[0] += 1
            return time_calls[0] * 2.0  # 2s per call, timeout at 5s

        with patch("pr_monitor.subprocess.run", side_effect=varying_counts), \
             patch("pr_monitor.GEMINI_MAX_WAIT", 5), \
             patch("pr_monitor.GEMINI_POLL_INTERVAL", 0), \
             patch("pr_monitor.time.time", side_effect=advancing_time), \
             patch("pr_monitor.time.sleep"):
            result = pr_monitor.poll_gemini_comments(wt, 5, logger)

        # Should return empty since never got stable count and timed out
        assert result == []
        # Verify polling actually happened (loop body executed)
        assert poll_count[0] >= 1, f"Expected at least 1 poll attempt, got {poll_count[0]}"


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

        def track_notify(worktree, pr_number, logger):
            call_order.append(("notify", worktree))

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
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


class TestGeminiRetryOnOverload:
    """AC-142: When Gemini overload is detected, monitor posts /gemini review and re-polls."""

    def test_gemini_retry_on_overload(self, tmp_path):
        """AC-142: overload triggers /gemini review post and re-poll of comments."""
        wt = _make_worktree(tmp_path)

        retry_comments = [
            {"id": 1, "user": "gemini", "path": "a.py", "line": 1, "body": "Fix this"},
        ]

        # poll_gemini_comments: first returns [] (overload), second returns comments
        gemini_calls = [0]

        def mock_poll_gemini(worktree, pr_number, logger):
            gemini_calls[0] += 1
            if gemini_calls[0] == 1:
                return []  # First poll: no comments (overload)
            return retry_comments  # Retry poll: comments appear

        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", side_effect=mock_poll_gemini), \
             patch("pr_monitor.detect_gemini_overload", return_value=True) as mock_detect, \
             patch("pr_monitor._post_gemini_retry") as mock_retry_post, \
             patch("pr_monitor.run_triage", return_value=[
                 {"id": 1, "action": "fixed", "description": "done", "commit": "abc"},
             ]), \
             patch("pr_monitor._reconcile_replies"), \
             patch("pr_monitor._poll_codeql"), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        assert exit_code == 0
        # Overload was detected on the initial empty poll
        mock_detect.assert_called_once()
        # /gemini review was posted
        mock_retry_post.assert_called_once()


class TestGeminiDoubleFailureNotify:
    """AC-143: After overload retry fails, monitor proceeds normally."""

    def test_gemini_double_failure_proceeds(self, tmp_path):
        """AC-143: overload retry fails, monitor still completes without triage."""
        wt = _make_worktree(tmp_path)

        # Both polls return empty
        with patch("pr_monitor.poll_ci", return_value="pass"), \
             patch("pr_monitor.poll_gemini_comments", return_value=[]), \
             patch("pr_monitor.detect_gemini_overload", return_value=True), \
             patch("pr_monitor._post_gemini_retry") as mock_retry_post, \
             patch("pr_monitor.run_triage") as mock_triage, \
             patch("pr_monitor._poll_codeql"), \
             patch("pr_monitor.mark_pr_ready", return_value=True), \
             patch("pr_monitor.run_retro", return_value="done"), \
             patch("pr_monitor.run_notify"):
            exit_code = pr_monitor.run_monitor(wt, 42, resume=False)

        assert exit_code == 0
        # Retry was posted but comments never appeared
        mock_retry_post.assert_called_once()
        # Triage was never invoked because no inline comments
        mock_triage.assert_not_called()
        # Monitor completed successfully
        result = pr_monitor.read_result(wt)
        assert result["status"] == "complete"


class TestCodeqlCheckPolling:
    """AC-144: _poll_codeql waits for CodeQL check to complete."""

    def test_poll_codeql_completes_when_codeql_done(self, tmp_path):
        """AC-144: _poll_codeql returns when CodeQL check state is COMPLETED."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        checks = [
            {"name": "CodeQL", "state": "COMPLETED", "conclusion": "SUCCESS"},
            {"name": "build", "state": "COMPLETED", "conclusion": "SUCCESS"},
        ]

        with patch("pr_monitor.subprocess.run", return_value=_gh_checks_json(checks)):
            # Should return without timing out
            _real_poll_codeql(wt, 42, logger)
        # If we reach here, it did not hang or raise

    def test_poll_codeql_waits_for_in_progress(self, tmp_path):
        """AC-144: _poll_codeql polls while CodeQL is IN_PROGRESS, returns on COMPLETED."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        in_progress_checks = [
            {"name": "CodeQL", "state": "IN_PROGRESS", "conclusion": None},
        ]
        completed_checks = [
            {"name": "CodeQL", "state": "COMPLETED", "conclusion": "SUCCESS"},
        ]

        call_count = [0]

        def mock_run(*args, **kwargs):
            call_count[0] += 1
            if call_count[0] <= 2:
                return _gh_checks_json(in_progress_checks)
            return _gh_checks_json(completed_checks)

        # Use advancing time to prevent actual 5 min wait
        time_val = [0.0]

        def advancing_time():
            time_val[0] += 10.0
            return time_val[0]

        with patch("pr_monitor.subprocess.run", side_effect=mock_run), \
             patch("pr_monitor.time.time", side_effect=advancing_time), \
             patch("pr_monitor.time.sleep"):
            _real_poll_codeql(wt, 42, logger)

        # Should have polled multiple times
        assert call_count[0] >= 3

    def test_poll_codeql_times_out(self, tmp_path):
        """AC-144: _poll_codeql returns after 5 min timeout even if CodeQL not done."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        pending_checks = [
            {"name": "CodeQL", "state": "IN_PROGRESS", "conclusion": None},
        ]

        # Use rapidly advancing time to simulate timeout
        time_val = [0.0]

        def advancing_time():
            time_val[0] += 120.0  # Jump 2 min each call
            return time_val[0]

        with patch("pr_monitor.subprocess.run",
                    return_value=_gh_checks_json(pending_checks)), \
             patch("pr_monitor.time.time", side_effect=advancing_time), \
             patch("pr_monitor.time.sleep"):
            _real_poll_codeql(wt, 42, logger)
        # Should return (timeout) without raising

    def test_poll_codeql_skips_in_shakedown(self, tmp_path):
        """AC-144: _poll_codeql returns immediately in shakedown mode."""
        wt = _make_worktree(tmp_path)
        logger = _make_logger(tmp_path)

        with patch("pr_monitor.SHAKEDOWN", "1"), \
             patch("pr_monitor.subprocess.run") as mock_run:
            _real_poll_codeql(wt, 42, logger)

        # Should not have called subprocess at all
        mock_run.assert_not_called()
