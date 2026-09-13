"""Behavior tests for CLI/Extension co-release alert reconciliation."""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))
import check_extension_corelease as cec  # noqa: E402
import release_model  # noqa: E402

WORKFLOW_PATH = REPO_ROOT / ".github" / "workflows" / "post-merge-release-check.yml"
LABEL = cec.CORELEASE_LABEL


def tag(name: str, commit: str) -> cec.ReleaseTag:
    return cec.ReleaseTag(name=name, commit=commit)


def issue(
    number: int,
    cli_tag: str,
    *,
    state: str = "OPEN",
    labeled: bool = True,
) -> cec.ExistingIssue:
    return cec.ExistingIssue(
        number=number,
        state=state,
        body=cec.issue_marker(cli_tag),
        labels=frozenset({LABEL} if labeled else set()),
    )


def actions(plan: dict, kind: str) -> list[dict]:
    return [action for action in plan["actions"] if action["kind"] == kind]


class TestSharedStrictSemVer:
    def test_uses_shared_semver_implementation(self):
        assert cec.SemVer is release_model.SemVer

    def test_stable_cli_selection_is_semver_not_ref_order(self):
        selected = cec.select_latest_stable_cli([
            tag("Cli-v1.4.0", "a"),
            tag("Cli-v1.10.0", "b"),
            tag("Cli-v1.5.0-beta.10", "c"),
        ])
        assert selected == tag("Cli-v1.10.0", "b")

    def test_prerelease_cli_and_malformed_unicode_digit_are_not_stable(self):
        assert cec.is_stable_cli_tag("Cli-v1.4.0") is True
        assert cec.is_stable_cli_tag("Cli-v1.5.0-beta.10") is False
        assert cec.is_stable_cli_tag("Cli-v١.5.0") is False

    def test_malformed_tag_is_diagnostic_and_cannot_outrank_stable(self):
        plan = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.0", "release"), tag("Cli-v٩.0.0", "bad")],
            extension_tags=[tag("Extension-v1.6.0", "release")],
            existing_issues=[],
        )
        assert plan["latest_cli_tag"] == "Cli-v1.4.0"
        assert plan["actions"] == []
        assert any("Malformed release tag 'Cli-v٩.0.0'" in d for d in plan["diagnostics"])


class TestIncident1375:
    """Cli-v1.4.0 first opened #1375; Extension-v1.6.0 shared its commit."""

    def test_cli_first_opens_alert_keyed_by_cli_tag(self):
        plan = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.0", "release-a")],
            extension_tags=[tag("Extension-v1.4.1", "older")],
            existing_issues=[],
        )
        create = actions(plan, "create")
        assert len(create) == 1
        assert create[0]["cli_tag"] == "Cli-v1.4.0"
        assert cec.issue_marker("Cli-v1.4.0") in create[0]["body"]
        assert "no co-located Extension-v* tag" in create[0]["title"]

    def test_later_colocated_extension_comments_and_closes_1375(self):
        plan = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.0", "318df745")],
            extension_tags=[
                tag("Extension-v1.4.1", "older"),
                tag("Extension-v1.6.0", "318df745"),
            ],
            existing_issues=[issue(1375, "Cli-v1.4.0")],
        )
        close = actions(plan, "close")
        assert len(close) == 1
        assert close[0]["issue_number"] == 1375
        assert close[0]["reason"] == "satisfied"
        assert "Extension-v1.6.0" in close[0]["comment"]
        assert actions(plan, "create") == []


class TestIncident1410:
    """Cli-v1.4.1 first opened #1410; Extension-v1.6.1 shared its commit."""

    def test_later_extension_reconciles_1410(self):
        plan = cec.reconcile_corelease(
            cli_tags=[
                tag("Cli-v1.4.0", "old"),
                tag("Cli-v1.4.1", "ab7b61f"),
            ],
            extension_tags=[
                tag("Extension-v1.6.0", "old"),
                tag("Extension-v1.6.1", "ab7b61f"),
            ],
            existing_issues=[issue(1410, "Cli-v1.4.1")],
        )
        assert [(a["issue_number"], a["reason"]) for a in actions(plan, "close")] == [
            (1410, "satisfied"),
        ]
        assert actions(plan, "create") == []

    def test_closed_1410_is_a_permanent_record_not_reopened(self):
        plan = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.1", "release")],
            extension_tags=[],
            existing_issues=[issue(1410, "Cli-v1.4.1", state="CLOSED")],
        )
        assert plan["actions"] == []
        assert plan["reason"] == "current CLI alert already recorded"


class TestConvergentReconciliation:
    def test_extension_first_then_cli_is_immediately_satisfied(self):
        # The Extension event sees no stable CLI. The later CLI event sees both
        # co-located refs and therefore never creates a transient alert.
        before_cli = cec.reconcile_corelease(
            cli_tags=[],
            extension_tags=[tag("Extension-v1.6.1", "same")],
            existing_issues=[],
        )
        after_cli = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.1", "same")],
            extension_tags=[tag("Extension-v1.6.1", "same")],
            existing_issues=[],
        )
        assert before_cli["actions"] == []
        assert after_cli["actions"] == []

    def test_cli_first_then_extension_converges_to_no_open_alert(self):
        before_extension = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.1", "same")],
            extension_tags=[],
            existing_issues=[],
        )
        assert len(actions(before_extension, "create")) == 1

        after_extension = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.1", "same")],
            extension_tags=[tag("Extension-v1.6.1", "same")],
            existing_issues=[issue(1410, "Cli-v1.4.1")],
        )
        assert [(a["issue_number"], a["reason"]) for a in actions(after_extension, "close")] == [
            (1410, "satisfied"),
        ]
        assert actions(after_extension, "create") == []

    def test_same_timestamp_is_irrelevant_when_commits_differ(self):
        plan = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.1", "cli-commit")],
            extension_tags=[tag("Extension-v9.0.0", "different-commit")],
            existing_issues=[],
        )
        assert len(actions(plan, "create")) == 1

    def test_higher_cli_closes_old_alert_and_opens_keyed_current_alert(self):
        plan = cec.reconcile_corelease(
            cli_tags=[
                tag("Cli-v1.4.0", "old"),
                tag("Cli-v1.4.1", "new"),
            ],
            extension_tags=[],
            existing_issues=[issue(1375, "Cli-v1.4.0")],
        )
        assert [(a["issue_number"], a["reason"]) for a in actions(plan, "close")] == [
            (1375, "superseded"),
        ]
        create = actions(plan, "create")
        assert len(create) == 1
        assert create[0]["cli_tag"] == "Cli-v1.4.1"

    def test_unlabeled_spoof_marker_does_not_block_current_alert(self):
        plan = cec.reconcile_corelease(
            cli_tags=[tag("Cli-v1.4.1", "new")],
            extension_tags=[],
            existing_issues=[issue(99, "Cli-v1.4.1", labeled=False)],
        )
        assert len(actions(plan, "create")) == 1


class TestIssueApplication:
    def test_create_and_close_use_body_files_and_no_shell(self):
        calls: list[list[str]] = []

        def runner(args):
            calls.append(list(args))
            return subprocess.CompletedProcess(args, 0, "", "")

        plan = {
            "actions": [
                {
                    "kind": "close",
                    "issue_number": 1375,
                    "cli_tag": "Cli-v1.4.0",
                    "reason": "satisfied",
                    "comment": "resolved",
                },
                {
                    "kind": "create",
                    "cli_tag": "Cli-v1.4.1",
                    "reason": "missing",
                    "title": "safe title",
                    "body": "safe body",
                },
            ],
        }
        cec.apply_plan(plan, "owner/repo", runner=runner)
        assert [call[:3] for call in calls] == [
            ["gh", "issue", "comment"],
            ["gh", "issue", "close"],
            ["gh", "issue", "create"],
        ]
        assert "--body-file" in calls[0]
        assert "--body-file" in calls[2]
        assert all(isinstance(call, list) for call in calls)

    def test_nonzero_github_result_stops_reconciliation(self):
        def runner(args):
            return subprocess.CompletedProcess(args, 1, "", "permission denied")

        with pytest.raises(RuntimeError, match="permission denied"):
            cec.apply_plan({
                "actions": [{
                    "kind": "create",
                    "cli_tag": "Cli-v1.4.1",
                    "reason": "missing",
                    "title": "title",
                    "body": "body",
                }],
            }, "owner/repo", runner=runner)

    def test_generated_issue_links_only_public_runbook(self):
        body = cec.build_issue_body("Cli-v1.4.1", "Extension-v1.6.0")
        assert "docs/RELEASE.md#cli-extension-co-release-reconciliation" in body
        assert ".claude/" not in body


class TestWorkflowWiring:
    def _load_workflow(self) -> dict:
        try:
            import yaml  # type: ignore
        except ImportError:
            pytest.skip("PyYAML not installed")
        return yaml.safe_load(WORKFLOW_PATH.read_text(encoding="utf-8"))

    def test_workflow_triggers_on_both_cli_and_extension_tags(self):
        workflow = self._load_workflow()
        on = workflow.get("on") or workflow.get(True)
        tags = on["push"]["tags"]
        assert "Cli-v*" in tags
        assert "Extension-v*" in tags

    def test_workflow_serializes_reconciliation_and_lists_all_records(self):
        workflow = self._load_workflow()
        job = workflow["jobs"]["extension-corelease-detection"]
        assert job["concurrency"]["cancel-in-progress"] is False
        runs = "\n".join(
            str(step.get("run", "")) for step in job["steps"]
        )
        assert "--state all" in runs
        assert "--apply" in runs
        assert "--has-open-issue" not in runs
        assert "gh issue create" not in runs
        assert "gh issue close" not in runs
        checkout = next(
            step for step in job["steps"]
            if str(step.get("uses", "")).startswith("actions/checkout@")
        )
        assert checkout["with"]["persist-credentials"] is False

    def test_workflow_retains_least_permissions(self):
        workflow = self._load_workflow()
        assert workflow["permissions"] == {"contents": "read", "issues": "write"}
