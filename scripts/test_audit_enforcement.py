#!/usr/bin/env python3
"""Behavioral tests for scripts/audit-enforcement.py.

Each test builds a tiny fake repo (a temp dir with a `.claude/skills/`,
`.claude/hooks/`, `.claude/settings.json`, `CLAUDE.md` layout) and invokes
audit-enforcement.py via subprocess against it. ACs: 165, 166, 167, 168,
172, 177.
"""
from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "scripts" / "audit-enforcement.py"


# Import the module so we can call cmd_* directly when easier.
_spec = importlib.util.spec_from_file_location("audit_enforcement", SCRIPT)
audit_enforcement = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(audit_enforcement)


def _make_fake_repo(skill_files: dict[str, str], hook_names: list[str],
                   wired_hooks: list[str], claude_md: str | None = None) -> str:
    tmp = tempfile.mkdtemp()
    skills_dir = os.path.join(tmp, ".claude", "skills")
    hooks_dir = os.path.join(tmp, ".claude", "hooks")
    os.makedirs(skills_dir, exist_ok=True)
    os.makedirs(hooks_dir, exist_ok=True)
    for name, content in skill_files.items():
        d = os.path.join(skills_dir, name)
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, "SKILL.md"), "w", encoding="utf-8") as f:
            f.write(content)
    for h in hook_names:
        with open(os.path.join(hooks_dir, f"{h}.py"), "w", encoding="utf-8") as f:
            f.write("# stub hook\n")
    settings = {
        "hooks": {
            "PreToolUse": [
                {"matcher": "Edit", "hooks": [
                    {"type": "command",
                     "command": f'python "$CLAUDE_PROJECT_DIR/.claude/hooks/{h}.py"'}
                ]} for h in wired_hooks
            ]
        }
    }
    with open(os.path.join(tmp, ".claude", "settings.json"), "w", encoding="utf-8") as f:
        json.dump(settings, f)
    if claude_md is not None:
        with open(os.path.join(tmp, "CLAUDE.md"), "w", encoding="utf-8") as f:
            f.write(claude_md)
    return tmp


def _run(repo: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(SCRIPT), "--repo-root", repo, *args],
        capture_output=True, text=True, timeout=30,
    )


class TestStrict(unittest.TestCase):
    """AC-165, AC-166."""

    def test_strict_pass(self):
        # AC-165: T1 marker references a hook that exists AND is wired
        repo = _make_fake_repo(
            skill_files={
                "alpha": "Step MUST run. <!-- enforcement: T1 hook:alpha-hook -->\n",
            },
            hook_names=["alpha-hook"],
            wired_hooks=["alpha-hook"],
        )
        self.addCleanup(shutil.rmtree, repo, ignore_errors=True)
        proc = _run(repo, "--strict")
        self.assertEqual(proc.returncode, 0,
                         f"strict should pass; stderr={proc.stderr}")
        self.assertIn("OK", proc.stdout)

    def test_strict_fail_missing_hook(self):
        # AC-166: T1 marker references a hook file that does not exist
        repo = _make_fake_repo(
            skill_files={
                "alpha": "Step MUST run. <!-- enforcement: T1 hook:ghost-hook -->\n",
            },
            hook_names=[],            # ghost-hook.py absent
            wired_hooks=[],
        )
        self.addCleanup(shutil.rmtree, repo, ignore_errors=True)
        proc = _run(repo, "--strict")
        self.assertEqual(proc.returncode, 1)
        self.assertIn("MISSING HOOK FILES", proc.stderr)
        self.assertIn("ghost-hook", proc.stderr)

    def test_strict_fail_unwired(self):
        # File exists but not in settings.json
        repo = _make_fake_repo(
            skill_files={
                "alpha": "Step MUST run. <!-- enforcement: T1 hook:alpha-hook -->\n",
            },
            hook_names=["alpha-hook"],
            wired_hooks=[],
        )
        self.addCleanup(shutil.rmtree, repo, ignore_errors=True)
        proc = _run(repo, "--strict")
        self.assertEqual(proc.returncode, 1)
        self.assertIn("NOT WIRED", proc.stderr)


class TestDiscover(unittest.TestCase):
    """AC-167."""

    def test_discover_mode(self):
        repo = _make_fake_repo(
            skill_files={
                "alpha": "Step MUST run.\nALWAYS commit.\n",  # both unmarked
            },
            hook_names=[], wired_hooks=[],
        )
        self.addCleanup(shutil.rmtree, repo, ignore_errors=True)
        proc = _run(repo, "--discover")
        self.assertEqual(proc.returncode, 1)
        self.assertIn("FOUND 2 unmarked", proc.stdout)
        self.assertIn("Step MUST run", proc.stdout)
        self.assertIn("ALWAYS commit", proc.stdout)

    def test_discover_clean(self):
        repo = _make_fake_repo(
            skill_files={
                "alpha": "Step MUST run. <!-- enforcement: T3 -->\n",
            },
            hook_names=[], wired_hooks=[],
        )
        self.addCleanup(shutil.rmtree, repo, ignore_errors=True)
        proc = _run(repo, "--discover")
        self.assertEqual(proc.returncode, 0)
        self.assertIn("zero unmarked", proc.stdout)

    def test_discover_ignores_code_fences(self):
        # Directives inside ```...``` blocks must NOT be flagged
        content = (
            "Real directive MUST hold. <!-- enforcement: T3 -->\n"
            "```\n"
            "You MUST do this thing\n"
            "NEVER do that thing\n"
            "```\n"
        )
        repo = _make_fake_repo(skill_files={"alpha": content},
                               hook_names=[], wired_hooks=[])
        self.addCleanup(shutil.rmtree, repo, ignore_errors=True)
        proc = _run(repo, "--discover")
        self.assertEqual(proc.returncode, 0,
                         f"code-fence content should not be flagged; out={proc.stdout}")


class TestReport(unittest.TestCase):
    """AC-177, AC-172."""

    def test_report_snapshot(self):
        # AC-177
        repo = _make_fake_repo(
            skill_files={
                "alpha": (
                    "Step1 MUST run. <!-- enforcement: T1 hook:alpha-hook -->\n"
                    "Step2 ALWAYS commit. <!-- enforcement: T3 -->\n"
                ),
                "beta": (
                    "Beta NEVER skip. <!-- enforcement: T2 hook:future -->\n"
                ),
            },
            hook_names=["alpha-hook"],
            wired_hooks=["alpha-hook"],
        )
        self.addCleanup(shutil.rmtree, repo, ignore_errors=True)
        proc = _run(repo, "--report")
        self.assertEqual(proc.returncode, 0)
        snap = Path(repo) / ".workflow" / "audit-snapshot.md"
        self.assertTrue(snap.exists(), "snapshot must be written")
        content = snap.read_text(encoding="utf-8")
        self.assertIn("# Audit Enforcement Snapshot", content)
        self.assertIn("Tier breakdown", content)
        self.assertIn("Per-file marker counts", content)
        self.assertIn("Hook coverage", content)
        self.assertIn("Top 5 longest", content)
        self.assertIn("**Date:**", content)
        # Tier counts
        self.assertIn("T1", content)
        self.assertIn("T2", content)
        self.assertIn("T3", content)

    def test_zero_unmarked_after_audit(self):
        # AC-172 — every directive marked → snapshot reports zero unmarked
        repo = _make_fake_repo(
            skill_files={
                "alpha": (
                    "Step1 MUST run. <!-- enforcement: T1 hook:alpha-hook -->\n"
                    "Step2 ALWAYS commit. <!-- enforcement: T3 -->\n"
                ),
            },
            hook_names=["alpha-hook"],
            wired_hooks=["alpha-hook"],
            claude_md="Top-level NEVER do that. <!-- enforcement: T3 -->\n",
        )
        self.addCleanup(shutil.rmtree, repo, ignore_errors=True)
        proc = _run(repo, "--report")
        self.assertEqual(proc.returncode, 0)
        snap = (Path(repo) / ".workflow" / "audit-snapshot.md").read_text("utf-8")
        self.assertIn("Zero unmarked directives", snap)


class TestAllT1DirectivesMarked(unittest.TestCase):
    """AC-168 — verify the real repo has zero unmarked directives."""

    def test_all_t1_directives_marked(self):
        proc = subprocess.run(
            [sys.executable, str(SCRIPT), "--discover"],
            cwd=str(REPO_ROOT), capture_output=True, text=True, timeout=30,
        )
        self.assertEqual(
            proc.returncode, 0,
            f"real repo has unmarked directives:\n{proc.stdout}",
        )


if __name__ == "__main__":
    unittest.main()
