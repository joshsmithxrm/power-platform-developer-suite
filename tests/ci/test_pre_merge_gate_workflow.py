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
    # Title/body edits can change size waivers or dependency classification.
    # Neither rule may be conditionally skipped for an edit or draft event.
    for job_id, script in (
        ("size", "scripts/ci/check_pr_size.py"),
        ("major-bump-tested", "scripts/ci/check_major_bump_tested.py"),
    ):
        job = workflow["jobs"][job_id]
        assert "if" not in job
        assert job.get("continue-on-error", "false") == "false"
        checks = [step for step in job["steps"] if script in step.get("run", "")]
        assert len(checks) == 1
        assert "if" not in checks[0]
        assert checks[0].get("continue-on-error", "false") == "false"


def test_aggregate_requires_success_from_both_rules():
    aggregate = _workflow()["jobs"]["pre-merge-gate"]
    assert aggregate["name"] == "Pre-Merge Gate"
    assert set(aggregate["needs"]) == {"size", "major-bump-tested"}
    assert aggregate["if"] == "always()"
    script = aggregate["steps"][0]["run"]
    # The only passing branch must require success from both jobs. Cancelled,
    # skipped or missing results cannot turn a queued event into a pass.
    condition = script.split("then", 1)[0]
    assert condition.replace("\\\n", "").split() == [
        "if", "[[", '"${{', "needs.size.result", '}}"', "==", '"success"',
        "&&", '"${{', "needs.major-bump-tested.result", '}}"', "==",
        '"success"', "]];",
    ]
    assert script.count("exit 0") == 1
    assert script.rstrip().endswith("exit 1")
