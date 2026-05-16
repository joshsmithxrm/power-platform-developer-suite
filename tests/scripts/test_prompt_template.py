"""Text audits for /start prompt template artifacts — issue #1098.

These tests assert that documentation files contain the required workflow
contract elements specified in `specs/feat-1098-prompt-template.md`.
"""
from __future__ import annotations

from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
SKILL = (_REPO / ".claude/skills/start/SKILL.md").read_text(encoding="utf-8")
REFERENCE = (_REPO / ".claude/skills/start/REFERENCE.md").read_text(encoding="utf-8")


def test_skill_step6c_shows_model_flag():
    """AC-05: SKILL.md Step 6c spawn invocation shows --model as an optional flag."""
    assert "--model" in SKILL, \
        "AC-05: SKILL.md must surface --model as an optional flag in the spawn invocation"


def test_skill_step6b_prompt_appendix_subsection():
    """AC-06: SKILL.md Step 6b contains a workflow contract / prompt appendix subsection."""
    lower = SKILL.lower()
    assert ("workflow contract" in lower) or ("prompt appendix" in lower), \
        "AC-06: SKILL.md must contain a workflow contract or prompt appendix subsection"


def test_reference_section7_design_gate():
    """AC-07: REFERENCE.md contains a §7 section titled 'Design-Gate Handoff Procedure'."""
    assert "§7" in REFERENCE, "AC-07: REFERENCE.md must contain a §7 section marker"
    assert "Design-Gate Handoff" in REFERENCE, \
        "AC-07: REFERENCE.md §7 must be titled 'Design-Gate Handoff Procedure'"


def test_workflow_contract_phase_blocked_command():
    """AC-08: workflow contract embeds the phase=blocked + set needs commands."""
    assert "workflow-state.py set phase blocked" in SKILL, \
        "AC-08: workflow contract must set phase=blocked at the design gate"
    assert "set needs" in SKILL, \
        "AC-08: workflow contract must set the `needs` field at the design gate"


def test_workflow_contract_bg_launch_instruction():
    """AC-09: workflow contract instructs the worker to launch pr_monitor via Bash run_in_background=true."""
    assert "run_in_background=true" in SKILL, \
        "AC-09: workflow contract must instruct Bash run_in_background=true for pr_monitor"


def test_workflow_contract_result_json_reference():
    """AC-10: workflow contract references the pr-monitor-result.json and final summary."""
    assert ".workflow/pr-monitor-result.json" in SKILL, \
        "AC-10: workflow contract must reference .workflow/pr-monitor-result.json"
    assert "final summary" in SKILL.lower(), \
        "AC-10: workflow contract must instruct the worker to produce a final summary"
