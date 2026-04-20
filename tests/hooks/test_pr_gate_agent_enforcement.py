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


def test_non_pr_create_from_agent_passes(tmp_path):
    # Non-``gh pr create`` commands must fall through even from agent cwd.
    agent_dir = tmp_path / ".claude" / "worktrees" / "agent-abcd"
    agent_dir.mkdir(parents=True)
    assert _run_hook("gh pr list", cwd=agent_dir).returncode == 0
    assert _run_hook("echo hi", cwd=agent_dir).returncode == 0
