"""AC-29: release-validation gate exists and fails on any AC miss.

This meta-test is the gate itself. It invokes pytest via subprocess against
all sibling AC tests (excluding itself) and asserts exit code 0. Any
failure in ACs 01-28 causes this test to fail, which in turn fails the
release-validation workflow.

Subprocess (not `pytest.main`) avoids fixture double-runs and collection
shadowing that can mask failures.
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent


def test_all_marketplace_listing_acs_green() -> None:
    """AC-29: re-run every other AC test in this directory; fail if any fail."""
    cmd = [
        sys.executable,
        "-m",
        "pytest",
        str(TESTS_DIR),
        f"--ignore={TESTS_DIR / 'test_release_gate.py'}",
        "-q",
    ]
    result = subprocess.run(
        cmd,
        cwd=TESTS_DIR.parents[1],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    assert result.returncode == 0, (
        "Marketplace-listing AC gate failed. Any AC failure blocks release.\n"
        f"stdout:\n{result.stdout}\n"
        f"stderr:\n{result.stderr}"
    )
