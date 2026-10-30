"""Least-privilege token policy for GitHub Actions (issue #1231).

Inspect every workflow, including future jobs, rather than pinning a job count.
The intentional write/OIDC grants below are also documented in
docs/CI-PERMISSIONS.md; changing them requires an explicit policy decision.
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_DIR = REPO_ROOT / ".github" / "workflows"
WORKFLOW_NAMES = sorted(
    path.name for path in WORKFLOW_DIR.iterdir()
    if path.suffix in {".yml", ".yaml"}
)
UNPRIVILEGED_WORKFLOWS = (
    "build.yml",
    "test.yml",
    "docs-smoke.yml",
    "marketplace-listing-gate.yml",
    "tui-e2e.yml",
    "workflow-tests.yml",
    "reference-cache-trust.yml",
)

# Each entry is a required, bounded exception, not permission to give writes
# to every job in the same workflow. New read-only jobs need no allowlist edit.
WRITE_PERMISSION_REASONS = {
    ("codeql.yml", "analyze", "security-events"): "Upload CodeQL SARIF results.",
    ("dependabot-label.yml", "label-major-updates", "issues"):
        "Maintain dependency labels using the trusted base workflow.",
    ("dependabot-label.yml", "label-major-updates", "pull-requests"):
        "Apply labels to Dependabot pull requests.",
    ("docs-release.yml", "release", "contents"):
        "Preserve the existing documentation release write grant.",
    ("docs-release.yml", "release", "pull-requests"):
        "Preserve the existing documentation release PR write grant.",
    ("integration-tests.yml", "integration-tests", "id-token"):
        "Request OIDC tokens for the configured test environment.",
    ("milestone-release-check.yml", "check-milestone-completion", "issues"):
        "Create or update milestone release tracking issues.",
    ("post-merge-release-check.yml", "patch-release-detection", "issues"):
        "Create or update patch release tracking issues.",
    ("post-merge-release-check.yml", "extension-corelease-detection", "issues"):
        "Create or update extension co-release tracking issues.",
    ("release-cadence-check.yml", "cadence-check", "issues"):
        "Create or update release cadence tracking issues.",
    ("release-cli.yml", "release", "contents"):
        "Publish the CLI GitHub release and assets.",
}


def _load_workflow(name: str) -> dict:
    workflow = yaml.safe_load((WORKFLOW_DIR / name).read_text(encoding="utf-8"))
    assert isinstance(workflow, dict), f"{name}: expected a workflow mapping"
    assert isinstance(workflow.get("jobs"), dict) and workflow["jobs"], (
        f"{name}: expected at least one job"
    )
    return workflow


def _effective_permissions(workflow: dict, job: dict):
    # A job map replaces the workflow map; an explicit {} must not fall back
    # to a workflow grant. Omitted scopes in either map are denied by GitHub.
    return job.get("permissions", workflow.get("permissions"))


def _assert_permissions_map(permissions, context: str) -> None:
    assert isinstance(permissions, dict), (
        f"{context}: declare an explicit permissions map; repository defaults, "
        "read-all, write-all, null and expressions are not allowed"
    )
    assert all(
        isinstance(scope, str) and level in {"read", "write", "none"}
        for scope, level in permissions.items()
    ), f"{context}: invalid permission scope/level: {permissions!r}"


@pytest.mark.parametrize("workflow_name", WORKFLOW_NAMES)
def test_every_job_has_explicit_permissions(workflow_name: str) -> None:
    workflow = _load_workflow(workflow_name)
    if "permissions" in workflow:
        _assert_permissions_map(workflow["permissions"], workflow_name)
    for job_name, job in workflow["jobs"].items():
        _assert_permissions_map(
            _effective_permissions(workflow, job), f"{workflow_name}:{job_name}"
        )


@pytest.mark.parametrize("workflow_name", UNPRIVILEGED_WORKFLOWS)
def test_unprivileged_workflows_use_minimum_permissions(workflow_name: str) -> None:
    workflow = _load_workflow(workflow_name)
    assert workflow.get("permissions") == {"contents": "read"}, (
        f"{workflow_name}: new jobs must inherit a contents: read baseline"
    )
    for job_name, job in workflow["jobs"].items():
        uses_paths_filter = any(
            str(step.get("uses", "")).startswith("dorny/paths-filter@")
            for step in job.get("steps", [])
        )
        if job_name.endswith("-status"):
            expected = {}
        elif uses_paths_filter:
            expected = {"contents": "read", "pull-requests": "read"}
        else:
            expected = {"contents": "read"}
        assert _effective_permissions(workflow, job) == expected, (
            f"{workflow_name}:{job_name}: expected only {expected!r}, got "
            f"{_effective_permissions(workflow, job)!r}"
        )


@pytest.mark.parametrize(
    "workflow_name", (*UNPRIVILEGED_WORKFLOWS, "integration-tests.yml")
)
def test_unprivileged_checkouts_do_not_persist_credentials(workflow_name: str) -> None:
    workflow = _load_workflow(workflow_name)
    for job_name, job in workflow["jobs"].items():
        for step_index, step in enumerate(job.get("steps", []), start=1):
            if str(step.get("uses", "")).startswith("actions/checkout@"):
                assert step.get("with", {}).get("persist-credentials") is False, (
                    f"{workflow_name}:{job_name}:step {step_index}: checkout must "
                    "explicitly set persist-credentials: false"
                )


def test_write_permissions_match_documented_exceptions() -> None:
    actual = set()
    for workflow_name in WORKFLOW_NAMES:
        workflow = _load_workflow(workflow_name)
        for job_name, job in workflow["jobs"].items():
            permissions = _effective_permissions(workflow, job)
            _assert_permissions_map(permissions, f"{workflow_name}:{job_name}")
            actual.update(
                (workflow_name, job_name, scope)
                for scope, level in permissions.items() if level == "write"
            )
    expected = set(WRITE_PERMISSION_REASONS)
    assert actual == expected, (
        f"Undocumented write/OIDC grants: {sorted(actual - expected)}; "
        f"required existing grants removed: {sorted(expected - actual)}"
    )


def test_integration_status_has_no_token_scopes() -> None:
    workflow = _load_workflow("integration-tests.yml")
    assert workflow["permissions"] == {}
    assert _effective_permissions(
        workflow, workflow["jobs"]["integration-tests-status"]
    ) == {}
    assert _effective_permissions(
        workflow, workflow["jobs"]["integration-tests"]
    ) == {"contents": "read", "id-token": "write"}
