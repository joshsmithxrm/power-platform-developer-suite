"""Integration test for /start skill call chain — AC-11.

Verifies that the daemon short ID returned from start-bg-spawn.py
flows correctly into inflight-register.py's --session flag and the
resulting in-flight-issues.json entry.

The test calls inflight_common and inflight-register module functions
directly (no subprocess) to keep the registry isolated to tmp_path.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO_ROOT / "scripts"

# Ensure scripts/ is on sys.path so `from inflight_common import ...` resolves
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))


def _load_module(reg_name: str, relpath: str):
    path = REPO_ROOT / relpath
    spec = importlib.util.spec_from_file_location(reg_name, str(path))
    mod = importlib.util.module_from_spec(spec)
    sys.modules[reg_name] = mod
    spec.loader.exec_module(mod)
    return mod


import inflight_common as _inflight_common  # noqa: E402 (after sys.path patch)

_inflight_register = _load_module("inflight_register_mod", "scripts/inflight-register.py")


def test_session_id_recorded(tmp_path, monkeypatch):
    """AC-11: daemon short ID from start-bg-spawn.py is recorded in the registry.

    Simulates what the skill does after receiving spawn JSON: calls
    inflight-register with --session <daemon_short> and asserts the
    resulting registry entry has session_id equal to the daemon short,
    not a random hex token.
    """
    fake_short = "deadbeef"
    registry_file = tmp_path / "in-flight-issues.json"

    def patched_state_path() -> Path:
        registry_file.parent.mkdir(parents=True, exist_ok=True)
        return registry_file

    monkeypatch.setattr(_inflight_common, "state_path", patched_state_path)

    args = argparse.Namespace(
        session=fake_short,
        branch="feat/test-bg",
        worktree=".worktrees/test-bg",
        issue=[9999],
        area=[],
        intent="test",
    )
    _inflight_register.register(args)

    assert registry_file.exists(), "registry file was not created by inflight-register"
    data = json.loads(registry_file.read_text(encoding="utf-8"))
    entries = data.get("open_work", [])
    assert any(e["session_id"] == fake_short for e in entries), (
        f"daemon short ID {fake_short!r} not found in registry entries: {entries}"
    )


def test_random_session_without_flag(tmp_path, monkeypatch):
    """Regression: when --session is omitted, a random hex is generated (not daemon ID).

    This test documents the existing behavior so the AC-11 test has a baseline
    to contrast against.
    """
    registry_file = tmp_path / "in-flight-issues.json"

    def patched_state_path() -> Path:
        registry_file.parent.mkdir(parents=True, exist_ok=True)
        return registry_file

    monkeypatch.setattr(_inflight_common, "state_path", patched_state_path)

    args = argparse.Namespace(
        session=None,  # omitted → random
        branch="feat/random-test",
        worktree=".worktrees/random-test",
        issue=[],
        area=[],
        intent="test",
    )
    entry = _inflight_register.register(args)

    assert entry["session_id"] != "deadbeef", "random ID should differ from daemon short"
    assert len(entry["session_id"]) == 8, "random ID should be 8-char hex"
