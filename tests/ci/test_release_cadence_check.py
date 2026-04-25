"""Unit tests for scripts/ci/check_release_cadence.py.

Run with: python -m pytest tests/ci/test_release_cadence_check.py -v

Each test exercises the public functions of check_release_cadence directly —
no source-code inspection, no string matching on implementation text.
"""
from __future__ import annotations

import json
import sys
from datetime import datetime, timedelta
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))
import check_release_cadence as crc  # noqa: E402


# ---------------------------------------------------------------------------
# AC-04 — opens issue when overdue
# ---------------------------------------------------------------------------

class TestOpensIssueWhenOverdue:
    """AC-04: issue is opened when >8 weeks have passed and commits exist."""

    def test_opens_issue_when_overdue(self):
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=9)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=15,
            has_open_check_in_issue=False,
        )

        assert result["should_open_issue"] is True
        assert result["weeks_since_release"] == 9
        assert result["unreleased_commits"] == 15

    def test_opens_issue_when_overdue_negative_case(self):
        """Negative: same dates but 0 commits — result must flip to False."""
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=9)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=0,
            has_open_check_in_issue=False,
        )

        assert result["should_open_issue"] is False


# ---------------------------------------------------------------------------
# AC-05 — no issue when release is recent
# ---------------------------------------------------------------------------

class TestNoIssueWhenRecentRelease:
    """AC-05: issue is NOT opened when a release was cut within the last 8 weeks."""

    def test_no_issue_when_recent_release(self):
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=3)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=10,
            has_open_check_in_issue=False,
        )

        assert result["should_open_issue"] is False
        # reason must mention "recent"
        assert "recent" in result["reason"]

    def test_no_issue_when_recent_release_negative_case(self):
        """Negative: push past the threshold — result must flip to True."""
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=9)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=10,
            has_open_check_in_issue=False,
        )

        assert result["should_open_issue"] is True


# ---------------------------------------------------------------------------
# AC-08 — no duplicate issue
# ---------------------------------------------------------------------------

class TestNoDuplicateIssue:
    """AC-08: issue is NOT opened when one is already open."""

    def test_no_duplicate_issue(self):
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=9)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=15,
            has_open_check_in_issue=True,
        )

        assert result["should_open_issue"] is False
        assert "duplicate" in result["reason"]


# ---------------------------------------------------------------------------
# AC-11 — no issue when 0 unreleased commits
# ---------------------------------------------------------------------------

class TestNoIssueWhenNoUnreleasedCommits:
    """AC-11: issue is NOT opened when >8 weeks but 0 unreleased commits."""

    def test_no_issue_when_no_unreleased_commits(self):
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=9)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=0,
            has_open_check_in_issue=False,
        )

        assert result["should_open_issue"] is False
        assert "no unreleased commits" in result["reason"]


# ---------------------------------------------------------------------------
# Boundary / threshold tests
# ---------------------------------------------------------------------------

class TestThresholdBoundary:
    def test_threshold_boundary_exactly_8_weeks(self):
        """Spec says '>8 weeks', so at exactly 8 weeks the issue must NOT open."""
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=8)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=5,
            has_open_check_in_issue=False,
        )

        assert result["should_open_issue"] is False
        assert result["weeks_since_release"] == 8

    def test_threshold_boundary_one_day_past_8_weeks(self):
        """One day past 8 full weeks still counts as 8 weeks (floor division)."""
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=8, days=1)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=5,
            has_open_check_in_issue=False,
        )

        # 57 days // 7 == 8 — still at the boundary, NOT >8 weeks
        assert result["should_open_issue"] is False

    def test_threshold_boundary_9_weeks(self):
        """At 9 complete weeks the issue MUST open (commits > 0, no duplicate)."""
        now = datetime(2026, 4, 24)
        last = now - timedelta(weeks=9)

        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=now,
            unreleased_commits=1,
            has_open_check_in_issue=False,
        )

        assert result["should_open_issue"] is True
        assert result["weeks_since_release"] == 9


# ---------------------------------------------------------------------------
# Issue formatting
# ---------------------------------------------------------------------------

class TestBuildIssueTitle:
    def test_build_issue_title_format(self):
        title = crc.build_issue_title(9, 15)
        assert "9 weeks" in title
        assert "15 commits" in title

    def test_build_issue_title_singular_values(self):
        title = crc.build_issue_title(1, 1)
        assert "1 weeks" in title or "1 week" in title
        assert "1 commits" in title or "1 commit" in title


class TestBuildIssueBody:
    def test_build_issue_body_includes_release_checklist(self):
        body = crc.build_issue_body(9, 15, "Cli-v1.0.0")
        assert "/release" in body
        assert "Cli-v1.0.0" in body

    def test_build_issue_body_includes_week_and_commit_counts(self):
        body = crc.build_issue_body(9, 15, "Cli-v1.0.0")
        assert "9" in body
        assert "15" in body

    def test_build_issue_body_includes_maintainer_options(self):
        body = crc.build_issue_body(9, 15, "Cli-v1.0.0")
        # Should mention the three options described in the spec
        assert "defer" in body.lower() or "Defer" in body
        assert "close" in body.lower() or "Close" in body


# ---------------------------------------------------------------------------
# main() entry point
# ---------------------------------------------------------------------------

class TestMain:
    def test_main_outputs_valid_json(self, capsys):
        rc = crc.main([
            "--last-release-date", "2026-02-01",
            "--current-date", "2026-04-24",
            "--unreleased-commits", "5",
        ])
        captured = capsys.readouterr()
        data = json.loads(captured.out)

        assert set(data.keys()) == {
            "should_open_issue",
            "reason",
            "weeks_since_release",
            "unreleased_commits",
        }
        assert rc == 0

    def test_main_overdue_scenario(self, capsys):
        crc.main([
            "--last-release-date", "2026-01-01",
            "--current-date", "2026-04-24",
            "--unreleased-commits", "12",
        ])
        data = json.loads(capsys.readouterr().out)
        assert data["should_open_issue"] is True
        assert data["weeks_since_release"] >= 16

    def test_main_duplicate_flag(self, capsys):
        crc.main([
            "--last-release-date", "2025-12-01",
            "--current-date", "2026-04-24",
            "--unreleased-commits", "20",
            "--has-open-check-in-issue",
        ])
        data = json.loads(capsys.readouterr().out)
        assert data["should_open_issue"] is False
        assert data["reason"] == "duplicate"

    def test_main_recent_release_scenario(self, capsys):
        crc.main([
            "--last-release-date", "2026-04-10",
            "--current-date", "2026-04-24",
            "--unreleased-commits", "3",
        ])
        data = json.loads(capsys.readouterr().out)
        assert data["should_open_issue"] is False
        assert "recent" in data["reason"]
