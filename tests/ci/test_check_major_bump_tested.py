"""Behavior tests for ecosystem-aware major dependency evidence enforcement."""
from __future__ import annotations

import sys
from pathlib import Path
from unittest.mock import patch

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_major_bump_tested as cmbt  # noqa: E402


def make_pr(
    *,
    number=1,
    title="",
    body="",
    labels=None,
    head_ref="",
    files=None,
    author_login="",
):
    return {
        "number": number,
        "title": title,
        "body": body,
        "labels": [{"name": name} for name in (labels or [])],
        "headRefName": head_ref,
        "files": [{"path": path} for path in (files or [])],
        "author": {"login": author_login},
    }


def major_pr(ecosystem: str) -> dict:
    if ecosystem == "nuget":
        return make_pr(
            title="deps: Bump Example.Package from 1.0.0 to 2.0.0",
            labels=["dependencies", "nuget"],
            head_ref="dependabot/nuget/Example.Package-2.0.0",
            files=["Directory.Packages.props"],
            author_login="dependabot[bot]",
        )
    if ecosystem == "npm":
        return make_pr(
            title="deps(extension): Bump example-package from 1.0.0 to 2.0.0",
            labels=["dependencies"],
            head_ref="dependabot/npm_and_yarn/src/PPDS.Extension/example-package-2.0.0",
            files=["src/PPDS.Extension/package.json"],
            author_login="dependabot[bot]",
        )
    if ecosystem == "github-actions":
        return make_pr(
            title="ci: bump actions/setup-python from 6 to 7",
            labels=["dependencies"],
            head_ref="dependabot/github_actions/actions/setup-python-7",
            files=[".github/workflows/workflow-tests.yml"],
            author_login="dependabot[bot]",
        )
    raise AssertionError(f"unsupported test ecosystem: {ecosystem}")


def check(workflow: str, name: str, state: str) -> dict:
    return {"workflow": workflow, "name": name, "state": state}


class TestRequiredEvidenceSelection:
    def test_nuget_requires_dotnet_unit_tests(self):
        classification = cmbt.classify.classify_pr(major_pr("nuget"))
        evidence, error = cmbt.required_evidence_for(classification)
        assert error == ""
        assert evidence == cmbt.RequiredEvidence("Test", "test", ".NET unit tests")

    def test_npm_requires_extension_build_and_tests(self):
        classification = cmbt.classify.classify_pr(major_pr("npm"))
        evidence, error = cmbt.required_evidence_for(classification)
        assert error == ""
        assert evidence == cmbt.RequiredEvidence(
            "Build", "extension", "Extension build and tests",
        )

    def test_github_actions_requires_workflow_policy_tests(self):
        classification = cmbt.classify.classify_pr(major_pr("github-actions"))
        evidence, error = cmbt.required_evidence_for(classification)
        assert error == ""
        assert evidence == cmbt.RequiredEvidence(
            "Python Tests", "workflow-tests", "executable workflow-policy tests",
        )

    def test_unknown_ecosystem_has_no_fallback_to_unrelated_tests(self):
        classification = cmbt.classify.Classification(
            pr_number=1,
            group="C",
            reason="ambiguous",
            ecosystem="unknown",
            update_type="major",
            package="example",
            from_version="1",
            to_version="2",
        )
        evidence, error = cmbt.required_evidence_for(classification)
        assert evidence is None
        assert "Cannot select relevant test evidence" in error


class TestCheckRequiredEvidence:
    EVIDENCE = cmbt.RequiredEvidence("Build", "extension", "Extension build and tests")

    def test_exact_success_passes(self):
        passed, message = cmbt.check_required_evidence(
            [check("Build", "extension", "SUCCESS")],
            self.EVIDENCE,
        )
        assert passed
        assert "ran and passed" in message

    def test_pass_state_is_accepted(self):
        passed, _ = cmbt.check_required_evidence(
            [check("Build", "extension", "pass")],
            self.EVIDENCE,
        )
        assert passed

    def test_same_job_name_from_wrong_workflow_does_not_pass(self):
        passed, message = cmbt.check_required_evidence(
            [check("Third Party", "extension", "SUCCESS")],
            self.EVIDENCE,
        )
        assert not passed
        assert "did not run" in message

    def test_skipped_fails(self):
        passed, message = cmbt.check_required_evidence(
            [check("Build", "extension", "SKIPPED")],
            self.EVIDENCE,
        )
        assert not passed
        assert "SKIPPED" in message

    def test_failure_fails(self):
        passed, message = cmbt.check_required_evidence(
            [check("Build", "extension", "FAILURE")],
            self.EVIDENCE,
        )
        assert not passed
        assert "FAILURE" in message

    def test_pending_fails(self):
        passed, message = cmbt.check_required_evidence(
            [check("Build", "extension", "IN_PROGRESS")],
            self.EVIDENCE,
        )
        assert not passed
        assert "still running" in message

    def test_missing_fails(self):
        passed, message = cmbt.check_required_evidence([], self.EVIDENCE)
        assert not passed
        assert "did not run" in message

    def test_successful_rerun_wins_over_prior_failure(self):
        passed, _ = cmbt.check_required_evidence(
            [
                check("Build", "extension", "FAILURE"),
                check("Build", "extension", "SUCCESS"),
            ],
            self.EVIDENCE,
        )
        assert passed


class TestMain:
    def test_non_dependency_pr_returns_zero_without_fetching_checks(self):
        pr = make_pr(labels=["bug"], author_login="alice")
        with patch.object(cmbt, "fetch_pr_payload", return_value=pr), \
             patch.object(cmbt, "fetch_pr_checks", return_value=[]) as fetch_checks:
            result = cmbt.main(["--pr", "1"])
        assert result == 0
        fetch_checks.assert_not_called()

    def test_minor_dependency_returns_zero_without_fetching_checks(self):
        pr = make_pr(
            title="Bump example from 1.2.0 to 1.3.0",
            labels=["dependencies"],
            files=["src/PPDS.Extension/package.json"],
            author_login="dependabot[bot]",
        )
        with patch.object(cmbt, "fetch_pr_payload", return_value=pr), \
             patch.object(cmbt, "fetch_pr_checks", return_value=[]) as fetch_checks:
            result = cmbt.main(["--pr", "1"])
        assert result == 0
        fetch_checks.assert_not_called()

    def test_nuget_major_passes_with_dotnet_test(self):
        with patch.object(cmbt, "fetch_pr_payload", return_value=major_pr("nuget")), \
             patch.object(
                 cmbt,
                 "fetch_pr_checks",
                 return_value=[check("Test", "test", "SUCCESS")],
             ):
            result = cmbt.main(["--pr", "1"])
        assert result == 0

    def test_npm_major_passes_with_extension_job_while_dotnet_is_skipped(self):
        checks = [
            check("Test", "test", "SKIPPED"),
            check("Build", "extension", "SUCCESS"),
        ]
        with patch.object(cmbt, "fetch_pr_payload", return_value=major_pr("npm")), \
             patch.object(cmbt, "fetch_pr_checks", return_value=checks):
            result = cmbt.main(["--pr", "1"])
        assert result == 0

    def test_actions_major_passes_with_workflow_tests_while_product_tests_skip(self):
        checks = [
            check("Test", "test", "SKIPPED"),
            check("Build", "extension", "SKIPPED"),
            check("Python Tests", "workflow-tests", "SUCCESS"),
        ]
        with patch.object(
            cmbt, "fetch_pr_payload", return_value=major_pr("github-actions"),
        ), patch.object(cmbt, "fetch_pr_checks", return_value=checks):
            result = cmbt.main(["--pr", "1"])
        assert result == 0

    def test_actions_major_fails_without_workflow_tests(self):
        checks = [check("Test", "test", "SKIPPED")]
        with patch.object(
            cmbt, "fetch_pr_payload", return_value=major_pr("github-actions"),
        ), patch.object(cmbt, "fetch_pr_checks", return_value=checks):
            result = cmbt.main(["--pr", "1"])
        assert result == 1

    def test_unparseable_dependency_with_known_ecosystem_still_requires_evidence(self):
        pr = make_pr(
            title="Update internal dependency",
            labels=["dependencies"],
            files=["src/PPDS.Extension/package.json"],
            author_login="dependabot[bot]",
        )
        with patch.object(cmbt, "fetch_pr_payload", return_value=pr), \
             patch.object(
                 cmbt,
                 "fetch_pr_checks",
                 return_value=[check("Build", "extension", "SUCCESS")],
             ):
            result = cmbt.main(["--pr", "1"])
        assert result == 0

    def test_unknown_ecosystem_fails_closed_without_fetching_checks(self):
        pr = make_pr(
            title="Bump example from 1.0.0 to 2.0.0",
            labels=["dependencies"],
            files=["docs/dependencies.md"],
            author_login="dependabot[bot]",
        )
        with patch.object(cmbt, "fetch_pr_payload", return_value=pr), \
             patch.object(cmbt, "fetch_pr_checks", return_value=[]) as fetch_checks:
            result = cmbt.main(["--pr", "1"])
        assert result == 1
        fetch_checks.assert_not_called()

    def test_github_failure_returns_two(self):
        with patch.object(
            cmbt, "fetch_pr_payload", side_effect=RuntimeError("gh boom"),
        ):
            result = cmbt.main(["--pr", "1"])
        assert result == 2
