"""Preserve required-rule evaluation when a draft PR becomes ready.

These are workflow contract checks. GitHub owns the single-pending-slot
scheduling behavior; local tests do not simulate or certify hosted scheduling.
"""
from pathlib import Path

import yaml


WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/pre-merge-gate.yml"


def _workflow():
    # BaseLoader retains GitHub's `on` key rather than parsing it as YAML 1.1
    # boolean True, and keeps scalar expressions intact for policy checks.
    return yaml.load(WORKFLOW.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def test_ready_for_review_preserves_running_gate():
    concurrency = _workflow()["concurrency"]
    assert concurrency["group"] == (
        "pre-merge-gate-${{ github.event.pull_request.number }}"
    )
    assert concurrency["cancel-in-progress"] == (
        "${{ github.event.action != 'ready_for_review' }}"
    )
    assert concurrency.get("queue", "single") == "single"


def test_required_events_and_rules_remain_enforced():
    workflow = _workflow()
    trigger = workflow["on"]["pull_request"]
    assert set(trigger["types"]) == {
        "opened", "synchronize", "reopened", "ready_for_review", "edited",
    }
    assert trigger["branches"] == ["main"]
    # Title/body edits can change dependency classification. The enforcing
    # rule may not be conditionally skipped for an edit or draft event.
    job = workflow["jobs"]["major-bump-tested"]
    script = "scripts/ci/check_major_bump_tested.py"
    assert "if" not in job
    assert job.get("continue-on-error", "false") == "false"
    checks = [step for step in job["steps"] if script in step.get("run", "")]
    assert len(checks) == 1
    assert "if" not in checks[0]
    assert checks[0].get("continue-on-error", "false") == "false"


def test_size_job_is_informational_and_runs_the_report():
    job = _workflow()["jobs"]["size"]
    runs = [step for step in job["steps"]
            if "scripts/ci/check_pr_size.py" in step.get("run", "")]
    assert len(runs) == 1
    # The script itself always exits 0; the job is named as informational.
    assert "informational" in job["name"].lower()


def test_aggregate_depends_only_on_major_bump_evidence():
    aggregate = _workflow()["jobs"]["pre-merge-gate"]
    assert aggregate["name"] == "Pre-Merge Gate"
    assert set(aggregate["needs"]) == {"major-bump-tested"}
    assert aggregate["if"] == "always()"
    script = aggregate["steps"][0]["run"]
    # PR size cannot influence the aggregate result.
    assert "needs.size" not in script
    # The only passing branch must require success from the evidence rule.
    # Cancelled, skipped or missing results cannot turn a queued event into a
    # pass.
    condition = script.split("then", 1)[0]
    assert condition.split() == [
        "if", "[[", '"${{', "needs.major-bump-tested.result", '}}"', "==",
        '"success"', "]];",
    ]
    assert script.count("exit 0") == 1
    assert script.rstrip().endswith("exit 1")


def test_no_size_waiver_anywhere_in_workflow_or_script():
    root = Path(__file__).resolve().parents[2]
    for path in (WORKFLOW, root / "scripts/ci/check_pr_size.py"):
        text = path.read_text(encoding="utf-8").lower()
        assert "size-waived" not in text
        assert "waiver" not in text
