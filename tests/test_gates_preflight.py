"""Tests for gates preflight fnm self-heal logic (issue #1087, closes #1083).

Structural checks verifying SKILL.md contains the correct Path B self-heal
patterns and REFERENCE.md documents the self-heal behavior + log line meaning.

Scenarios covered:
- bad-snapshot state (fnm on PATH, npm absent) → self-heal succeeds, log line emitted
- fnm missing entirely → loud-fail per existing behavior
- npm already on PATH → no self-heal branch entered
- fnm runs but npm still absent → loud-fail

Run: ``pytest tests/test_gates_preflight.py -v``
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parent.parent
GATES_SKILL = REPO_ROOT / ".claude" / "skills" / "gates" / "SKILL.md"
GATES_REFERENCE = REPO_ROOT / ".claude" / "skills" / "gates" / "REFERENCE.md"


def _preflight_block() -> str:
    content = GATES_SKILL.read_text(encoding="utf-8")
    start = content.find("Step 1.5")
    end = content.find("### Step 2", start)
    return content[start : end if end != -1 else len(content)]


def _section8() -> str:
    content = GATES_REFERENCE.read_text(encoding="utf-8")
    start = content.find("## §8")
    if start == -1:
        return content
    nxt = re.search(r"^## §", content[start + 1 :], re.MULTILINE)
    return content[start : start + 1 + nxt.start()] if nxt else content[start:]


# ---------------------------------------------------------------------------
# SKILL.md structural checks
# ---------------------------------------------------------------------------


class TestPreflight_FnmSelfHealSkill:
    """SKILL.md Step 1.5 must contain full Path B self-heal logic."""

    def test_fnm_reachability_check_present(self):
        """Scenario: bad-snapshot — self-heal branch checks fnm before proceeding."""
        assert "command -v fnm" in _preflight_block(), (
            "Preflight block missing fnm reachability check ('command -v fnm')"
        )

    def test_fnm_env_eval_present(self):
        """Scenario: bad-snapshot — fnm env output is eval'd to activate node/npm."""
        block = _preflight_block()
        assert "fnm env" in block and "eval" in block, (
            "Preflight block missing 'eval ... fnm env' activation command"
        )

    def test_self_heal_log_line_exact(self):
        """Scenario: bad-snapshot healed — log line is exactly 'preflight: self-healed via fnm activation'."""
        assert "preflight: self-healed via fnm activation" in _preflight_block(), (
            "Preflight block missing exact log line: "
            "'preflight: self-healed via fnm activation'"
        )

    def test_loud_fail_when_npm_still_absent(self):
        """Scenario: fnm runs but npm still absent — or fnm missing — loud-fail emitted."""
        assert "FAIL (preflight): npm missing" in _preflight_block(), (
            "Preflight block must emit 'FAIL (preflight): npm missing' "
            "when npm is absent after self-heal attempt (or when fnm is missing)"
        )

    def test_dotnet_preflight_unchanged(self):
        """dotnet preflight is not changed — loud-fail still present."""
        assert "FAIL (preflight): dotnet missing" in _preflight_block(), (
            "dotnet preflight loud-fail was unexpectedly removed from Step 1.5"
        )

    def test_npm_block_uses_shell_brace_grouping(self):
        """npm self-heal is a brace group { ... } not a bare one-liner."""
        block = _preflight_block()
        # The npm check must open a { block (multi-line self-heal)
        assert re.search(r"command -v npm.*\|\|.*\{", block, re.DOTALL), (
            "npm preflight should use a brace-grouped self-heal block, not a bare one-liner"
        )


# ---------------------------------------------------------------------------
# REFERENCE.md structural checks
# ---------------------------------------------------------------------------


class TestPreflight_FnmSelfHealReference:
    """REFERENCE.md §8 must document self-heal behavior and log line meaning."""

    def test_self_heal_section_present(self):
        """REFERENCE.md §8 has a sub-section on fnm self-heal."""
        section = _section8()
        assert "self-heal" in section.lower() or "self-healed" in section.lower(), (
            "REFERENCE.md §8 missing self-heal sub-section"
        )

    def test_log_line_documented(self):
        """REFERENCE.md §8 documents the exact self-heal log line."""
        assert "self-healed via fnm activation" in _section8(), (
            "REFERENCE.md §8 missing documentation of the "
            "'preflight: self-healed via fnm activation' log line"
        )

    def test_bad_snapshot_root_cause_documented(self):
        """REFERENCE.md §8 explains the bad-snapshot / per-shell PATH root cause."""
        section = _section8()
        assert "fnm" in section and (
            "PATH" in section or "per-shell" in section or "snapshot" in section
        ), (
            "REFERENCE.md §8 missing bad-snapshot / fnm per-shell PATH explanation"
        )

    def test_four_step_recovery_documented(self):
        """REFERENCE.md §8 documents all four steps of the self-heal algorithm."""
        section = _section8()
        # Steps should include: check fnm, eval fnm env, re-check npm, log or fail
        assert "command -v fnm" in section or "fnm" in section, (
            "REFERENCE.md §8 missing step: check if fnm is reachable"
        )
        assert "fnm env" in section, (
            "REFERENCE.md §8 missing step: eval fnm env output"
        )
        assert "self-healed via fnm activation" in section, (
            "REFERENCE.md §8 missing step: log line on success"
        )
        assert "loud-fail" in section or "FAIL" in section, (
            "REFERENCE.md §8 missing step: loud-fail when self-heal cannot recover"
        )
