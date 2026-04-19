"""Unit tests for scripts/ci/check_pr_size.py.

Run with: python -m pytest tests/ci/test_check_pr_size.py -v
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from unittest.mock import patch

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_pr_size  # noqa: E402


def make_pr(*, additions=0, deletions=0, files=0, title="", body=""):
    return {
        "additions": additions,
        "deletions": deletions,
        "changedFiles": files,
        "title": title,
        "body": body,
    }


# ---------------------------------------------------------------------------
# Limits
# ---------------------------------------------------------------------------

class TestSizeLimits:
    def test_under_both_limits_passes(self):
        passed, msg = check_pr_size.check_pr_size(
            make_pr(additions=100, deletions=50, files=10),
        )
        assert passed
        assert "OK" in msg
        assert "10 files" in msg
        assert "150 LoC" in msg

    def test_at_file_limit_passes(self):
        # MAX_FILES=50; equal is OK (limit is strictly greater than).
        passed, _ = check_pr_size.check_pr_size(make_pr(files=50, additions=10))
        assert passed

    def test_at_loc_limit_passes(self):
        passed, _ = check_pr_size.check_pr_size(make_pr(additions=2000))
        assert passed

    def test_just_over_file_limit_fails(self):
        passed, msg = check_pr_size.check_pr_size(make_pr(files=51))
        assert not passed
        assert "51 files > 50" in msg

    def test_just_over_loc_limit_fails(self):
        passed, msg = check_pr_size.check_pr_size(
            make_pr(additions=1500, deletions=501),
        )
        assert not passed
        assert "2001 LoC > 2000" in msg

    def test_loc_counts_additions_plus_deletions(self):
        # Pure additions
        p1, _ = check_pr_size.check_pr_size(make_pr(additions=2001))
        assert not p1
        # Pure deletions
        p2, _ = check_pr_size.check_pr_size(make_pr(deletions=2001))
        assert not p2
        # Mix
        p3, _ = check_pr_size.check_pr_size(make_pr(additions=1001, deletions=1001))
        assert not p3

    def test_both_limits_breached_reports_both(self):
        passed, msg = check_pr_size.check_pr_size(
            make_pr(files=200, additions=5000, deletions=2500),
        )
        assert not passed
        assert "200 files > 50" in msg
        assert "7500 LoC > 2000" in msg

    def test_pr792_scenario(self):
        # The retro item: 131 files / 7.5K LoC.
        passed, msg = check_pr_size.check_pr_size(
            make_pr(files=131, additions=4500, deletions=3000),
        )
        assert not passed
        assert "131 files > 50" in msg


# ---------------------------------------------------------------------------
# Waiver detection
# ---------------------------------------------------------------------------

class TestWaiver:
    def test_waiver_in_title_bypasses(self):
        passed, msg = check_pr_size.check_pr_size(
            make_pr(
                files=100,
                title="huge refactor [size-waived: vendored 3p code, no review needed]",
            ),
        )
        assert passed
        assert "waiver accepted" in msg
        assert "vendored 3p code" in msg

    def test_waiver_in_body_bypasses(self):
        passed, msg = check_pr_size.check_pr_size(
            make_pr(
                files=100,
                body="some description\n\n[size-waived: codegen output]\n\nmore text",
            ),
        )
        assert passed
        assert "codegen output" in msg

    def test_waiver_with_empty_reason_rejected(self):
        passed, _ = check_pr_size.check_pr_size(
            make_pr(files=100, title="big change [size-waived: ]"),
        )
        assert not passed

    def test_waiver_with_whitespace_only_reason_rejected(self):
        passed, _ = check_pr_size.check_pr_size(
            make_pr(files=100, title="big change [size-waived:    ]"),
        )
        assert not passed

    def test_waiver_is_case_sensitive(self):
        # `[Size-Waived:` should NOT bypass — exact match required.
        passed, _ = check_pr_size.check_pr_size(
            make_pr(files=100, title="big change [Size-Waived: reason here]"),
        )
        assert not passed

    def test_no_waiver_when_under_limits_still_passes(self):
        passed, msg = check_pr_size.check_pr_size(
            make_pr(files=10, additions=10),
        )
        assert passed
        assert "OK" in msg

    def test_find_size_waiver_returns_reason(self):
        assert check_pr_size.find_size_waiver(
            "[size-waived: foo bar]", "",
        ) == "foo bar"

    def test_find_size_waiver_none_when_missing(self):
        assert check_pr_size.find_size_waiver("plain title", "plain body") is None


# ---------------------------------------------------------------------------
# Malformed input
# ---------------------------------------------------------------------------

class TestMalformedInput:
    def test_non_numeric_additions_fails(self):
        passed, msg = check_pr_size.check_pr_size(
            {"additions": "abc", "deletions": 0, "changedFiles": 0, "title": "", "body": ""},
        )
        assert not passed
        assert "malformed" in msg

    def test_missing_fields_default_to_zero(self):
        passed, _ = check_pr_size.check_pr_size({})
        assert passed  # all zeros — under limits

    def test_null_additions_treated_as_zero(self):
        passed, msg = check_pr_size.check_pr_size(
            {"additions": None, "deletions": 0, "changedFiles": 5,
             "title": "", "body": ""},
        )
        assert passed
        assert "OK" in msg

    def test_null_deletions_treated_as_zero(self):
        passed, msg = check_pr_size.check_pr_size(
            {"additions": 10, "deletions": None, "changedFiles": 5,
             "title": "", "body": ""},
        )
        assert passed
        assert "OK" in msg

    def test_null_changed_files_treated_as_zero(self):
        passed, msg = check_pr_size.check_pr_size(
            {"additions": 10, "deletions": 0, "changedFiles": None,
             "title": "", "body": ""},
        )
        assert passed
        assert "0 files" in msg

    def test_all_null_size_fields_treated_as_zero(self):
        passed, _ = check_pr_size.check_pr_size(
            {"additions": None, "deletions": None, "changedFiles": None,
             "title": "", "body": ""},
        )
        assert passed


# ---------------------------------------------------------------------------
# CLI entry
# ---------------------------------------------------------------------------

class TestMain:
    def test_main_with_json_arg_under_limits_returns_0(self):
        rc = check_pr_size.main([
            "--json", json.dumps(make_pr(files=10, additions=100)),
        ])
        assert rc == 0

    def test_main_with_json_arg_over_limits_returns_1(self):
        rc = check_pr_size.main([
            "--json", json.dumps(make_pr(files=200)),
        ])
        assert rc == 1

    def test_main_invalid_json_returns_2(self):
        rc = check_pr_size.main(["--json", "{not valid"])
        assert rc == 2

    def test_main_with_pr_calls_fetch(self):
        with patch.object(
            check_pr_size, "fetch_pr_json",
            return_value=make_pr(files=10, additions=10),
        ) as m:
            rc = check_pr_size.main(["--pr", "42"])
        assert rc == 0
        m.assert_called_once_with(42)

    def test_main_with_pr_gh_failure_returns_2(self):
        with patch.object(
            check_pr_size, "fetch_pr_json",
            side_effect=RuntimeError("gh boom"),
        ):
            rc = check_pr_size.main(["--pr", "42"])
        assert rc == 2
