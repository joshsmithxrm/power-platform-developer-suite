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
