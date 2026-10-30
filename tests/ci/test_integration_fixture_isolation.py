"""Keep concurrent PR/main runs from deploying and cleaning the same live fixture."""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = ".github/workflows/integration-tests.yml"


@pytest.fixture(scope="module")
def workflow() -> dict:
    return yaml.safe_load((REPO_ROOT / WORKFLOW_PATH).read_text(encoding="utf-8"))


def test_main_and_distinct_pull_requests_share_the_fixture_lock(workflow: dict) -> None:
    groups = set()
    for ref in ("refs/heads/main", "refs/pull/1477/merge", "refs/pull/1480/merge"):
        group = workflow["concurrency"]["group"]
        context = {
            "workflow": workflow["name"],
            "repository": "owner/ppds",
            "ref": ref,
        }
        for key, value in context.items():
            group = group.replace("$" + "{{ github." + key + " }}", value)
        assert "$" + "{{" not in group, "New group expressions need cross-run isolation coverage"
        assert group.strip(), "A shared fixture requires a nonempty concurrency group"
        groups.add(group.casefold())
    assert len(groups) == 1, "PR and main runs must not deploy/clean the fixture concurrently"


def test_active_cleanup_and_older_pending_runs_are_not_cancelled(workflow: dict) -> None:
    concurrency = workflow["concurrency"]
    assert concurrency.get("cancel-in-progress", False) is False
    assert concurrency.get("queue") == "max", "Default queueing replaces the older pending run"


@pytest.mark.parametrize("event", ["push", "pull_request"])
def test_workflow_edits_receive_the_integration_gate(workflow: dict, event: str) -> None:
    triggers = workflow.get("on") or workflow[True]  # YAML 1.1 treats 'on' as a boolean.
    assert WORKFLOW_PATH in triggers[event]["paths"]


def test_shared_lock_preserves_parallel_shards_and_blocking_aggregate(workflow: dict) -> None:
    job = workflow["jobs"]["integration-tests"]
    assert job["environment"] == "test-dataverse"
    assert "concurrency" not in job, "The shared lock belongs around the whole run"
    assert job["strategy"]["fail-fast"] is False
    assert job["strategy"].get("max-parallel", 5) >= 5
    assert {row["shard"] for row in job["strategy"]["matrix"]["include"]} == {
        "daemon", "auth", "pooling", "bulk-resilience", "cli-smoke",
    }
    assert not job.get("continue-on-error", False)
    gate = workflow["jobs"]["integration-tests-status"]
    assert gate["name"] == "Integration Tests"
    assert gate["needs"] == "integration-tests"
    assert gate["if"] == "always()"
    assert "needs.integration-tests.result" in gate["steps"][0]["run"]
