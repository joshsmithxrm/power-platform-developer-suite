"""Tests for the authoritative workflow-owned release-label manifest."""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import sync_release_labels as srl  # noqa: E402


MANIFEST = REPO_ROOT / "scripts" / "ci" / "release_labels.json"
WORKFLOWS = [
    REPO_ROOT / ".github" / "workflows" / "post-merge-release-check.yml",
    REPO_ROOT / ".github" / "workflows" / "milestone-release-check.yml",
    REPO_ROOT / ".github" / "workflows" / "release-cadence-check.yml",
]


def test_manifest_defines_exactly_four_workflow_owned_release_labels() -> None:
    labels = srl.load_manifest(MANIFEST)
    assert {label.name for label in labels} == {
        "release:patch",
        "release:minor",
        "release:cadence-check",
        "release:extension-corelease",
    }
    assert all(label.description for label in labels)


def test_sync_uses_idempotent_create_or_update_for_every_label() -> None:
    commands: list[list[str]] = []

    def runner(command):
        commands.append(list(command))
        return subprocess.CompletedProcess(command, 0, stdout="", stderr="")

    labels = srl.load_manifest(MANIFEST)
    srl.sync_labels("owner/repo", labels, runner=runner)
    srl.sync_labels("owner/repo", labels, runner=runner)

    assert len(commands) == len(labels) * 2
    assert all(command[:3] == ["gh", "label", "create"] for command in commands)
    assert all("--force" in command for command in commands)
    assert all("--repo" in command and "owner/repo" in command for command in commands)


def test_sync_failure_names_exact_label_and_cli_error() -> None:
    def runner(command):
        return subprocess.CompletedProcess(
            command,
            1,
            stdout="",
            stderr="HTTP 403: Resource not accessible by integration",
        )

    label = srl.ReleaseLabel("release:patch", "0E8A16", "Patch release")
    with pytest.raises(srl.LabelSyncError) as error:
        srl.sync_labels("owner/repo", [label], runner=runner)

    assert "release:patch" in str(error.value)
    assert "owner/repo" in str(error.value)
    assert "HTTP 403" in str(error.value)


def test_release_workflows_reconcile_labels_from_manifest() -> None:
    for workflow in WORKFLOWS:
        text = workflow.read_text(encoding="utf-8")
        assert "scripts/ci/sync_release_labels.py" in text, workflow
    combined = "\n".join(path.read_text(encoding="utf-8") for path in WORKFLOWS)
    assert 'gh label create "release:' not in combined
