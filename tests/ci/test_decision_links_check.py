"""Tests for the decision-link check (#1790).

Covers scripts/ci/check_decision_links.py and its workflow.

Run with: python -m pytest tests/ci/test_decision_links_check.py -q
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_decision_links as cdl  # noqa: E402

LINK = "https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1791#issuecomment-1234567"


def check(after: str, before: str = "", path: str = "specs/example.md"):
    return cdl.evaluate([cdl.FileChange(path=path, before=before, after=after)])


# ---------------------------------------------------------------------------
# The phrase list
# ---------------------------------------------------------------------------

class TestPhrases:
    @pytest.mark.parametrize("sentence, category", [
        ("This was approved by the maintainer.", "approval"),
        ("The maintainer approved the change.", "approval"),
        ("Signed off by the maintainer.", "approval"),
        ("The maintainer decided to keep the flag.", "approval"),
        ("The owner ratified option A.", "approval"),
        ("The approval covers the 2.0 release only.", "approval"),
        ("This is an exception to rule 3.1.", "exception"),
        ("The CLI is exempt from the freeze.", "exception"),
        ("The size limit is waived for this release.", "exception"),
        ("`--legacy` will be removed in 2.0.", "removal"),
        ("The option is scheduled for removal.", "removal"),
        ("It is removed in v3 of the package.", "removal"),
        ("Releases ship no earlier than two weeks after the tag.", "date"),
        ("The old profile format works until 2027-01-01.", "date"),
        ("Users must migrate by March 2027.", "date"),
        ("There is a waiting period of eight weeks.", "date"),
    ])
    def test_decision_sentence_is_recognised(self, sentence, category):
        assert cdl.decision_category(sentence) == category

    @pytest.mark.parametrize("sentence", [
        "The command throws an exception when the table is missing.",
        "Human approval gates the merge.",
        "Metadata cannot grant approval.",
        "The command has a 60-second deadline.",
        "Observed live on 2026-11-10 in the English-base test environment.",
        "Removed the `--legacy` flag (#123).",
        "Adds `--dry-run` to `plugins deploy`.",
    ])
    def test_ordinary_sentence_is_not_decision_text(self, sentence):
        assert cdl.decision_category(sentence) is None

    def test_phrases_are_listed_in_one_place(self):
        """Every phrase lives in DECISION_PHRASES; the matcher uses only that list."""
        categories = {category for category, _ in cdl.DECISION_PHRASES}
        assert categories == {"approval", "exception", "removal", "date"}
        assert len(cdl._COMPILED) == len(cdl.DECISION_PHRASES)
        for pattern in (pattern for _, pattern in cdl.DECISION_PHRASES):
            re.compile(pattern)
        source = (REPO_ROOT / "scripts" / "ci" / "check_decision_links.py").read_text(encoding="utf-8")
        assert source.count("DECISION_PHRASES: tuple") == 1


# ---------------------------------------------------------------------------
# Links and "proposed"
# ---------------------------------------------------------------------------

class TestLinks:
    @pytest.mark.parametrize("link", [
        LINK,
        "https://github.com/o/r/pull/1831#issuecomment-99",
        "https://github.com/o/r/pull/1831#discussion_r42",
        "https://github.com/o/r/pull/1831#pullrequestreview-7",
        "https://github.com/o/r/issues/1791#issue-3001",
    ])
    def test_comment_links_count(self, link):
        assert check(f"Approved by the maintainer ([decision]({link})).\n") == []

    @pytest.mark.parametrize("text", [
        "Approved by the maintainer (#1791).",
        "Approved by the maintainer (https://github.com/o/r/issues/1791).",
        "Approved by the maintainer (https://example.com/o/r/issues/1#issuecomment-1).",
    ])
    def test_issue_number_or_plain_issue_link_does_not_count(self, text):
        found = check(text + "\n")
        assert len(found) == 1 and found[0].category == "approval"

    def test_proposed_sentence_passes(self):
        assert check("Proposed: `--legacy` will be removed in 2.0.\n") == []

    def test_link_must_be_in_the_same_sentence(self):
        text = f"The flag will be removed in 2.0. The decision is at {LINK}.\n"
        found = check(text)
        assert [f.category for f in found] == ["removal"]

    def test_link_on_a_wrapped_continuation_line_counts(self):
        text = f"The flag will be removed in 2.0, as the maintainer\ndecided in {LINK}.\n"
        assert check(text) == []


# ---------------------------------------------------------------------------
# Only changed lines, only in-scope files
# ---------------------------------------------------------------------------

class TestScope:
    @pytest.mark.parametrize("path, expected", [
        ("specs/release-cycle.md", True),
        ("specs/sub/thing.md", True),
        ("docs/releases/v1.8.0.md", True),
        ("src/PPDS.Cli/CHANGELOG.md", True),
        ("src/PPDS.Cli/README.md", False),
        ("src/PPDS.Cli/Commands/CHANGELOG.md", False),
        ("docs/RELEASE.md", False),
        ("specs/diagram.png", False),
        ("README.md", False),
    ])
    def test_in_scope(self, path, expected):
        assert cdl.in_scope(path) is expected

    def test_existing_unlinked_sentence_does_not_fail(self):
        before = "# Spec\n\nThis was approved by the maintainer.\n"
        after = before + "\nA new neutral sentence.\n"
        assert check(after, before) == []

    def test_added_unlinked_sentence_fails_with_its_line(self):
        before = "# Spec\n\nIntro.\n"
        after = before + "\nThe old flag will be removed in 2.0.\n"
        found = check(after, before)
        assert len(found) == 1
        assert found[0].line == 5
        assert found[0].category == "removal"
        assert "specs/example.md:5" in found[0].message()

    def test_editing_one_line_of_a_sentence_checks_the_whole_sentence(self):
        before = "The flag is kept for\nnow.\n"
        after = "The flag will be removed in\n2.0 for now.\n"
        assert len(check(after, before)) == 1
        # A change elsewhere in the paragraph's other sentence does not pull
        # in an untouched decision sentence.
        before = "Approved by the maintainer. Second sentence.\n"
        after = "Approved by the maintainer. Second sentence, edited.\n"
        assert len(check(after, before)) == 1  # same line, so it is checked
        before = "Approved by the maintainer.\nSecond sentence.\n"
        after = "Approved by the maintainer.\nSecond sentence, edited.\n"
        assert check(after, before) == []

    def test_out_of_scope_file_is_not_checked(self):
        assert check("Approved by the maintainer.\n", path="docs/RELEASE.md") == []

    def test_deleted_file_is_not_checked(self):
        assert cdl.evaluate([cdl.FileChange(path="specs/x.md", before="Approved.\n", after=None)]) == []

    def test_headings_code_and_comments_are_skipped(self):
        text = (
            "## [1.4.2] - 2026-10-30\n\n"
            "### Approved by nobody\n\n"
            "```\nwill be removed in 2.0\n```\n\n"
            "<!-- approved by the maintainer -->\n"
        )
        assert check(text) == []

    def test_changelog_entry_and_table_row(self):
        changelog = "## [1.5.0] - Unreleased\n\n- `--legacy` will be removed in 2.0 (#12).\n"
        assert len(check(changelog, path="src/PPDS.Cli/CHANGELOG.md")) == 1
        table = "| AC-1 | Approved exception for the CLI | test | ✅ |\n"
        assert len(check(table)) == 1


# ---------------------------------------------------------------------------
# Command line
# ---------------------------------------------------------------------------

def _git(cwd: Path, *args: str) -> str:
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


class TestCommandLine:
    def test_git_mode_checks_only_the_pull_requests_lines(self, tmp_path, capsys):
        _git(tmp_path, "init", "-q", "-b", "main")
        _git(tmp_path, "config", "user.email", "test@example.invalid")
        _git(tmp_path, "config", "user.name", "Test")
        spec = tmp_path / "specs" / "a.md"
        spec.parent.mkdir()
        spec.write_text("# A\n\nThis was approved by the maintainer.\n", encoding="utf-8")
        _git(tmp_path, "add", ".")
        _git(tmp_path, "commit", "-q", "-m", "base")
        base = _git(tmp_path, "rev-parse", "HEAD")

        spec.write_text(spec.read_text() + "\nA neutral line.\n", encoding="utf-8")
        _git(tmp_path, "commit", "-q", "-am", "neutral")
        args = ["--base", base, "--head", "HEAD", "--repo-root", str(tmp_path)]
        assert cdl.main(args) == 0
        assert "Decision-link check passed." in capsys.readouterr().out

        spec.write_text(spec.read_text() + "\n`--x` will be removed in 2.0.\n", encoding="utf-8")
        _git(tmp_path, "commit", "-q", "-am", "removal")
        assert cdl.main(args) == 1
        out = capsys.readouterr().out
        assert "::error file=specs/a.md,line=7,title=Decision links::" in out

        spec.write_text(spec.read_text().replace(
            "`--x` will be removed in 2.0.", f"`--x` will be removed in 2.0 ({LINK})."), encoding="utf-8")
        _git(tmp_path, "commit", "-q", "-am", "linked")
        assert cdl.main(args) == 0

    def test_input_json_mode(self, tmp_path):
        path = tmp_path / "changes.json"
        path.write_text(json.dumps({"files": [
            {"path": "docs/releases/v2.0.0.md", "before": "", "after": "Approved by the maintainer.\n"},
        ]}), encoding="utf-8")
        assert cdl.main(["--input-json", str(path)]) == 1
        path.write_text(json.dumps({"files": [
            {"path": "docs/releases/v2.0.0.md", "before": "", "after": f"Approved by the maintainer ({LINK}).\n"},
        ]}), encoding="utf-8")
        assert cdl.main(["--input-json", str(path)]) == 0

    def test_unreadable_input_exits_2(self, tmp_path):
        path = tmp_path / "changes.json"
        path.write_text("nope", encoding="utf-8")
        assert cdl.main(["--input-json", str(path)]) == 2

    def test_whole_tree_reports_and_exits_0(self, tmp_path, capsys):
        (tmp_path / "specs").mkdir()
        (tmp_path / "specs" / "a.md").write_text("Approved by the maintainer.\nNeutral.\n", encoding="utf-8")
        (tmp_path / "docs" / "releases").mkdir(parents=True)
        (tmp_path / "docs" / "releases" / "v1.md").write_text(f"Approved ({LINK}).\n", encoding="utf-8")
        assert cdl.main(["--whole-tree", "--repo-root", str(tmp_path)]) == 0
        assert capsys.readouterr().out.strip().endswith('1 sentence(s) would fail: {"approval": 1}')


# ---------------------------------------------------------------------------
# Workflow contract
# ---------------------------------------------------------------------------

WORKFLOW = REPO_ROOT / ".github" / "workflows" / "decision-link-check.yml"


def _workflow(path: Path = WORKFLOW) -> dict:
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


class TestWorkflow:
    def test_runs_on_pull_requests_to_main(self):
        trigger = _workflow()["on"]["pull_request"]
        assert {"opened", "synchronize", "reopened"} <= set(trigger["types"])
        assert trigger["branches"] == ["main"]

    def test_runs_the_script_against_the_pull_request_range(self):
        steps = _workflow()["jobs"]["decision-links"]["steps"]
        checkout = next(s for s in steps if s.get("uses", "").startswith("actions/checkout@"))
        assert checkout["with"]["fetch-depth"] == "0"
        assert checkout["with"]["persist-credentials"] == "false"
        step = next(s for s in steps if "check_decision_links.py" in s.get("run", ""))
        assert step["run"] == 'python scripts/ci/check_decision_links.py --base "$BASE_SHA" --head "$HEAD_SHA"'
        assert step["env"] == {
            "BASE_SHA": "${{ github.event.pull_request.base.sha }}",
            "HEAD_SHA": "${{ github.event.pull_request.head.sha }}",
        }

    def test_reads_only_and_is_not_in_the_pre_merge_aggregate(self):
        assert _workflow()["permissions"] == {"contents": "read"}
        gate = _workflow(REPO_ROOT / ".github" / "workflows" / "pre-merge-gate.yml")
        assert "decision-links" not in gate["jobs"]["pre-merge-gate"]["needs"]
