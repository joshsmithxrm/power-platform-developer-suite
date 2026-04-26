#!/usr/bin/env python3
"""Tests for AC-178, AC-179, AC-180 - in-flight auto-deregister."""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

REPO_ROOT = Path(__file__).resolve().parent.parent
HOOKS_DIR = REPO_ROOT / ".claude" / "hooks"
HOOK = HOOKS_DIR / "inflight-auto-deregister.py"


def _make_repo_with_state(entries):
    tmp = tempfile.mkdtemp()
    state_dir = os.path.join(tmp, ".claude", "state")
    os.makedirs(state_dir, exist_ok=True)
    state = {"version": 1, "updated": "now", "open_work": entries}
    with open(os.path.join(state_dir, "in-flight-issues.json"), "w", encoding="utf-8") as f:
        json.dump(state, f)
    scripts_src = REPO_ROOT / "scripts"
    scripts_dst = os.path.join(tmp, "scripts")
    os.makedirs(scripts_dst, exist_ok=True)
    for fname in ("inflight-deregister.py", "inflight-check.py", "inflight_common.py"):
        shutil.copy(scripts_src / fname, os.path.join(scripts_dst, fname))
    return tmp


def _run_hook(payload, project_dir):
    env = os.environ.copy()
    env["CLAUDE_PROJECT_DIR"] = project_dir
    return subprocess.run(
        [sys.executable, str(HOOK)],
        input=json.dumps(payload),
        env=env, cwd=project_dir,
        capture_output=True, text=True, timeout=15,
    )


class TestInflightDeregisterOnMerge(unittest.TestCase):
    """AC-179: PostToolUse on Bash(gh pr merge / git branch -D) deregisters on exit 0."""

    def test_inflight_deregister_on_merge(self):
        tmp = _make_repo_with_state([
            {"branch": "feat/x", "session_id": "abc"},
            {"branch": "feat/y", "session_id": "def"},
        ])
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "hook_event_name": "PostToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": "git branch -D feat/x"},
            "tool_response": {"exit_code": 0, "returncode": 0},
        }
        proc = _run_hook(payload, tmp)
        self.assertEqual(proc.returncode, 0)
        with open(os.path.join(tmp, ".claude", "state",
                              "in-flight-issues.json"), encoding="utf-8") as f:
            state = json.load(f)
        branches = {e["branch"] for e in state["open_work"]}
        self.assertNotIn("feat/x", branches,
                         "merged branch must be deregistered")
        self.assertIn("feat/y", branches, "other branches remain")

    def test_no_op_on_failed_command(self):
        tmp = _make_repo_with_state([{"branch": "feat/x", "session_id": "abc"}])
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "hook_event_name": "PostToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": "git branch -D feat/x"},
            "tool_response": {"exit_code": 1, "returncode": 1},
        }
        proc = _run_hook(payload, tmp)
        self.assertEqual(proc.returncode, 0)
        with open(os.path.join(tmp, ".claude", "state",
                              "in-flight-issues.json"), encoding="utf-8") as f:
            state = json.load(f)
        branches = {e["branch"] for e in state["open_work"]}
        self.assertIn("feat/x", branches,
                      "failed command must NOT deregister")


class TestStaleTtlDetection(unittest.TestCase):
    """AC-180: inflight-check.py reports >7-day-old entries as stale."""

    def _load_module(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location(
            "inflight_check", REPO_ROOT / "scripts" / "inflight-check.py"
        )
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod

    def test_stale_ttl_detection(self):
        mod = self._load_module()
        old = (datetime.now(timezone.utc) - timedelta(days=10)).isoformat()
        recent = (datetime.now(timezone.utc) - timedelta(days=2)).isoformat()
        conflicts = [
            {"branch": "feat/old", "session_id": "a1", "started": old},
            {"branch": "feat/new", "session_id": "b2", "started": recent},
        ]
        with patch.object(mod, "_live_worktree_branches", return_value=set()):
            annotated = mod.annotate_stale(conflicts)
        old_entry = next(e for e in annotated if e["branch"] == "feat/old")
        new_entry = next(e for e in annotated if e["branch"] == "feat/new")
        self.assertTrue(old_entry.get("stale"),
                        "entry older than 7 days must be marked stale")
        self.assertFalse(new_entry.get("stale"),
                         "recent entry must NOT be marked stale")

    def test_stale_skipped_when_branch_has_live_worktree(self):
        mod = self._load_module()
        old = (datetime.now(timezone.utc) - timedelta(days=10)).isoformat()
        conflicts = [{"branch": "feat/old", "session_id": "a1", "started": old}]
        with patch.object(mod, "_live_worktree_branches",
                          return_value={"feat/old"}):
            annotated = mod.annotate_stale(conflicts)
        self.assertFalse(annotated[0].get("stale"),
                         "branch with live worktree must NOT be stale")


class TestPrMonitorTerminalDeregisters(unittest.TestCase):
    """AC-178: pr_monitor terminal step calls inflight-deregister before notify."""

    def test_terminal_deregisters_inflight(self):
        import importlib.util
        sys.path.insert(0, str(REPO_ROOT / "scripts"))
        spec = importlib.util.spec_from_file_location(
            "pr_monitor", REPO_ROOT / "scripts" / "pr_monitor.py"
        )
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        self.assertTrue(hasattr(mod, "_deregister_inflight"),
                        "pr_monitor must expose _deregister_inflight (AC-178)")

        captured = {"calls": []}

        def fake_run(cmd, **kwargs):
            captured["calls"].append(list(cmd))
            class R:
                returncode = 0
                stdout = "feat/branch-x\n"
                stderr = ""
            return R()

        class FakeLogger:
            def log(self, *a, **k):
                pass

        with patch.object(mod.subprocess, "run", side_effect=fake_run):
            mod._deregister_inflight("/tmp/wt", FakeLogger())

        self.assertGreaterEqual(len(captured["calls"]), 2)
        deregister_call = next(
            (c for c in captured["calls"]
             if "inflight-deregister.py" in " ".join(c)),
            None,
        )
        self.assertIsNotNone(deregister_call,
                             "pr_monitor must call inflight-deregister.py")
        self.assertIn("--branch", deregister_call)
        self.assertIn("feat/branch-x", deregister_call)


if __name__ == "__main__":
    unittest.main()
