#!/usr/bin/env python3
"""Unit tests for pr_monitor.py model routing and escalation visibility.

Usage:
    python -m unittest scripts.test_pr_monitor
    python -m pytest scripts/test_pr_monitor.py
"""
import io
import json
import os
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch


sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


class _FakeProc:
    def __init__(self):
        self.returncode = 0

    def wait(self, timeout=None):
        return 0

    def terminate(self):
        pass

    def kill(self):
        pass


class _FakeLogger:
    def __init__(self):
        self.entries = []

    def log(self, *args, **kwargs):
        self.entries.append((args, kwargs))


def _capture_popen_cmd(callable_under_test):
    """Run callable; intercept Popen and return the argv it was given."""
    captured = {}

    def _fake_popen(cmd, **kwargs):
        captured["cmd"] = list(cmd)
        return _FakeProc()

    with patch("pr_monitor.subprocess.Popen", side_effect=_fake_popen):
        callable_under_test()

    return captured.get("cmd", [])


class _FakeHandle:
    """Stand-in for claude_dispatch.BgHandle / HeadlessHandle."""

    def __init__(self, transcript_path=None):
        self.transcript_path = transcript_path or ""
        self.returncode = 0

    def wait(self, timeout=None):
        return 0

    def terminate(self):
        pass

    def kill(self):
        pass


class TestMonitorModelRouting(unittest.TestCase):
    """AC-04 (#1098): pr_monitor.run_triage passes model="haiku" to dispatch.

    Patches claude_dispatch.spawn directly to capture the kwargs the dispatch
    layer receives, rather than relying on subprocess interception (which
    misses interactive-mode argv).
    """

    def test_triage_uses_haiku(self):
        import pr_monitor
        import claude_dispatch

        captured = {}

        def _fake_spawn(**kwargs):
            captured["kwargs"] = kwargs
            # Write a non-empty transcript so the copy step doesn't blow up.
            stage_log = kwargs.get("stage_log")
            if stage_log:
                with open(stage_log, "w", encoding="utf-8") as f:
                    f.write("")
            return _FakeHandle(transcript_path=stage_log or "")

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow", "stages"),
                        exist_ok=True)
            logger = _FakeLogger()
            with patch("pr_monitor.SHAKEDOWN", False), \
                 patch("pr_monitor.build_triage_prompt", return_value="prompt"), \
                 patch("pr_monitor.parse_triage_jsonl", return_value=[]), \
                 patch.object(claude_dispatch, "spawn", side_effect=_fake_spawn):
                pr_monitor.run_triage(worktree, 123, [], logger)

        self.assertIn("kwargs", captured, "claude_dispatch.spawn was not called")
        kwargs = captured["kwargs"]
        self.assertEqual(
            kwargs.get("model"), "haiku",
            f"AC-04 (#1098): triage must dispatch with model='haiku', "
            f"got {kwargs.get('model')!r}",
        )
        self.assertEqual(kwargs.get("agent"), "gemini-triage")

    def test_retro_uses_sonnet(self):
        """Retro stays on sonnet — verify run_retro dispatches with sonnet."""
        import pr_monitor
        import claude_dispatch

        captured = {}

        def _fake_spawn(**kwargs):
            captured["kwargs"] = kwargs
            stage_log = kwargs.get("stage_log")
            if stage_log:
                with open(stage_log, "w", encoding="utf-8") as f:
                    f.write("")
            return _FakeHandle(transcript_path=stage_log or "")

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow", "stages"),
                        exist_ok=True)
            logger = _FakeLogger()
            with patch("pr_monitor.SHAKEDOWN", False), \
                 patch.object(claude_dispatch, "spawn", side_effect=_fake_spawn):
                try:
                    pr_monitor.run_retro(worktree, logger)
                except Exception:
                    # run_retro may attempt post-spawn work (parsing/notify)
                    # we only care that dispatch was invoked with correct model
                    pass

        self.assertIn("kwargs", captured, "claude_dispatch.spawn was not called")
        self.assertEqual(
            captured["kwargs"].get("model"), "sonnet",
            "retro must continue using model='sonnet' (unchanged by #1098)",
        )


class TestTerminalDeregistersInflight(unittest.TestCase):
    """AC-178: pr_monitor terminal step calls inflight-deregister before notify."""

    def test_terminal_deregisters_inflight(self):
        """_notify_terminal must call _deregister_inflight before run_notify."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow"), exist_ok=True)
            logger = _FakeLogger()
            call_order = []
            with patch("pr_monitor._deregister_inflight",
                       side_effect=lambda *a, **k: call_order.append("deregister")), \
                 patch("pr_monitor.run_notify",
                       side_effect=lambda *a, **k: call_order.append("notify")):
                pr_monitor._notify_terminal(worktree, 123, logger, "msg")

        self.assertIn("deregister", call_order,
                      "_notify_terminal must call _deregister_inflight (AC-178)")
        self.assertIn("notify", call_order)
        self.assertLess(call_order.index("deregister"), call_order.index("notify"),
                        "deregister must happen BEFORE notify")

    def test_deregister_inflight_calls_script(self):
        """_deregister_inflight invokes scripts/inflight-deregister.py with --branch."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            logger = _FakeLogger()
            captured = {}

            def _fake_run(cmd, **kwargs):
                captured.setdefault("calls", []).append(list(cmd))
                m = MagicMock()
                m.returncode = 0
                m.stdout = "feat/test-branch\n"
                m.stderr = ""
                return m

            with patch("pr_monitor.subprocess.run", side_effect=_fake_run):
                pr_monitor._deregister_inflight(worktree, logger)

            calls = captured.get("calls", [])
            # First call is git rev-parse to get branch; second is the deregister script.
            self.assertGreaterEqual(len(calls), 2)
            deregister_call = calls[1]
            self.assertIn("scripts/inflight-deregister.py", deregister_call)
            self.assertIn("--branch", deregister_call)
            self.assertIn("feat/test-branch", deregister_call)


class TestPostRepliesDedup(unittest.TestCase):
    """Issue #1096: post_replies must consult get_unreplied_comments before POSTing."""

    def setUp(self):
        import pr_monitor
        pr_monitor._POSTED_REPLY_KEYS.clear()

    def tearDown(self):
        import pr_monitor
        pr_monitor._POSTED_REPLY_KEYS.clear()

    def test_skips_already_replied_comment(self):
        """AC: comment_id X already replied on GitHub → no POST, logs SKIPPED_ALREADY_REPLIED."""
        import pr_monitor

        triage_results = [{"id": 3251204411, "action": "fixed",
                           "description": "Fixed it", "commit": "abc123"}]
        logger = _FakeLogger()

        with patch("pr_monitor.get_unreplied_comments", return_value=[]), \
             patch("pr_monitor._post_replies_common") as mock_post:
            pr_monitor.post_replies("/worktree", 1094, triage_results, logger)

        mock_post.assert_not_called()
        events = [e[0][1] for e in logger.entries]
        self.assertIn("SKIPPED_ALREADY_REPLIED", events,
                      "Must log SKIPPED_ALREADY_REPLIED when comment is already replied")
        skipped = next(e for e in logger.entries if e[0][1] == "SKIPPED_ALREADY_REPLIED")
        self.assertEqual(skipped[1].get("comment_id"), 3251204411)
        self.assertEqual(skipped[1].get("action"), "fixed")

    def test_fail_open_when_unreplied_query_errors(self):
        """Gemini #1121: GH query error → fail-open (still POST), don't silently skip all."""
        import pr_monitor
        from triage_common import UnrepliedQueryError

        triage_results = [{"id": 3251204411, "action": "fixed",
                           "description": "Fixed it", "commit": "abc123"}]
        logger = _FakeLogger()

        with patch("pr_monitor.get_unreplied_comments",
                   side_effect=UnrepliedQueryError("gh api exited 1: boom")), \
             patch("pr_monitor._post_replies_common") as mock_post:
            pr_monitor.post_replies("/worktree", 1094, triage_results, logger)

        mock_post.assert_called_once()
        events = [e[0][1] for e in logger.entries]
        self.assertIn("UNREPLIED_QUERY_FAILED_FAIL_OPEN", events)
        self.assertNotIn("SKIPPED_ALREADY_REPLIED", events,
                         "Must not silently drop replies on query error")

    def test_posts_when_comment_unreplied(self):
        """AC: comment_id X not yet replied on GitHub → POST proceeds as before."""
        import pr_monitor

        triage_results = [{"id": 3251204411, "action": "fixed",
                           "description": "Fixed it", "commit": "abc123"}]
        logger = _FakeLogger()
        unreplied = [{"id": 3251204411, "user": "gemini-code-assist[bot]",
                      "path": "file.py", "line": 10, "body": "Consider X"}]

        with patch("pr_monitor.get_unreplied_comments", return_value=unreplied), \
             patch("pr_monitor._post_replies_common") as mock_post:
            pr_monitor.post_replies("/worktree", 1094, triage_results, logger)

        mock_post.assert_called_once()
        events = [e[0][1] for e in logger.entries]
        self.assertNotIn("SKIPPED_ALREADY_REPLIED", events)

    def test_reply_body_strips_already_fixed_prefix(self):
        """Style nit #1096: 'Already fixed in commit X — …' prefix is stripped."""
        import pr_monitor

        item = {"action": "fixed", "commit": "eb19a01e6",
                "description": "Already fixed in commit eb19a01e6 — REFERENCE.md line 120 already uses curl"}
        body = pr_monitor._reply_body_for(item)
        self.assertEqual(body, "Fixed in eb19a01e6 — REFERENCE.md line 120 already uses curl")
        self.assertNotIn("Already fixed", body)

    def test_reply_body_strips_already_dismissed_prefix(self):
        """Style nit #1096: 'Already dismissed in commit X — …' prefix is stripped."""
        import pr_monitor

        item = {"action": "dismissed",
                "description": "Already dismissed in commit abc123 — not applicable here"}
        body = pr_monitor._reply_body_for(item)
        self.assertEqual(body, "Not applicable — not applicable here")
        self.assertNotIn("Already dismissed", body)

    def test_reply_body_normal_description_unchanged(self):
        """Descriptions without the prefix pass through unmodified."""
        import pr_monitor

        item = {"action": "fixed", "commit": "abc123",
                "description": "Updated the config file"}
        body = pr_monitor._reply_body_for(item)
        self.assertEqual(body, "Fixed in abc123 — Updated the config file")


class TestEscalationVisibility(unittest.TestCase):
    """#1088: non-clean exits must post comment + set state marker + add label + notify."""

    def _make_fake_run(self, calls):
        def _fake_run(cmd, **kwargs):
            calls.append(list(cmd))
            m = MagicMock()
            m.returncode = 0
            m.stdout = ""
            m.stderr = ""
            return m
        return _fake_run

    def test_post_escalation_comment_calls_gh(self):
        """_post_escalation_comment must invoke gh pr comment on non-clean exit."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow"), exist_ok=True)
            logger = _FakeLogger()
            calls = []

            with patch("pr_monitor.subprocess.run", side_effect=self._make_fake_run(calls)), \
                 patch("pr_monitor.SHAKEDOWN", ""):
                pr_monitor._post_escalation_comment(
                    worktree, 42, "stuck-ci-fix-exhausted",
                    os.path.join(worktree, ".workflow", "pr-monitor.log"),
                    logger,
                )

            comment_calls = [c for c in calls if "comment" in c]
            self.assertTrue(comment_calls,
                            "gh pr comment must be invoked on non-clean exit")
            self.assertTrue(
                any("42" in c for c in comment_calls),
                "gh pr comment must target the correct PR number"
            )

    def test_add_escalation_label_calls_gh(self):
        """_add_escalation_label must add status:monitor-escalated label."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow"), exist_ok=True)
            logger = _FakeLogger()
            calls = []

            with patch("pr_monitor.subprocess.run", side_effect=self._make_fake_run(calls)), \
                 patch("pr_monitor.SHAKEDOWN", ""):
                pr_monitor._add_escalation_label(worktree, 42, logger)

            add_label_calls = [c for c in calls if "--add-label" in c]
            self.assertTrue(add_label_calls, "gh pr edit --add-label must be called")
            self.assertTrue(
                any("status:monitor-escalated" in " ".join(c) for c in add_label_calls),
                "label 'status:monitor-escalated' must be added to the PR"
            )

    def test_set_terminal_state_marker_writes_state(self):
        """_set_terminal_state_marker must write pr_monitor.terminal_state to state.json."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow"), exist_ok=True)
            logger = _FakeLogger()

            pr_monitor._set_terminal_state_marker(
                worktree, "stuck-ci-fix-exhausted", "PR #42: stuck-ci-fix-exhausted", logger
            )

            state_path = os.path.join(worktree, ".workflow", "state.json")
            self.assertTrue(os.path.exists(state_path), "state.json must be written")
            with open(state_path, encoding="utf-8") as f:
                state = json.load(f)
            pm = state.get("pr_monitor", {})
            self.assertEqual(pm.get("terminal_state"), "stuck-ci-fix-exhausted",
                             "terminal_state must be recorded")
            self.assertIn("timestamp", pm, "timestamp must be recorded")
            self.assertIn("reason", pm, "reason must be recorded")

    def test_set_terminal_state_marker_preserves_existing_state(self):
        """_set_terminal_state_marker must not clobber existing state.json keys."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow"), exist_ok=True)
            logger = _FakeLogger()
            state_path = os.path.join(worktree, ".workflow", "state.json")
            # Write pre-existing state
            with open(state_path, "w", encoding="utf-8") as f:
                json.dump({"branch": "feat/test", "pr": {"url": "https://x"}}, f)

            pr_monitor._set_terminal_state_marker(
                worktree, "monitor-crash", "crash msg", logger
            )

            with open(state_path, encoding="utf-8") as f:
                state = json.load(f)
            self.assertEqual(state.get("branch"), "feat/test",
                             "existing keys must be preserved")
            self.assertEqual(state["pr_monitor"]["terminal_state"], "monitor-crash")

    def test_notify_terminal_all_four_actions_fire(self):
        """_notify_terminal must invoke all 4 escalation actions in order."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow"), exist_ok=True)
            logger = _FakeLogger()
            actions = []

            with patch("pr_monitor._deregister_inflight",
                       side_effect=lambda *a, **k: None), \
                 patch("pr_monitor._set_terminal_state_marker",
                       side_effect=lambda *a, **k: actions.append("state_marker")), \
                 patch("pr_monitor._post_escalation_comment",
                       side_effect=lambda *a, **k: actions.append("comment")), \
                 patch("pr_monitor._add_escalation_label",
                       side_effect=lambda *a, **k: actions.append("label")), \
                 patch("pr_monitor.run_notify",
                       side_effect=lambda *a, **k: actions.append("notify")):
                pr_monitor._notify_terminal(
                    worktree, 42, logger,
                    "PR #42: stuck-ci-fix-exhausted\n  CI: fail"
                )

        self.assertIn("state_marker", actions, "Action 1: state marker must be set")
        self.assertIn("comment", actions, "Action 2: GitHub comment must be posted")
        self.assertIn("label", actions, "Action 3: label must be added")
        self.assertIn("notify", actions, "Action 4: platform notification must fire")

    def test_clean_exit_no_escalation_comment(self):
        """Clean exit path (_step_notify) must not invoke _post_escalation_comment."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow"), exist_ok=True)
            logger = _FakeLogger()
            escalation_called = []

            with patch("pr_monitor._post_escalation_comment",
                       side_effect=lambda *a, **k: escalation_called.append(True)), \
                 patch("pr_monitor.run_notify",
                       side_effect=lambda *a, **k: None):
                pr_monitor._step_notify(worktree, 42, logger, {"status": "ready"})

            self.assertFalse(escalation_called,
                             "Clean exit must not call _post_escalation_comment")

    def test_escalation_skipped_in_shakedown(self):
        """Escalation actions must be no-ops in shakedown mode."""
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow"), exist_ok=True)
            logger = _FakeLogger()
            calls = []

            with patch("pr_monitor.subprocess.run",
                       side_effect=self._make_fake_run(calls)), \
                 patch("pr_monitor.SHAKEDOWN", "1"):
                pr_monitor._post_escalation_comment(
                    worktree, 42, "stuck-ci-fix-exhausted",
                    os.path.join(worktree, ".workflow", "pr-monitor.log"),
                    logger,
                )
                pr_monitor._add_escalation_label(worktree, 42, logger)

            self.assertFalse(calls,
                             "No gh calls must be made in shakedown mode")


if __name__ == "__main__":
    unittest.main()
