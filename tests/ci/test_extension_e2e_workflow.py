"""Configuration tests for the blocking Extension E2E workflow gate."""
from __future__ import annotations

from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = REPO_ROOT / ".github" / "workflows" / "build.yml"
RAMP_DOC_PATH = REPO_ROOT / "src" / "PPDS.Extension" / "e2e" / "README.md"


def _load_workflow() -> dict:
    try:
        import yaml  # type: ignore
    except ImportError:
        pytest.skip("PyYAML not installed; cannot parse workflow YAML")

    with WORKFLOW_PATH.open(encoding="utf-8") as workflow_file:
        return yaml.safe_load(workflow_file)


def _step(job: dict, name: str) -> dict:
    return next(
        step for step in job["steps"]
        if step.get("name") == name
    )


def test_extension_e2e_matrix_is_blocking_and_keeps_its_supported_versions():
    workflow = _load_workflow()
    job = workflow["jobs"]["extension-e2e"]
    e2e_step = _step(job, "Run Extension E2E tests")

    assert job["strategy"]["matrix"]["vscode-version"] == ["minimum", "stable"]
    assert "continue-on-error" not in job
    assert "continue-on-error" not in e2e_step
    assert e2e_step["env"]["VSCODE_E2E_VERSION"] == "${{ matrix.vscode-version }}"
    assert "xvfb-run" in e2e_step["run"]
    assert "npm run test:e2e" in e2e_step["run"]
    assert _step(job, "Setup Node.js")["with"]["node-version"] == "22"


def test_build_status_consumes_the_blocking_e2e_result():
    workflow = _load_workflow()
    gate = workflow["jobs"]["build-status"]
    gate_script = _step(gate, "Check build status")["run"]

    assert "extension-e2e" in gate["needs"]
    assert "needs.extension-e2e.result" in gate_script


def test_failed_e2e_run_always_uploads_diagnostics():
    workflow = _load_workflow()
    job = workflow["jobs"]["extension-e2e"]
    diagnostics = _step(job, "Upload E2E diagnostics")

    assert diagnostics["if"] == "always() && steps.e2e.outcome == 'failure'"
    assert diagnostics["with"]["if-no-files-found"] == "warn"
    assert "playwright-report/" in diagnostics["with"]["path"]
    assert "test-results/" in diagnostics["with"]["path"]


def test_documentation_records_completed_promotion():
    documentation = RAMP_DOC_PATH.read_text(encoding="utf-8").lower()

    assert "blocking ci gate" in documentation
    assert "four consecutive clean" in documentation
    assert "pull-request and `main` runs" in documentation
    assert "without consuming the startup retry" in documentation
