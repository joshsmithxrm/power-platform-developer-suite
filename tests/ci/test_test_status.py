"""Behavior and workflow wiring for the required test aggregate."""
import importlib.util
import re
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
        "test-linux": {"result": "skipped"},
        "single-file-extraction": {"result": "skipped"},
        "vsix-artifacts": {"result": "skipped"},
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


@pytest.mark.parametrize("job", ["test", "test-linux"])
@pytest.mark.parametrize("result", ["failure", "cancelled", "skipped", "missing"])
def test_selected_dotnet_suite_must_succeed_on_each_platform(job, result):
    payload = needs()
    payload["changes"]["outputs"]["code"] = "true"
    payload["test"]["result"] = payload["test-linux"]["result"] = "success"
    assert gate.failures(payload) == []
    payload[job]["result"] = result
    assert any(error.startswith(f"{job}:") for error in gate.failures(payload))


def test_linux_dotnet_suite_runs_the_same_tests_and_feeds_the_gate():
    jobs = yaml.safe_load((ROOT / ".github/workflows/test.yml").read_text())["jobs"]
    windows, linux = jobs["test"], jobs["test-linux"]
    assert windows["runs-on"] == "windows-latest" and linux["runs-on"] == "ubuntu-latest"
    assert linux["needs"] == "changes" and linux["if"] == windows["if"] == "needs.changes.outputs.code == 'true'"
    assert linux["timeout-minutes"] <= 30
    assert "test-linux" in jobs["test-status"]["needs"]
    assert ("code", "test-linux") in gate.SUITES

    def step(job, name):
        return next(item for item in job["steps"] if item.get("name") == name)

    # Same SDKs and the same selection of tests; only coverage collection differs.
    assert step(linux, "Setup .NET")["with"] == step(windows, "Setup .NET")["with"]
    selection = '--filter "Category!=Integration"'
    assert selection in step(windows, "Test with Coverage")["run"]
    assert step(linux, "Test")["run"] == (
        f"dotnet test --configuration Release --no-build --verbosity normal {selection}"
        " --logger trx --results-directory ./test-results")
    # A failure names its tests as annotations; naming them never changes the job's result.
    naming = step(linux, "Name the failed tests")
    assert naming["if"] == "failure()" and "::error title=Failed test::" in naming["run"]
    assert linux["steps"].index(naming) == linux["steps"].index(step(linux, "Test")) + 1
    assert "continue-on-error" not in linux and all("continue-on-error" not in item for item in linux["steps"])
    assert "secrets." not in yaml.safe_dump(linux)


@pytest.mark.parametrize("result", ["failure", "cancelled", "skipped", "missing"])
def test_selected_vsix_matrix_must_succeed(result):
    payload = needs()
    payload["changes"]["outputs"]["extraction"] = "true"
    payload["single-file-extraction"]["result"] = "success"
    payload["vsix-artifacts"]["result"] = result
    assert any("vsix-artifacts" in error for error in gate.failures(payload))


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
    # The Windows job must keep existing and keep failing (not skipping) without its tools.
    windows = called["jobs"]["windows-tool-tests"]
    assert windows["runs-on"] == "windows-2025" and windows["env"]["PPDS_REQUIRE_WINDOWS_TOOLS"] == "1"
    assert windows["timeout-minutes"] <= 30
    dotnet = next(step for step in windows["steps"] if step.get("uses", "").startswith("actions/setup-dotnet@"))
    assert re.fullmatch(r"actions/setup-dotnet@[0-9a-f]{40}", dotnet["uses"])  # Pinned to a commit.
    assert dotnet["with"] == {"global-json-file": "global.json"}
    command = next(step["run"] for step in windows["steps"] if "pytest" in step.get("run", "") and "-q" in step["run"])
    for target in ("tests/ci/test_inspect_release_assemblies.py",
                   "test_pwsh_needs_pathext_to_launch_a_native_tool_under_the_driver_environment",
                   "test_signer_process_preserves_common_data_without_inheriting_secrets"):
        assert target in command
