"""Tests for extension-publish.yml workflow configuration (AC-14).

Verifies that the workflow triggers on Extension-v* tag pushes and
infers the release channel from the tag version's odd/even minor
convention.

Run with: python -m pytest tests/ci/test_extension_publish_workflow.py -v
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = REPO_ROOT / ".github" / "workflows" / "extension-publish.yml"


@pytest.fixture(scope="module")
def workflow() -> dict:
    """Parse the workflow YAML once per module."""
    if not WORKFLOW_PATH.exists():
        pytest.fail(f"extension-publish.yml not found at {WORKFLOW_PATH}")
    return yaml.safe_load(WORKFLOW_PATH.read_text(encoding="utf-8"))


def test_tag_push_trigger(workflow: dict) -> None:
    """AC-14: Workflow triggers on push of Extension-v* tags."""
    on = workflow.get("on") or workflow.get(True)
    assert "push" in on, "Workflow must have a push trigger"
    push = on["push"]
    assert "tags" in push, "Push trigger must filter on tags"
    tags = push["tags"]
    assert any("Extension-v" in t for t in tags), (
        f"Push tags filter must include Extension-v* pattern, got: {tags}"
    )


def test_no_release_trigger(workflow: dict) -> None:
    """The release:published trigger is removed in favor of direct tag push."""
    on = workflow.get("on") or workflow.get(True)
    assert "release" not in on, (
        "release trigger should be removed — replaced by push:tags:Extension-v*"
    )


def test_workflow_dispatch_still_available(workflow: dict) -> None:
    """Manual dispatch remains available for dry-run and channel override."""
    on = workflow.get("on") or workflow.get(True)
    assert "workflow_dispatch" in on, "workflow_dispatch trigger must be preserved"
    inputs = on["workflow_dispatch"].get("inputs", {})
    assert "dry_run" in inputs, "dry_run input must be preserved"
    assert "channel" in inputs, "channel input must be preserved"


def test_publish_conditions_allow_push_events(workflow: dict) -> None:
    """Publish steps must fire on push events (not just release/dispatch)."""
    workflow_text = WORKFLOW_PATH.read_text(encoding="utf-8")
    assert "github.event_name == 'push'" in workflow_text, (
        "Publish step conditions must allow push events"
    )


def test_channel_inference_from_tag_version(workflow: dict) -> None:
    """AC-14: Channel is inferred from tag version — odd minor = pre-release,
    even minor = stable. The workflow must contain the MINOR % 2 arithmetic."""
    workflow_text = WORKFLOW_PATH.read_text(encoding="utf-8")
    assert "MINOR % 2" in workflow_text, (
        "Channel determination must use MINOR % 2 to infer odd/even convention"
    )
    assert "Extension-v" in workflow_text and "sed" in workflow_text, (
        "Channel determination must parse minor version from Extension-v* tag"
    )


def test_publish_checkout_fetches_full_history_and_tags(workflow: dict) -> None:
    """MinVer must be able to see the Cli-v* co-release tag."""
    steps = workflow["jobs"]["publish"]["steps"]
    checkout = next(step for step in steps if step.get("name") == "Checkout code")
    checkout_with = checkout.get("with", {})
    assert checkout_with.get("fetch-depth") == 0, (
        "Extension publish checkout must fetch full history for MinVer"
    )
    assert checkout_with.get("fetch-tags") is True, (
        "Extension publish checkout must fetch tags for MinVer"
    )


def test_bundle_is_pinned_to_co_release_cli_version(workflow: dict) -> None:
    """The release must resolve an exact Cli-v* co-tag and verify the bundle."""
    steps = workflow["jobs"]["publish"]["steps"]
    resolve_cli = next(step for step in steps if step.get("id") == "resolve-cli")
    resolve_run = resolve_cli.get("run", "")
    assert "git describe --tags --exact-match --match 'Cli-v*'" in resolve_run
    assert "steps.resolve-tag.outputs.sha" in resolve_run

    bundle = next(step for step in steps if str(step.get("name", "")).startswith("Bundle CLI"))
    bundle_run = bundle.get("run", "")
    assert "--expected-version" in bundle_run
    assert "steps.resolve-cli.outputs.version" in bundle_run
