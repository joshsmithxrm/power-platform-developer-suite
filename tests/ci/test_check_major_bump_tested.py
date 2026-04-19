"""Unit tests for scripts/ci/check_major_bump_tested.py.

Run with: python -m pytest tests/ci/test_check_major_bump_tested.py -v
"""
from __future__ import annotations

import sys
from pathlib import Path
from unittest.mock import patch

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_major_bump_tested as cmbt  # noqa: E402


def make_pr(*, number=1, title="", body="", labels=None, head_ref="",
            files=None, author_login=""):
    return {
        "number": number,
        "title": title,
        "body": body,
        "labels": [{"name": n} for n in (labels or [])],
        "headRefName": head_ref,
        "files": [{"path": p} for p in (files or [])],
        "author": {"login": author_login},
    }


# ---------------------------------------------------------------------------
# Dependabot detection
# ---------------------------------------------------------------------------

class TestIsDependabotPr:
    def test_label_dependencies_matches(self):
        assert cmbt.is_dependabot_pr(make_pr(labels=["dependencies"]))

    def test_author_app_dependabot_matches(self):
        assert cmbt.is_dependabot_pr(make_pr(author_login="app/dependabot"))

    def test_author_dependabot_bot_matches(self):
        assert cmbt.is_dependabot_pr(make_pr(author_login="dependabot[bot]"))

    def test_no_label_no_author_returns_false(self):
        assert not cmbt.is_dependabot_pr(
            make_pr(labels=["bug"], author_login="some-human"),
        )

    def test_label_match_is_case_insensitive(self):
        assert cmbt.is_dependabot_pr(make_pr(labels=["Dependencies"]))


# ---------------------------------------------------------------------------
# Major bump detection (delegates to classify_pr)
# ---------------------------------------------------------------------------

class TestIsMajorBump:
    def test_pr806_vite_5_to_8_is_major(self):
        # The retro item: vite 5 -> 8.
        pr = make_pr(
            title="Bump vite from 5.0.0 to 8.0.0",
            head_ref="dependabot/npm_and_yarn/vite-8.0.0",
            labels=["dependencies", "npm_and_yarn"],
        )
        assert cmbt.is_major_bump(pr)

    def test_minor_bump_is_not_major(self):
        pr = make_pr(
            title="Bump foo from 1.2.0 to 1.3.0",
            head_ref="dependabot/npm_and_yarn/foo-1.3.0",
            labels=["dependencies", "npm_and_yarn"],
        )
        assert not cmbt.is_major_bump(pr)

    def test_patch_bump_is_not_major(self):
        pr = make_pr(
            title="Bump foo from 1.2.3 to 1.2.4",
            head_ref="dependabot/npm_and_yarn/foo-1.2.4",
            labels=["dependencies", "npm_and_yarn"],
        )
        assert not cmbt.is_major_bump(pr)

    def test_grouped_bump_is_not_flagged_as_major(self):
        # Grouped bumps are unknown/Group B per classify; not major here.
        pr = make_pr(
            title="Bump the github-actions group with 3 updates",
            labels=["dependencies", "github_actions"],
        )
        assert not cmbt.is_major_bump(pr)

    def test_v_prefixed_action_major(self):
        pr = make_pr(
            title="Bump actions/checkout from v3 to v4",
            labels=["dependencies", "github_actions"],
        )
        assert cmbt.is_major_bump(pr)


# ---------------------------------------------------------------------------
# Test-job state evaluation
# ---------------------------------------------------------------------------

class TestCheckTestJobRan:
    def test_success_passes(self):
        passed, msg = cmbt.check_test_job_ran([
            {"name": "test", "state": "SUCCESS"},
        ])
        assert passed
        assert "ran and passed" in msg

    def test_pass_state_passes(self):
        # gh sometimes emits 'pass' in older versions
        passed, _ = cmbt.check_test_job_ran([{"name": "test", "state": "pass"}])
        assert passed

    def test_skipped_fails(self):
        # The PR #806 scenario.
        passed, msg = cmbt.check_test_job_ran([
            {"name": "test", "state": "SKIPPED"},
        ])
        assert not passed
        assert "SKIPPED" in msg
        assert "test" in msg

    def test_failure_fails(self):
        passed, msg = cmbt.check_test_job_ran([
            {"name": "test", "state": "FAILURE"},
        ])
        assert not passed
        assert "FAILURE" in msg

    def test_pending_fails(self):
        passed, msg = cmbt.check_test_job_ran([
            {"name": "test", "state": "IN_PROGRESS"},
        ])
        assert not passed
        assert "still running" in msg

    def test_missing_test_job_fails(self):
        passed, msg = cmbt.check_test_job_ran([
            {"name": "check-changes", "state": "SUCCESS"},
            {"name": "lint", "state": "SUCCESS"},
        ])
        assert not passed
        assert "did not run" in msg

    def test_empty_checks_fails(self):
        passed, msg = cmbt.check_test_job_ran([])
        assert not passed
        assert "did not run" in msg

    def test_other_jobs_alongside_test_success_passes(self):
        passed, _ = cmbt.check_test_job_ran([
            {"name": "test", "state": "SUCCESS"},
            {"name": "check-changes", "state": "SUCCESS"},
            {"name": "lint", "state": "SUCCESS"},
        ])
        assert passed


# ---------------------------------------------------------------------------
# Main entry — wiring
# ---------------------------------------------------------------------------

class TestMain:
    def test_non_dependabot_pr_returns_0(self):
        pr = make_pr(labels=["bug"], author_login="alice")
        with patch.object(cmbt, "fetch_pr_payload", return_value=pr):
            rc = cmbt.main(["--pr", "1"])
        assert rc == 0

    def test_dependabot_minor_bump_returns_0_without_checking_jobs(self):
        pr = make_pr(
            title="Bump foo from 1.2.0 to 1.3.0",
            labels=["dependencies"],
            author_login="dependabot[bot]",
        )
        with patch.object(cmbt, "fetch_pr_payload", return_value=pr), \
             patch.object(cmbt, "fetch_pr_checks", return_value=[]) as m:
            rc = cmbt.main(["--pr", "1"])
        assert rc == 0
        m.assert_not_called()

    def test_dependabot_major_bump_with_passing_test_returns_0(self):
        pr = make_pr(
            title="Bump foo from 1.2.0 to 2.0.0",
            labels=["dependencies"],
            author_login="dependabot[bot]",
        )
        with patch.object(cmbt, "fetch_pr_payload", return_value=pr), \
             patch.object(
                cmbt, "fetch_pr_checks",
                return_value=[{"name": "test", "state": "SUCCESS"}],
             ):
            rc = cmbt.main(["--pr", "1"])
        assert rc == 0

    def test_dependabot_major_bump_with_skipped_test_returns_1(self):
        # The PR #806 retro scenario.
        pr = make_pr(
            title="Bump vite from 5.0.0 to 8.0.0",
            labels=["dependencies"],
            author_login="dependabot[bot]",
        )
        with patch.object(cmbt, "fetch_pr_payload", return_value=pr), \
             patch.object(
                cmbt, "fetch_pr_checks",
                return_value=[{"name": "test", "state": "SKIPPED"}],
             ):
            rc = cmbt.main(["--pr", "1"])
        assert rc == 1

    def test_gh_failure_returns_2(self):
        with patch.object(
            cmbt, "fetch_pr_payload", side_effect=RuntimeError("gh boom"),
        ):
            rc = cmbt.main(["--pr", "1"])
        assert rc == 2
