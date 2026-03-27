"""Tests for /verify workflow checks (checks 4-7)."""
import json
import os
import sys

import pytest

# Add scripts/ to path so we can import verify_workflow_checks
sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir, "scripts"))
import verify_workflow_checks


class TestSkillFrontmatter:
    def test_skill_frontmatter_missing_name(self, tmp_path):
        """AC-19: Skill frontmatter validation catches missing name."""
        skill = tmp_path / "SKILL.md"
        skill.write_text("---\ndescription: A skill\n---\n# Test\n")
        errors = verify_workflow_checks.check_skill_frontmatter(str(skill))
        assert any("name" in e.lower() for e in errors)

    def test_skill_frontmatter_missing_description(self, tmp_path):
        """AC-19: Skill frontmatter validation catches missing description."""
        skill = tmp_path / "SKILL.md"
        skill.write_text("---\nname: test\n---\n# Test\n")
        errors = verify_workflow_checks.check_skill_frontmatter(str(skill))
        assert any("description" in e.lower() for e in errors)

    def test_skill_frontmatter_valid(self, tmp_path):
        """AC-19: Valid frontmatter passes."""
        skill = tmp_path / "SKILL.md"
        skill.write_text("---\nname: test\ndescription: A test skill\n---\n# Test\n")
        errors = verify_workflow_checks.check_skill_frontmatter(str(skill))
        assert errors == []


class TestAgentFrontmatter:
    def test_agent_frontmatter_invalid_tool(self, tmp_path):
        """AC-20: Agent frontmatter validation catches invalid tool names."""
        agent = tmp_path / "agent.md"
        agent.write_text("---\nname: test\ntools:\n  - FakeTool\n---\n# Test\n")
        errors = verify_workflow_checks.check_agent_frontmatter(str(agent))
        assert any("FakeTool" in e for e in errors)

    def test_agent_frontmatter_valid(self, tmp_path):
        """AC-20: Valid agent frontmatter passes."""
        agent = tmp_path / "agent.md"
        agent.write_text("---\nname: test\ntools:\n  - Read\n  - Grep\n---\n# Test\n")
        errors = verify_workflow_checks.check_agent_frontmatter(str(agent))
        assert errors == []

    def test_agent_frontmatter_bash_pattern(self, tmp_path):
        """AC-20: Bash with pattern is valid."""
        agent = tmp_path / "agent.md"
        agent.write_text("---\nname: test\ntools:\n  - Bash(git diff:*)\n---\n# Test\n")
        errors = verify_workflow_checks.check_agent_frontmatter(str(agent))
        assert errors == []


class TestDeadLinkDetection:
    def test_dead_link_detection(self, tmp_path):
        """AC-21: Dead link detection finds references to non-existent files."""
        skill = tmp_path / "SKILL.md"
        skill.write_text("---\nname: test\ndescription: test\n---\n# Test\nSee [link](./nonexistent-file.md)\n")
        errors = verify_workflow_checks.check_skill_file_references(str(skill), str(tmp_path))
        assert any("nonexistent" in e for e in errors)

    def test_dead_link_valid(self, tmp_path):
        """AC-21: Valid file references pass."""
        target = tmp_path / "existing.md"
        target.write_text("# Exists\n")
        skill = tmp_path / "SKILL.md"
        skill.write_text("---\nname: test\ndescription: test\n---\n# Test\nSee [link](./existing.md)\n")
        errors = verify_workflow_checks.check_skill_file_references(str(skill), str(tmp_path))
        assert errors == []


class TestRetroStoreSchema:
    def test_missing_retro_store_no_error(self, tmp_path):
        """AC-32: Missing .retros/summary.json is not an error."""
        errors = verify_workflow_checks.check_retro_store_schema(str(tmp_path / "nonexistent.json"))
        assert errors == []

    def test_retro_store_schema_mismatch_rebuild(self, tmp_path):
        """AC-33: Wrong schema_version triggers rebuild message."""
        store = tmp_path / "summary.json"
        store.write_text(json.dumps({
            "schema_version": 2,
            "last_updated": "2026-03-27",
            "total_retros": 0,
            "findings_by_category": {},
            "metrics": {}
        }))
        errors = verify_workflow_checks.check_retro_store_schema(str(store))
        assert any("rebuild" in e.lower() for e in errors)

    def test_retro_store_valid(self, tmp_path):
        """Valid retro store passes."""
        store = tmp_path / "summary.json"
        store.write_text(json.dumps({
            "schema_version": 1,
            "last_updated": "2026-03-27",
            "total_retros": 0,
            "findings_by_category": {},
            "metrics": {"avg_fix_ratio": 0.0, "pipeline_success_rate": 0.0, "avg_convergence_rounds": 0.0}
        }))
        errors = verify_workflow_checks.check_retro_store_schema(str(store))
        assert errors == []
