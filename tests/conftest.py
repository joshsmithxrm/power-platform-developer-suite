"""Shared test fixtures.

Ensures ``scripts/`` is importable for tests that reference helper modules
by bare name, and ``tests/`` for shared test helpers such as ``hang_bound``
(explicitly, so it holds in every pytest import mode, not only ``prepend``).
"""
from __future__ import annotations

import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parent.parent
_SCRIPTS = _REPO / "scripts"
if str(_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS))
_TESTS = _REPO / "tests"
if str(_TESTS) not in sys.path:
    sys.path.insert(0, str(_TESTS))
