"""Tests for skill routing gates (AC-01 through AC-07, AC-11, AC-12).

Phases 2 (backlog G1), 3 (investigate G2), 4 (design G3), 5 (spec self-test).
Tests are grep-based structural checks per the spec AC table — they verify
the SKILL.md files contain the required advisory text, telemetry keys, and
ordering guarantees.
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parent.parent
BACKLOG_SKILL = REPO_ROOT / ".claude" / "skills" / "backlog" / "SKILL.md"
INVESTIGATE_SKILL = REPO_ROOT / ".claude" / "skills" / "investigate" / "SKILL.md"
DESIGN_SKILL = REPO_ROOT / ".claude" / "skills" / "design" / "SKILL.md"
SPEC_FILE = REPO_ROOT / "specs" / "skill-routing-gates.md"


def _step_block(content: str, heading_pattern: str) -> str:
    """Return the text from the first match of heading_pattern to the next
    same-level heading (same number of leading # chars) or end of string.

    heading_pattern must match a string whose leading characters are '#' —
    e.g. r'^### Step 7:'. The level is extracted from the match, not the pattern.
    """
    match = re.search(heading_pattern, content, re.MULTILINE)
    if not match:
        return ""
    start = match.start()
    # Determine the heading level from the matched text, not the pattern
    level_match = re.match(r"^(#+)", match.group(0))
    level = len(level_match.group(1)) if level_match else 3
    # Find the next heading of the same or higher level after start
    next_heading = re.search(r"^" + re.escape("#" * level) + r"[# ]", content[match.end():], re.MULTILINE)
    if next_heading:
        return content[start : match.end() + next_heading.start()]
    return content[start:]


# ---------------------------------------------------------------------------
# Phase 2 — /backlog Step 0 (G1)  AC-01, AC-02, AC-03, AC-11(G1)
# ---------------------------------------------------------------------------


class TestBacklogStep0:
    def _content(self) -> str:
        return BACKLOG_SKILL.read_text(encoding="utf-8")

    def _step0_block(self) -> str:
        return _step_block(self._content(), r"^### Step 0\b")

    def test_backlog_step0_present_and_ordered(self):
        """AC-01: Step 0 exists, precedes Step 1, has enforcement marker and 'run /investigate'."""
        content = self._content()

        # (a) file contains Step 0 heading
        assert re.search(r"^### Step 0\b", content, re.MULTILINE), \
            "backlog SKILL.md missing '### Step 0' heading"

        # (b) next ### heading after Step 0 is ### 1. Parse Arguments
        step0_match = re.search(r"^### Step 0\b", content, re.MULTILINE)
        after_step0 = content[step0_match.end():]
        next_heading = re.search(r"^### .+", after_step0, re.MULTILINE)
        assert next_heading, "No heading found after Step 0 in backlog SKILL.md"
        assert "1. Parse Arguments" in next_heading.group(0), \
            f"Heading after Step 0 should be '### 1. Parse Arguments', got: {next_heading.group(0)!r}"

        block = self._step0_block()

        # (c) enforcement marker
        assert "<!-- enforcement: T3 advisory" in block, \
            "Step 0 block missing '<!-- enforcement: T3 advisory'"

        # (d) must contain 'run /investigate'
        assert "run /investigate" in block, \
            "Step 0 block missing the phrase 'run /investigate'"

    def test_backlog_step0_lists_keywords(self):
        """AC-02: All seven trigger keyword phrases are verbatim in Step 0 block."""
        block = self._step0_block()
        keywords = [
            "broad concept",
            "think out loud",
            "need to figure out",
            "let's explore",
            "strategic",
            "not sure what",
            "should we",
        ]
        for kw in keywords:
            assert kw in block, \
                f"Step 0 block missing keyword phrase: {kw!r}"

    def test_backlog_step0_documents_skip_list(self):
        """AC-03: Skip list contains all five skip tokens near 'skip'/'does not apply'."""
        block = self._step0_block()
        skip_tokens = ["triage", "review", "validate", "dispatch"]
        for token in skip_tokens:
            assert token in block, \
                f"Step 0 block missing skip token: {token!r}"
        # 'no-arg' or 'no arguments'
        assert "no-arg" in block or "no arguments" in block, \
            "Step 0 block missing 'no-arg' or 'no arguments'"
        # The block must contain 'skip' or 'does not apply'
        assert "skip" in block.lower() or "does not apply" in block.lower(), \
            "Step 0 block missing 'skip' or 'does not apply' context"

    def test_backlog_emits_three_telemetry_keys(self):
        """AC-11 (G1): All three telemetry bump keys are present in the file."""
        content = self._content()
        keys = [
            "bump routing_gates.backlog.fired_count",
            "bump routing_gates.backlog.honored_count",
            "bump routing_gates.backlog.overridden_count",
        ]
        for key in keys:
            assert key in content, \
                f"backlog SKILL.md missing telemetry key: {key!r}"


# ---------------------------------------------------------------------------
# Phase 3 — /investigate Step 7 + Step 8 (G2)  AC-04, AC-05, AC-11(G2)
# ---------------------------------------------------------------------------


class TestInvestigateEpicOption:
    def _content(self) -> str:
        return INVESTIGATE_SKILL.read_text(encoding="utf-8")

    def _step7_block(self) -> str:
        return _step_block(self._content(), r"^### Step 7:")

    def _step8_block(self) -> str:
        return _step_block(self._content(), r"^### Step 8:")

    def test_investigate_step7_step8_epic_option(self):
        """AC-04: Step 7 has option (4) File as epic; Step 8 branches on it; both have enforcement marker."""
        step7 = self._step7_block()
        step8 = self._step8_block()

        # (a) Step 7 lists option (4) File as epic
        assert re.search(r"4\.\s+\*\*File as epic\*\*|4\.\s+File as epic|\(4\)\s+File as epic", step7), \
            "Step 7 block missing option (4) File as epic"

        # (b) Step 8 references option (4) and /backlog
        assert re.search(r"option \(4\)|option\(4\)", step8), \
            "Step 8 block missing reference to 'option (4)'"
        assert "/backlog" in step8, \
            "Step 8 block missing '/backlog'"

        # (c) enforcement marker in both blocks
        assert "<!-- enforcement: T3 advisory" in step7, \
            "Step 7 block missing '<!-- enforcement: T3 advisory'"
        assert "<!-- enforcement: T3 advisory" in step8, \
            "Step 8 block missing '<!-- enforcement: T3 advisory'"

    def test_investigate_epic_decomposition_documented(self):
        """AC-05: Step 8 states Constraints and Decisions items become child issues."""
        step8 = self._step8_block()
        assert "Constraints and Decisions" in step8, \
            "Step 8 block missing 'Constraints and Decisions'"
        assert "child issue" in step8 or "child-issue" in step8, \
            "Step 8 block missing 'child issue' or 'child-issue'"

    def test_investigate_emits_two_telemetry_keys(self):
        """AC-11 (G2): Both G2 telemetry bump keys are present in the file."""
        content = self._content()
        keys = [
            "bump routing_gates.investigate.epic_offered_count",
            "bump routing_gates.investigate.epic_chosen_count",
        ]
        for key in keys:
            assert key in content, \
                f"investigate SKILL.md missing telemetry key: {key!r}"


# ---------------------------------------------------------------------------
# Phase 4 — /design Step 2 checkpoint (G3)  AC-06, AC-07, AC-11(G3)
# ---------------------------------------------------------------------------


class TestDesignMultiConcernCheckpoint:
    def _content(self) -> str:
        return DESIGN_SKILL.read_text(encoding="utf-8")

    def test_design_step2_checkpoint_ordered(self):
        """AC-06: 'Understand the idea' < 'Multi-Concern Checkpoint' < 'Explore approaches',
        checkpoint has enforcement marker."""
        content = self._content()

        understand_idx = content.find("Understand the idea")
        checkpoint_idx = content.find("Multi-Concern Checkpoint")
        explore_idx = content.find("Explore approaches")

        assert understand_idx != -1, "design SKILL.md missing 'Understand the idea'"
        assert checkpoint_idx != -1, "design SKILL.md missing 'Multi-Concern Checkpoint'"
        assert explore_idx != -1, "design SKILL.md missing 'Explore approaches'"

        assert understand_idx < checkpoint_idx, \
            "'Understand the idea' must appear before 'Multi-Concern Checkpoint'"
        assert checkpoint_idx < explore_idx, \
            "'Multi-Concern Checkpoint' must appear before 'Explore approaches'"

        # Enforcement marker is in the checkpoint block (between Checkpoint and Explore approaches)
        checkpoint_block = content[checkpoint_idx:explore_idx]
        assert "<!-- enforcement: T3 advisory" in checkpoint_block, \
            "Multi-Concern Checkpoint block missing '<!-- enforcement: T3 advisory'"

    def test_design_checkpoint_documents_heuristics(self):
        """AC-07: Checkpoint block documents both heuristics."""
        content = self._content()
        checkpoint_idx = content.find("Multi-Concern Checkpoint")
        assert checkpoint_idx != -1, "design SKILL.md missing 'Multi-Concern Checkpoint'"

        # Find the checkpoint block (up to next bold heading or major heading)
        after = content[checkpoint_idx:]
        # Grab everything up to next ### heading (the Explore approaches section)
        explore_idx = after.find("**Explore approaches")
        if explore_idx != -1:
            block = after[:explore_idx]
        else:
            block = after[:2000]  # generous window

        assert "more than 3 sub-features" in block or "> 3" in block or "> 3 distinct" in block, \
            "Checkpoint block missing 'more than 3 sub-features' heuristic"
        assert "ship independently" in block, \
            "Checkpoint block missing 'ship independently' heuristic"

    def test_design_emits_four_telemetry_keys(self):
        """AC-11 (G3): All four G3 telemetry bump keys are present in the file."""
        content = self._content()
        keys = [
            "bump routing_gates.design.fired_count",
            "bump routing_gates.design.cohesion_confirmed_count",
            "bump routing_gates.design.split_count",
            "bump routing_gates.design.proceed_anyway_count",
        ]
        for key in keys:
            assert key in content, \
                f"design SKILL.md missing telemetry key: {key!r}"


# ---------------------------------------------------------------------------
# Phase 5 — Spec self-test  AC-12
# ---------------------------------------------------------------------------


class TestSpecChainingGraph:
    def test_spec_chaining_graph_covers_new_gates(self):
        """AC-12: Spec contains all three 'New (G1/G2/G3)' markers."""
        content = SPEC_FILE.read_text(encoding="utf-8")
        for marker in ("**New (G1)**", "**New (G2)**", "**New (G3)**"):
            assert marker in content, \
                f"specs/skill-routing-gates.md missing chaining graph marker: {marker!r}"
