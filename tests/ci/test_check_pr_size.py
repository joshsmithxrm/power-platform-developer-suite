"""Unit tests for scripts/ci/check_pr_size.py.

PR size is informational: the script reports the numbers and always exits 0.

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
# Reporting
# ---------------------------------------------------------------------------

class TestDescribeSize:
    def test_small_pr_reports_numbers(self):
        msg = check_pr_size.describe_size(
            make_pr(additions=100, deletions=50, files=10),
        )
        assert "10 files" in msg
        assert "150 LoC" in msg
        assert "100 added" in msg
        assert "50 deleted" in msg
        assert "informational" in msg.lower()
        assert "large" not in msg.lower()

    def test_large_by_files_is_flagged_but_informational(self):
        msg = check_pr_size.describe_size(make_pr(files=131, additions=10))
        assert "131 files" in msg
        assert "large" in msg.lower()
        assert "informational" in msg.lower()

    def test_large_by_loc_is_flagged(self):
        msg = check_pr_size.describe_size(
            make_pr(additions=4500, deletions=3000, files=3),
        )
        assert "7500 LoC" in msg
        assert "large" in msg.lower()

    def test_at_thresholds_is_not_flagged(self):
        msg = check_pr_size.describe_size(make_pr(files=50, additions=2000))
        assert "large" not in msg.lower()

    def test_loc_counts_additions_plus_deletions(self):
        assert "2002 LoC" in check_pr_size.describe_size(
            make_pr(additions=1001, deletions=1001),
        )

    def test_waiver_text_is_not_parsed(self):
        msg = check_pr_size.describe_size(
            make_pr(files=100, title="x [size-waived: reason]",
                    body="[size-waived: reason]"),
        )
        assert "waiver" not in msg.lower()
        assert "waived" not in msg.lower()
        assert "large" in msg.lower()
        assert not hasattr(check_pr_size, "find_size_waiver")
        assert not hasattr(check_pr_size, "SIZE_WAIVED_RE")

    def test_missing_and_null_fields_default_to_zero(self):
        assert "0 files" in check_pr_size.describe_size({})
        msg = check_pr_size.describe_size(
            {"additions": None, "deletions": None, "changedFiles": None},
        )
        assert "0 files" in msg
        assert "0 LoC" in msg

    def test_non_numeric_data_is_reported_not_raised(self):
        msg = check_pr_size.describe_size({"additions": "abc"})
        assert "could not" in msg.lower()
        assert "informational" in msg.lower()


# ---------------------------------------------------------------------------
# CLI entry: always exit 0
# ---------------------------------------------------------------------------

class TestMain:
    def test_small_pr_exits_0(self, capsys):
        rc = check_pr_size.main([
            "--json", json.dumps(make_pr(files=10, additions=100)),
        ])
        assert rc == 0
        assert "10 files" in capsys.readouterr().out

    def test_large_pr_exits_0_and_reports_numbers(self, capsys):
        rc = check_pr_size.main([
            "--json", json.dumps(make_pr(files=200, additions=5000, deletions=2500)),
        ])
        out = capsys.readouterr().out
        assert rc == 0
        assert "200 files" in out
        assert "7500 LoC" in out

    def test_large_pr_without_any_waiver_still_exits_0(self):
        rc = check_pr_size.main(["--json", json.dumps(make_pr(files=1000))])
        assert rc == 0

    def test_invalid_json_exits_0_with_warning(self, capsys):
        rc = check_pr_size.main(["--json", "{not valid"])
        captured = capsys.readouterr()
        assert rc == 0
        assert "::warning" in captured.out
        assert "could not be read" in captured.out

    def test_with_pr_calls_fetch(self):
        with patch.object(
            check_pr_size, "fetch_pr_json",
            return_value=make_pr(files=10, additions=10),
        ) as m:
            rc = check_pr_size.main(["--pr", "42"])
        assert rc == 0
        m.assert_called_once_with(42)

    def test_unreadable_pr_exits_0_with_warning(self, capsys):
        with patch.object(
            check_pr_size, "fetch_pr_json",
            side_effect=RuntimeError("gh boom"),
        ):
            rc = check_pr_size.main(["--pr", "42"])
        out = capsys.readouterr().out
        assert rc == 0
        assert "::warning" in out
        assert "gh boom" in out
        assert "informational" in out.lower()

    def test_summary_written_to_github_step_summary(self, tmp_path, monkeypatch):
        summary = tmp_path / "summary.md"
        monkeypatch.setenv("GITHUB_STEP_SUMMARY", str(summary))
        rc = check_pr_size.main([
            "--json", json.dumps(make_pr(files=60, additions=10)),
        ])
        assert rc == 0
        text = summary.read_text(encoding="utf-8")
        assert "60 files" in text
        assert "informational" in text.lower()

    def test_unreadable_pr_summary_notes_error(self, tmp_path, monkeypatch):
        summary = tmp_path / "summary.md"
        monkeypatch.setenv("GITHUB_STEP_SUMMARY", str(summary))
        with patch.object(
            check_pr_size, "fetch_pr_json", side_effect=RuntimeError("gh boom"),
        ):
            rc = check_pr_size.main(["--pr", "7"])
        assert rc == 0
        assert "gh boom" in summary.read_text(encoding="utf-8")

    def test_unwritable_summary_does_not_fail(self, tmp_path, monkeypatch):
        monkeypatch.setenv("GITHUB_STEP_SUMMARY", str(tmp_path / "no" / "dir" / "s.md"))
        rc = check_pr_size.main(["--json", json.dumps(make_pr(files=1))])
        assert rc == 0

    def test_no_summary_env_is_fine(self, monkeypatch):
        monkeypatch.delenv("GITHUB_STEP_SUMMARY", raising=False)
        assert check_pr_size.main(["--json", json.dumps(make_pr(files=1))]) == 0

    def test_unexpected_oserror_from_read_exits_0_with_warning(self, capsys):
        with patch.object(
            check_pr_size, "fetch_pr_json", side_effect=PermissionError("denied"),
        ):
            rc = check_pr_size.main(["--pr", "42"])
        out = capsys.readouterr().out
        assert rc == 0
        assert "::warning" in out
        assert "denied" in out

    def test_unicode_decode_error_from_read_exits_0(self, capsys):
        err = UnicodeDecodeError("utf-8", bytes([255]), 0, 1, "bad byte")
        with patch.object(check_pr_size, "fetch_pr_json", side_effect=err):
            rc = check_pr_size.main(["--pr", "42"])
        assert rc == 0
        assert "::warning" in capsys.readouterr().out

    def test_non_finite_number_exits_0_with_warning(self, capsys):
        # json.loads accepts Infinity; int(inf) raises OverflowError.
        rc = check_pr_size.main(["--json", '{"additions": Infinity}'])
        out = capsys.readouterr().out
        assert rc == 0
        assert "informational" in out.lower()

    def test_describe_size_non_finite_does_not_raise(self):
        msg = check_pr_size.describe_size({"additions": float("inf")})
        assert "could not" in msg.lower()
        msg = check_pr_size.describe_size({"additions": float("nan")})
        assert "could not" in msg.lower()

    def test_unexpected_error_in_describe_exits_0_with_warning(self, capsys):
        with patch.object(
            check_pr_size, "describe_size", side_effect=RuntimeError("kaboom"),
        ):
            rc = check_pr_size.main(["--json", "{}"])
        out = capsys.readouterr().out
        assert rc == 0
        assert "::warning" in out
        assert "kaboom" in out
