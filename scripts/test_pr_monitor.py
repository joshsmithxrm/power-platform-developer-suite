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


class TestMonitorUsesSonnet(unittest.TestCase):
    """AC-155: pr_monitor spawns triage and retro with --model sonnet."""

    def test_triage_uses_sonnet(self):
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow", "stages"),
                        exist_ok=True)
            logger = _FakeLogger()
            with patch("pr_monitor.SHAKEDOWN", False), \
                 patch("pr_monitor.build_triage_prompt", return_value="prompt"), \
                 patch("pr_monitor.parse_triage_jsonl", return_value=[]):
                cmd = _capture_popen_cmd(
                    lambda: pr_monitor.run_triage(worktree, 123, [], logger)
                )

        self.assertIn("--model", cmd, "triage cmd must include --model flag")
        idx = cmd.index("--model")
        self.assertEqual(cmd[idx + 1], "sonnet")
        self.assertIn("--agent", cmd)
        self.assertIn("gemini-triage", cmd)

    def test_retro_uses_sonnet(self):
        import pr_monitor

        with tempfile.TemporaryDirectory() as worktree:
            os.makedirs(os.path.join(worktree, ".workflow", "stages"),
                        exist_ok=True)
            logger = _FakeLogger()
            with patch("pr_monitor.SHAKEDOWN", False):
                cmd = _capture_popen_cmd(
                    lambda: pr_monitor.run_retro(worktree, logger)
                )

        self.assertIn("--model", cmd, "retro cmd must include --model flag")
        idx = cmd.index("--model")
        self.assertEqual(cmd[idx + 1], "sonnet")

    def test_monitor_uses_sonnet(self):
        """AC-155: combined behavioural assertion on both triage and retro
        cmd construction. Exercises the extracted cmd helpers so we observe
        the real argv list rather than string-matching the source.
        """
        import pr_monitor

        triage_cmd = pr_monitor._build_triage_cmd("any prompt")
        self.assertIn("--model", triage_cmd)
        self.assertEqual(
            triage_cmd[triage_cmd.index("--model") + 1], "sonnet",
            "triage must run on sonnet (AC-155)",
        )
        # Floating alias, not pinned model id
        self.assertNotIn(
            "claude-",
            triage_cmd[triage_cmd.index("--model") + 1],
        )

        retro_cmd = pr_monitor._build_retro_cmd("/retro")
        self.assertIn("--model", retro_cmd)
        self.assertEqual(
            retro_cmd[retro_cmd.index("--model") + 1], "sonnet",
            "retro must run on sonnet (AC-155)",
        )
        self.assertNotIn(
            "claude-",
            retro_cmd[retro_cmd.index("--model") + 1],
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
