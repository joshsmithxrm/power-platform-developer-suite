"""Shared test fixtures.

Auto-skip claude_dispatch version check in unit tests — the check would
require a live ``claude --version`` subprocess; tests mock subprocess.Popen
at unrelated points and the version call gets caught in the mock net.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parent.parent
_SCRIPTS = _REPO / "scripts"
if str(_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS))


@pytest.fixture(autouse=True)
def _skip_dispatch_version_check():
    """Mark claude_dispatch's version check as already-passed for the test."""
    try:
        import claude_dispatch
    except ImportError:
        yield
        return
    original = claude_dispatch._VERSION_CHECKED
    claude_dispatch._VERSION_CHECKED = True
    try:
        yield
    finally:
        claude_dispatch._VERSION_CHECKED = original
