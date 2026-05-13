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
    """Return an env dict that isolates the script from the real git repo."""
    import os
    env = os.environ.copy()
    env["CLAUDE_PROJECT_DIR"] = str(tmp_path)
    # WORKFLOW_STATE_TEST_SKIP_GIT=1 makes _is_main_branch() return False and
    # _get_worktree_root() fall through to CLAUDE_PROJECT_DIR without
    # calling git — a documented, controlled isolation mechanism.
    env["WORKFLOW_STATE_TEST_SKIP_GIT"] = "1"
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


def test_workflow_state_bump_rejects_non_dict_intermediate(tmp_path):
    """Bumping `a.b` when `a` is a non-dict must fail without mutating state."""
    env = _make_env(tmp_path)
    _write_state(tmp_path, {"a": "scalar"})
    result = _run(["bump", "a.b"], env=env)
    assert result.returncode != 0
    # Existing non-dict value at intermediate path must be preserved.
    state = _read_state(tmp_path)
    assert state == {"a": "scalar"}


# ---------------------------------------------------------------------------
# AC-09: non-integer value at key → non-zero exit + stderr "non-integer"
# Spec says "string, list, or dict"; bool is also rejected because it is a
# subclass of int — bumping True would silently turn it into the integer 2.
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("bad_value,label", [
    ("hello", "string"),
    ([1, 2], "list"),
    ({"nested": 1}, "dict"),
    (True, "bool"),
])
def test_workflow_state_bump_rejects_non_integer(tmp_path, bad_value, label):
    env = _make_env(tmp_path)
    _write_state(tmp_path, {"foo": bad_value})
    result = _run(["bump", "foo"], env=env)
    assert result.returncode != 0, f"Expected non-zero exit for {label} value"
    assert "non-integer" in result.stderr, f"Expected 'non-integer' in stderr for {label} value"
    # State unchanged
    state = _read_state(tmp_path)
    assert state["foo"] == bad_value, f"State must be unchanged for {label} value"


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


def test_workflow_state_bump_requires_key_argument(tmp_path):
    """`bump` with no key prints usage to stderr and exits non-zero."""
    env = _make_env(tmp_path)
    result = _run(["bump"], env=env)
    assert result.returncode != 0
    assert "bump <key>" in result.stderr
    assert not _state_file(tmp_path).exists()
