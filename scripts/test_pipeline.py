#!/usr/bin/env python3
"""Unit tests for pipeline.py stall-recovery functions.

Usage:
    python scripts/test_pipeline.py              # run all
    python -m pytest scripts/test_pipeline.py    # via pytest
"""
import io
import json
import os
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pipeline import (
    auto_commit_stranded,
    classify_activity,
    compute_resume_stage,
    should_converge,
    write_result,
)


class TestAutoCommitStranded(unittest.TestCase):

    def _logger(self):
        return io.StringIO()

    @patch("pipeline.subprocess.run")
    @patch("pipeline.log")
    def test_clean_worktree_returns_false(self, mock_log, mock_run):
        mock_run.return_value = MagicMock(returncode=0, stdout="", stderr="")
        result = auto_commit_stranded("/tmp/wt", "converge", self._logger())
        self.assertFalse(result)
        mock_run.assert_called_once()
        self.assertIn("--porcelain", mock_run.call_args[0][0])

    @patch("pipeline.subprocess.run")
    @patch("pipeline.log")
    def test_dirty_worktree_commits_with_add_u(self, mock_log, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout=" M file.py\n", stderr=""),
            MagicMock(returncode=0),
            MagicMock(returncode=0, stdout="", stderr=""),
        ]
        result = auto_commit_stranded("/tmp/wt", "converge-r1", self._logger())
        self.assertTrue(result)
        add_call = mock_run.call_args_list[1]
        self.assertIn("-u", add_call[0][0])

    @patch("pipeline.subprocess.run")
    @patch("pipeline.log")
    def test_add_all_uses_dash_A(self, mock_log, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout=" M file.py\n", stderr=""),
            MagicMock(returncode=0),
            MagicMock(returncode=0, stdout="", stderr=""),
        ]
        auto_commit_stranded("/tmp/wt", "converge-r1", self._logger(),
                              add_all=True)
        add_call = mock_run.call_args_list[1]
        self.assertIn("-A", add_call[0][0])

    @patch("pipeline.subprocess.run")
    @patch("pipeline.log")
    def test_scope_extracted_from_stage_name(self, mock_log, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout=" M file.py\n", stderr=""),
            MagicMock(returncode=0),
            MagicMock(returncode=0, stdout="", stderr=""),
        ]
        auto_commit_stranded("/tmp/wt", "converge-r1", self._logger())
        commit_msg = mock_run.call_args_list[2][0][0][-1]
        self.assertTrue(commit_msg.startswith("chore(converge):"))

    @patch("pipeline.subprocess.run")
    @patch("pipeline.log")
    def test_custom_reason_in_commit_and_log(self, mock_log, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout=" M file.py\n", stderr=""),
            MagicMock(returncode=0),
            MagicMock(returncode=0, stdout="", stderr=""),
        ]
        logger = self._logger()
        auto_commit_stranded("/tmp/wt", "converge-r1", logger,
                              reason="stranded files after STALL_TIMEOUT")
        commit_msg = mock_run.call_args_list[2][0][0][-1]
        self.assertIn("stranded files after STALL_TIMEOUT", commit_msg)
        mock_log.assert_called_with(
            logger, "converge-r1", "AUTO_COMMIT",
            reason="stranded files after STALL_TIMEOUT")

    @patch("pipeline.subprocess.run")
    @patch("pipeline.log")
    def test_none_worktree_returns_false(self, mock_log, mock_run):
        result = auto_commit_stranded(None, "converge", self._logger())
        self.assertFalse(result)
        mock_run.assert_not_called()

    @patch("pipeline.subprocess.run")
    @patch("pipeline.log")
    def test_commit_failure_returns_false_and_logs(self, mock_log, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout=" M file.py\n", stderr=""),
            MagicMock(returncode=0),
            MagicMock(returncode=1, stdout="", stderr="error: something"),
        ]
        logger = self._logger()
        result = auto_commit_stranded("/tmp/wt", "converge", logger)
        self.assertFalse(result)
        mock_log.assert_called_with(
            logger, "converge", "AUTO_COMMIT_FAILED",
            reason="error: something")

    @patch("pipeline.subprocess.run")
    @patch("pipeline.log")
    def test_nothing_to_commit_silent(self, mock_log, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout=" M file.py\n", stderr=""),
            MagicMock(returncode=0),
            MagicMock(returncode=1, stdout="nothing to commit", stderr=""),
        ]
        result = auto_commit_stranded("/tmp/wt", "converge", self._logger())
        self.assertFalse(result)
        mock_log.assert_not_called()

    @patch("pipeline.subprocess.run", side_effect=OSError("disk full"))
    @patch("pipeline.log")
    def test_os_error_returns_false(self, mock_log, mock_run):
        logger = self._logger()
        result = auto_commit_stranded("/tmp/wt", "converge", logger)
        self.assertFalse(result)
        mock_log.assert_called_with(
            logger, "converge", "AUTO_COMMIT_FAILED",
            reason="subprocess error")


class TestComputeResumeStage(unittest.TestCase):

    def test_direct_stages_returned_as_is(self):
        for stage in ["implement", "gates", "verify", "qa", "review",
                       "converge", "pr", "retro"]:
            self.assertEqual(compute_resume_stage(stage), stage)

    def test_reconverge_variants_map_to_converge(self):
        for name in ["gates-reconverge", "verify-reconverge",
                      "qa-reconverge", "review-reconverge"]:
            self.assertEqual(compute_resume_stage(name), "converge",
                             msg=f"{name} should map to converge")

    def test_retry_suffix_maps_to_base(self):
        self.assertEqual(compute_resume_stage("implement-retry"), "implement")
        self.assertEqual(compute_resume_stage("verify-retry"), "verify")

    def test_unknown_stage_returned_as_is(self):
        self.assertEqual(compute_resume_stage("unknown-stage"), "unknown-stage")


class TestWriteResultResumeCommand(unittest.TestCase):

    def test_resume_command_included(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            wf = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf)
            cmd = "python scripts/pipeline.py --from converge --worktree /tmp/wt"
            write_result(tmpdir, "failed", 100, {},
                         failed_stage="converge", resume_command=cmd)
            with open(os.path.join(wf, "pipeline-result.json")) as f:
                data = json.load(f)
            self.assertEqual(data["resume_command"], cmd)
            self.assertEqual(data["status"], "failed")
            self.assertEqual(data["failed_stage"], "converge")

    def test_resume_command_absent_on_success(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            wf = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf)
            write_result(tmpdir, "complete", 100, {})
            with open(os.path.join(wf, "pipeline-result.json")) as f:
                data = json.load(f)
            self.assertNotIn("resume_command", data)

    def test_resume_command_absent_when_none(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            wf = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf)
            write_result(tmpdir, "failed", 100, {},
                         failed_stage="converge", resume_command=None)
            with open(os.path.join(wf, "pipeline-result.json")) as f:
                data = json.load(f)
            self.assertNotIn("resume_command", data)


class TestClassifyActivityPreserved(unittest.TestCase):

    def test_output_growth_resets_idle(self):
        activity, idle = classify_activity(200, 100, 0, 0, 1, 1, 3)
        self.assertEqual(activity, "active")
        self.assertEqual(idle, 0)

    def test_git_growth_resets_idle(self):
        activity, idle = classify_activity(100, 100, 5, 3, 1, 1, 3)
        self.assertEqual(activity, "active")
        self.assertEqual(idle, 0)

    def test_commit_growth_resets_idle(self):
        activity, idle = classify_activity(100, 100, 0, 0, 5, 3, 3)
        self.assertEqual(activity, "active")
        self.assertEqual(idle, 0)

    def test_children_resets_idle(self):
        activity, idle = classify_activity(100, 100, 0, 0, 1, 1, 3,
                                           has_children=True)
        self.assertEqual(activity, "active")
        self.assertEqual(idle, 0)

    def test_no_activity_increments_idle(self):
        activity, idle = classify_activity(100, 100, 0, 0, 1, 1, 0)
        self.assertEqual(activity, "idle")
        self.assertEqual(idle, 1)

    def test_stalled_after_three_idle(self):
        activity, idle = classify_activity(100, 100, 0, 0, 1, 1, 2)
        self.assertEqual(activity, "stalled")
        self.assertEqual(idle, 3)


class TestShouldConvergePreserved(unittest.TestCase):

    def test_passed_no_findings_skips(self):
        run, _ = should_converge({"review": {"passed": True, "findings": 0}})
        self.assertFalse(run)

    def test_passed_with_findings_triggers(self):
        run, _ = should_converge({"review": {"passed": True, "findings": 5}})
        self.assertTrue(run)

    def test_not_passed_triggers(self):
        run, _ = should_converge({"review": {"passed": False}})
        self.assertTrue(run)


if __name__ == "__main__":
    unittest.main()
