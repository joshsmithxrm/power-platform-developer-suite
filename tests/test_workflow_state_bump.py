"""Behavioral tests for `workflow-state.py bump <key>` (AC-08, AC-09, AC-10).

Follows test_start_skill_fixes.py conventions: subprocess.run via _run(),
CLAUDE_PROJECT_DIR pointed at tmp_path so the script never touches the real
.workflow/state.json.
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = str(REPO_ROOT / "scripts" / "workflow-state.py")


def _run(args, *, cwd=None, env=None):
    """Invoke workflow-state.py with DEVNULL stdin (Windows OSError guard)."""
    return subprocess.run(
        [sys.executable, SCRIPT] + args,
        capture_output=True,
        text=True,
        stdin=subprocess.DEVNULL,
        cwd=cwd,
        env=env,
    )


def _make_env(tmp_path: Path) -> dict:
    """Return an env dict that points CLAUDE_PROJECT_DIR at tmp_path."""
    import os
    env = os.environ.copy()
    env["CLAUDE_PROJECT_DIR"] = str(tmp_path)
    env["GIT_DIR"] = "disabled"  # ensure _is_main_branch() → False (no git)
    return env


def _state_file(tmp_path: Path) -> Path:
    return tmp_path / ".workflow" / "state.json"


def _read_state(tmp_path: Path) -> dict:
    f = _state_file(tmp_path)
    return json.loads(f.read_text(encoding="utf-8")) if f.exists() else {}


def _write_state(tmp_path: Path, data: dict) -> None:
    wf = tmp_path / ".workflow"
    wf.mkdir(exist_ok=True)
    (wf / "state.json").write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


# ---------------------------------------------------------------------------
# AC-08: initializes to 1 on first bump, increments on subsequent bumps
# ---------------------------------------------------------------------------

def test_workflow_state_bump_initializes_to_one(tmp_path):
    env = _make_env(tmp_path)
    result = _run(["bump", "foo"], env=env)
    assert result.returncode == 0, result.stderr
    state = _read_state(tmp_path)
    assert state["foo"] == 1


def test_workflow_state_bump_increments(tmp_path):
    env = _make_env(tmp_path)
    _run(["bump", "foo"], env=env)
    result = _run(["bump", "foo"], env=env)
    assert result.returncode == 0, result.stderr
    state = _read_state(tmp_path)
    assert state["foo"] == 2


def test_workflow_state_bump_nested_path(tmp_path):
    """Nested dotted key initializes the full path on first bump."""
    env = _make_env(tmp_path)
    result = _run(["bump", "routing_gates.backlog.fired_count"], env=env)
    assert result.returncode == 0, result.stderr
    state = _read_state(tmp_path)
    assert state["routing_gates"]["backlog"]["fired_count"] == 1


# ---------------------------------------------------------------------------
# AC-09: non-integer value at key → non-zero exit + stderr "non-integer"
# ---------------------------------------------------------------------------

def test_workflow_state_bump_rejects_non_integer(tmp_path):
    env = _make_env(tmp_path)
    _write_state(tmp_path, {"foo": "hello"})
    result = _run(["bump", "foo"], env=env)
    assert result.returncode != 0
    assert "non-integer" in result.stderr
    # State unchanged
    state = _read_state(tmp_path)
    assert state["foo"] == "hello"


# ---------------------------------------------------------------------------
# AC-10: invalid key pattern → non-zero exit + stderr "invalid key"
# ---------------------------------------------------------------------------

def test_workflow_state_bump_validates_key(tmp_path):
    env = _make_env(tmp_path)
    result = _run(["bump", "foo bar"], env=env)
    assert result.returncode != 0
    assert "invalid key" in result.stderr
    # State file must not be created or modified
    assert not _state_file(tmp_path).exists()
