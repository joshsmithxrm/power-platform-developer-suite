"""Behavior tests for shared-classifier dependency labeling."""
from __future__ import annotations

import sys
from pathlib import Path
from unittest.mock import patch

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import label_major_dependency as labeler  # noqa: E402


def make_pr(*, number=1, title="", labels=None, head_ref="", files=None, author="dependabot[bot]"):
    return {
        "number": number,
        "title": title,
        "body": "",
        "labels": [{"name": name} for name in (labels or ["dependencies"])],
        "headRefName": head_ref,
        "files": [{"path": path} for path in (files or [])],
        "author": {"login": author},
    }


class TestEvaluateAndLabel:
    def test_realistic_github_actions_major_gets_label(self):
        pr = make_pr(
            number=1380,
            title="ci: bump actions/setup-dotnet from 5 to 6",
            head_ref="dependabot/github_actions/actions/setup-dotnet-6",
            files=[".github/workflows/build.yml", ".github/workflows/test.yml"],
        )
        with patch.object(labeler, "apply_evaluation_label") as apply_label:
            changed, message = labeler.evaluate_and_label(pr)
        assert changed
        assert "major github-actions" in message
        apply_label.assert_called_once_with(1380)

    def test_npm_major_uses_same_classifier_and_gets_label(self):
        pr = make_pr(
            number=1338,
            title="deps(extension): bump vscode-jsonrpc from 8.2.1 to 9.0.1",
            head_ref="dependabot/npm_and_yarn/src/PPDS.Extension/vscode-jsonrpc-9.0.1",
            files=["src/PPDS.Extension/package.json"],
        )
        with patch.object(labeler, "apply_evaluation_label") as apply_label:
            changed, message = labeler.evaluate_and_label(pr)
        assert changed
        assert "major npm" in message
        apply_label.assert_called_once_with(1338)

    def test_minor_update_is_not_labeled(self):
        pr = make_pr(
            title="deps: Bump example from 1.2.0 to 1.3.0",
            head_ref="dependabot/nuget/example-1.3.0",
            files=["Directory.Packages.props"],
        )
        with patch.object(labeler, "apply_evaluation_label") as apply_label:
            changed, message = labeler.evaluate_and_label(pr)
        assert not changed
        assert "minor" in message
        apply_label.assert_not_called()

    def test_minor_update_removes_stale_evaluation_label(self):
        pr = make_pr(
            title="deps: Bump example from 1.2.0 to 1.3.0",
            labels=["dependencies", "status:needs-evaluation"],
            head_ref="dependabot/nuget/example-1.3.0",
            files=["Directory.Packages.props"],
        )
        with patch.object(labeler, "apply_evaluation_label") as apply_label, \
             patch.object(labeler, "remove_evaluation_label") as remove_label:
            changed, message = labeler.evaluate_and_label(pr)
        assert not changed
        assert "minor" in message
        apply_label.assert_not_called()
        remove_label.assert_called_once_with(1)

    def test_unclassifiable_update_is_labeled_fail_closed(self):
        pr = make_pr(
            title="Update internal dependency",
            files=["src/PPDS.Extension/package.json"],
        )
        with patch.object(labeler, "apply_evaluation_label") as apply_label:
            changed, message = labeler.evaluate_and_label(pr)
        assert changed
        assert "unknown npm" in message
        apply_label.assert_called_once_with(1)

    def test_non_dependency_pr_is_not_labeled(self):
        pr = make_pr(
            title="fix: improve output",
            labels=["bug"],
            files=["src/PPDS.Cli/Program.cs"],
            author="alice",
        )
        with patch.object(labeler, "apply_evaluation_label") as apply_label:
            changed, message = labeler.evaluate_and_label(pr)
        assert not changed
        assert "Not a dependency-update PR" in message
        apply_label.assert_not_called()

    def test_invalid_pr_number_fails_before_mutation(self):
        pr = make_pr(
            number=0,
            title="Bump example from 1.0.0 to 2.0.0",
            files=["Directory.Packages.props"],
        )
        with patch.object(labeler, "apply_evaluation_label") as apply_label:
            try:
                labeler.evaluate_and_label(pr)
            except RuntimeError as error:
                assert "no valid pull request number" in str(error)
            else:
                raise AssertionError("expected invalid PR number to fail")
        apply_label.assert_not_called()


class TestApplyEvaluationLabel:
    def test_uses_repository_evaluation_label(self):
        with patch.object(labeler, "_run_gh", return_value="") as run_gh:
            labeler.apply_evaluation_label(42)
        run_gh.assert_called_once_with([
            "pr", "edit", "42", "--add-label", "status:needs-evaluation",
        ])

    def test_removes_repository_evaluation_label(self):
        with patch.object(labeler, "_run_gh", return_value="") as run_gh:
            labeler.remove_evaluation_label(42)
        run_gh.assert_called_once_with([
            "pr", "edit", "42", "--remove-label", "status:needs-evaluation",
        ])


class TestMain:
    def test_label_failure_returns_two(self):
        pr = make_pr(
            title="Bump example from 1.0.0 to 2.0.0",
            files=["Directory.Packages.props"],
        )
        with patch.object(labeler, "fetch_pr_payload", return_value=pr), \
             patch.object(
                 labeler,
                 "apply_evaluation_label",
                 side_effect=RuntimeError("label denied"),
             ):
            result = labeler.main(["--pr", "1"])
        assert result == 2

    def test_minor_update_returns_zero_without_label(self):
        pr = make_pr(
            title="Bump example from 1.0.0 to 1.0.1",
            files=["Directory.Packages.props"],
        )
        with patch.object(labeler, "fetch_pr_payload", return_value=pr), \
             patch.object(labeler, "apply_evaluation_label") as apply_label:
            result = labeler.main(["--pr", "1"])
        assert result == 0
        apply_label.assert_not_called()
