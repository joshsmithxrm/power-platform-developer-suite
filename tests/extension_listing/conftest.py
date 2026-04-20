"""pytest configuration for marketplace-listing tests.

Injects the repo root into sys.path so tests can import `_helpers` as a
sibling module regardless of where pytest is invoked from.
"""
from __future__ import annotations

import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))
