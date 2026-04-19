#!/usr/bin/env python3
"""Tests for in-flight cross-session state coordination.

Covers:
  * register / deregister / check happy paths
  * stale-entry pruning (24h + branch gone)
  * concurrent register/deregister via ThreadPoolExecutor
  * the retro B3 three-session scenario that motivated the feature
"""
from __future__ import annotations

import concurrent.futures
import json
import os
import sys
import threading
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts"))


@pytest.fixture
def tmp_state(tmp_path, monkeypatch):
    """Redirect state_path() to a temp file for isolated tests."""
    import inflight_common as ic

    state_file = tmp_path / "in-flight.json"
    monkeypatch.setattr(ic, "state_path", lambda: state_file)
    yield state_file


@pytest.fixture
def register_module():
    return _import_script("inflight-register")


@pytest.fixture
def deregister_module():
    return _import_script("inflight-deregister")


@pytest.fixture
def check_module():
    return _import_script("inflight-check")


def _import_script(name: str):
    """Import a script with a hyphen in its filename via importlib spec."""
    import importlib.util

    path = REPO_ROOT / "scripts" / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), path)
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = mod
    spec.loader.exec_module(mod)
    return mod


# ---------------------------------------------------------------------------
# register
# ---------------------------------------------------------------------------


class TestRegister:
    def test_creates_new_entry(self, tmp_state, register_module):
        args = register_module.parse_args([
            "--branch", "feat/foo",
            "--worktree", ".worktrees/foo",
            "--issue", "101",
            "--issue", "102",
            "--area", "src/A/,src/B/",
            "--intent", "test work",
        ])
        entry = register_module.register(args)

        assert entry["branch"] == "feat/foo"
        assert entry["issues"] == [101, 102]
        assert entry["areas"] == ["src/A/", "src/B/"]
        assert entry["intent"] == "test work"
        assert len(entry["session_id"]) == 8

        data = json.loads(tmp_state.read_text(encoding="utf-8"))
        assert data["version"] == 1
        assert len(data["open_work"]) == 1

    def test_replaces_entry_for_same_branch(self, tmp_state, register_module):
        a = register_module.parse_args(["--branch", "feat/x", "--issue", "1"])
        register_module.register(a)
        b = register_module.parse_args(["--branch", "feat/x", "--issue", "2"])
        register_module.register(b)

        data = json.loads(tmp_state.read_text(encoding="utf-8"))
        assert len(data["open_work"]) == 1
        assert data["open_work"][0]["issues"] == [2]

    def test_keeps_distinct_branches(self, tmp_state, register_module):
        register_module.register(register_module.parse_args(["--branch", "feat/a"]))
        register_module.register(register_module.parse_args(["--branch", "feat/b"]))
        data = json.loads(tmp_state.read_text(encoding="utf-8"))
        branches = sorted(e["branch"] for e in data["open_work"])
        assert branches == ["feat/a", "feat/b"]


# ---------------------------------------------------------------------------
# deregister
# ---------------------------------------------------------------------------


class TestDeregister:
    def test_removes_by_branch(self, tmp_state, register_module, deregister_module):
        register_module.register(register_module.parse_args(["--branch", "feat/a"]))
        register_module.register(register_module.parse_args(["--branch", "feat/b"]))

        removed = deregister_module.deregister(branch="feat/a")
        assert len(removed) == 1
        assert removed[0]["branch"] == "feat/a"

        data = json.loads(tmp_state.read_text(encoding="utf-8"))
        assert [e["branch"] for e in data["open_work"]] == ["feat/b"]

    def test_removes_by_session(self, tmp_state, register_module, deregister_module):
        entry = register_module.register(
            register_module.parse_args(["--branch", "feat/x", "--session", "deadbeef"])
        )
        removed = deregister_module.deregister(session=entry["session_id"])
        assert len(removed) == 1

    def test_idempotent_when_missing(self, tmp_state, deregister_module):
        # No entries; deregister should succeed and return [].
        removed = deregister_module.deregister(branch="feat/never-registered")
        assert removed == []


# ---------------------------------------------------------------------------
# check
# ---------------------------------------------------------------------------


class TestCheck:
    def test_no_conflict_returns_empty(self, tmp_state, check_module):
        conflicts = check_module.check(area="src/Foo/", do_prune=False)
        assert conflicts == []

    def test_same_issue_is_conflict(self, tmp_state, register_module, check_module):
        register_module.register(
            register_module.parse_args(["--branch", "feat/x", "--issue", "802"])
        )
        conflicts = check_module.check(issue=802, do_prune=False)
        assert len(conflicts) == 1
        assert conflicts[0]["branch"] == "feat/x"

    def test_overlapping_area_is_conflict(self, tmp_state, register_module, check_module):
        register_module.register(register_module.parse_args([
            "--branch", "feat/cli",
            "--area", "src/PPDS.Cli/",
        ]))
        # Sub-path of an existing area should conflict.
        conflicts = check_module.check(
            area="src/PPDS.Cli/Plugins/Foo.cs", do_prune=False,
        )
        assert len(conflicts) == 1

    def test_disjoint_area_is_no_conflict(self, tmp_state, register_module, check_module):
        register_module.register(register_module.parse_args([
            "--branch", "feat/cli",
            "--area", "src/PPDS.Cli/",
        ]))
        conflicts = check_module.check(
            area="src/PPDS.Tui/Screens/", do_prune=False,
        )
        assert conflicts == []

    def test_self_excluded(self, tmp_state, register_module, check_module):
        entry = register_module.register(register_module.parse_args([
            "--branch", "feat/me",
            "--session", "11111111",
            "--issue", "9",
        ]))
        # Same session checking its own issue should NOT conflict.
        conflicts = check_module.check(
            issue=9, exclude_session=entry["session_id"], do_prune=False,
        )
        assert conflicts == []


# ---------------------------------------------------------------------------
# stale pruning
# ---------------------------------------------------------------------------


class TestPrune:
    def test_prunes_old_entry_with_no_branch(self, tmp_state):
        import inflight_common as ic

        old_started = (datetime.now(timezone.utc) - timedelta(hours=48)).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        )
        state = {
            "version": 1,
            "updated": ic.now_utc_iso(),
            "open_work": [
                {"branch": "feat/dead", "started": old_started, "issues": [], "areas": []},
                {"branch": "feat/alive", "started": ic.now_utc_iso(), "issues": [], "areas": []},
            ],
        }
        pruned = ic.prune_stale(state, branch_exists=lambda b: False)
        assert {p["branch"] for p in pruned} == {"feat/dead"}
        assert [e["branch"] for e in state["open_work"]] == ["feat/alive"]

    def test_keeps_old_entry_if_branch_exists(self, tmp_state):
        import inflight_common as ic

        old_started = (datetime.now(timezone.utc) - timedelta(hours=48)).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        )
        state = {
            "version": 1,
            "updated": ic.now_utc_iso(),
            "open_work": [
                {"branch": "feat/active", "started": old_started, "issues": [], "areas": []},
            ],
        }
        pruned = ic.prune_stale(state, branch_exists=lambda b: True)
        assert pruned == []
        assert len(state["open_work"]) == 1


# ---------------------------------------------------------------------------
# concurrency
# ---------------------------------------------------------------------------


class TestConcurrency:
    def test_parallel_registers_no_corruption(self, tmp_state, register_module):
        """5 concurrent register calls on different branches → 5 entries, valid JSON."""

        def register_one(i: int):
            args = register_module.parse_args([
                "--branch", f"feat/parallel-{i}",
                "--issue", str(1000 + i),
                "--area", f"src/Module{i}/",
            ])
            return register_module.register(args)

        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
            results = list(pool.map(register_one, range(5)))

        assert len(results) == 5
        data = json.loads(tmp_state.read_text(encoding="utf-8"))
        branches = sorted(e["branch"] for e in data["open_work"])
        assert branches == [f"feat/parallel-{i}" for i in range(5)]

    def test_parallel_register_then_deregister(self, tmp_state, register_module,
                                                deregister_module):
        """Register 5, deregister 5 in interleaved fashion."""

        for i in range(5):
            args = register_module.parse_args(["--branch", f"feat/p-{i}"])
            register_module.register(args)

        def deregister_one(i: int):
            return deregister_module.deregister(branch=f"feat/p-{i}")

        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
            list(pool.map(deregister_one, range(5)))

        data = json.loads(tmp_state.read_text(encoding="utf-8"))
        assert data["open_work"] == []


# ---------------------------------------------------------------------------
# Retro B3 scenario: three sessions overlap, last one filing duplicate issue
# ---------------------------------------------------------------------------


class TestRetroB3Scenario:
    """Reproduce the exact scenario that produced issue #802.

    Wave timeline:
      * t=0   session ce9a2a05 ships feature in PR #797 (registers area X)
      * t=+5h session b293be36 wakes up, considers filing issue #802
              ("feature missing in area X") — should see ce9a2a05's claim
              and halt.
      * t=+6h session cd0c578e starts work, sees both prior entries.
    """

    def test_check_blocks_duplicate_filing(self, tmp_state, register_module,
                                            check_module):
        # Session A ships feature in area X.
        register_module.register(register_module.parse_args([
            "--branch", "feat/feature-x",
            "--session", "ce9a2a05",
            "--area", "src/PPDS.Audit/Capture/",
            "--intent", "ship feature X (will become PR #797)",
        ]))

        # Session B is about to file issue claiming feature X is missing.
        # Before `gh issue create`, it calls inflight-check.
        conflicts = check_module.check(
            area="src/PPDS.Audit/Capture/",
            exclude_session="b293be36",
            do_prune=False,
        )
        assert len(conflicts) == 1, "session B should see ce9a2a05's claim"
        assert conflicts[0]["session_id"] == "ce9a2a05"

        # Session C starts work, also detects the active claim.
        conflicts_c = check_module.check(
            area="src/PPDS.Audit/Capture/Pipeline.cs",
            exclude_session="cd0c578e",
            do_prune=False,
        )
        assert len(conflicts_c) == 1
        assert conflicts_c[0]["session_id"] == "ce9a2a05"
