"""Tests for retro SKILL.md auto-commit requirements (issue #1074).

Structural checks that SKILL.md Phase 8 and Phase 9 include the git add / commit
instructions that prevent dirty-worktree friction at session end.
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
RETRO_SKILL = REPO_ROOT / ".claude" / "skills" / "retro" / "SKILL.md"


def _phase_block(content: str, phase_num: int) -> str:
    """Return text from the Phase N heading to the next ### heading or end of file."""
    match = re.search(rf"^### Phase {phase_num}\.", content, re.MULTILINE)
    if not match:
        return ""
    start = match.start()
    next_h = re.search(r"^### ", content[match.end():], re.MULTILINE)
    if next_h:
        return content[start: match.end() + next_h.start()]
    return content[start:]


class TestRetroAutoCommit:
    def _content(self) -> str:
        return RETRO_SKILL.read_text(encoding="utf-8")

    def test_phase8_stages_summary_md(self):
        """Phase 8 must include git add to stage the .retros/ summary .md."""
        block = _phase_block(self._content(), 8)
        assert block, "Phase 8 heading not found in retro SKILL.md"
        assert "git add" in block, "Phase 8 must include 'git add' to stage the summary .md"
        assert ".retros/" in block, "Phase 8 git add must reference .retros/ path"

    def test_phase9_commits_both_artifacts(self):
        """Phase 9 must include git add for summary.json and git commit."""
        block = _phase_block(self._content(), 9)
        assert block, "Phase 9 heading not found in retro SKILL.md"
        assert "git add" in block, "Phase 9 must include 'git add' for summary.json"
        assert "summary.json" in block, "Phase 9 must reference summary.json in git add"
        assert "git commit" in block, "Phase 9 must include 'git commit'"

    def test_phase9_commit_message_format(self):
        """Phase 9 commit subject must use 'retro: <branch> session summary' format."""
        block = _phase_block(self._content(), 9)
        assert "retro:" in block, "Phase 9 commit message must start with 'retro:'"
        assert "session summary" in block, (
            "Phase 9 commit message must include 'session summary'"
        )

    def test_phase9_commit_includes_executive_synthesis_body(self):
        """Phase 9 commit must instruct inclusion of executive synthesis as body."""
        block = _phase_block(self._content(), 9)
        assert "executive synthesis" in block.lower(), (
            "Phase 9 must instruct that executive synthesis is the commit body"
        )


INTERACTION_PATTERNS = REPO_ROOT / ".claude" / "interaction-patterns.md"
RETRO_REFERENCE = REPO_ROOT / ".claude" / "skills" / "retro" / "REFERENCE.md"


class TestRetroPhase5OneAtATime:
    def _content(self) -> str:
        return RETRO_SKILL.read_text(encoding="utf-8")

    def test_phase5_one_at_a_time_not_bulk_plan(self):
        """Phase 5 must use one-at-a-time UX, not bulk-plan."""
        block = _phase_block(self._content(), 5)
        assert block, "Phase 5 heading not found in retro SKILL.md"
        assert "one-at-a-time" in block.lower() or "one at a time" in block.lower(), (
            "Phase 5 must reference one-at-a-time presentation"
        )
        assert "bulk plan" not in block.lower(), (
            "Phase 5 must not reference bulk-plan UX"
        )

    def test_phase5_references_4a(self):
        """Phase 5 must reference interaction-patterns.md §4a."""
        block = _phase_block(self._content(), 5)
        assert "4a" in block, "Phase 5 must reference interaction-patterns.md §4a"

    def test_phase5_references_reference_9(self):
        """Phase 5 must reference REFERENCE.md §9 for the template."""
        block = _phase_block(self._content(), 5)
        assert "§9" in block or "SS9" in block or "section 9" in block.lower(), (
            "Phase 5 must reference REFERENCE.md §9"
        )

    def test_reference_9_contains_finding_template(self):
        """REFERENCE.md §9 must include the F<N> template fields."""
        content = RETRO_REFERENCE.read_text(encoding="utf-8")
        assert "What I found" in content
        assert "My recommendation" in content
        assert "Rationale" in content
        assert "Your call" in content

    def test_interaction_patterns_contains_4a(self):
        """interaction-patterns.md must contain §4a section."""
        content = INTERACTION_PATTERNS.read_text(encoding="utf-8")
        assert "4a" in content, "interaction-patterns.md must contain §4a"
        assert "one-at-a-time" in content.lower() or "one at a time" in content.lower(), (
            "§4a must describe the one-at-a-time variant"
        )


class TestRetroPhase6EnvelopeDispatch:
    def _content(self) -> str:
        return RETRO_SKILL.read_text(encoding="utf-8")

    def test_phase6_references_goal_envelope(self):
        """Phase 6 must reference goal-envelope.json."""
        block = _phase_block(self._content(), 6)
        assert block, "Phase 6 heading not found in retro SKILL.md"
        assert "goal-envelope.json" in block, "Phase 6 must reference goal-envelope.json"

    def test_phase6_references_goal_supervisor(self):
        """Phase 6 must reference goal_supervisor.py."""
        block = _phase_block(self._content(), 6)
        assert "goal_supervisor.py" in block, "Phase 6 must reference goal_supervisor.py"

    def test_phase6_defer_uses_backlog_skill(self):
        """Phase 6 must invoke Skill(skill='backlog') for DEFER, not draft inline."""
        block = _phase_block(self._content(), 6)
        assert 'skill="backlog"' in block or "skill='backlog'" in block, (
            "Phase 6 must route DEFER findings via Skill(skill='backlog')"
        )

    def test_phase6_no_inline_draft(self):
        """Phase 6 must prohibit inline artifact drafting."""
        block = _phase_block(self._content(), 6)
        content_lower = block.lower()
        assert "never" in content_lower or "not draft" in content_lower, (
            "Phase 6 must explicitly prohibit inline drafting"
        )

    def test_reference_10_contains_envelope_details(self):
        """REFERENCE.md §10 must document envelope dispatch details."""
        content = RETRO_REFERENCE.read_text(encoding="utf-8")
        assert "§10" in content, "REFERENCE.md must contain §10"
        assert "goal-envelope.json" in content
        assert "goal_supervisor.py" in content


class TestRetroCompleteBoundary:
    def _content(self) -> str:
        return RETRO_SKILL.read_text(encoding="utf-8")

    def test_retro_complete_declared_in_phase9(self):
        """Phase 9 must declare the retro-complete boundary."""
        block = _phase_block(self._content(), 9)
        assert "retro is complete" in block.lower() or "retro complete" in block.lower() or (
            "complete" in block.lower() and "retro" in block.lower()
        ), "Phase 9 must declare the retro-complete boundary"

    def test_retro_boundary_routes_to_skills(self):
        """The boundary section must direct new artifacts to receiving skills."""
        block = _phase_block(self._content(), 9)
        assert 'skill="backlog"' in block or "skill='backlog'" in block, (
            "Phase 9 boundary must reference Skill(skill='backlog')"
        )

    def test_reference_10_handoff_table(self):
        """REFERENCE.md §10 must document the handoff discipline table."""
        content = RETRO_REFERENCE.read_text(encoding="utf-8")
        assert "Handoff discipline" in content
        assert "backlog" in content
        assert "design" in content
        assert "investigate" in content
