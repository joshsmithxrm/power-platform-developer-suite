"""Offline checks for the live Integration hold, not live execution coverage."""
from copy import deepcopy
import hashlib
import os
from pathlib import Path
import subprocess

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
REFUSAL_SCRIPT = (
    'echo "::error::Live integration validation is held; no live tests were run."\n'
    'echo "Requires exact source/target/suite authorization and reviewed fixture '
    'ownership/cleanup safety or an approved exclusive disposable environment lifecycle."\n'
    'exit 1\n'
)
# LF-normalized reviewed build/test workflow baselines.
# Node 26 draft refresh: reviewed tooling selection and runtime-change routing,
# plus a blocking Node 22 extension compatibility lane. Permissions and secret
# references are unchanged. Test retains the read-only artifact route, SDK
# servicing and automatic full-.NET routing for SDK/config/props/targets/workflow
# inputs; the separate Integration Tests workflow remains a tokenless refusal.
# No Git calls or mutable refs are used by these tests.
REVIEWED_WORKFLOW_HASHES = {
    "build.yml": "ac1877f8c0b7d8070b59622b3e85af2db9098a9b2d253b37948d472db18b91d1",
    "test.yml": "cbae345d66708f3f34f857858237428e840c2cc90308be8aa8c768dfe6a4c946",
}


def load_workflow(name):
    return yaml.safe_load((REPO_ROOT / ".github/workflows" / name).read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def workflow():
    return load_workflow("integration-tests.yml")


def event_key(workflow):
    return "on" if "on" in workflow else True  # YAML 1.1 parses unquoted on as True.


def assert_manual_only(workflow):
    assert workflow[event_key(workflow)] == {"workflow_dispatch": {}}


def assert_refusal_contract(workflow):
    assert_manual_only(workflow)
    assert set(workflow) == {"name", event_key(workflow), "permissions", "jobs"}
    assert workflow["name"] == "Integration Tests"
    assert workflow["permissions"] == {}
    assert set(workflow["jobs"]) == {"integration-tests-hold"}
    job = workflow["jobs"]["integration-tests-hold"]
    assert set(job) == {"name", "runs-on", "timeout-minutes", "permissions", "steps"}
    assert job["name"] == "Integration Tests (held)"
    assert job["runs-on"] == "ubuntu-latest"
    assert type(job["timeout-minutes"]) is int and job["timeout-minutes"] == 2
    assert job["permissions"] == {}
    assert job["steps"] == [{"name": "Refuse live execution", "shell": "bash", "run": REFUSAL_SCRIPT}]


def test_integration_subscribes_only_to_input_free_manual_dispatch(workflow):
    assert_manual_only(workflow)


@pytest.mark.parametrize("event", ["push", "pull_request", "pull_request_target",
                                    "workflow_run", "workflow_call", "schedule"])
def test_automatic_or_callable_trigger_mutants_are_rejected(workflow, event):
    mutant = deepcopy(workflow)
    mutant[event_key(mutant)][event] = {}
    with pytest.raises(AssertionError):
        assert_manual_only(mutant)


def test_manual_dispatch_input_mutant_is_rejected(workflow):
    mutant = deepcopy(workflow)
    mutant[event_key(mutant)]["workflow_dispatch"] = {"inputs": {"command": {"type": "string"}}}
    with pytest.raises(AssertionError):
        assert_manual_only(mutant)


def test_integration_is_one_bounded_tokenless_refusal(workflow):
    assert_refusal_contract(workflow)


@pytest.mark.parametrize("scope,key,value", [
    ("workflow", "permissions", {"contents": "read"}),
    ("workflow", "env", {"TOKEN": "${{ secrets.SYNTHETIC }}"}),
    ("job", "permissions", {"id-token": "write"}),
    ("job", "environment", "test-dataverse"),
    ("job", "strategy", {"matrix": {"shard": ["live"]}}),
    ("job", "needs", "candidate"),
    ("job", "if", "false"),
    ("job", "continue-on-error", True),
    ("job", "timeout-minutes", 0),
    ("step", "if", "false"),
    ("step", "continue-on-error", True),
    ("step", "uses", "actions/checkout@v6"),
    ("step", "uses", "actions/cache@v6"),
    ("step", "env", {"TOKEN": "${{ secrets.SYNTHETIC }}"}),
    ("step", "run", REFUSAL_SCRIPT.replace("exit 1", "exit 0")),
    ("step", "run", "echo 'Tests passed (or skipped)'\nexit 0\n"),
    ("step", "run", "dotnet test --filter Category=Integration\n"),
])
def test_unsafe_hold_mutants_are_rejected_without_execution(workflow, scope, key, value):
    mutant = deepcopy(workflow)
    job = mutant["jobs"]["integration-tests-hold"]
    target = {"workflow": mutant, "job": job, "step": job["steps"][0]}[scope]
    target[key] = value
    with pytest.raises(AssertionError):
        assert_refusal_contract(mutant)


def test_actual_refusal_bash_fails_and_reports_no_live_tests(workflow, tmp_path):
    # Validate every executable byte before launching only the fixed refusal.
    assert_refusal_contract(workflow)
    bash = Path("C:/Program Files/Git/bin/bash.exe") if os.name == "nt" else Path("/bin/bash")
    assert bash.is_file(), "Bash is required; refusal behavior must not silently skip"
    env = {"PATH": "", "HOME": str(tmp_path), "TMPDIR": str(tmp_path), "LC_ALL": "C"}
    if os.name == "nt":
        env["SYSTEMROOT"] = os.environ["SYSTEMROOT"]
    result = subprocess.run(
        [str(bash), "--noprofile", "--norc", "-e", "-o", "pipefail", "-c",
         workflow["jobs"]["integration-tests-hold"]["steps"][0]["run"]],
        env=env, cwd=tmp_path, stdin=subprocess.DEVNULL,
        capture_output=True, text=True, timeout=10, check=False,
    )
    assert result.returncode == 1, result.stderr
    assert result.stderr == ""
    assert result.stdout == (
        "::error::Live integration validation is held; no live tests were run.\n"
        "Requires exact source/target/suite authorization and reviewed fixture ownership/cleanup "
        "safety or an approved exclusive disposable environment lifecycle.\n"
    )


@pytest.mark.parametrize("name,expected_hash", REVIEWED_WORKFLOW_HASHES.items())
def test_automatic_build_and_test_git_contents_match_reviewed_baselines(name, expected_hash):
    content = (REPO_ROOT / ".github/workflows" / name).read_bytes().replace(b"\r\n", b"\n")
    assert hashlib.sha256(content).hexdigest() == expected_hash


@pytest.mark.parametrize("name,aggregate", [("build.yml", "build-status"), ("test.yml", "test-status")])
def test_automatic_events_and_required_aggregate_names_are_preserved(name, aggregate):
    workflow = load_workflow(name)
    expected_events = {
        "push": {"branches": ["main"]}, "pull_request": {"branches": ["main"]},
    }
    expected_name = aggregate
    if name == "test.yml":
        expected_events["workflow_dispatch"] = {}
        expected_name = "${{ github.event_name == 'workflow_dispatch' && 'Native candidate qualification' || 'test-status' }}"
    assert workflow[event_key(workflow)] == expected_events
    job = workflow["jobs"][aggregate]
    assert job.get("name", aggregate) == expected_name
    assert job["if"] == "always()"
