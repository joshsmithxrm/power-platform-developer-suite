"""Tests for docs-release.yml workflow build configuration.

Verifies that the build step targets specific projects (not the entire
solution) and references the correct assembly names for the reflectors.

Root cause: dotnet build PPDS.sln -c Release -o artifacts/bin triggers
NETSDK1194 — test projects race-write runtimeconfig.json into the shared
output directory, causing IO failures.

Run with: python -m pytest tests/ci/test_docs_release_workflow.py -v
"""
from __future__ import annotations

from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = REPO_ROOT / ".github" / "workflows" / "docs-release.yml"


@pytest.fixture(scope="module")
def workflow() -> dict:
    """Parse the workflow YAML once per module."""
    if not WORKFLOW_PATH.exists():
        pytest.fail(f"docs-release.yml not found at {WORKFLOW_PATH}")
    return yaml.safe_load(WORKFLOW_PATH.read_text(encoding="utf-8")) or {}


@pytest.fixture(scope="module")
def workflow_text() -> str:
    """Raw text of the workflow for pattern-based assertions."""
    return WORKFLOW_PATH.read_text(encoding="utf-8")


def test_build_does_not_target_solution(workflow_text: str) -> None:
    """The build step must NOT run 'dotnet build PPDS.sln' with -o.

    NETSDK1194: The --output option isn't supported when building a
    solution. All projects race-write to the same dir causing IO failures.
    """
    assert "dotnet build PPDS.sln" not in workflow_text, (
        "Build must target individual projects, not the solution. "
        "dotnet build PPDS.sln -o triggers NETSDK1194."
    )


def test_build_targets_specific_projects(workflow_text: str) -> None:
    """Build step must explicitly build PPDS.Cli and PPDS.Mcp projects."""
    assert "PPDS.Cli.csproj" in workflow_text or "PPDS.Cli/" in workflow_text, (
        "Build step must target PPDS.Cli project explicitly"
    )
    assert "PPDS.Mcp.csproj" in workflow_text or "PPDS.Mcp/" in workflow_text, (
        "Build step must target PPDS.Mcp project explicitly"
    )


def test_cli_assembly_name_is_correct(workflow_text: str) -> None:
    """cli-reflect must reference ppds.dll (PPDS.Cli's AssemblyName is 'ppds')."""
    assert "ppds.dll" in workflow_text, (
        "cli-reflect must use the actual assembly name ppds.dll, "
        "not PPDS.Cli.dll"
    )


def test_mcp_assembly_name_is_correct(workflow_text: str) -> None:
    """mcp-reflect must reference ppds-mcp-server.dll (PPDS.Mcp's AssemblyName)."""
    assert "ppds-mcp-server.dll" in workflow_text, (
        "mcp-reflect must use the actual assembly name ppds-mcp-server.dll, "
        "not PPDS.Mcp.dll"
    )


def test_dry_run_dispatch_preserved(workflow: dict) -> None:
    """workflow_dispatch with dry_run input must be preserved."""
    on_config = workflow.get("on") or workflow.get(True) or {}
    assert "workflow_dispatch" in on_config, "workflow_dispatch trigger must be preserved"
    dispatch = on_config.get("workflow_dispatch") or {}
    inputs = dispatch.get("inputs", {})
    assert "dry_run" in inputs, "dry_run input must be preserved"
