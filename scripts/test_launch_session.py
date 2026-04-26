#!/usr/bin/env python3
"""Unit tests for launch-claude-session.py model selection.

Usage:
    python -m unittest scripts.test_launch_session
    python -m pytest scripts/test_launch_session.py
"""
import importlib.util
import os
import sys
import unittest


# launch-claude-session.py has a hyphen in the name, so import via spec.
_HERE = os.path.dirname(os.path.abspath(__file__))
_SOURCE = os.path.join(_HERE, "launch-claude-session.py")
_spec = importlib.util.spec_from_file_location("launch_claude_session", _SOURCE)
launch_claude_session = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(launch_claude_session)


class TestLaunchUsesOpus(unittest.TestCase):
    """AC-156: launch-claude-session.py uses --model opus (floating)."""

    def test_launch_uses_opus(self):
        """AC-156 named test: build_launch_script emits --model opus (floating)."""
        script = launch_claude_session.build_launch_script(
            target_windows_path=r"C:\repo",
            claude_path=r"C:\bin\claude.exe",
            prompt="hello",
        )
        self.assertIn("--model opus", script)
        # Negative: must not contain a pinned Opus version (the fix for AC-156).
        self.assertNotIn("claude-opus-4-6", script)
        self.assertNotIn("claude-opus-4-7", script)

    def test_build_launch_script_uses_opus(self):
        # Retained alias of test_launch_uses_opus for back-compat.
        self.test_launch_uses_opus()

    def test_no_pinned_opus_in_module_source(self):
        with open(_SOURCE, encoding="utf-8") as f:
            source = f.read()
        self.assertNotIn(
            "claude-opus-4-6", source,
            "launch-claude-session.py must not pin Opus to a specific version "
            "(AC-156: floating ID).",
        )


if __name__ == "__main__":
    unittest.main()
