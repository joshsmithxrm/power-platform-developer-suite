"""Tests for the pr-gate hook's agent-vs-human entry-point enforcement.

Covers the three branches the hook distinguishes:
  1. Agent context WITHOUT ``/pr`` skill marker -> BLOCKED
  2. Agent context WITH    ``/pr`` skill marker -> proceeds to workflow checks
  3. Human context                              -> existing workflow checks only

See ``.claude/hooks/pr-gate.py`` and ``.claude/skills/pr/SKILL.md`` Canonical
Entry Point. Complements ``test_hook_envelope.py`` which covers stdin contract.

Run: ``pytest tests/hooks/test_pr_gate_agent_enforcement.py -v``
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path


HOOK_PATH = Path(__file__).resolve().parents[2] / ".claude" / "hooks" / "pr-gate.py"


def _load_hook():
    hooks_dir = str(HOOK_PATH.parent)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec = importlib.util.spec_from_file_location("pr_gate", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


# ---------------------------------------------------------------------------
# subprocess helpers (Windows-safe stdio handling for pytest captured mode)
# ---------------------------------------------------------------------------


def _run(argv, cwd=None, env=None, input_str=None):
    kw = dict(cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
              text=True, timeout=30)
    if input_str is not None:
        return subprocess.run(argv, input=input_str, **kw)
    return subprocess.run(argv, stdin=subprocess.DEVNULL, **kw)


def _git(path, args):
    r = _run(["git", *args], cwd=str(path))
    if r.returncode != 0:
        raise RuntimeError(f"git {args} failed: {r.stderr}")
    return r.stdout


def _init_repo(path):
    _git(path, ["init", "-q", "-b", "main"])
    _git(path, ["config", "user.email", "test@example.com"])
    _git(path, ["config", "user.name", "Test"])
    (path / "README.md").write_text("x\n")
    _git(path, ["add", "."])
    _git(path, ["commit", "-q", "-m", "init"])
    _git(path, ["update-ref", "refs/remotes/origin/main", "HEAD"])
    return _git(path, ["rev-parse", "HEAD"]).strip()


def _clean_env():
    env = os.environ.copy()
    for v in ("CLAUDE_CODE_AGENT", "CLAUDE_AGENT_ID", "CLAUDE_CODE_SUBAGENT",
              "PPDS_PR_GATE_HUMAN"):
        env.pop(v, None)
    return env


def _run_hook(command, cwd, env_extra=None):
    env = _clean_env()
    env["CLAUDE_PROJECT_DIR"] = str(cwd)
    if env_extra:
        env.update(env_extra)
    payload = {"tool_name": "Bash", "tool_input": {"command": command}}
    return _run([sys.executable, str(HOOK_PATH)], cwd=str(cwd), env=env,
                input_str=json.dumps(payload))


def _write_state(cwd, state):
    (cwd / ".workflow").mkdir(exist_ok=True)
    (cwd / ".workflow" / "state.json").write_text(json.dumps(state))


def _passing_state(head):
    # Minimal state that clears commit-ref + verify + qa checks for a
    # no-diff branch (affected-surfaces set is empty -> "require at least
    # one verify/qa" heuristic applies).
    return {
        "gates": {"passed": "2026-04-19T00:00:00+00:00", "commit_ref": head},
        "review": {"passed": "2026-04-19T00:01:00+00:00", "commit_ref": head},
        "verify": {"cli_commit_ref": head},
        "qa": {"cli_commit_ref": head},
    }


# ---------------------------------------------------------------------------
# Unit tests on helpers
# ---------------------------------------------------------------------------


import pytest


@pytest.mark.parametrize("cwd,env,expected", [
    # Human context
    ("/home/j/ppds", {}, False),
    ("/x", {"CLAUDE_CODE_AGENT": ""}, False),
    # Agent context via cwd or env
    ("/home/j/ppds/.claude/worktrees/agent-abcd/src", {}, True),
    (r"C:\ppds\.claude\worktrees\agent-1234", {}, True),
    ("/x", {"CLAUDE_CODE_AGENT": "1"}, True),
    ("/x", {"CLAUDE_AGENT_ID": "xyz"}, True),
    ("/x", {"CLAUDE_CODE_SUBAGENT": "1"}, True),
    # Human override beats both signals
    ("/home/j/ppds/.claude/worktrees/agent-abcd", {"PPDS_PR_GATE_HUMAN": "1"}, False),
    ("/x", {"CLAUDE_CODE_AGENT": "1", "PPDS_PR_GATE_HUMAN": "1"}, False),
    # Override value != "1" does not override
    ("/home/j/ppds/.claude/worktrees/agent-abcd", {"PPDS_PR_GATE_HUMAN": "true"}, True),
    # Project-level .worktrees/ paths (R-01 retro finding from PR #933)
    ("/home/j/ppds/.worktrees/toolbar-search-consistency", {}, True),
    ("/home/j/ppds/.worktrees/pr-gate-worktree-enforcement", {}, True),
    (r"C:\Users\josh\ppds\.worktrees\some-feature", {}, True),
    ("/home/j/ppds/.worktrees/toolbar-search-consistency/src", {}, True),
    # Human override beats .worktrees/ path too
    ("/home/j/ppds/.worktrees/toolbar-search-consistency", {"PPDS_PR_GATE_HUMAN": "1"}, False),
    # Non-agent .claude/worktrees/ (not just agent-* prefix)
    ("/home/j/ppds/.claude/worktrees/review-1234", {}, True),
    # AC-09 (#1067): CLAUDE_PROJECT_DIR contains /.claude/worktrees/ and cwd
    # is the main repo root (foreground worktree, P-3b pattern).
    pytest.param(
        "/home/j/ppds",
        {"CLAUDE_PROJECT_DIR": "/home/j/ppds/.claude/worktrees/session-name"},
        True,
        id="project_dir_claude_worktrees",
    ),
    # AC-10 (#1067): CLAUDE_PROJECT_DIR contains /.worktrees/ and cwd is the
    # main repo root (P-3a/P-3b shared root cause).
    pytest.param(
        "/home/j/ppds",
        {"CLAUDE_PROJECT_DIR": "/home/j/ppds/.worktrees/feat-x"},
        True,
        id="project_dir_worktrees",
    ),
    # AC-11 (#1067): nested bg-worktree path
    # .worktrees/<outer>/worktree-<inner> in CLAUDE_PROJECT_DIR while cwd is
    # the main repo root (P-3a primary scope).
    pytest.param(
        "/home/j/ppds",
        {"CLAUDE_PROJECT_DIR": "/home/j/ppds/.worktrees/feat-x/worktree-sub"},
        True,
        id="nested_bg_worktree",
    ),
    # AC-13 (#1067): PPDS_PR_GATE_HUMAN=1 override beats CLAUDE_PROJECT_DIR
    # worktree signal.
    pytest.param(
        "/home/j/ppds",
        {"CLAUDE_PROJECT_DIR": "/home/j/ppds/.worktrees/feat-x",
         "PPDS_PR_GATE_HUMAN": "1"},
        False,
        id="human_override_beats_project_dir",
    ),
])
def test_is_agent_context(cwd, env, expected):
    assert hook._is_agent_context(cwd=cwd, env=env) is expected


@pytest.mark.parametrize("state,expected", [
    ({}, False),
    ({"pr": {"url": "..."}}, False),
    ({"pr": {"invoked_via_skill": False}}, False),
    ({"pr": {"invoked_via_skill": True}}, True),
    ({"pr": None}, False),
])
def test_has_pr_skill_marker(state, expected):
    assert hook._has_pr_skill_marker(state) is expected


# ---------------------------------------------------------------------------
# End-to-end: the three branches
# ---------------------------------------------------------------------------


class TestAgentBlockedWithoutMarker:
    def test_agent_cwd_no_state_blocks(self, tmp_path):
        agent_dir = tmp_path / ".claude" / "worktrees" / "agent-deadbeef"
        agent_dir.mkdir(parents=True)
        _init_repo(agent_dir)
        r = _run_hook("gh pr create --draft", cwd=agent_dir)
        assert r.returncode == 2, r.stderr
        assert "must go through the `/pr` skill" in r.stderr
        assert "Canonical Entry Point" in r.stderr

    def test_agent_env_with_full_state_but_no_marker_blocks(self, tmp_path):
        _init_repo(tmp_path)
        head = _git(tmp_path, ["rev-parse", "HEAD"]).strip()
        _write_state(tmp_path, _passing_state(head))
        r = _run_hook(
            "gh pr create --draft -t t -b b",
            cwd=tmp_path,
            env_extra={"CLAUDE_CODE_AGENT": "1"},
        )
        assert r.returncode == 2, r.stderr
        assert "must go through the `/pr` skill" in r.stderr

    def test_worktree_cwd_no_state_blocks(self, tmp_path):
        """R-01 regression: project-level .worktrees/ must enforce /pr skill."""
        wt_dir = tmp_path / ".worktrees" / "toolbar-search-consistency"
        wt_dir.mkdir(parents=True)
        _init_repo(wt_dir)
        r = _run_hook("gh pr create --draft", cwd=wt_dir)
        assert r.returncode == 2, r.stderr
        assert "must go through the `/pr` skill" in r.stderr

    def test_worktree_cwd_with_full_state_but_no_marker_blocks(self, tmp_path):
        """R-01 regression: worktree + full state but no /pr marker -> blocked."""
        wt_dir = tmp_path / ".worktrees" / "feature-branch"
        wt_dir.mkdir(parents=True)
        _init_repo(wt_dir)
        head = _git(wt_dir, ["rev-parse", "HEAD"]).strip()
        _write_state(wt_dir, _passing_state(head))
        r = _run_hook("gh pr create --draft -t t -b b", cwd=wt_dir)
        assert r.returncode == 2, r.stderr
        assert "must go through the `/pr` skill" in r.stderr


class TestAgentAllowedWithMarker:
    def test_agent_with_marker_and_full_state_passes(self, tmp_path):
        _init_repo(tmp_path)
        head = _git(tmp_path, ["rev-parse", "HEAD"]).strip()
        state = _passing_state(head)
        state["pr"] = {"invoked_via_skill": True}
        _write_state(tmp_path, state)
        r = _run_hook(
            "gh pr create --draft -t t -b b",
            cwd=tmp_path,
            env_extra={"CLAUDE_CODE_AGENT": "1"},
        )
        assert r.returncode == 0, r.stderr
        assert "must go through the `/pr` skill" not in r.stderr

    def test_worktree_with_marker_and_full_state_passes(self, tmp_path):
        """R-01 regression: worktree + /pr marker + full state -> allowed."""
        wt_dir = tmp_path / ".worktrees" / "feature-branch"
        wt_dir.mkdir(parents=True)
        _init_repo(wt_dir)
        head = _git(wt_dir, ["rev-parse", "HEAD"]).strip()
        state = _passing_state(head)
        state["pr"] = {"invoked_via_skill": True}
        _write_state(wt_dir, state)
        r = _run_hook("gh pr create --draft -t t -b b", cwd=wt_dir)
        assert r.returncode == 0, r.stderr
        assert "must go through the `/pr` skill" not in r.stderr


class TestHumanUnaffected:
    def test_human_no_state_sees_original_message(self, tmp_path):
        _init_repo(tmp_path)
        r = _run_hook("gh pr create --draft", cwd=tmp_path)
        assert r.returncode == 2
        assert "must go through the `/pr` skill" not in r.stderr
        assert "No workflow state found" in r.stderr

    def test_human_full_state_passes(self, tmp_path):
        _init_repo(tmp_path)
        head = _git(tmp_path, ["rev-parse", "HEAD"]).strip()
        _write_state(tmp_path, _passing_state(head))
        r = _run_hook("gh pr create --draft -t t -b b", cwd=tmp_path)
        assert r.returncode == 0, r.stderr

    def test_human_override_from_worktree_passes(self, tmp_path):
        agent_dir = tmp_path / ".claude" / "worktrees" / "agent-1234"
        agent_dir.mkdir(parents=True)
        _init_repo(agent_dir)
        head = _git(agent_dir, ["rev-parse", "HEAD"]).strip()
        _write_state(agent_dir, _passing_state(head))
        r = _run_hook(
            "gh pr create --draft -t t -b b",
            cwd=agent_dir,
            env_extra={"PPDS_PR_GATE_HUMAN": "1"},
        )
        assert r.returncode == 0, r.stderr


class TestForegroundWorktreeViaProjectDir:
    """AC-12, AC-13 (#1067): hook subprocess CWD = main repo root,
    CLAUDE_PROJECT_DIR = worktree. Without the CLAUDE_PROJECT_DIR check,
    _is_agent_context() returns False and a foreground worktree session can
    bypass `/pr` via raw `gh pr create`.
    """

    def test_foreground_worktree_via_project_dir_blocked(self, tmp_path):
        """AC-12: CWD=main-root + CLAUDE_PROJECT_DIR=worktree + no /pr marker
        → blocked with the agent-bypass message."""
        main_path = tmp_path / "main"
        main_path.mkdir()
        _init_repo(main_path)
        worktree = main_path / ".claude" / "worktrees" / "session-name"
        worktree.mkdir(parents=True)

        env = _clean_env()
        env["CLAUDE_PROJECT_DIR"] = str(worktree)
        payload = {"tool_name": "Bash",
                   "tool_input": {"command": "gh pr create --draft"}}
        r = _run([sys.executable, str(HOOK_PATH)], cwd=str(main_path),
                 env=env, input_str=json.dumps(payload))
        assert r.returncode == 2, r.stderr
        assert "must go through the `/pr` skill" in r.stderr, r.stderr

    def test_human_override_beats_project_dir(self, tmp_path):
        """AC-13: PPDS_PR_GATE_HUMAN=1 forces human context even when
        CLAUDE_PROJECT_DIR is a worktree path. The block message switches
        to the generic 'No workflow state found' rather than agent bypass."""
        main_path = tmp_path / "main"
        main_path.mkdir()
        _init_repo(main_path)
        worktree = main_path / ".claude" / "worktrees" / "session-name"
        worktree.mkdir(parents=True)

        env = _clean_env()
        env["CLAUDE_PROJECT_DIR"] = str(worktree)
        env["PPDS_PR_GATE_HUMAN"] = "1"
        payload = {"tool_name": "Bash",
                   "tool_input": {"command": "gh pr create --draft"}}
        r = _run([sys.executable, str(HOOK_PATH)], cwd=str(main_path),
                 env=env, input_str=json.dumps(payload))
        assert r.returncode == 2, r.stderr
        assert "must go through the `/pr` skill" not in r.stderr, r.stderr
        assert "No workflow state found" in r.stderr, r.stderr


def test_non_pr_create_from_agent_passes(tmp_path):
    # Non-``gh pr create`` commands must fall through even from agent cwd.
    agent_dir = tmp_path / ".claude" / "worktrees" / "agent-abcd"
    agent_dir.mkdir(parents=True)
    assert _run_hook("gh pr list", cwd=agent_dir).returncode == 0
    assert _run_hook("echo hi", cwd=agent_dir).returncode == 0
