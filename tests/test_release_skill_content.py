"""Tests for /release skill content (AC-06, AC-07).

These verify that the /release skill SKILL.md file documents the
single-package patch flow and the stabilization branch escape hatch.
The SKILL.md file IS the canonical runbook — when it changes, these
tests catch regressions in the documented process.

Run with: python -m pytest tests/test_release_skill_content.py -v
"""
from __future__ import annotations

from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[1]
SKILL_PATH = REPO_ROOT / ".claude" / "skills" / "release" / "SKILL.md"


@pytest.fixture(scope="module")
def skill_text() -> str:
    """Read SKILL.md once per module."""
    if not SKILL_PATH.exists():
        pytest.fail(f"/release skill not found at {SKILL_PATH}")
    return SKILL_PATH.read_text(encoding="utf-8")


def test_patch_procedure_documented(skill_text: str) -> None:
    """AC-06: SKILL.md contains a 'Patch Release Procedure' section
    documenting the abbreviated single-package flow.
    """
    # Heading present
    assert "## Patch Release Procedure" in skill_text, (
        "Expected '## Patch Release Procedure' heading in /release SKILL.md"
    )
    # Key terms in the section content (case-insensitive)
    lowered = skill_text.lower()
    assert "single-package" in lowered or "single package" in lowered, (
        "Patch Release Procedure should mention single-package scope"
    )
    assert "abbreviated" in lowered, (
        "Patch Release Procedure should describe the flow as abbreviated"
    )
    assert "changelog" in lowered, (
        "Patch Release Procedure should reference CHANGELOG updates"
    )


def test_stabilization_branch_documented(skill_text: str) -> None:
    """AC-07: SKILL.md contains a 'Stabilization Branch' section
    documenting when to create one and how to merge back.
    """
    # Heading present
    assert "## Stabilization Branch" in skill_text, (
        "Expected '## Stabilization Branch' heading in /release SKILL.md"
    )
    lowered = skill_text.lower()
    # Branch naming convention
    assert "release/x.y" in lowered, (
        "Stabilization Branch section should document release/X.Y naming"
    )
    # Cherry-pick discipline
    assert "cherry-pick" in lowered, (
        "Stabilization Branch section should describe cherry-pick discipline"
    )
    # Merge-back path
    assert "merge back" in lowered or "merge-back" in lowered or "merge back to main" in lowered, (
        "Stabilization Branch section should describe merging back to main"
    )


def test_patch_procedure_appears_before_known_gotchas(skill_text: str) -> None:
    """The new sections should be inside the Process documentation, before
    the troubleshooting/recovery sections. Catches accidental placement at
    the very end of the file."""
    patch_idx = skill_text.find("## Patch Release Procedure")
    gotchas_idx = skill_text.find("## Known Gotchas")
    assert patch_idx > 0, "Patch Release Procedure section missing"
    assert gotchas_idx > 0, "Known Gotchas section missing"
    assert patch_idx < gotchas_idx, (
        "Patch Release Procedure should appear before Known Gotchas"
    )


def test_stabilization_branch_appears_before_known_gotchas(skill_text: str) -> None:
    """Symmetric check for the Stabilization Branch section placement."""
    stab_idx = skill_text.find("## Stabilization Branch")
    gotchas_idx = skill_text.find("## Known Gotchas")
    assert stab_idx > 0, "Stabilization Branch section missing"
    assert gotchas_idx > 0, "Known Gotchas section missing"
    assert stab_idx < gotchas_idx, (
        "Stabilization Branch should appear before Known Gotchas"
    )
