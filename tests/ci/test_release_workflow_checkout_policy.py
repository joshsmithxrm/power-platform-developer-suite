"""Policy tests for checkout usage in release-tracking workflows."""
from __future__ import annotations

from pathlib import Path

import pytest

yaml = pytest.importorskip("yaml")

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_DIR = REPO_ROOT / ".github" / "workflows"


def _load_workflow(name: str) -> dict:
    with (WORKFLOW_DIR / name).open(encoding="utf-8") as stream:
        return yaml.safe_load(stream)


def _event_config(workflow: dict) -> dict:
    """Return the workflow trigger map across YAML 1.1/1.2 parsers."""
    return workflow.get("on") or workflow.get(True)


def _checkout_steps(workflow: dict) -> list[dict]:
    return [
        step
        for job in workflow["jobs"].values()
        for step in job.get("steps", [])
        if str(step.get("uses", "")).startswith("actions/checkout@")
    ]


@pytest.mark.parametrize(
    ("workflow_name", "expected_count"),
    [
        ("milestone-release-check.yml", 1),
        ("post-merge-release-check.yml", 2),
        ("release-cadence-check.yml", 1),
    ],
)
def test_release_tracking_workflows_use_checkout_v6(
    workflow_name: str,
    expected_count: int,
) -> None:
    workflow = _load_workflow(workflow_name)
    checkout_steps = _checkout_steps(workflow)

    assert len(checkout_steps) == expected_count
    assert {step["uses"] for step in checkout_steps} == {"actions/checkout@v6"}


def test_post_merge_checkout_preserves_event_revision_and_full_history() -> None:
    workflow = _load_workflow("post-merge-release-check.yml")
    patch_steps = workflow["jobs"]["patch-release-detection"]["steps"]
    patch_checkout = next(
        step for step in patch_steps if str(step.get("uses", "")).startswith("actions/checkout@")
    )
    corelease_steps = workflow["jobs"]["extension-corelease-detection"]["steps"]
    corelease_checkout = next(
        step for step in corelease_steps if str(step.get("uses", "")).startswith("actions/checkout@")
    )

    assert patch_checkout["with"] == {
        "ref": "${{ github.event.pull_request.merge_commit_sha }}",
        "fetch-depth": 0,
    }
    assert corelease_checkout["with"] == {"fetch-depth": 0}
    assert workflow["permissions"] == {
        "contents": "read",
        "issues": "write",
        "pull-requests": "read",
    }

    events = _event_config(workflow)
    assert "pull_request_target" not in events
    assert events["pull_request"] == {"types": ["closed"], "branches": ["main"]}
    assert events["push"] == {"tags": ["Cli-v*"]}


def test_cadence_checkout_preserves_main_ref_and_full_history() -> None:
    workflow = _load_workflow("release-cadence-check.yml")
    checkout = _checkout_steps(workflow)[0]

    assert checkout["with"] == {"ref": "main", "fetch-depth": 0}
    assert workflow["permissions"] == {"contents": "read", "issues": "write"}


def test_milestone_checkout_preserves_event_revision_and_permissions() -> None:
    workflow = _load_workflow("milestone-release-check.yml")
    checkout = _checkout_steps(workflow)[0]

    assert "with" not in checkout
    assert workflow["permissions"] == {
        "contents": "read",
        "issues": "write",
        "pull-requests": "read",
    }
    assert _event_config(workflow)["milestone"] == {"types": ["closed"]}
