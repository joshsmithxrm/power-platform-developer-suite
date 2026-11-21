"""Pre-merge gate workflow contract: which events cancel a running gate.

These are workflow contract checks. GitHub owns the single-pending-slot
scheduling behavior; local tests do not simulate or certify hosted scheduling.
"""
import re
from pathlib import Path

import pytest
import yaml


WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/pre-merge-gate.yml"

# Events that keep the PR's head commit. They must not cancel a gate that is
# evaluating that same head (#1685); their run waits in the pending slot.
SAME_HEAD_ACTIONS = {"ready_for_review", "edited"}
# Events that can bring a new head commit. They must cancel the obsolete run.
NEW_HEAD_ACTIONS = {"opened", "synchronize", "reopened"}

# The only accepted shape of `cancel-in-progress`: a conjunction of
# `github.event.action != '<action>'` terms. Anything else fails the test, so
# the evaluation below cannot drift from what GitHub evaluates.
_NOT_EQUAL_TERM = r"github\.event\.action != '([a-z_]+)'"
_CANCEL_EXPRESSION = re.compile(
    r"^\$\{\{ " + _NOT_EQUAL_TERM + r"(?: && " + _NOT_EQUAL_TERM + r")* \}\}$"
)


def _workflow():
    # BaseLoader retains GitHub's `on` key rather than parsing it as YAML 1.1
    # boolean True, and keeps scalar expressions intact for policy checks.
    return yaml.load(WORKFLOW.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def _non_cancelling_actions(expression: str) -> set[str]:
    """Return the actions for which the expression evaluates to false."""
    assert _CANCEL_EXPRESSION.match(expression), (
        f"cancel-in-progress has an unexpected shape: {expression!r}"
    )
    return set(re.findall(_NOT_EQUAL_TERM, expression))


def _cancels(action: str) -> bool:
    expression = _workflow()["concurrency"]["cancel-in-progress"]
    return action not in _non_cancelling_actions(expression)


def test_one_concurrency_group_per_pull_request():
    concurrency = _workflow()["concurrency"]
    # Keyed by PR number only (not by head SHA): a run for a new head commit
    # must share the group with the older run so it can cancel it.
    assert concurrency["group"] == (
        "pre-merge-gate-${{ github.event.pull_request.number }}"
    )
    assert concurrency.get("queue", "single") == "single"


def test_every_trigger_type_is_classified():
    types = set(_workflow()["on"]["pull_request"]["types"])
    assert types == SAME_HEAD_ACTIONS | NEW_HEAD_ACTIONS


@pytest.mark.parametrize("action", sorted(SAME_HEAD_ACTIONS))
def test_same_head_event_does_not_cancel_running_gate(action):
    # An edit of the title, body or base while the gate runs must not leave a
    # cancelled Rule 3 job or a failed aggregate on the PR (#1685).
    assert not _cancels(action)


@pytest.mark.parametrize("action", sorted(NEW_HEAD_ACTIONS))
def test_new_head_event_cancels_obsolete_gate(action):
    # A real new commit still replaces the older run.
    assert _cancels(action)


def test_expression_shape_is_enforced():
    with pytest.raises(AssertionError):
        _non_cancelling_actions("${{ github.event.action == 'synchronize' }}")
    with pytest.raises(AssertionError):
        _non_cancelling_actions(
            "${{ github.event.action != 'edited' || github.event.action != 'x' }}"
        )
    assert _non_cancelling_actions(
        "${{ github.event.action != 'a' && github.event.action != 'b_c' }}"
    ) == {"a", "b_c"}


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
