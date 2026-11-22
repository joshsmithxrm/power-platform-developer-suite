"""The required build aggregate blocks a pull request whose dependency review did not succeed."""
from __future__ import annotations

import os
import re
import subprocess
from pathlib import Path

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
BASH = Path("C:/Program Files/Git/bin/bash.exe") if os.name == "nt" else Path("/bin/bash")


def jobs() -> dict:
    return yaml.safe_load((ROOT / ".github/workflows/build.yml").read_text(encoding="utf-8"))["jobs"]


def run_gate(event: str, review: str, others: str = "success") -> subprocess.CompletedProcess:
    """Run the real gate script with the workflow expressions filled in."""
    script = next(step["run"] for step in jobs()["build-status"]["steps"] if step.get("name") == "Check build status")
    values = {"github.event_name": event, "needs.dependency-review.result": review, "needs.changes.result": "success"}
    rendered = re.sub(r"\$\{\{([^}]*)\}\}", lambda match: values.get(match.group(1).strip(), others), script)
    assert "${{" not in rendered
    assert BASH.is_file(), "Bash is required; the gate's behaviour must not silently skip"
    return subprocess.run([str(BASH), "--noprofile", "--norc", "-e", "-c", rendered],
                          stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=30, check=False)


def test_dependency_review_is_wired_into_the_required_gate():
    review, gate = jobs()["dependency-review"], jobs()["build-status"]
    assert review["if"] == "github.event_name == 'pull_request'"
    assert "continue-on-error" not in review and all("continue-on-error" not in step for step in review["steps"])
    action = next(step for step in review["steps"] if step.get("uses", "").startswith("actions/dependency-review-action@"))
    assert action["with"]["fail-on-severity"] == "high"
    assert "dependency-review" in gate["needs"] and gate["if"] == "always()"


def test_pull_request_passes_when_dependency_review_succeeded():
    result = run_gate("pull_request", "success")
    assert result.returncode == 0, result.stdout + result.stderr


@pytest.mark.parametrize("review", ["failure", "cancelled", "skipped", ""])
def test_pull_request_is_blocked_unless_dependency_review_succeeded(review):
    result = run_gate("pull_request", review)
    assert result.returncode == 1
    assert "Dependency review did not succeed on a pull request" in result.stdout


def test_push_passes_with_dependency_review_skipped_by_design():
    result = run_gate("push", "skipped")
    assert result.returncode == 0, result.stdout + result.stderr


@pytest.mark.parametrize("review", ["failure", "cancelled", "success", ""])
def test_push_rejects_any_other_dependency_review_result(review):
    result = run_gate("push", review)
    assert result.returncode == 1
    assert "unexpected result on a push" in result.stdout


@pytest.mark.parametrize("event", ["workflow_dispatch", "pull_request_target", "merge_group", "schedule", ""])
@pytest.mark.parametrize("review", ["success", "skipped", "failure"])
def test_an_event_without_a_rule_fails_closed(event, review):
    result = run_gate(event, review)
    assert result.returncode == 1
    assert "No dependency review rule for event" in result.stdout


@pytest.mark.parametrize("event,review", [("pull_request", "success"), ("push", "skipped")])
def test_a_failed_build_still_blocks_when_dependency_review_is_satisfied(event, review):
    result = run_gate(event, review, others="failure")
    assert result.returncode == 1
    assert "Build failed" in result.stdout
