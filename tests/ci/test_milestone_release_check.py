"""Unit tests for scripts/ci/check_milestone_completion.py.

Run with: python -m pytest tests/ci/test_milestone_release_check.py -v

Covers AC-03 and AC-09 from specs/release-cycle.md.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_milestone_completion as cmc  # noqa: E402


# ---------------------------------------------------------------------------
# AC-03: opens issue when milestone has merged PRs
# ---------------------------------------------------------------------------

class TestOpensIssueOnMilestoneComplete:
    """AC-03: milestone-release-check.yml opens a GitHub issue when a milestone
    reaches 100% closed with merged PRs."""

    def test_opens_issue_on_milestone_complete(self):
        """should_open_release_issue returns True for a non-empty PR list."""
        prs = [
            {"number": 1, "title": "x"},
            {"number": 2, "title": "y"},
            {"number": 3, "title": "z"},
        ]
        assert cmc.should_open_release_issue(prs) is True

    def test_build_issue_body_contains_milestone_title(self):
        """build_issue_body includes the milestone title in the returned body."""
        body = cmc.build_issue_body(
            "v1.1.0",
            "First minor",
            [{"number": 1, "title": "feat A"}, {"number": 2, "title": "fix B"}],
        )
        assert "v1.1.0" in body

    def test_build_issue_body_contains_pr_titles(self):
        """build_issue_body includes PR titles in the returned body."""
        body = cmc.build_issue_body(
            "v1.1.0",
            "First minor",
            [{"number": 1, "title": "feat A"}, {"number": 2, "title": "fix B"}],
        )
        assert "feat A" in body
        assert "fix B" in body

    def test_build_issue_body_contains_pr_numbers(self):
        """build_issue_body includes PR numbers (as #N) in the returned body."""
        body = cmc.build_issue_body(
            "v1.1.0",
            "First minor",
            [{"number": 1, "title": "feat A"}, {"number": 2, "title": "fix B"}],
        )
        assert "#1" in body
        assert "#2" in body


# ---------------------------------------------------------------------------
# AC-09: does NOT open issue on empty milestone
# ---------------------------------------------------------------------------

class TestNoIssueOnEmptyMilestone:
    """AC-09: milestone-release-check.yml does NOT open a release issue when a
    milestone is closed with 0 merged PRs."""

    def test_no_issue_on_empty_milestone(self):
        """should_open_release_issue returns False for an empty PR list."""
        assert cmc.should_open_release_issue([]) is False

    def test_main_empty_prs_produces_no_output_and_exits_0(self, tmp_path, capsys):
        """main() with an empty merged-prs file prints nothing and returns 0."""
        prs_file = tmp_path / "merged_prs.json"
        prs_file.write_text("[]", encoding="utf-8")

        rc = cmc.main([
            "--milestone", "v1.1.0",
            "--merged-prs", str(prs_file),
        ])

        captured = capsys.readouterr()
        assert rc == 0
        assert captured.out == ""


# ---------------------------------------------------------------------------
# Additional behavioral tests
# ---------------------------------------------------------------------------

class TestBuildIssueBodyDeferred:
    def test_body_includes_deferred_count_when_nonzero(self):
        """build_issue_body includes the deferred count when deferred_count > 0."""
        body = cmc.build_issue_body(
            "v1.2.0",
            "",
            [{"number": 5, "title": "feat C"}],
            deferred_count=2,
        )
        assert "2" in body

    def test_body_omits_deferred_when_zero(self):
        """build_issue_body does not mention deferred items when count is 0."""
        body = cmc.build_issue_body(
            "v1.2.0",
            "",
            [{"number": 5, "title": "feat C"}],
            deferred_count=0,
        )
        # Should not contain misleading "Deferred: 2" or similar text
        assert "Deferred:" not in body


class TestBuildIssueBodyChecklist:
    def test_body_includes_release_checklist(self):
        """build_issue_body includes a checklist item referencing /release."""
        body = cmc.build_issue_body(
            "v1.1.0",
            "",
            [{"number": 1, "title": "some PR"}],
        )
        assert "/release" in body


class TestMainWritesToStdout:
    def test_main_writes_body_to_stdout_when_prs_present(self, tmp_path, capsys):
        """main() with non-empty merged-prs writes the issue body to stdout."""
        prs_file = tmp_path / "merged_prs.json"
        prs_file.write_text(
            json.dumps([
                {"number": 10, "title": "Add feature X"},
                {"number": 11, "title": "Fix bug Y"},
            ]),
            encoding="utf-8",
        )

        rc = cmc.main([
            "--milestone", "v2.0.0",
            "--description", "Big release",
            "--merged-prs", str(prs_file),
        ])

        captured = capsys.readouterr()
        assert rc == 0
        assert "v2.0.0" in captured.out
        assert "#10" in captured.out
        assert "Add feature X" in captured.out
        assert "/release" in captured.out
