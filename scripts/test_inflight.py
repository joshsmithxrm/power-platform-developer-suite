#!/usr/bin/env python3
"""Unit tests for inflight-check.py 7-day TTL stale detection (AC-180).

Usage:
    python -m unittest scripts.test_inflight
    python -m pytest scripts/test_inflight.py
"""
from __future__ import annotations

import importlib.util
import io
import os
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch


SCRIPT_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPT_DIR))


def _load_inflight_check():
    """Load scripts/inflight-check.py (filename has a hyphen)."""
    spec = importlib.util.spec_from_file_location(
        "inflight_check_under_test",
        str(SCRIPT_DIR / "inflight-check.py"),
    )
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


class TestStaleTTLDetection(unittest.TestCase):
    """AC-180: registrations >7 days old with no live worktree are reported [stale]."""

    def setUp(self):
        self.mod = _load_inflight_check()

    def _entry(self, *, started: datetime, branch: str = "feat/old"):
        return {
            "session_id": "abcd1234",
            "started": started.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "branch": branch,
            "issues": [42],
            "areas": ["src/PPDS.Cli/"],
        }

    def test_stale_ttl_detection(self):
        """Old entry + branch missing from live worktrees → marked stale."""
        old = datetime.now(timezone.utc) - timedelta(days=10)
        entry = self._entry(started=old, branch="feat/long-gone")
        self.assertTrue(self.mod._is_stale(entry, live_branches=set()))
        # annotate_stale flips a 'stale' field on the dict.
        # Patch _live_worktree_branches so the test doesn't shell out to
        # `git worktree list` (I-12: deterministic, no live git dependency).
        with patch.object(self.mod, "_live_worktree_branches",
                          return_value=set()):
            annotated = self.mod.annotate_stale([entry])
        self.assertTrue(annotated[0].get("stale"))

    def test_recent_entry_not_stale(self):
        """Entry younger than 7 days is never stale even with no live branch."""
        recent = datetime.now(timezone.utc) - timedelta(days=2)
        entry = self._entry(started=recent, branch="feat/in-progress")
        self.assertFalse(self.mod._is_stale(entry, live_branches=set()))

    def test_old_with_live_worktree_not_stale(self):
        """Old entry but branch IS in live worktrees → not stale (still active)."""
        old = datetime.now(timezone.utc) - timedelta(days=30)
        entry = self._entry(started=old, branch="feat/active")
        self.assertFalse(self.mod._is_stale(entry, live_branches={"feat/active"}))

    def test_main_does_not_change_exit_code(self):
        """Stale entries are informational only — exit code reflects conflicts only."""
        # Build a temp state file with no entries (no conflicts) and verify exit 0.
        with tempfile.TemporaryDirectory() as tmpdir:
            state_path = Path(tmpdir) / "state.json"
            state_path.write_text(
                '{"version": 1, "updated": "2026-04-26T00:00:00Z", "open_work": []}'
            )
            with patch.object(self.mod, "locked_state") as mock_locked, \
                 patch.object(self.mod, "prune_stale", return_value=[]), \
                 patch.object(self.mod, "find_conflicts", return_value=[]):
                # Provide a context manager that yields (None, empty state)
                class _CM:
                    def __enter__(self_inner):
                        return (None, {"open_work": []})
                    def __exit__(self_inner, *a):
                        return False
                mock_locked.return_value = _CM()
                rc = self.mod.main(["--issue", "42", "--no-prune"])
                self.assertEqual(rc, 0, "no conflicts → exit 0 even if stale logic ran")


if __name__ == "__main__":
    unittest.main()
