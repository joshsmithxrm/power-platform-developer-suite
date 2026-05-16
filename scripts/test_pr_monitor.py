#!/usr/bin/env python3
"""Unit tests for pr_monitor.py model routing.

Usage:
    python -m unittest scripts.test_pr_monitor
    python -m pytest scripts/test_pr_monitor.py
"""
import io
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


if __name__ == "__main__":
    unittest.main()
