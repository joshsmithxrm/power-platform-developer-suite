"""Smoke test for #1137 — /await-operator + /resume-with-answer round-trip.

Drives the helper scripts directly (no real bg session spawn — that would
require a Claude daemon in CI). Verifies the four state transitions the
issue calls out in AC-6:

  1. pause artifact written
  2. daemon state.json flipped to state=blocked with needs populated
  3. resume answer written
  4. daemon state cleared (state=working, needs="")

The skill SKILL.md files are also sanity-checked for the required
contract phrasing (frontmatter args, "EXIT THE TURN" directive, schema
sections).
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest


REPO_ROOT = Path(__file__).resolve().parent.parent
AWAIT_SCRIPT = REPO_ROOT / "scripts" / "await_operator.py"
RESUME_SCRIPT = REPO_ROOT / "scripts" / "resume_with_answer.py"
AWAIT_SKILL = REPO_ROOT / ".claude" / "skills" / "await-operator" / "SKILL.md"
RESUME_SKILL = REPO_ROOT / ".claude" / "skills" / "resume-with-answer" / "SKILL.md"
HOOK = REPO_ROOT / ".claude" / "hooks" / "preuse-askuserquestion.py"


# ---------------------------------------------------------------------------
# Helper-script round-trip
# ---------------------------------------------------------------------------

def _make_fake_session(tmp_path: Path, short: str = "abcd1234") -> tuple[Path, Path]:
    """Build a fake job-dir + worktree pair mimicking a live bg session."""
    worktree = tmp_path / "worktree"
    worktree.mkdir()
    # Need a git repo for await_operator's worktree root detection. Fall back
    # to CLAUDE_PROJECT_DIR when git is unavailable; we point CPD at worktree.
    (worktree / ".workflow").mkdir()

    job_dir = tmp_path / "jobs" / short
    job_dir.mkdir(parents=True)
    state = {
        "state": "working",
        "detail": "running…",
        "tempo": "active",
        "cwd": str(worktree),
        "daemonShort": short,
        "createdAt": "2026-05-16T00:00:00.000Z",
    }
    (job_dir / "state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
    return worktree, job_dir


def _run_await(worktree: Path, job_dir: Path, *, artifact: str,
               question: str, options: list[str]) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    env["CLAUDE_JOB_DIR"] = str(job_dir)
    env["CLAUDE_PROJECT_DIR"] = str(worktree)
    cmd = [sys.executable, str(AWAIT_SCRIPT),
           "--artifact-path", artifact,
           "--question", question]
    for opt in options:
        cmd.extend(["--option", opt])
    return subprocess.run(cmd, cwd=str(worktree), env=env,
                          stdin=subprocess.DEVNULL,
                          capture_output=True, text=True, timeout=15)


def _run_resume(short: str, choice: str, *, worktree: Path,
                fake_home: Path) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    # resume_with_answer resolves ~/.claude/jobs/<short>/state.json.
    # Point USERPROFILE / HOME at our fake home so the lookup hits our fixture.
    env["USERPROFILE"] = str(fake_home)
    env["HOME"] = str(fake_home)
    cmd = [sys.executable, str(RESUME_SCRIPT),
           short, choice,
           "--worktree", str(worktree),
           "--operator", "smoke-test"]
    return subprocess.run(cmd, env=env, stdin=subprocess.DEVNULL,
                          capture_output=True, text=True, timeout=15)


def test_pause_then_resume_round_trip(tmp_path: Path) -> None:
    short = "abcd1234"
    worktree, job_dir = _make_fake_session(tmp_path, short=short)

    # ----- Phase 1: pause -----
    proc = _run_await(worktree, job_dir,
                      artifact=".plans/draft.md",
                      question="Approve the draft?",
                      options=["Approve", "Reject", "Revise"])
    assert proc.returncode == 0, f"await_operator failed: {proc.stderr}"

    pending = worktree / ".workflow" / "pending-ratification.json"
    assert pending.exists(), "pending-ratification.json was not written"
    payload = json.loads(pending.read_text(encoding="utf-8"))
    assert payload["question"] == "Approve the draft?"
    assert payload["artifact_path"] == ".plans/draft.md"
    assert payload["options"] == ["Approve", "Reject", "Revise"]
    assert payload["session_short"] == short
    assert "created_at" in payload

    # ----- Phase 2: state flip -----
    state = json.loads((job_dir / "state.json").read_text(encoding="utf-8"))
    assert state["state"] == "blocked"
    assert "review .workflow/pending-ratification.json" in state["needs"]
    assert state["cwd"] == str(worktree), "cwd field must be preserved"

    # ----- Phase 3: idempotency — second pause overwrites cleanly -----
    proc2 = _run_await(worktree, job_dir,
                       artifact=".plans/draft.md",
                       question="Approve the draft v2?",
                       options=["Yes", "No"])
    assert proc2.returncode == 0
    payload2 = json.loads(pending.read_text(encoding="utf-8"))
    assert payload2["question"] == "Approve the draft v2?"
    assert payload2["options"] == ["Yes", "No"]

    # ----- Phase 4: resume -----
    # resume_with_answer expects ~/.claude/jobs/<short>/state.json.
    # Build a fake home that satisfies that layout, mirroring our job_dir.
    fake_home = tmp_path / "fakehome"
    claude_jobs = fake_home / ".claude" / "jobs" / short
    claude_jobs.mkdir(parents=True)
    (claude_jobs / "state.json").write_text(
        (job_dir / "state.json").read_text(encoding="utf-8"), encoding="utf-8")

    proc3 = _run_resume(short, "Approve",
                        worktree=worktree,
                        fake_home=fake_home)
    assert proc3.returncode == 0, f"resume_with_answer failed: {proc3.stderr}"

    answer_path = worktree / ".workflow" / "operator-answer.json"
    assert answer_path.exists(), "operator-answer.json was not written"
    answer = json.loads(answer_path.read_text(encoding="utf-8"))
    assert answer["choice"] == "Approve"
    assert answer["operator"] == "smoke-test"
    assert "answered_at" in answer

    cleared = json.loads(
        (claude_jobs / "state.json").read_text(encoding="utf-8"))
    assert cleared["state"] == "working"
    assert cleared["needs"] == ""


def test_await_operator_refuses_without_job_dir(tmp_path: Path) -> None:
    """The skill must not pretend to pause if CLAUDE_JOB_DIR is unset
    (interactive session → wrong primitive)."""
    env = os.environ.copy()
    env.pop("CLAUDE_JOB_DIR", None)
    proc = subprocess.run(
        [sys.executable, str(AWAIT_SCRIPT),
         "--artifact-path", "x.md", "--question", "y?"],
        cwd=str(tmp_path), env=env,
        stdin=subprocess.DEVNULL,
        capture_output=True, text=True, timeout=10,
    )
    assert proc.returncode == 1
    assert "CLAUDE_JOB_DIR" in proc.stderr


# ---------------------------------------------------------------------------
# Static contract checks on the SKILL.md files
# ---------------------------------------------------------------------------

def test_await_skill_frontmatter_and_contract() -> None:
    content = AWAIT_SKILL.read_text(encoding="utf-8")
    # AC-1: frontmatter present with name
    assert content.startswith("---"), "SKILL.md must start with frontmatter"
    assert "name: await-operator" in content
    # AC-1: documents the three args
    for arg in ("artifact_path", "question", "options"):
        assert arg in content, f"SKILL.md must document {arg!r} arg"
    # AC-4: EXIT THE TURN directive
    assert "EXIT THE TURN" in content or "exit the turn" in content.lower()
    # AC-5: documents the resume mechanism
    assert "/resume-with-answer" in content
    # AC-2: documents the schema
    assert "pending-ratification.json" in content
    # Schema fields
    for key in ("question", "artifact_path", "options", "created_at", "session_short"):
        assert key in content


def test_resume_skill_frontmatter_and_contract() -> None:
    content = RESUME_SKILL.read_text(encoding="utf-8")
    assert content.startswith("---")
    assert "name: resume-with-answer" in content
    assert "operator-answer.json" in content
    # Documents both halves of the round-trip
    assert "/await-operator" in content
    for key in ("choice", "answered_at", "operator"):
        assert key in content


def test_hook_blocks_only_in_bg_sessions() -> None:
    """Static smoke: hook references CLAUDE_JOB_DIR and tool_name guard."""
    content = HOOK.read_text(encoding="utf-8")
    assert "CLAUDE_JOB_DIR" in content
    assert "AskUserQuestion" in content
    assert "/await-operator" in content


def test_hook_pretooluse_behavior(tmp_path: Path) -> None:
    """Run the hook directly with a fake PreToolUse payload."""
    payload = {"tool_name": "AskUserQuestion",
               "tool_input": {"questions": []}}

    # In a bg session (CLAUDE_JOB_DIR set), should block (exit 2).
    env = os.environ.copy()
    env["CLAUDE_JOB_DIR"] = str(tmp_path)
    proc = subprocess.run(
        [sys.executable, str(HOOK)],
        input=json.dumps(payload), env=env,
        capture_output=True, text=True, timeout=10,
    )
    assert proc.returncode == 2
    assert "/await-operator" in proc.stderr

    # In an interactive session (CLAUDE_JOB_DIR unset), should allow (exit 0).
    env_int = os.environ.copy()
    env_int.pop("CLAUDE_JOB_DIR", None)
    proc2 = subprocess.run(
        [sys.executable, str(HOOK)],
        input=json.dumps(payload), env=env_int,
        capture_output=True, text=True, timeout=10,
    )
    assert proc2.returncode == 0

    # Wrong tool_name in bg session → still allow.
    env_other = os.environ.copy()
    env_other["CLAUDE_JOB_DIR"] = str(tmp_path)
    proc3 = subprocess.run(
        [sys.executable, str(HOOK)],
        input=json.dumps({"tool_name": "Bash"}), env=env_other,
        capture_output=True, text=True, timeout=10,
    )
    assert proc3.returncode == 0
