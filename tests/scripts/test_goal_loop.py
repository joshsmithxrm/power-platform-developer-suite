"""Unit tests for scripts/goal_loop.py — ACs 01-16 from specs/goal-driven-implement.md."""
from __future__ import annotations

import dataclasses
import re
import subprocess
import sys
from pathlib import Path

import pytest

# tests/conftest.py prepends scripts/ to sys.path; reach the module by name.
import goal_loop
from goal_loop import (
    Goal,
    GoalLoopOutcome,
    GoalLoopResult,
    LastVerificationResult,
    read_goal_from_spec,
    run_until_green,
)
from claude_dispatch import BlockedSessionError


def _make_run(returncode: int, stdout: str = "", stderr: str = ""):
    def fake_run(cmd, **kw):
        return subprocess.CompletedProcess(cmd, returncode, stdout, stderr)
    return fake_run


def _make_sequence(*results):
    """Return a fake subprocess.run that yields the supplied (rc, out, err) tuples in order."""
    iterator = iter(results)
    def fake_run(cmd, **kw):
        rc, out, err = next(iterator)
        return subprocess.CompletedProcess(cmd, rc, out, err)
    return fake_run


# ---------- AC-01: extract verification_command ----------


def test_read_goal_from_spec_extracts_verification_command(tmp_path):
    spec = tmp_path / "spec.md"
    spec.write_text(
        "# Sample\n\n"
        "**Status:** Draft\n"
        "**Verification:** `pytest tests/foo.py -q`\n\n"
        "---\n\n"
        "## Overview\n",
        encoding="utf-8",
    )
    goal = read_goal_from_spec(spec)
    assert goal.verification_command == "pytest tests/foo.py -q"


# ---------- AC-02: absent verification line returns None (backward compatible) ----------


def test_read_goal_from_spec_returns_none_when_missing(tmp_path):
    spec = tmp_path / "spec.md"
    spec.write_text(
        "# Sample\n\n**Status:** Draft\n\n---\n\nbody without verification\n",
        encoding="utf-8",
    )
    goal = read_goal_from_spec(spec)
    assert goal.verification_command is None
    assert goal.max_iterations == 10


# ---------- AC-03: max_iterations default + override ----------


def test_read_goal_from_spec_max_iterations_default_and_override(tmp_path):
    default_spec = tmp_path / "default.md"
    default_spec.write_text(
        "**Verification:** `pytest`\n\n---\n", encoding="utf-8"
    )
    assert read_goal_from_spec(default_spec).max_iterations == 10

    override_spec = tmp_path / "override.md"
    override_spec.write_text(
        "**Verification:** `pytest`\n**Verification Max Iterations:** 4\n\n---\n",
        encoding="utf-8",
    )
    assert read_goal_from_spec(override_spec).max_iterations == 4


# ---------- AC-04: GREEN on first pass ----------


def test_run_until_green_returns_green_on_first_pass(monkeypatch):
    captured: list[str] = []
    def fake_run(cmd, **kw):
        captured.append(cmd)
        return subprocess.CompletedProcess(cmd, 0, "ok\n", "")
    monkeypatch.setattr(goal_loop.subprocess, "run", fake_run)
    fix_calls: list = []
    goal = Goal(verification_command="pytest -q", max_iterations=10)

    result = run_until_green(goal, attempt_fix=lambda r: fix_calls.append(r))

    assert result.outcome is GoalLoopOutcome.GREEN
    assert result.iterations == 1
    assert result.last_exit_code == 0
    assert captured == ["pytest -q"]
    assert fix_calls == []


# ---------- AC-05: fix-then-reverify ----------


def test_run_until_green_invokes_fix_then_reverifies(monkeypatch):
    monkeypatch.setattr(
        goal_loop.subprocess,
        "run",
        _make_sequence((1, "fail1\n", ""), (0, "ok\n", "")),
    )
    fix_inputs: list[LastVerificationResult] = []
    def fix(r):
        fix_inputs.append(r)

    result = run_until_green(
        Goal(verification_command="pytest", max_iterations=5),
        attempt_fix=fix,
    )

    assert result.outcome is GoalLoopOutcome.GREEN
    assert result.iterations == 2
    assert len(fix_inputs) == 1
    assert fix_inputs[0].iteration == 1
    assert fix_inputs[0].exit_code == 1
    assert fix_inputs[0].stdout == "fail1\n"


# ---------- AC-06: ITERATION_CAP ----------


def test_run_until_green_returns_iteration_cap(monkeypatch):
    # Each iteration emits a different stdout so STUCK_OUTPUT cannot trip.
    counter = {"n": 0}
    def fake_run(cmd, **kw):
        counter["n"] += 1
        return subprocess.CompletedProcess(cmd, 1, f"different-{counter['n']}\n", "")
    monkeypatch.setattr(goal_loop.subprocess, "run", fake_run)
    fix_calls: list = []

    result = run_until_green(
        Goal(verification_command="pytest", max_iterations=4),
        attempt_fix=lambda r: fix_calls.append(r),
    )

    assert result.outcome is GoalLoopOutcome.ITERATION_CAP
    assert result.iterations == 4
    # fix invoked between each pair of verifications: cap - 1 = 3 calls.
    assert len(fix_calls) == 3


# ---------- AC-07: STUCK_OUTPUT on 3 identical non-zero hashes ----------


def test_run_until_green_returns_stuck_output_on_three_identical_hashes(monkeypatch):
    monkeypatch.setattr(
        goal_loop.subprocess,
        "run",
        _make_run(1, "same error\n", ""),
    )
    fix_calls: list = []

    result = run_until_green(
        Goal(verification_command="pytest", max_iterations=10),
        attempt_fix=lambda r: fix_calls.append(r),
    )

    assert result.outcome is GoalLoopOutcome.STUCK_OUTPUT
    assert result.iterations == 3
    assert result.stuck_hash is not None and len(result.stuck_hash) == 64
    # fix attempted after iter1 and iter2; iter3 trips stuck before calling fix.
    assert len(fix_calls) == 2


# ---------- AC-08: STUCK_OUTPUT does not trip when output changes ----------


def test_stuck_output_does_not_trip_when_output_changes(monkeypatch):
    monkeypatch.setattr(
        goal_loop.subprocess,
        "run",
        _make_sequence(
            (1, "fail A\n", ""),
            (1, "fail B\n", ""),
            (1, "fail C\n", ""),
            (0, "ok\n", ""),
        ),
    )
    fix_calls: list = []

    result = run_until_green(
        Goal(verification_command="pytest", max_iterations=10),
        attempt_fix=lambda r: fix_calls.append(r),
    )

    assert result.outcome is GoalLoopOutcome.GREEN
    assert result.iterations == 4
    assert len(fix_calls) == 3


# ---------- AC-09: BLOCKED_HARD propagates needs ----------


def test_run_until_green_blocked_hard_with_needs(monkeypatch):
    monkeypatch.setattr(goal_loop.subprocess, "run", _make_run(1, "fail\n", ""))
    def fix(r):
        raise BlockedSessionError(short="abcd1234", needs="confirm whether table X is read-only")

    result = run_until_green(
        Goal(verification_command="pytest", max_iterations=10),
        attempt_fix=fix,
    )

    assert result.outcome is GoalLoopOutcome.BLOCKED_HARD
    assert result.blocked_needs == "confirm whether table X is read-only"
    assert result.iterations == 1


# ---------- AC-10: empty-needs blocked is tolerated (PR #1051 d1bfe877 mirror) ----------


def test_run_until_green_tolerates_empty_needs_blocked(monkeypatch):
    monkeypatch.setattr(
        goal_loop.subprocess,
        "run",
        _make_sequence((1, "fail\n", ""), (0, "ok\n", "")),
    )
    raised = {"once": False}
    def fix(r):
        if not raised["once"]:
            raised["once"] = True
            raise BlockedSessionError(short="abcd1234", needs="")
        # second call should not happen because iter2 will go GREEN

    result = run_until_green(
        Goal(verification_command="pytest", max_iterations=5),
        attempt_fix=fix,
    )

    assert result.outcome is GoalLoopOutcome.GREEN
    assert result.iterations == 2
    assert raised["once"] is True


# ---------- AC-11: FIX_ERROR for arbitrary exceptions ----------


def test_run_until_green_returns_fix_error(monkeypatch):
    monkeypatch.setattr(goal_loop.subprocess, "run", _make_run(1, "fail\n", ""))
    sentinel = RuntimeError("kaboom")
    def fix(r):
        raise sentinel

    result = run_until_green(
        Goal(verification_command="pytest", max_iterations=10),
        attempt_fix=fix,
    )

    assert result.outcome is GoalLoopOutcome.FIX_ERROR
    assert result.error is sentinel


# ---------- AC-12: Goal is frozen ----------


def test_goal_dataclass_is_frozen():
    goal = Goal(verification_command="pytest", max_iterations=10)
    with pytest.raises(dataclasses.FrozenInstanceError):
        goal.verification_command = "rm -rf /"  # type: ignore[misc]


# ---------- AC-13: stderr progress lines ----------


def test_run_until_green_emits_stderr_progress_lines(monkeypatch, capsys):
    monkeypatch.setattr(
        goal_loop.subprocess,
        "run",
        _make_sequence((1, "x\n", ""), (0, "ok\n", "")),
    )

    run_until_green(
        Goal(verification_command="pytest", max_iterations=5),
        attempt_fix=lambda r: None,
    )
    captured = capsys.readouterr()

    pattern = re.compile(r"^goal-loop iter=\d+/5 exit=-?\d+ hash=[0-9a-f]{8}$")
    lines = [ln for ln in captured.err.splitlines() if ln.startswith("goal-loop")]
    assert len(lines) == 2
    for line in lines:
        assert pattern.match(line), f"line did not match expected format: {line!r}"


# ---------- AC-14: ValueError for non-positive max_iterations in frontmatter ----------


def test_read_goal_from_spec_rejects_nonpositive_max_iterations(tmp_path):
    spec = tmp_path / "spec.md"
    spec.write_text(
        "**Verification:** `pytest`\n**Verification Max Iterations:** 0\n\n---\n",
        encoding="utf-8",
    )
    with pytest.raises(ValueError, match="verification_max_iterations must be >= 1"):
        read_goal_from_spec(spec)

    spec.write_text(
        "**Verification:** `pytest`\n**Verification Max Iterations:** -3\n\n---\n",
        encoding="utf-8",
    )
    with pytest.raises(ValueError, match="verification_max_iterations must be >= 1"):
        read_goal_from_spec(spec)


# ---------- AC-15: run_until_green rejects invalid max_iterations ----------


def test_run_until_green_rejects_invalid_max_iterations():
    goal = Goal(verification_command="pytest", max_iterations=10)
    with pytest.raises(ValueError, match="max_iterations must be >= 1"):
        run_until_green(goal, attempt_fix=lambda r: None, max_iterations=0)
    with pytest.raises(ValueError, match="max_iterations must be >= 1"):
        run_until_green(goal, attempt_fix=lambda r: None, max_iterations=-1)


# ---------- AC-16: SKILL.md documents the goal-loop steps ----------


def test_implement_skill_documents_goal_loop_steps():
    skill_path = (
        Path(__file__).resolve().parents[2]
        / ".claude"
        / "skills"
        / "implement"
        / "SKILL.md"
    )
    text = skill_path.read_text(encoding="utf-8")
    assert "Step 5.G" in text, "SKILL.md must document the per-phase Step 5.G goal-verify"
    assert "Step 5.5" in text, "SKILL.md must document the pre-tail Step 5.5 sweep"
    assert "scripts/goal_loop.py" in text, "SKILL.md must reference scripts/goal_loop.py by path"


# ---------- Bonus coverage: empty verification command rejected at parse time ----------


def test_read_goal_from_spec_rejects_empty_verification_command(tmp_path):
    spec = tmp_path / "spec.md"
    spec.write_text("**Verification:** ``\n\n---\n", encoding="utf-8")
    with pytest.raises(ValueError, match="verification_command must be non-empty"):
        read_goal_from_spec(spec)


# ---------- Bonus coverage: only frontmatter is scanned ----------


def test_read_goal_from_spec_ignores_verification_lines_after_separator(tmp_path):
    spec = tmp_path / "spec.md"
    spec.write_text(
        "# Sample\n\n**Status:** Draft\n\n---\n\n"
        "## Example\n\n"
        "**Verification:** `should-not-be-picked-up`\n",
        encoding="utf-8",
    )
    goal = read_goal_from_spec(spec)
    assert goal.verification_command is None


# ---------- Bonus coverage: non-callable attempt_fix rejected ----------


def test_run_until_green_rejects_non_callable_attempt_fix():
    goal = Goal(verification_command="pytest", max_iterations=10)
    with pytest.raises(TypeError, match="attempt_fix must be callable"):
        run_until_green(goal, attempt_fix=42)  # type: ignore[arg-type]
