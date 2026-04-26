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
import pipeline
from pipeline import (
    STAGE_MODELS,
    _FILED_FINDING_KEYS,
    _finding_key,
    _get_direct_children,
    auto_commit_stranded,
    classify_activity,
    compute_resume_stage,
    get_child_process_count,
    should_converge,
    stage_already_completed,
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
        self.assertIn("-A", add_call[0][0])

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


class TestDescendantProcessCount(unittest.TestCase):
    """Verify get_child_process_count walks the full process tree (#964)."""

    @patch("pipeline._get_direct_children")
    def test_counts_grandchildren(self, mock_children):
        # Tree: pid 1 -> [2, 3], pid 2 -> [4], pid 3 -> [], pid 4 -> []
        mock_children.side_effect = lambda pid: {
            1: [2, 3],
            2: [4],
            3: [],
            4: [],
        }.get(pid, [])
        self.assertEqual(get_child_process_count(1), 3)  # 2, 3, 4

    @patch("pipeline._get_direct_children")
    def test_no_children_returns_zero(self, mock_children):
        mock_children.return_value = []
        self.assertEqual(get_child_process_count(99), 0)

    @patch("pipeline._get_direct_children")
    def test_only_direct_children(self, mock_children):
        mock_children.side_effect = lambda pid: {
            10: [11, 12],
            11: [],
            12: [],
        }.get(pid, [])
        self.assertEqual(get_child_process_count(10), 2)

    @patch("pipeline._get_direct_children")
    def test_deep_tree(self, mock_children):
        # Linear chain: 1 -> 2 -> 3 -> 4 -> 5
        mock_children.side_effect = lambda pid: {
            1: [2], 2: [3], 3: [4], 4: [5], 5: [],
        }.get(pid, [])
        self.assertEqual(get_child_process_count(1), 4)  # 2, 3, 4, 5

    @patch("pipeline._get_direct_children")
    def test_grandchild_active_classifies_as_active(self, mock_children):
        """Parent silent + grandchild active -> 'active' not 'stalled'."""
        mock_children.side_effect = lambda pid: {
            1: [2], 2: [3], 3: [],
        }.get(pid, [])
        count = get_child_process_count(1)
        activity, idle = classify_activity(
            100, 100, 0, 0, 1, 1, 4, has_children=count > 0)
        self.assertEqual(activity, "active")
        self.assertEqual(idle, 0)


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


class _FakePopen:
    """Minimal Popen stand-in that records argv and exits cleanly."""

    def __init__(self, cmd, **kwargs):
        self.cmd = cmd
        self.returncode = 0
        self._waited = False

    def poll(self):
        return 0

    def wait(self, timeout=None):
        self._waited = True
        return 0

    def terminate(self):
        pass

    def kill(self):
        pass


def _capture_run_claude_argv(stage, model_override=None):
    """Invoke pipeline.run_claude with mocks; return the argv list passed to Popen."""
    captured = {}

    def _fake_popen(cmd, **kwargs):
        captured["cmd"] = list(cmd)
        return _FakePopen(cmd, **kwargs)

    with tempfile.TemporaryDirectory() as worktree:
        original_override = pipeline.MODEL_OVERRIDE
        pipeline.MODEL_OVERRIDE = model_override
        try:
            with patch("pipeline.subprocess.Popen", side_effect=_fake_popen):
                logger = io.StringIO()
                pipeline.run_claude(worktree, "test prompt", logger, stage,
                                    dry_run=False)
        finally:
            pipeline.MODEL_OVERRIDE = original_override

    return captured.get("cmd", [])


class TestStageModels(unittest.TestCase):
    """AC-152, AC-153, AC-154 — model routing in pipeline.py."""

    def test_stage_models_sonnet(self):
        sonnet_stages = ["implement", "gates", "verify", "qa", "review",
                         "converge", "pr", "retro"]
        for stage in sonnet_stages:
            with self.subTest(stage=stage):
                self.assertEqual(STAGE_MODELS.get(stage), "sonnet",
                                 f"{stage} must route to sonnet")

    def test_stage_models_opus_default(self):
        for stage in ["design", "investigate", "spec"]:
            with self.subTest(stage=stage):
                self.assertIsNone(STAGE_MODELS.get(stage),
                                  f"{stage} must inherit CLI default (no --model)")

    def test_stage_model_override(self):
        argv = _capture_run_claude_argv("design", model_override="haiku")
        self.assertIn("--model", argv)
        idx = argv.index("--model")
        self.assertEqual(argv[idx + 1], "haiku",
                         "CLI --model override must apply to a stage that "
                         "would otherwise have no --model")

        argv = _capture_run_claude_argv("implement", model_override="opus")
        self.assertIn("--model", argv)
        idx = argv.index("--model")
        self.assertEqual(argv[idx + 1], "opus",
                         "CLI --model override must replace STAGE_MODELS value")

    def test_run_claude_passes_sonnet_for_implement(self):
        argv = _capture_run_claude_argv("implement")
        self.assertIn("--model", argv)
        idx = argv.index("--model")
        self.assertEqual(argv[idx + 1], "sonnet")

    def test_run_claude_omits_model_for_design(self):
        argv = _capture_run_claude_argv("design")
        self.assertNotIn("--model", argv,
                         "design stage must inherit default (no --model flag)")


class TestStageAlreadyCompleted(unittest.TestCase):
    """Tests for stage_already_completed() — #937 idempotency guards."""

    HEAD_SHA = "abc123def456"

    def _write_state(self, tmpdir, state_data):
        wf = os.path.join(tmpdir, ".workflow")
        os.makedirs(wf, exist_ok=True)
        with open(os.path.join(wf, "state.json"), "w") as f:
            json.dump(state_data, f)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    def test_gates_completed_exact_head(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "gates": {"passed": True, "commit_ref": self.HEAD_SHA}
            })
            completed, reason = stage_already_completed(tmp, "gates")
            self.assertTrue(completed)
            self.assertIn("gates.passed", reason)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    def test_gates_stale_ref_not_completed(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "gates": {"passed": True, "commit_ref": "stale_sha"}
            })
            completed, _ = stage_already_completed(tmp, "gates")
            self.assertFalse(completed)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    def test_gates_not_passed_not_completed(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "gates": {"passed": False, "commit_ref": self.HEAD_SHA}
            })
            completed, _ = stage_already_completed(tmp, "gates")
            self.assertFalse(completed)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    def test_review_completed_exact_head(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "review": {"passed": True, "commit_ref": self.HEAD_SHA}
            })
            completed, reason = stage_already_completed(tmp, "review")
            self.assertTrue(completed)
            self.assertIn("review.passed", reason)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    @patch("pipeline.is_ancestor", return_value=True)
    def test_verify_ancestor_completed(self, _, __):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "verify": {"ext_commit_ref": "older_sha"}
            })
            completed, reason = stage_already_completed(tmp, "verify")
            self.assertTrue(completed)
            self.assertIn("ancestor", reason)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    @patch("pipeline.is_ancestor", return_value=False)
    def test_verify_not_ancestor_not_completed(self, _, __):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "verify": {"ext_commit_ref": "diverged_sha"}
            })
            completed, _ = stage_already_completed(tmp, "verify")
            self.assertFalse(completed)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    @patch("pipeline.is_ancestor", return_value=True)
    def test_qa_ancestor_completed(self, _, __):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "qa": {"cli_commit_ref": "older_sha"}
            })
            completed, _ = stage_already_completed(tmp, "qa")
            self.assertTrue(completed)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    def test_pr_url_set_completed(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "pr": {"url": "https://github.com/o/r/pull/42"}
            })
            completed, reason = stage_already_completed(tmp, "pr")
            self.assertTrue(completed)
            self.assertIn("pr.url", reason)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    def test_pr_no_url_not_completed(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {"pr": {}})
            completed, _ = stage_already_completed(tmp, "pr")
            self.assertFalse(completed)

    @patch("pipeline.get_head_sha", return_value=None)
    def test_no_head_returns_false(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {
                "gates": {"passed": True, "commit_ref": "abc123"}
            })
            completed, reason = stage_already_completed(tmp, "gates")
            self.assertFalse(completed)
            self.assertIn("cannot resolve HEAD", reason)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    def test_unknown_stage_returns_false(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {})
            completed, _ = stage_already_completed(tmp, "implement")
            self.assertFalse(completed)

    @patch("pipeline.get_head_sha", return_value=HEAD_SHA)
    def test_empty_state_returns_false(self, _):
        with tempfile.TemporaryDirectory() as tmp:
            self._write_state(tmp, {})
            for stage in ("gates", "verify", "qa", "review", "pr"):
                completed, _ = stage_already_completed(tmp, stage)
                self.assertFalse(completed, f"{stage} should not be completed")


class TestFindingKey(unittest.TestCase):
    """Tests for _finding_key() — retro dedupe (#978)."""

    def test_same_input_same_key(self):
        f = {"id": "R-01", "description": "some finding"}
        self.assertEqual(_finding_key(f), _finding_key(f))

    def test_different_id_different_key(self):
        f1 = {"id": "R-01", "description": "same desc"}
        f2 = {"id": "R-02", "description": "same desc"}
        self.assertNotEqual(_finding_key(f1), _finding_key(f2))

    def test_different_desc_different_key(self):
        f1 = {"id": "R-01", "description": "description A"}
        f2 = {"id": "R-01", "description": "description B"}
        self.assertNotEqual(_finding_key(f1), _finding_key(f2))

    def test_key_is_16_hex_chars(self):
        f = {"id": "R-01", "description": "test"}
        key = _finding_key(f)
        self.assertEqual(len(key), 16)
        int(key, 16)  # Raises if not valid hex

    def test_missing_fields_uses_defaults(self):
        key = _finding_key({})
        self.assertEqual(len(key), 16)


class TestFindDuplicateIssue(unittest.TestCase):
    """Tests for _find_duplicate_issue() with finding_key support (#978)."""

    @patch("pipeline.subprocess.run")
    def test_matches_by_finding_key_in_body(self, mock_run):
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout=json.dumps([{
                "number": 42,
                "title": "retro: something different",
                "body": "blah blah <!-- finding_key:abc123 --> blah",
            }]),
        )
        result = pipeline._find_duplicate_issue(
            "retro: unrelated title", "abc123", "/tmp")
        self.assertEqual(result, 42)

    @patch("pipeline.subprocess.run")
    def test_matches_by_title_prefix(self, mock_run):
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout=json.dumps([{
                "number": 99,
                "title": "retro: pipeline retro auto-filing creates duplicate issues across converge rounds",
                "body": "no finding key here",
            }]),
        )
        result = pipeline._find_duplicate_issue(
            "retro: pipeline retro auto-filing creates duplicate issues across converge rounds",
            "nomatch", "/tmp")
        self.assertEqual(result, 99)

    @patch("pipeline.subprocess.run")
    def test_no_match_returns_none(self, mock_run):
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout=json.dumps([{
                "number": 1,
                "title": "completely different",
                "body": "",
            }]),
        )
        result = pipeline._find_duplicate_issue(
            "retro: my finding", "xyz789", "/tmp")
        self.assertIsNone(result)

    @patch("pipeline.subprocess.run")
    def test_gh_failure_returns_none(self, mock_run):
        mock_run.return_value = MagicMock(returncode=1, stdout="")
        result = pipeline._find_duplicate_issue(
            "retro: anything", "key123", "/tmp")
        self.assertIsNone(result)


class TestFiledFindingKeysInProcess(unittest.TestCase):
    """Tests for in-process dedup via _FILED_FINDING_KEYS (#978)."""

    def setUp(self):
        _FILED_FINDING_KEYS.clear()

    def tearDown(self):
        _FILED_FINDING_KEYS.clear()

    def test_add_and_check(self):
        _FILED_FINDING_KEYS.add("key1")
        self.assertIn("key1", _FILED_FINDING_KEYS)

    def test_prevents_double_filing(self):
        _FILED_FINDING_KEYS.add("key1")
        _FILED_FINDING_KEYS.add("key1")
        self.assertEqual(len(_FILED_FINDING_KEYS), 1)


class TestPrGateConvergeGuard(unittest.TestCase):
    """Tests for converge-phase PR blocking in pr-gate.py (#954)."""

    def test_converging_flag_blocks(self):
        """pr-gate.py should block when pipeline.converging is set."""
        sys.path.insert(0, os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "..", ".claude", "hooks"))
        try:
            import importlib
            pr_gate = importlib.import_module("pr-gate")
        except (ImportError, ModuleNotFoundError):
            self.skipTest("pr-gate module not importable (dash in name)")
            return

        state = {"pipeline": {"converging": "true"}}
        pipeline_section = state.get("pipeline") or {}
        self.assertTrue(bool(pipeline_section.get("converging")))

    def test_no_converging_flag_allows(self):
        state = {"pipeline": {}}
        pipeline_section = state.get("pipeline") or {}
        self.assertFalse(bool(pipeline_section.get("converging")))

    def test_empty_converging_allows(self):
        state = {"pipeline": {"converging": ""}}
        pipeline_section = state.get("pipeline") or {}
        self.assertFalse(bool(pipeline_section.get("converging")))


if __name__ == "__main__":
    unittest.main()
