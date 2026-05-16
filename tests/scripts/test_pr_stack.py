"""Unit tests for scripts/pr_stack.py — ACs 01-14 from specs/feat-1070-pr-stack-alpha.md."""
from __future__ import annotations

import json
from pathlib import Path

import pytest

# tests/conftest.py prepends scripts/ to sys.path; reach the module by name.
import pr_stack


def _valid_entry(id="pr-1", branch_suffix="pr1", depends_on=None):
    return {
        "id": id,
        "title": f"feat: {id}",
        "branch_suffix": branch_suffix,
        "plan": f".plans/foo-{id}.md",
        "files": ["src/a.py"],
        "size_estimate": "~100 LOC",
        "depends_on": depends_on or [],
        "ac_refs": [],
    }


def _valid_envelope(n=2):
    entries = [
        _valid_entry(
            f"pr-{i}",
            f"pr{i}",
            depends_on=[f"pr-{i-1}"] if i > 1 else [],
        )
        for i in range(1, n + 1)
    ]
    return {
        "schema_version": "1.0",
        "spec": "specs/foo.md",
        "created_at": "2026-05-15T00:00:00+00:00",
        "stack": entries,
    }


class TestPrStack:
    """ACs 01-07, 12-13 — pr_stack.py helper behavior."""

    def test_build_envelope_returns_required_keys(self):  # AC-01
        entries = [
            _valid_entry("pr-1", "pr1"),
            _valid_entry("pr-2", "pr2", depends_on=["pr-1"]),
        ]
        envelope = pr_stack.build_envelope("specs/foo.md", entries)
        assert envelope["schema_version"] == "1.0"
        assert envelope["spec"] == "specs/foo.md"
        assert "created_at" in envelope and envelope["created_at"] != ""
        assert isinstance(envelope["stack"], list)
        assert len(envelope["stack"]) == 2

    def test_build_envelope_single_entry_requires_justification(self):  # AC-02
        with pytest.raises(ValueError, match="justification"):
            pr_stack.build_envelope("specs/foo.md", [_valid_entry()])

    def test_build_envelope_single_entry_empty_justification_raises(self):  # AC-02 (empty case)
        with pytest.raises(ValueError, match="justification"):
            pr_stack.build_envelope(
                "specs/foo.md", [_valid_entry()], justification="   "
            )

    @pytest.mark.parametrize(
        "field",
        [
            "id",
            "title",
            "branch_suffix",
            "plan",
            "files",
            "size_estimate",
            "depends_on",
            "ac_refs",
        ],
    )
    def test_validate_envelope_missing_required_field(self, field):  # AC-03
        envelope = _valid_envelope(2)
        del envelope["stack"][1][field]
        with pytest.raises(ValueError, match=field):
            pr_stack.validate_envelope(envelope)

    def test_validate_envelope_unknown_depends_on(self):  # AC-04
        envelope = _valid_envelope(2)
        envelope["stack"][1]["depends_on"] = ["pr-does-not-exist"]
        with pytest.raises(ValueError, match="unknown id"):
            pr_stack.validate_envelope(envelope)

    def test_validate_envelope_circular_dependency(self):  # AC-05 (direct cycle)
        envelope = _valid_envelope(2)
        envelope["stack"][0]["depends_on"] = ["pr-2"]
        envelope["stack"][1]["depends_on"] = ["pr-1"]
        with pytest.raises(ValueError, match="circular"):
            pr_stack.validate_envelope(envelope)

    def test_validate_envelope_long_chain_cycle(self):  # AC-05 (longer cycle)
        envelope = _valid_envelope(3)
        envelope["stack"][0]["depends_on"] = ["pr-3"]
        envelope["stack"][1]["depends_on"] = ["pr-1"]
        envelope["stack"][2]["depends_on"] = ["pr-2"]
        with pytest.raises(ValueError, match="circular"):
            pr_stack.validate_envelope(envelope)

    def test_write_envelope_writes_json(self, tmp_path):  # AC-06
        envelope = _valid_envelope(2)
        out = tmp_path / "stack.json"
        pr_stack.write_envelope(envelope, out)
        assert out.exists()
        content = out.read_text(encoding="utf-8")
        assert content.endswith("\n")
        loaded = json.loads(content)
        assert loaded["spec"] == envelope["spec"]
        assert len(loaded["stack"]) == 2
        # 2-space indent: the first nested key should be preceded by two spaces.
        assert '\n  "spec"' in content

    def test_write_envelope_does_not_write_on_invalid(self, tmp_path):  # AC-07
        invalid = {
            "schema_version": "1.0",
            "spec": "",
            "created_at": "x",
            "stack": [],
        }
        out = tmp_path / "stack.json"
        with pytest.raises(ValueError):
            pr_stack.write_envelope(invalid, out)
        assert not out.exists()

    def test_build_envelope_single_entry_with_justification(self):  # AC-12
        envelope = pr_stack.build_envelope(
            "specs/foo.md",
            [_valid_entry()],
            justification="phases share a DB migration",
        )
        assert envelope["justification"] == "phases share a DB migration"
        assert len(envelope["stack"]) == 1

    def test_validate_envelope_single_entry_no_justification(self):  # AC-13
        envelope = _valid_envelope(1)
        # _valid_envelope returns no justification key by default.
        assert "justification" not in envelope
        with pytest.raises(ValueError, match="justification"):
            pr_stack.validate_envelope(envelope)

    def test_validate_envelope_branch_suffix_with_slash(self):
        envelope = _valid_envelope(2)
        envelope["stack"][0]["branch_suffix"] = "pr1/alpha"
        with pytest.raises(ValueError, match="slash"):
            pr_stack.validate_envelope(envelope)

    def test_validate_envelope_empty_files_list(self):
        envelope = _valid_envelope(2)
        envelope["stack"][0]["files"] = []
        with pytest.raises(ValueError, match="files"):
            pr_stack.validate_envelope(envelope)

    def test_validate_envelope_duplicate_id(self):
        envelope = _valid_envelope(2)
        envelope["stack"][1]["id"] = "pr-1"
        envelope["stack"][1]["depends_on"] = []
        with pytest.raises(ValueError, match="unique"):
            pr_stack.validate_envelope(envelope)

    def test_validate_envelope_accepts_unknown_optional_fields(self):
        envelope = _valid_envelope(2)
        envelope["future_field"] = "ignored"
        envelope["stack"][0]["session_id"] = "abc123"
        # Should not raise — additive fields allowed (forward compat with #1069).
        pr_stack.validate_envelope(envelope)

    def test_validate_envelope_accepts_minor_version_bump(self):
        envelope = _valid_envelope(2)
        envelope["schema_version"] = "1.1"
        pr_stack.validate_envelope(envelope)

    def test_validate_envelope_rejects_major_version_bump(self):
        envelope = _valid_envelope(2)
        envelope["schema_version"] = "2.0"
        with pytest.raises(ValueError, match="major 1"):
            pr_stack.validate_envelope(envelope)

    def test_cli_validate_exit_zero_on_valid(self, tmp_path, capsys):
        envelope = _valid_envelope(2)
        path = tmp_path / "stack.json"
        pr_stack.write_envelope(envelope, path)
        rc = pr_stack.main(["pr_stack.py", "validate", str(path)])
        captured = capsys.readouterr()
        assert rc == 0
        assert captured.out == ""

    def test_cli_validate_exit_one_on_invalid(self, tmp_path, capsys):
        path = tmp_path / "stack.json"
        path.write_text(
            json.dumps(
                {
                    "schema_version": "1.0",
                    "spec": "",
                    "created_at": "x",
                    "stack": [],
                }
            ),
            encoding="utf-8",
        )
        rc = pr_stack.main(["pr_stack.py", "validate", str(path)])
        captured = capsys.readouterr()
        assert rc == 1
        assert captured.out == ""
        assert captured.err != ""

    def test_cli_validate_exit_one_on_missing_file(self, tmp_path, capsys):
        missing = tmp_path / "nope.json"
        rc = pr_stack.main(["pr_stack.py", "validate", str(missing)])
        captured = capsys.readouterr()
        assert rc == 1
        assert captured.out == ""

    def test_cli_usage_on_bad_args(self, capsys):
        rc = pr_stack.main(["pr_stack.py"])
        captured = capsys.readouterr()
        assert rc == 1
        assert "usage" in captured.err


class TestPrStackSkill:
    """ACs 08-11, 14 — /design SKILL.md + REFERENCE.md documents the PR-stack flow."""

    REPO = Path(__file__).resolve().parent.parent.parent
    SKILL = REPO / ".claude" / "skills" / "design" / "SKILL.md"
    REF = REPO / ".claude" / "skills" / "design" / "REFERENCE.md"

    def _skill(self) -> str:
        return self.SKILL.read_text(encoding="utf-8")

    def _ref(self) -> str:
        return self.REF.read_text(encoding="utf-8")

    def test_design_skill_documents_step_4d(self):  # AC-08
        t = self._skill()
        assert "4.D" in t or "Step 4.D" in t
        assert "independently" in t or "independent" in t

    def test_design_skill_documents_step_4e(self):  # AC-09
        t = self._skill()
        assert "4.E" in t or "Step 4.E" in t
        assert "pr_stack.py" in t
        assert "PR Stack" in t

    def test_design_skill_specifies_stack_json_path(self):  # AC-10
        assert "stack.json" in self._skill()

    def test_design_skill_documents_decline_path(self):  # AC-11
        t = self._skill().lower()
        assert (
            "decline" in t
            or "skip to step 5" in t
            or "no artifacts" in t
        )

    def test_design_skill_pr_stack_section_has_files_and_size(self):  # AC-14
        combined = self._skill() + self._ref()
        assert "files" in combined
        assert "size" in combined.lower()
