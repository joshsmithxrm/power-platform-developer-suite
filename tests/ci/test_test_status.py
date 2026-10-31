"""Behavior and workflow wiring for the required test aggregate."""
import importlib.util
from pathlib import Path

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("test_status", ROOT / "scripts/ci/check_test_status.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def needs(selected="true", result="success"):
    return {
        "changes": {"result": "success", "outputs": {"code": "false", "extraction": "false", "python": selected}},
        "test": {"result": "skipped"},
        "single-file-extraction": {"result": "skipped"},
        "python-tests": {"result": result},
    }


@pytest.mark.parametrize("selected,result,passes", [
    ("true", "success", True), ("true", "failure", False),
    ("true", "cancelled", False), ("true", "skipped", False),
    ("true", "missing", False), ("false", "skipped", True),
    ("false", "success", True), ("false", "cancelled", False),
    ("false", "failure", False), ("false", "missing", False),
    (None, "skipped", False), ("", "success", False),
])
def test_suite_truth_table(selected, result, passes):
    assert (not gate.failures(needs(selected, result))) == passes


@pytest.mark.parametrize("result", ["failure", "cancelled", "skipped", None])
def test_classifier_must_succeed(result):
    payload = needs()
    payload["changes"]["result"] = result
    assert gate.failures(payload)


def test_needed_dotnet_cannot_unexpectedly_skip():
    payload = needs()
    payload["changes"]["outputs"]["code"] = "true"
    assert gate.failures(payload)


def test_reusable_python_suite_feeds_always_reporting_gate():
    workflow = yaml.safe_load((ROOT / ".github/workflows/test.yml").read_text())
    trigger = workflow.get("on", workflow.get(True))
    assert "paths" not in trigger["pull_request"]
    jobs = workflow["jobs"]
    assert jobs["python-tests"]["uses"] == "./.github/workflows/workflow-tests.yml"
    assert jobs["python-tests"]["if"] == "needs.changes.outputs.python == 'true'"
    assert "python-tests" in jobs["test-status"]["needs"]
    assert jobs["test-status"]["if"] == "always()"
    step = jobs["test-status"]["steps"][-1]
    assert step["env"]["TEST_NEEDS_JSON"] == "${{ toJSON(needs) }}"
    assert step["run"] == "python3 scripts/ci/check_test_status.py"
    filters = yaml.safe_load(jobs["changes"]["steps"][1]["with"]["filters"])
    for required in ["src/**", "tests/**", "scripts/**", ".github/**", "specs/**", ".claude/**", "README.md",
                     "pyproject.toml", "pytest.ini", "**/conftest.py", "**/requirements*.txt"]:
        assert required in filters["python"]
    called = yaml.safe_load((ROOT / ".github/workflows/workflow-tests.yml").read_text())
    called_triggers = called.get("on", called.get(True))
    assert "workflow_call" in called_triggers
    assert "pull_request" not in called_triggers
    assert "push" not in called_triggers
    assert "concurrency" not in called
    assert any(step.get("run") == "python -m pytest tests/ -q"
               for step in called["jobs"]["workflow-tests"]["steps"])
