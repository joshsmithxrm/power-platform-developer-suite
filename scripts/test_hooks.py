#!/usr/bin/env python3
"""Behavioral unit tests for v9.0 enforcement hooks.

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
import time
import unittest
from datetime import datetime, timezone
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
HOOKS_DIR = REPO_ROOT / ".claude" / "hooks"


def _run_hook(hook_name: str, payload: dict, *, project_dir: str,
              env_extra: dict | None = None,
              timeout: int = 10) -> subprocess.CompletedProcess:
    hook_path = HOOKS_DIR / hook_name
    env = os.environ.copy()
    env["CLAUDE_PROJECT_DIR"] = project_dir
    env.pop("PPDS_PIPELINE", None)
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


def _make_state(project_dir: str, state: dict) -> str:
    wf = os.path.join(project_dir, ".workflow")
    os.makedirs(wf, exist_ok=True)
    p = os.path.join(wf, "state.json")
    with open(p, "w", encoding="utf-8") as f:
        json.dump(state, f)
    return p


def _git_init_branch(project_dir: str, branch: str = "feat/x"):
    """Make project_dir look enough like a git repo to satisfy the stop hook."""
    subprocess.run(["git", "init", "-q", "-b", branch], cwd=project_dir, check=True)
    subprocess.run(["git", "config", "user.email", "test@example.com"],
                   cwd=project_dir, check=True)
    subprocess.run(["git", "config", "user.name", "test"], cwd=project_dir, check=True)
    # Empty initial commit so HEAD exists
    subprocess.run(["git", "commit", "--allow-empty", "-q", "-m", "init"],
                   cwd=project_dir, check=True)


# ---------------------------------------------------------------------------
# session-stop-workflow.py — PR phase / monitor / pr-invocation gates
# ---------------------------------------------------------------------------


class TestStopHookMonitorGate(unittest.TestCase):
    """AC-147, AC-148, AC-149."""

    def _setup(self, state: dict, branch: str = "feat/x") -> str:
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        _git_init_branch(tmp, branch)
        _make_state(tmp, state)
        return tmp

    def test_stop_hook_blocks_pr_without_monitor(self):
        # AC-147
        tmp = self._setup({"phase": "pr", "pr": {}})
        proc = _run_hook("session-stop-workflow.py", {}, project_dir=tmp)
        self.assertEqual(proc.returncode, 2,
                         f"expected exit 2; got {proc.returncode}\nstdout={proc.stdout}\nstderr={proc.stderr}")
        self.assertIn("monitor_launched", proc.stdout)

    def test_stop_hook_allows_pr_with_monitor(self):
        # AC-148
        tmp = self._setup({
            "phase": "pr",
            "pr": {"monitor_launched": "2026-04-26T12:00:00Z"},
        })
        proc = _run_hook("session-stop-workflow.py", {}, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)

    def test_stop_hook_allows_pr_with_fallback(self):
        # AC-149
        tmp = self._setup({
            "phase": "pr",
            "pr": {"monitor_launched": "fallback: claude not on PATH"},
        })
        proc = _run_hook("session-stop-workflow.py", {}, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)


class TestStopHookPrInvocationGate(unittest.TestCase):
    """AC-169, AC-170, AC-171."""

    def _setup(self, state: dict, *, ahead: int = 0) -> str:
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        _git_init_branch(tmp, "feat/x")
        # Stub origin/main to current HEAD (no commits ahead) by default.
        head = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=tmp, capture_output=True, text=True
        ).stdout.strip()
        subprocess.run(["git", "update-ref", "refs/remotes/origin/main", head],
                       cwd=tmp, check=True)
        for i in range(ahead):
            subprocess.run(
                ["git", "commit", "--allow-empty", "-q", "-m", f"c{i}"],
                cwd=tmp, check=True,
            )
        _make_state(tmp, state)
        return tmp

    def test_stop_hook_blocks_no_pr_invocation(self):
        # AC-169 — commits ahead, phase=implementing, invoked_via_skill missing
        tmp = self._setup({"phase": "implementing", "pr": {}}, ahead=1)
        proc = _run_hook("session-stop-workflow.py", {}, project_dir=tmp)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("/pr was not invoked", proc.stdout)

    def test_stop_hook_allows_pr_phase(self):
        # AC-170 — phase=pr always bypasses pr-invocation gate (5b handles it)
        tmp = self._setup(
            {"phase": "pr", "pr": {"monitor_launched": "ts"}}, ahead=1,
        )
        proc = _run_hook("session-stop-workflow.py", {}, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)

    def test_stop_hook_allows_no_commits(self):
        # AC-171 — zero commits ahead → no block
        tmp = self._setup({"phase": "implementing", "pr": {}}, ahead=0)
        proc = _run_hook("session-stop-workflow.py", {}, project_dir=tmp)
        # May still block on missing gates/verify/etc., but not on pr-invocation.
        # Confirm stderr/stdout doesn't contain the pr-invocation block reason.
        self.assertNotIn("/pr was not invoked", proc.stdout)


class TestStopHookEscapeValve(unittest.TestCase):
    """AC-176."""

    def test_three_strike_escape_valve(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        _git_init_branch(tmp, "feat/x")
        _make_state(tmp, {
            "phase": "implementing",
            "stop_hook_count": 3,
            "pr": {},
        })
        proc = _run_hook("session-stop-workflow.py", {}, project_dir=tmp)
        self.assertEqual(proc.returncode, 0,
                         "after 3 blocks, escape valve must allow exit")
        self.assertIn("OVERRIDE_GRANTED", proc.stderr)


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
# retro-html-guard.py
# ---------------------------------------------------------------------------


class TestRetroHtmlGuard(unittest.TestCase):
    """AC-150, AC-151."""

    def test_retro_html_guard_blocks_interactive(self):
        # AC-150 — PPDS_PIPELINE unset, .retros/*.html → block
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "tool_name": "Write",
            "tool_input": {"file_path": ".retros/2026-04-26.html",
                           "content": "<html></html>"},
        }
        proc = _run_hook("retro-html-guard.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("BLOCKED", proc.stderr)

    def test_retro_html_guard_allows_pipeline(self):
        # AC-151 — PPDS_PIPELINE=1 → allow
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "tool_name": "Write",
            "tool_input": {"file_path": ".retros/2026-04-26.html",
                           "content": "<html></html>"},
        }
        proc = _run_hook("retro-html-guard.py", payload, project_dir=tmp,
                         env_extra={"PPDS_PIPELINE": "1"})
        self.assertEqual(proc.returncode, 0)

    def test_retro_html_guard_ignores_non_retro(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "tool_name": "Write",
            "tool_input": {"file_path": "docs/something.html", "content": "<html/>"},
        }
        proc = _run_hook("retro-html-guard.py", payload, project_dir=tmp)
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
        with open(os.path.join(wf, "worktree-remove.lock"), "w") as f:
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


# ---------------------------------------------------------------------------
# taskcreate-cap.py
# ---------------------------------------------------------------------------


class TestTaskCreateCap(unittest.TestCase):
    """AC-173, AC-174."""

    def _setup_inflight(self, project_dir: str, count: int):
        d = os.path.join(project_dir, ".claude", "state")
        os.makedirs(d, exist_ok=True)
        entries = [
            {"id": f"#{i}", "status": "active"} for i in range(count)
        ]
        with open(os.path.join(d, "in-flight-issues.json"), "w") as f:
            json.dump({"open_work": entries}, f)

    def test_taskcreate_cap_blocks_fourth(self):
        # AC-173 — 3 in-flight, attempting 4th
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        self._setup_inflight(tmp, 3)
        payload = {"tool_name": "Agent",
                   "tool_input": {"description": "x", "prompt": "y"}}
        proc = _run_hook("taskcreate-cap.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("BLOCKED", proc.stderr)

    def test_taskcreate_cap_allows_under_limit(self):
        # AC-174 — 2 in-flight → allow
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        self._setup_inflight(tmp, 2)
        payload = {"tool_name": "Agent",
                   "tool_input": {"description": "x", "prompt": "y"}}
        proc = _run_hook("taskcreate-cap.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)

    def test_taskcreate_cap_no_state_allows(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {"tool_name": "Agent",
                   "tool_input": {"description": "x", "prompt": "y"}}
        proc = _run_hook("taskcreate-cap.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)


# ---------------------------------------------------------------------------
# debug-first.py
# ---------------------------------------------------------------------------


class TestDebugFirst(unittest.TestCase):
    """AC-175."""

    def _record_failure(self, project_dir: str, command: str = "dotnet test"):
        wf = os.path.join(project_dir, ".workflow")
        os.makedirs(wf, exist_ok=True)
        with open(os.path.join(wf, "last_failure"), "w") as f:
            json.dump({
                "timestamp": datetime.now(timezone.utc).isoformat(),
                "command": command,
            }, f)

    def test_debug_first_blocks_retry(self):
        # AC-175 — last_failure exists, no /debug since → block
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        self._record_failure(tmp)
        payload = {
            "hook_event_name": "PreToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": "dotnet test PPDS.sln"},
        }
        proc = _run_hook("debug-first.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("BLOCKED", proc.stderr)

    def test_debug_first_allows_when_no_prior_failure(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "hook_event_name": "PreToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": "dotnet test PPDS.sln"},
        }
        proc = _run_hook("debug-first.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)

    def test_debug_first_allows_after_debug_run(self):
        # last_failure exists but debug.last_run is newer → allow
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        self._record_failure(tmp)
        # Sleep just enough for debug.last_run to be strictly after
        time.sleep(0.05)
        future = datetime.now(timezone.utc).isoformat()
        _make_state(tmp, {"debug": {"last_run": future}})
        payload = {
            "hook_event_name": "PreToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": "dotnet test PPDS.sln"},
        }
        proc = _run_hook("debug-first.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)

    def test_debug_first_records_failure_post(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        payload = {
            "hook_event_name": "PostToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": "dotnet test PPDS.sln"},
            "tool_response": {"returncode": 1},
        }
        proc = _run_hook("debug-first.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)
        # last_failure file should now exist
        self.assertTrue(
            os.path.exists(os.path.join(tmp, ".workflow", "last_failure"))
        )


class TestInflightAutoDeregister(unittest.TestCase):
    """AC-179: PostToolUse on `gh pr merge`/`git branch -D` deregisters branch."""

    def _setup_registry(self, project_dir: str, branch: str):
        state_dir = os.path.join(project_dir, ".claude", "state")
        os.makedirs(state_dir, exist_ok=True)
        path = os.path.join(state_dir, "in-flight-issues.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump({
                "version": 1,
                "updated": datetime.now(timezone.utc).isoformat(),
                "open_work": [{
                    "session_id": "deadbeef",
                    "started": datetime.now(timezone.utc).isoformat(),
                    "branch": branch,
                    "issues": [42],
                    "areas": ["x/"],
                }],
            }, f)
        # Copy helper scripts the hook shells out to.
        scripts_src = REPO_ROOT / "scripts"
        scripts_dst = os.path.join(project_dir, "scripts")
        os.makedirs(scripts_dst, exist_ok=True)
        for fname in ("inflight-deregister.py", "inflight_common.py"):
            shutil.copy(scripts_src / fname, os.path.join(scripts_dst, fname))
        return path

    def _read_registry(self, project_dir: str):
        path = os.path.join(project_dir, ".claude", "state", "in-flight-issues.json")
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)

    def test_inflight_deregister_on_merge(self):
        """Successful `git branch -D feat/foo` removes the entry from the registry.

        Runs against the real repo's scripts/ + a tempdir registry. We use
        CLAUDE_PROJECT_DIR=REPO_ROOT so the hook can find scripts/, and override
        the registry path via PPDS_INFLIGHT_STATE_FILE if supported, otherwise
        we patch the registry and restore.
        """
        # Stage a fake scripts/ in a tempdir that mirrors the real repo's
        # scripts/inflight-deregister.py + scripts/inflight_common.py.
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        scripts_dst = os.path.join(tmp, "scripts")
        os.makedirs(scripts_dst, exist_ok=True)
        for fname in ("inflight-deregister.py", "inflight_common.py"):
            shutil.copy(os.path.join(REPO_ROOT, "scripts", fname),
                        os.path.join(scripts_dst, fname))
        self._setup_registry(tmp, "feat/foo")

        payload = {
            "hook_event_name": "PostToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": "git branch -D feat/foo"},
            "tool_response": {"exit_code": 0},
        }
        proc = _run_hook("inflight-auto-deregister.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0,
                         msg=f"hook stderr: {proc.stderr!r}")

        state = self._read_registry(tmp)
        branches = [e.get("branch") for e in state.get("open_work", [])]
        self.assertNotIn("feat/foo", branches,
                         "Successful merge/delete must deregister branch (AC-179)")

    def test_inflight_no_deregister_on_failure(self):
        """Non-zero exit (failed merge/delete) leaves the registry untouched."""
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        scripts_dst = os.path.join(tmp, "scripts")
        os.makedirs(scripts_dst, exist_ok=True)
        for fname in ("inflight-deregister.py", "inflight_common.py"):
            shutil.copy(os.path.join(REPO_ROOT, "scripts", fname),
                        os.path.join(scripts_dst, fname))
        self._setup_registry(tmp, "feat/keep")

        payload = {
            "hook_event_name": "PostToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": "git branch -D feat/keep"},
            "tool_response": {"exit_code": 1},
        }
        proc = _run_hook("inflight-auto-deregister.py", payload, project_dir=tmp)
        self.assertEqual(proc.returncode, 0)

        state = self._read_registry(tmp)
        branches = [e.get("branch") for e in state.get("open_work", [])]
        self.assertIn("feat/keep", branches,
                      "Failed merge/delete must NOT deregister")


if __name__ == "__main__":
    unittest.main()
