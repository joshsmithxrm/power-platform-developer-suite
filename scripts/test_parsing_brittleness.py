#!/usr/bin/env python3
"""Regression tests for the two systemic parsing/encoding bug classes.

(a) UTF-8 round-trip through workflow-state.py (em-dash, smart quotes, arrows)
(b) Launch helper with hostile prompt content (PROMPT_EOF, $(whoami), backticks, '@ mid-line)
(c) Emoji-only state value (U+1F600)
(d) Scan all .py files for bare open() calls missing encoding=

Usage:
    python -m pytest scripts/test_parsing_brittleness.py
"""
import glob
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.dirname(_HERE)

_WS_SOURCE = os.path.join(_HERE, "workflow-state.py")
_ws_spec = importlib.util.spec_from_file_location("workflow_state", _WS_SOURCE)
workflow_state = importlib.util.module_from_spec(_ws_spec)
_ws_spec.loader.exec_module(workflow_state)

_LS_SOURCE = os.path.join(_HERE, "launch-claude-session.py")
_ls_spec = importlib.util.spec_from_file_location("launch_claude_session", _LS_SOURCE)
launch_claude_session = importlib.util.module_from_spec(_ls_spec)
_ls_spec.loader.exec_module(launch_claude_session)


class TestUtf8RoundTrip(unittest.TestCase):
    """State values with non-Latin-1 codepoints must survive set/get."""

    def _roundtrip(self, value):
        with tempfile.TemporaryDirectory() as tmp:
            state_dir = os.path.join(tmp, ".workflow")
            os.makedirs(state_dir)
            state_path = os.path.join(state_dir, "state.json")
            env = {**os.environ, "GIT_WORK_TREE": tmp, "PYTHONUTF8": "1"}
            proc = subprocess.run(
                [sys.executable, _WS_SOURCE, "set", "test.key", value],
                cwd=tmp, capture_output=True, encoding="utf-8",
                timeout=10, env=env, stdin=subprocess.DEVNULL,
            )
            self.assertEqual(proc.returncode, 0, proc.stderr)
            with open(state_path, "r", encoding="utf-8") as f:
                state = json.load(f)
            return state.get("test", {}).get("key")

    def test_em_dash_smart_quotes_arrow(self):
        value = "– ‘em’ →"
        got = self._roundtrip(value)
        self.assertEqual(got, value)

    def test_emoji_only(self):
        value = "\U0001f600"
        got = self._roundtrip(value)
        self.assertEqual(got, value)

    def test_value_stdin_roundtrip(self):
        value = "– ‘em’ →"
        with tempfile.TemporaryDirectory() as tmp:
            state_dir = os.path.join(tmp, ".workflow")
            os.makedirs(state_dir)
            state_path = os.path.join(state_dir, "state.json")
            env = {**os.environ, "GIT_WORK_TREE": tmp, "PYTHONUTF8": "1"}
            proc = subprocess.run(
                [sys.executable, _WS_SOURCE, "set", "--value-stdin", "test.key"],
                cwd=tmp, capture_output=True, encoding="utf-8",
                timeout=10, input=value, env=env,
            )
            self.assertEqual(proc.returncode, 0, proc.stderr)
            with open(state_path, "r", encoding="utf-8") as f:
                state = json.load(f)
            self.assertEqual(state["test"]["key"], value)


class TestLaunchPromptSafety(unittest.TestCase):
    """Prompts with shell metacharacters must survive into the .ps1."""

    def test_prompt_eof_in_body(self):
        prompt = "Line1\nPROMPT_EOF\nLine3"
        script = launch_claude_session.build_launch_script(
            r"C:\repo", r"C:\bin\claude.exe", prompt,
        )
        self.assertIn("PROMPT_EOF", script)

    def test_dollar_whoami_preserved(self):
        prompt = "Hello $(whoami) world"
        script = launch_claude_session.build_launch_script(
            r"C:\repo", r"C:\bin\claude.exe", prompt,
        )
        self.assertIn("$(whoami)", script)

    def test_backticks_preserved(self):
        prompt = "Run `git status` now"
        script = launch_claude_session.build_launch_script(
            r"C:\repo", r"C:\bin\claude.exe", prompt,
        )
        self.assertIn("`git status`", script)

    def test_terminator_mid_line_ok(self):
        prompt = "It's a test '@ midline is fine"
        script = launch_claude_session.build_launch_script(
            r"C:\repo", r"C:\bin\claude.exe", prompt,
        )
        self.assertIn("'@ midline", script)

    def test_terminator_at_line_start_rejected(self):
        prompt = "Line1\n'@ this would break\nLine3"
        with self.assertRaises(ValueError):
            launch_claude_session.build_launch_script(
                r"C:\repo", r"C:\bin\claude.exe", prompt,
            )

    def test_indented_terminator_rejected(self):
        prompt = "Line1\n  '@ indented also breaks\nLine3"
        with self.assertRaises(ValueError):
            launch_claude_session.build_launch_script(
                r"C:\repo", r"C:\bin\claude.exe", prompt,
            )

    def test_prompt_stdin_dry_run(self):
        prompt = "Test with – em-dash and $(whoami)"
        with tempfile.TemporaryDirectory() as tmp:
            result = launch_claude_session.launch(
                target=tmp,
                name="test-stdin",
                prompt=prompt,
                dry_run=True,
                launch_dir=tmp,
            )
            self.assertEqual(result, 0)
            script_path = os.path.join(tmp, "launch-test-stdin.ps1")
            with open(script_path, "r", encoding="utf-8") as f:
                content = f.read()
            self.assertIn("$(whoami)", content)
            self.assertIn("–", content)


class TestNoBareOpen(unittest.TestCase):
    """All open() calls in production code must specify encoding=."""

    _OPEN_RE = re.compile(r'\bopen\s*\(')
    _ENCODING_RE = re.compile(r'encoding\s*=')
    _OS_OPEN_RE = re.compile(r'os\.open\s*\(')
    _SKIP_PATTERNS = ('Popen(', '_FakePopen', '_fake_popen')

    def test_no_bare_open_in_hooks(self):
        self._check_directory(os.path.join(_REPO, ".claude", "hooks"))

    def test_no_bare_open_in_scripts(self):
        self._check_directory(_HERE, exclude_pattern="test_")

    def _check_directory(self, directory, exclude_pattern=None):
        issues = []
        for filepath in sorted(glob.glob(os.path.join(directory, "*.py"))):
            basename = os.path.basename(filepath)
            if exclude_pattern and basename.startswith(exclude_pattern):
                continue
            with open(filepath, "r", encoding="utf-8") as f:
                for lineno, line in enumerate(f, 1):
                    stripped = line.strip()
                    if stripped.startswith("#"):
                        continue
                    if self._OS_OPEN_RE.search(stripped):
                        continue
                    if any(p in stripped for p in self._SKIP_PATTERNS):
                        continue
                    if not self._OPEN_RE.search(stripped):
                        continue
                    code = stripped.split("#")[0]
                    if self._OPEN_RE.search(code) and not self._ENCODING_RE.search(code):
                        issues.append(f"{filepath}:{lineno}: {stripped[:100]}")
        self.assertEqual(issues, [], f"Bare open() calls found:\n" + "\n".join(issues))


if __name__ == "__main__":
    unittest.main()
