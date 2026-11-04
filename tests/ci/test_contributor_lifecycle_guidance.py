"""Check real documentation entry points and the executable minimal example."""
from __future__ import annotations

import json
from pathlib import Path
import re
import subprocess
import sys

import pytest

ROOT = Path(__file__).resolve().parents[2]
POLICY = ROOT / "docs/CONTRIBUTOR-LIFECYCLE.md"


@pytest.mark.parametrize("relative", [
    "CONTRIBUTING.md", "docs/MERGE-POLICY.md",
    ".claude/skills/implement/SKILL.md", ".claude/skills/pr/SKILL.md",
    ".claude/skills/release/SKILL.md",
])
def test_entry_points_resolve_to_the_single_shared_policy(relative):
    source = ROOT / relative
    links = re.findall(r"\]\(([^)]*CONTRIBUTOR-LIFECYCLE\.md)\)", source.read_text(encoding="utf-8"))
    assert len(links) == 1
    assert (source.parent / links[0]).resolve(strict=True) == POLICY.resolve(strict=True)


def test_pr_template_exposes_routes_and_shared_policy_reference():
    template = (ROOT / ".github/pull_request_template.md").read_text(encoding="utf-8")
    section = template.split("## Contributor provenance\n", 1)[1].split("\n## ", 1)[0]
    assert "docs/CONTRIBUTOR-LIFECYCLE.md" in section
    assert len(re.findall(r"^- \[ \]", section, re.MULTILINE)) == 4
    assert "No external contribution" in section
    assert "Original contributor PR" in section
    assert "Work incorporated elsewhere" in section
    assert template.count("## Testing\n") == 1


def test_documented_no_contribution_example_runs_without_other_evidence(tmp_path):
    match = re.search(r"```json\n(.+?)\n```", POLICY.read_text(encoding="utf-8"), re.DOTALL)
    assert match is not None
    record = json.loads(match[1])
    assert record == {"schema_version": 1, "mode": "none", "reason": "No external contribution is incorporated."}
    path = tmp_path / "record.json"
    path.write_text(json.dumps(record), encoding="utf-8")
    result = subprocess.run(
        [sys.executable, str(ROOT / "scripts/ci/check_contributor_provenance.py"),
         "--record", str(path), "--stage", "plan"],
        cwd=tmp_path, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=15, check=False,
    )
    assert result.returncode == 0, result.stderr
    output = json.loads(result.stdout)
    assert output["valid"] is True
