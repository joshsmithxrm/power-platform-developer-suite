"""Unit tests for scripts/ci/check_release_cadence.py.

Run with: python -m pytest tests/ci/test_release_cadence_check.py -v

Each test exercises the public functions of check_release_cadence directly —
no source-code inspection, no string matching on implementation text.
"""
from __future__ import annotations

import json
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))
import check_release_cadence as crc  # noqa: E402
import release_model  # noqa: E402

WORKFLOW_PATH = REPO_ROOT / ".github" / "workflows" / "release-cadence-check.yml"


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


class TestUtcAwareProductionTimestamps:
    def test_git_iso_strict_timestamp_and_utc_now_are_compatible(self, capsys):
        """Regression for the scheduled-run aware/naive subtraction failure."""
        rc = crc.main([
            "--last-release-date", "2026-07-15T00:19:24-05:00",
            "--current-date", "2026-09-17T05:19:24Z",
            "--unreleased-commits", "3",
        ])
        result = json.loads(capsys.readouterr().out)
        assert rc == 0
        assert result["weeks_since_release"] == 9
        assert result["should_open_issue"] is True

    def test_evaluate_normalizes_naive_and_aware_values_to_utc(self):
        result = crc.evaluate_cadence(
            last_release_date=datetime(2026, 1, 1),
            current_date=datetime(2026, 3, 5, tzinfo=timezone.utc),
            unreleased_commits=1,
            has_open_check_in_issue=False,
        )
        assert result["weeks_since_release"] == 9

    def test_offsets_are_normalized_before_flooring_weeks(self):
        last = crc.parse_utc_timestamp("2026-01-01T23:00:00-06:00")
        current = crc.parse_utc_timestamp("2026-03-06T05:00:00Z")
        result = crc.evaluate_cadence(
            last_release_date=last,
            current_date=current,
            unreleased_commits=1,
            has_open_check_in_issue=False,
        )
        assert result["weeks_since_release"] == 9

    def test_invalid_timestamp_fails_instead_of_silently_skipping(self, capsys):
        rc = crc.main([
            "--last-release-date", "not-a-timestamp",
            "--current-date", "2026-09-12T00:00:00Z",
            "--unreleased-commits", "1",
        ])
        captured = capsys.readouterr()
        assert rc == 2
        assert captured.out == ""
        assert "--last-release-date" in captured.err


class TestStrictReleaseTagSelection:
    def test_uses_shared_strict_semver(self):
        assert crc.SemVer is release_model.SemVer

    def test_most_recent_valid_release_wins_across_independent_lines(self):
        tags = [
            crc.ReleaseTagWithDate(
                "Cli-v9.0.0",
                crc.parse_utc_timestamp("2026-08-01T00:00:00Z"),
            ),
            crc.ReleaseTagWithDate(
                "Extension-v1.6.1",
                crc.parse_utc_timestamp("2026-09-12T05:41:07-05:00"),
            ),
        ]
        result = crc.select_latest_cadence_release(
            tags,
            {"Cli-v", "Extension-v", "v"},
        )
        assert result["latest_tag"] == "Extension-v1.6.1"
        assert result["latest_tag_date"] == "2026-09-12T10:41:07+00:00"

    def test_newer_malformed_tag_is_diagnostic_and_cannot_win(self):
        tags = [
            crc.ReleaseTagWithDate(
                "Cli-v1.4.1",
                crc.parse_utc_timestamp("2026-09-12T05:41:07-05:00"),
            ),
            crc.ReleaseTagWithDate(
                "Cli-v٢.0.0",
                crc.parse_utc_timestamp("2026-09-13T00:00:00Z"),
            ),
            crc.ReleaseTagWithDate(
                "unrelated-v99.0.0",
                crc.parse_utc_timestamp("2026-09-14T00:00:00Z"),
            ),
        ]
        result = crc.select_latest_cadence_release(
            tags,
            {"Cli-v", "Extension-v", "v"},
        )
        assert result["latest_tag"] == "Cli-v1.4.1"
        assert len(result["diagnostics"]) == 1
        assert "Cli-v٢.0.0" in result["diagnostics"][0]

    def test_repository_prefixes_are_discovered_from_release_graph(self):
        prefixes = crc.discover_release_prefixes(REPO_ROOT)
        assert {"Cli-v", "Extension-v", "Query-v", "v"} <= prefixes


class TestWorkflowWiring:
    def test_workflow_uses_tested_strict_tag_discovery(self):
        text = WORKFLOW_PATH.read_text(encoding="utf-8")
        assert "check_release_cadence.py --find-latest-release" in text
        assert "--sort=-creatordate" not in text

    def test_workflow_passes_production_iso_timestamp_without_rewriting_it(self):
        text = WORKFLOW_PATH.read_text(encoding="utf-8")
        assert "last_tag_date" in text
        assert '--last-release-date "${{ steps.tag-info.outputs.last_tag_date }}"' in text


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
        assert "docs/RELEASE.md" in body
        assert ".claude/skills" not in body
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
