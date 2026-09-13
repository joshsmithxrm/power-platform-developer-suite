"""Behavioral tests for safe, idempotent patch release records."""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import patch_release_issue as pri  # noqa: E402


FIXTURE = Path(__file__).parent / "fixtures" / "pr_1399_labeled_event.json"
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "post-merge-release-check.yml"


def _late_label_payload() -> dict:
    return json.loads(FIXTURE.read_text(encoding="utf-8"))


def _closed_payload(number: int = 1400) -> dict:
    payload = _late_label_payload()
    payload["action"] = "closed"
    payload["number"] = number
    payload.pop("label", None)
    payload["pull_request"]["number"] = number
    payload["pull_request"]["title"] = "fix: a patch-worthy change"
    return payload


def _plan(*, release_needed: bool = True) -> dict:
    return {
        "release_needed": release_needed,
        "release_targets": ["PPDS.Cli", "PPDS.Extension"] if release_needed else [],
    }


def _release_record(number: int, state: str, body: str) -> dict:
    return {
        "number": number,
        "state": state,
        "body": body,
        "labels": [{"name": pri.PATCH_LABEL}],
    }


class TestEventEligibility:
    def test_label_present_when_pr_merges(self) -> None:
        assert pri.event_is_eligible(pri.parse_event(_closed_payload())) is True

    def test_closed_event_without_patch_label_is_not_eligible(self) -> None:
        payload = _closed_payload()
        payload["pull_request"]["labels"] = []
        assert pri.event_is_eligible(pri.parse_event(payload)) is False

    def test_pr_1399_late_label_fixture_is_eligible(self) -> None:
        event = pri.parse_event(_late_label_payload())
        assert event.number == 1399
        assert event.action == "labeled"
        assert event.merged is True
        assert pri.event_is_eligible(event) is True

    def test_label_on_unmerged_pr_is_not_eligible(self) -> None:
        payload = _late_label_payload()
        payload["pull_request"]["merged"] = False
        assert pri.event_is_eligible(pri.parse_event(payload)) is False

    def test_label_on_non_main_pr_is_not_eligible(self) -> None:
        payload = _late_label_payload()
        payload["pull_request"]["base"]["ref"] = "release/maintenance"
        assert pri.event_is_eligible(pri.parse_event(payload)) is False

    def test_unrelated_label_on_merged_pr_is_not_eligible(self) -> None:
        payload = _late_label_payload()
        payload["label"]["name"] = "type:bug"
        assert pri.event_is_eligible(pri.parse_event(payload)) is False

    def test_merged_patch_pr_from_fork_is_eligible(self) -> None:
        payload = _late_label_payload()
        payload["pull_request"]["head"] = {
            "ref": "contributor-patch",
            "repo": {
                "fork": True,
                "full_name": "contributor/power-platform-developer-suite",
            },
        }

        event = pri.parse_event(payload)

        assert event.repository == "joshsmithxrm/power-platform-developer-suite"
        assert pri.event_is_eligible(event) is True

    def test_workflow_triggers_on_close_and_late_label(self) -> None:
        import yaml

        workflow = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
        triggers = workflow.get("on") or workflow.get(True)
        assert "pull_request" not in triggers
        assert set(triggers["pull_request_target"]["types"]) == {
            "closed",
            "labeled",
        }
        assert triggers["pull_request_target"]["branches"] == ["main"]


class TestPatchRecordIdempotency:
    def test_first_eligible_event_creates_record(self) -> None:
        event = pri.parse_event(_closed_payload())
        decision = pri.decide_patch_record(event, _plan(), [])
        assert decision["should_create"] is True
        assert decision["marker"] == pri.record_marker(1400)

    @pytest.mark.parametrize("state", ["OPEN", "CLOSED"])
    def test_existing_record_in_any_state_is_not_duplicated_or_reopened(
        self, state: str
    ) -> None:
        event = pri.parse_event(_closed_payload())
        existing = [
            _release_record(77, state, f"prior body\n{event.marker}\n")
        ]
        decision = pri.decide_patch_record(event, _plan(), existing)
        assert decision == {
            "should_create": False,
            "reason": "existing-record",
            "marker": event.marker,
            "existing_issue_number": 77,
            "existing_issue_state": state,
        }

    def test_label_removal_and_readd_still_reuses_closed_record(self) -> None:
        payload = _closed_payload()
        payload["action"] = "labeled"
        payload["label"] = {"name": pri.PATCH_LABEL}
        event = pri.parse_event(payload)
        decision = pri.decide_patch_record(
            event,
            _plan(),
            [_release_record(88, "CLOSED", event.marker)],
        )
        assert decision["should_create"] is False
        assert decision["existing_issue_state"] == "CLOSED"

    def test_different_prs_receive_different_records(self) -> None:
        first = pri.parse_event(_closed_payload(1400))
        second = pri.parse_event(_closed_payload(1401))
        assert first.marker != second.marker

        existing = [_release_record(90, "OPEN", first.marker)]
        assert pri.decide_patch_record(second, _plan(), existing)["should_create"] is True

    def test_unlabeled_spoof_marker_does_not_suppress_record(self) -> None:
        event = pri.parse_event(_closed_payload())
        spoof = {
            "number": 91,
            "state": "OPEN",
            "body": event.marker,
            "labels": [{"name": "type:bug"}],
        }

        decision = pri.decide_patch_record(event, _plan(), [spoof])

        assert decision["should_create"] is True
        assert decision["reason"] == "create-record"

    def test_no_product_impact_does_not_create_record(self) -> None:
        event = pri.parse_event(_closed_payload())
        decision = pri.decide_patch_record(event, _plan(release_needed=False), [])
        assert decision["should_create"] is False
        assert decision["reason"] == "no-product-impact"

    def test_workflow_serializes_and_searches_all_issue_states(self) -> None:
        text = WORKFLOW.read_text(encoding="utf-8")
        assert "patch-release-record-${{ github.event.pull_request.number }}" in text
        assert '--label "release:patch"' in text
        assert "--state all" in text
        assert "--json number,state,body,labels" in text


class TestWorkflowTrustBoundary:
    def test_fork_events_use_trusted_automation_and_data_only_merge_checkout(
        self,
    ) -> None:
        import yaml

        workflow_text = WORKFLOW.read_text(encoding="utf-8")
        workflow = yaml.safe_load(workflow_text)
        assert "github.event.pull_request.head" not in workflow_text
        triggers = workflow.get("on") or workflow.get(True)
        assert "pull_request_target" in triggers
        assert "pull_request" not in triggers
        assert workflow["permissions"] == {"contents": "read", "issues": "write"}

        job = workflow["jobs"]["patch-release-detection"]
        condition = job["if"]
        assert "github.event.pull_request.merged == true" in condition
        assert "github.event.pull_request.base.ref == 'main'" in condition
        assert "github.event.action == 'closed'" in condition
        assert "github.event.action == 'labeled'" in condition
        assert "github.event.label.name == 'release:patch'" in condition

        checkouts = {
            step["name"]: step
            for step in job["steps"]
            if str(step.get("uses", "")).startswith("actions/checkout@")
        }
        analyzed = checkouts["Checkout analyzed merge commit"]["with"]
        automation = checkouts["Checkout pinned release automation"]["with"]
        assert analyzed["ref"] == "${{ github.event.pull_request.merge_commit_sha }}"
        assert analyzed["path"] == "analyzed"
        assert analyzed["persist-credentials"] is False
        assert automation["ref"] == "${{ github.workflow_sha }}"
        assert automation["path"] == "automation"
        assert automation["persist-credentials"] is False

        run_text = "\n".join(
            str(step.get("run", "")) for step in job["steps"]
        )
        assert all(
            not str(step.get("uses", "")).startswith("./analyzed")
            for step in job["steps"]
        )
        assert all(
            step.get("working-directory") != "analyzed" for step in job["steps"]
        )
        assert "python analyzed/" not in run_text
        assert "bash analyzed/" not in run_text
        assert "sh analyzed/" not in run_text
        assert "./analyzed/" not in run_text
        assert "python automation/scripts/ci/release_plan.py" in run_text
        assert "python automation/scripts/ci/patch_release_issue.py" in run_text
        assert "--repo-root analyzed" in run_text


class TestSafeIssueRendering:
    def test_hostile_title_is_data_and_cannot_change_issue_structure(self) -> None:
        payload = _closed_payload()
        hostile = '</pre><h1>injected</h1>\n$(touch owned)\n`whoami` & "quoted"'
        payload["pull_request"]["title"] = hostile
        event = pri.parse_event(payload)

        title = pri.build_issue_title(event.number, _plan()["release_targets"])
        body = pri.build_issue_body(event, "## Advisory\n\nSafe plan.")

        assert hostile not in title
        assert "&lt;/pre&gt;&lt;h1&gt;injected&lt;/h1&gt;" in body
        assert "</pre><h1>injected" not in body
        assert body.count("<pre>") == 1
        assert body.count("</pre>") == 1
        assert event.marker in body

    def test_body_links_public_release_runbook(self) -> None:
        event = pri.parse_event(_closed_payload())
        body = pri.build_issue_body(event, "## Advisory")
        assert (
            "https://github.com/joshsmithxrm/power-platform-developer-suite/"
            "blob/main/docs/RELEASE.md#release-scope-analysis"
        ) in body
        assert ".claude/skills" not in body

    def test_cli_writes_title_body_and_decision_files(self, tmp_path: Path) -> None:
        event_path = tmp_path / "event.json"
        plan_path = tmp_path / "plan.json"
        markdown_path = tmp_path / "plan.md"
        existing_path = tmp_path / "issues.json"
        title_path = tmp_path / "title.txt"
        body_path = tmp_path / "body.md"
        decision_path = tmp_path / "decision.json"
        payload = _closed_payload()
        payload["pull_request"]["title"] = "line one\n$(echo not-executed)"
        event_path.write_text(json.dumps(payload), encoding="utf-8")
        plan_path.write_text(json.dumps(_plan()), encoding="utf-8")
        markdown_path.write_text("## Advisory\n", encoding="utf-8")
        existing_path.write_text("[]", encoding="utf-8")

        rc = pri.main(
            [
                "--event", str(event_path),
                "--plan-json", str(plan_path),
                "--plan-markdown", str(markdown_path),
                "--existing-issues", str(existing_path),
                "--title-out", str(title_path),
                "--body-out", str(body_path),
                "--decision-out", str(decision_path),
            ]
        )

        assert rc == 0
        assert title_path.read_text(encoding="utf-8").startswith(
            "Patch release review for PR #1400"
        )
        assert "$(echo not-executed)" in body_path.read_text(encoding="utf-8")
        assert json.loads(decision_path.read_text(encoding="utf-8"))["should_create"] is True

    def test_workflow_passes_python_body_file_without_pr_title_interpolation(self) -> None:
        text = WORKFLOW.read_text(encoding="utf-8")
        assert "automation/scripts/ci/patch_release_issue.py" in text
        assert "--body-file issue-body.md" in text
        assert "PR_TITLE=" not in text
        assert ".pull_request.title" not in text
