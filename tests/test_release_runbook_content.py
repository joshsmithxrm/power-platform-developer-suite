"""Tests for the release runbook content (AC-06, AC-07, AC-13, AC-16).

These verify that docs/runbooks/release.md documents the single-package
patch flow, the stabilization branch escape hatch, the security review
gate for stable releases, and the unified tag convention. docs/runbooks/release.md IS the
canonical release runbook; when it changes, these tests catch
regressions in the documented process.

Run with: python -m pytest tests/test_release_runbook_content.py -v
"""
from __future__ import annotations

from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[1]
RUNBOOK_PATH = REPO_ROOT / "docs" / "runbooks" / "release.md"


@pytest.fixture(scope="module")
def runbook_text() -> str:
    """Read the release runbook once per module."""
    if not RUNBOOK_PATH.exists():
        pytest.fail(f"Release runbook not found at {RUNBOOK_PATH}")
    return RUNBOOK_PATH.read_text(encoding="utf-8")


def test_patch_procedure_documented(runbook_text: str) -> None:
    """AC-06: The release runbook contains a 'Patch Release Procedure' section
    documenting the abbreviated single-package flow.
    """
    # Heading present
    assert "## Patch Release Procedure" in runbook_text, (
        "Expected '## Patch Release Procedure' heading in docs/runbooks/release.md"
    )
    # Key terms in the section content (case-insensitive)
    lowered = runbook_text.lower()
    assert "single-package" in lowered or "single package" in lowered, (
        "Patch Release Procedure should mention single-package scope"
    )
    assert "abbreviated" in lowered, (
        "Patch Release Procedure should describe the flow as abbreviated"
    )
    assert "changelog" in lowered, (
        "Patch Release Procedure should reference CHANGELOG updates"
    )


def test_stabilization_branch_documented(runbook_text: str) -> None:
    """AC-07: The release runbook contains a 'Stabilization Branch' section
    documenting when to create one and how to merge back.
    """
    # Heading present
    assert "## Stabilization Branch" in runbook_text, (
        "Expected '## Stabilization Branch' heading in docs/runbooks/release.md"
    )
    lowered = runbook_text.lower()
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


def test_patch_procedure_appears_before_known_gotchas(runbook_text: str) -> None:
    """The new sections should be inside the Process documentation, before
    the troubleshooting/recovery sections. Catches accidental placement at
    the very end of the file."""
    patch_idx = runbook_text.find("## Patch Release Procedure")
    gotchas_idx = runbook_text.find("## Known Gotchas")
    assert patch_idx > 0, "Patch Release Procedure section missing"
    assert gotchas_idx > 0, "Known Gotchas section missing"
    assert patch_idx < gotchas_idx, (
        "Patch Release Procedure should appear before Known Gotchas"
    )


def test_stabilization_branch_appears_before_known_gotchas(runbook_text: str) -> None:
    """Symmetric check for the Stabilization Branch section placement."""
    stab_idx = runbook_text.find("## Stabilization Branch")
    gotchas_idx = runbook_text.find("## Known Gotchas")
    assert stab_idx > 0, "Stabilization Branch section missing"
    assert gotchas_idx > 0, "Known Gotchas section missing"
    assert stab_idx < gotchas_idx, (
        "Stabilization Branch should appear before Known Gotchas"
    )


def test_security_review_gate_documented(runbook_text: str) -> None:
    """AC-13: The release runbook enforces security review gate for stable releases.

    The prerequisite and pre-merge verification sections must require a
    security review artifact (docs/qa/security-review-*.md) for stable
    releases (vX.Y.0) and explicitly exempt patches/prereleases.
    """
    lowered = runbook_text.lower()
    assert "security review" in lowered, (
        "The release runbook should mention security review"
    )
    assert "security-review" in lowered, (
        "The release runbook should reference the /security-review artifact path"
    )
    assert "docs/qa/security-review" in runbook_text, (
        "The release runbook should specify the artifact path docs/qa/security-review-*.md"
    )
    assert "enforced" in lowered or "cannot proceed" in lowered, (
        "The release runbook should use enforcement language (not just 'should')"
    )
    assert "patch" in lowered, (
        "The release runbook should mention that patches are exempt from the gate"
    )


def test_unified_tag_convention_documented(runbook_text: str) -> None:
    """AC-16: The release runbook documents the unified v* tag convention alongside
    per-package tags.
    """
    assert "Unified v* Tag" in runbook_text or "unified tag" in runbook_text.lower(), (
        "The release runbook should have a section documenting the unified v* tag"
    )
    assert "docs-release.yml" in runbook_text, (
        "The release runbook should reference docs-release.yml as the workflow triggered by unified tags"
    )
    assert "per-package" in runbook_text.lower(), (
        "The release runbook should contrast unified tags with per-package tags"
    )
    assert "v1.1.0" in runbook_text or "vX.Y.0" in runbook_text, (
        "The release runbook should show an example of a unified tag"
    )


def test_explained_release_scope_advisory_documented(runbook_text: str) -> None:
    """AC-19: the release runbook uses the same explained public model."""
    assert "scripts/ci/release_plan.py" in runbook_text
    assert "direct product changes" in runbook_text.lower()
    assert "downstream deliverables" in runbook_text.lower()
    assert "same-commit minver tag prerequisites" in runbook_text.lower()
    assert "advisory only" in runbook_text.lower()


def test_refname_sort_is_rejected_for_semver_selection(runbook_text: str) -> None:
    """AC-17: version selection is delegated to strict SemVer ordering."""
    assert "Do not substitute `git --sort=-v:refname`" in runbook_text
    assert "beta.10" in runbook_text


def test_patch_flow_reviews_minver_prerequisites(runbook_text: str) -> None:
    """AC-20: focused stable patches account for same-commit dependencies."""
    patch_section = runbook_text.split("## Patch Release Procedure", 1)[1]
    patch_section = patch_section.split("## Stabilization Branch", 1)[0]
    assert "MinVer prerequisites" in patch_section
    assert "Dataverse-v" in patch_section
    assert "Query-v" in patch_section
