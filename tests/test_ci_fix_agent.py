#!/usr/bin/env python3
"""Tests for .claude/agents/ci-fix.md (AC-189, AC-190)."""
import os

import pytest
import yaml  # PyYAML — parse YAML front matter


REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AGENT_PATH = os.path.join(REPO_ROOT, ".claude", "agents", "ci-fix.md")


def _parse_frontmatter(path):
    """Parse YAML front matter from a markdown file with --- delimiters."""
    with open(path, "r", encoding="utf-8") as f:
        content = f.read()
    if not content.startswith("---"):
        return {}, content
    end = content.find("---", 3)
    if end == -1:
        return {}, content
    fm_text = content[3:end].strip()
    body = content[end + 3:]
    return yaml.safe_load(fm_text), body


class TestCiFixAgentProfile:
    """AC-190: .claude/agents/ci-fix.md profile validation."""

    def test_agent_profile_exists_with_restricted_tools_and_floating_model(self):
        """ci-fix.md exists, model is floating sonnet, tools are restricted (no Agent, no web)."""
        assert os.path.exists(AGENT_PATH), f"ci-fix.md not found at {AGENT_PATH}"

        fm, body = _parse_frontmatter(AGENT_PATH)

        # Model must be floating (no date suffix like "-20251001")
        model = fm.get("model", "")
        assert model, "model field must be set"
        assert "sonnet" in model.lower(), f"model must be sonnet, got: {model!r}"
        import re
        assert not re.search(r"-\d{8}", model), (
            f"model must be floating (no version pin), got: {model!r}"
        )

        # Tools must exist and be restricted
        tools = fm.get("tools", [])
        assert tools, "tools must be specified"
        allowed = {"Bash", "Read", "Edit", "Write", "Grep", "Glob"}
        forbidden = {"Agent", "WebFetch", "WebSearch"}
        tool_set = set(tools)
        for t in forbidden:
            assert t not in tool_set, f"Forbidden tool {t!r} found in ci-fix.md"
        for t in allowed:
            assert t in tool_set, f"Required tool {t!r} missing from ci-fix.md"


class TestCiFixAgentPromptGuardrails:
    """AC-189: Scope-guardrails preamble in ci-fix agent body."""

    def test_prompt_contains_scope_guardrails(self):
        """Agent body includes G1 guardrail: stay within diff, no preexisting cop-outs,
        escalation_reason required on escalate, scope_violation flag."""
        assert os.path.exists(AGENT_PATH)
        _, body = _parse_frontmatter(AGENT_PATH)
        body_lower = body.lower()

        # G1: stay within diff
        assert "git diff main...head" in body_lower or "within" in body_lower, (
            "Agent body must reference staying within the PR diff (G1)"
        )
        # No "preexisting" cop-outs
        assert "preexisting" in body_lower, (
            "Agent body must forbid 'preexisting' cop-outs"
        )
        # escalation_reason required on escalate
        assert "escalation_reason" in body, (
            "Agent body must reference escalation_reason field"
        )
        # scope_violation flag
        assert "scope_violation" in body, (
            "Agent body must reference scope_violation field"
        )
