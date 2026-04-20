"""Tests for pre-commit-validate gates-dedup (Finding #27b).

Skips build+test when ``.workflow/state.json`` shows
``gates.commit_ref == HEAD`` — i.e. /gates already validated this commit.
Falls back to full validation on any error (missing/corrupt state, git
unavailable) so regressions are never silently masked.

Run: ``pytest tests/hooks/test_precommit_gates_dedup.py -v``
"""
from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path
from unittest import mock

import pytest


HOOK_PATH = Path(__file__).resolve().parents[2] / ".claude" / "hooks" / "pre-commit-validate.py"


def _load_hook():
    hooks_dir = str(HOOK_PATH.parent)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec = importlib.util.spec_from_file_location("pre_commit_validate", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


def _write_state(tmp_path, state):
    (tmp_path / ".workflow").mkdir(exist_ok=True)
    (tmp_path / ".workflow" / "state.json").write_text(json.dumps(state), encoding="utf-8")


def _mock_git(sha, returncode=0):
    rv = mock.Mock()
    rv.returncode = returncode
    rv.stdout = (sha + "\n") if sha else ""
    rv.stderr = ""
    return rv


class TestGatesFresh:
    """Skip path: gates.commit_ref == HEAD."""

    def test_matches_head_returns_true(self, tmp_path):
        _write_state(tmp_path, {"gates": {"passed": "now", "commit_ref": "deadbeef"}})
        with mock.patch.object(hook.subprocess, "run", return_value=_mock_git("deadbeef")):
            assert hook._gates_fresh(str(tmp_path)) is True

    @pytest.mark.parametrize("state", [
        {},                                                      # no state
        {"branch": "foo"},                                       # no gates key
        {"gates": {"passed": None, "commit_ref": "abc"}},        # gates not passed
        {"gates": {"passed": "now"}},                            # no commit_ref
    ])
    def test_returns_false_when_preconditions_missing(self, tmp_path, state):
        _write_state(tmp_path, state)
        assert hook._gates_fresh(str(tmp_path)) is False

    def test_stale_ref_returns_false(self, tmp_path):
        # gates ran against older HEAD — must fall through to full validation.
        _write_state(tmp_path, {"gates": {"passed": "now", "commit_ref": "old111"}})
        with mock.patch.object(hook.subprocess, "run", return_value=_mock_git("new222")):
            assert hook._gates_fresh(str(tmp_path)) is False

    def test_staged_changes_returns_false(self, tmp_path):
        # gates ran against HEAD, but new changes have been staged since —
        # those changes are un-validated, so skip-path must not fire.
        # Regression guard for Gemini #3107805851 (PR #841).
        _write_state(tmp_path, {"gates": {"passed": "now", "commit_ref": "deadbeef"}})
        # rev-parse HEAD → deadbeef (match); diff --cached --quiet HEAD → rc=1 (staged diff)
        calls = [_mock_git("deadbeef"), _mock_git("", returncode=1)]
        with mock.patch.object(hook.subprocess, "run", side_effect=calls):
            assert hook._gates_fresh(str(tmp_path)) is False

    def test_no_staged_changes_returns_true(self, tmp_path):
        # gates match AND index is clean relative to HEAD — skip fires.
        _write_state(tmp_path, {"gates": {"passed": "now", "commit_ref": "deadbeef"}})
        calls = [_mock_git("deadbeef"), _mock_git("", returncode=0)]
        with mock.patch.object(hook.subprocess, "run", side_effect=calls):
            assert hook._gates_fresh(str(tmp_path)) is True

    def test_staged_diff_git_error_returns_false(self, tmp_path):
        # If the index-drift check itself errors (rc != 0, != 1), be safe
        # and fall through rather than assume clean.
        _write_state(tmp_path, {"gates": {"passed": "now", "commit_ref": "deadbeef"}})
        calls = [_mock_git("deadbeef"), _mock_git("", returncode=128)]
        with mock.patch.object(hook.subprocess, "run", side_effect=calls):
            assert hook._gates_fresh(str(tmp_path)) is False

    def test_staged_diff_git_unavailable_returns_false(self, tmp_path):
        # rev-parse succeeds, diff subprocess raises → fall through.
        _write_state(tmp_path, {"gates": {"passed": "now", "commit_ref": "deadbeef"}})
        calls = [_mock_git("deadbeef"), FileNotFoundError()]
        with mock.patch.object(hook.subprocess, "run", side_effect=calls):
            assert hook._gates_fresh(str(tmp_path)) is False


class TestFailSafety:
    """On any error, return False so full validation still runs."""

    def test_no_state_file(self, tmp_path):
        assert hook._gates_fresh(str(tmp_path)) is False

    def test_corrupt_state(self, tmp_path):
        (tmp_path / ".workflow").mkdir()
        (tmp_path / ".workflow" / "state.json").write_text("{not json", encoding="utf-8")
        assert hook._gates_fresh(str(tmp_path)) is False

    @pytest.mark.parametrize("exc", [FileNotFoundError(), __import__("subprocess").TimeoutExpired("git", 10)])
    def test_git_unavailable(self, tmp_path, exc):
        _write_state(tmp_path, {"gates": {"passed": "now", "commit_ref": "deadbeef"}})
        with mock.patch.object(hook.subprocess, "run", side_effect=exc):
            assert hook._gates_fresh(str(tmp_path)) is False

    def test_git_nonzero(self, tmp_path):
        _write_state(tmp_path, {"gates": {"passed": "now", "commit_ref": "deadbeef"}})
        with mock.patch.object(hook.subprocess, "run", return_value=_mock_git("", returncode=1)):
            assert hook._gates_fresh(str(tmp_path)) is False
