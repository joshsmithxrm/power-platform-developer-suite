#!/usr/bin/env python3
"""Behavioral unit tests for surviving enforcement hooks.

Each test invokes the hook script as a subprocess with a JSON payload on
stdin and asserts on the exit code and (optionally) stderr/stdout. The hook
files live in `.claude/hooks/`. Tests use a tempdir as CLAUDE_PROJECT_DIR.

Run:
    python -m unittest scripts.test_hooks
    python -m pytest scripts/test_hooks.py
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
HOOKS_DIR = REPO_ROOT / ".claude" / "hooks"


def _run_hook(hook_name: str, payload: dict, *, project_dir: str,
              env_extra: dict | None = None,
              timeout: int = 10) -> subprocess.CompletedProcess:
    hook_path = HOOKS_DIR / hook_name
    env = os.environ.copy()
    env["CLAUDE_PROJECT_DIR"] = project_dir
    env.pop("PPDS_SHAKEDOWN", None)
    if env_extra:
        env.update(env_extra)
    proc = subprocess.run(
        [sys.executable, str(hook_path)],
        input=json.dumps(payload),
        env=env,
        capture_output=True,
        text=True,
        timeout=timeout,
        cwd=project_dir,
    )
    return proc


# ---------------------------------------------------------------------------
# skill-line-cap.py
# ---------------------------------------------------------------------------


class TestSkillLineCap(unittest.TestCase):
    """AC-157, AC-158."""

    def test_skill_line_cap_blocks(self):
        # 151 lines — over cap of 150
        content = "\n".join([f"line {i}" for i in range(151)]) + "\n"
        payload = {
            "tool_name": "Write",
            "tool_input": {"file_path": ".claude/skills/foo/SKILL.md",
                           "content": content},
        }
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        proc = _run_hook("skill-line-cap.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("BLOCKED", proc.stderr)

    def test_skill_line_cap_allows(self):
        content = "\n".join([f"line {i}" for i in range(149)]) + "\n"
        payload = {
            "tool_name": "Write",
            "tool_input": {"file_path": ".claude/skills/foo/SKILL.md",
                           "content": content},
        }
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        proc = _run_hook("skill-line-cap.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)

    def test_skill_line_cap_at_boundary_allowed(self):
        # AC-158 — exactly 150 lines + trailing newline must be ALLOWED
        content = "\n".join([f"line {i}" for i in range(150)]) + "\n"
        payload = {
            "tool_name": "Write",
            "tool_input": {"file_path": ".claude/skills/foo/SKILL.md",
                           "content": content},
        }
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        proc = _run_hook("skill-line-cap.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0,
                         f"expected exit 0; got {proc.returncode}\nstderr={proc.stderr}")

    def test_skill_line_cap_ignores_non_skill_md(self):
        content = "\n".join([f"line {i}" for i in range(500)]) + "\n"
        payload = {
            "tool_name": "Write",
            "tool_input": {"file_path": "src/foo.cs", "content": content},
        }
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        proc = _run_hook("skill-line-cap.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)


# ---------------------------------------------------------------------------
# worktree-safety.py
# ---------------------------------------------------------------------------


class TestWorktreeSafety(unittest.TestCase):
    """AC-163, AC-164."""

    def test_worktree_safety_blocks_main(self):
        # AC-163 — git worktree remove on the main repo root
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": f"git worktree remove {tmp}"},
        }
        proc = _run_hook("worktree-safety.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("BLOCKED", proc.stderr)

    def test_worktree_safety_blocks_parallel(self):
        # AC-164 — concurrent removal (lock file with live PID)
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        wf = os.path.join(tmp, ".workflow")
        os.makedirs(wf, exist_ok=True)
        # Use the test runner's own PID — guaranteed alive.
        with open(os.path.join(wf, "worktree-remove.lock"), "w", encoding="utf-8") as f:
            f.write(str(os.getpid()))
        # Target a different worktree path so the main-root check doesn't fire.
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": "git worktree remove ../other-wt"},
        }
        proc = _run_hook("worktree-safety.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("in progress", proc.stderr)

    def test_worktree_safety_allows_normal_remove(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "tool_name": "Bash",
            "tool_input": {"command": "git worktree remove ../other-wt"},
        }
        proc = _run_hook("worktree-safety.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)


if __name__ == "__main__":
    unittest.main()
