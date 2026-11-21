"""Tests for the decision-link check (#1790).

Covers scripts/ci/check_decision_links.py and its workflow. The phrase tests
are generated from the phrase list itself: every entry and every fragment
alternative carries an example sentence, and removing that entry or
alternative must stop its example being caught.

Run with: python -m pytest tests/ci/test_decision_links_check.py -q
"""
from __future__ import annotations

import json
import subprocess
import sys
from datetime import date
from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_decision_links as cdl  # noqa: E402

TODAY = date(2026, 11, 21)
REPO = cdl.DEFAULT_REPOSITORY
LINK = f"https://github.com/{REPO}/issues/1791#issuecomment-1234567"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "decision_links"


def check(after: str, before: str = "", path: str = "specs/example.md", today: date = TODAY):
    return cdl.evaluate([cdl.FileChange(path=path, before=before, after=after)], today=today)


def caught(sentence: str, matchers=None, today: date = TODAY):
    return cdl.decision_category(sentence, today, matchers)


# ---------------------------------------------------------------------------
# Generated from the phrase list: each entry and alternative is proven
# ---------------------------------------------------------------------------

PHRASE_IDS = [f"{i}:{p.category}:{p.pattern}" for i, p in enumerate(cdl.DECISION_PHRASES)]
ALTERNATIVES = [(name, j) for name, alts in cdl.FRAGMENTS.items() for j in range(len(alts))]
ALT_IDS = [f"{name}[{j}]:{cdl.FRAGMENTS[name][j].pattern}" for name, j in ALTERNATIVES]


class TestPhraseListIsProven:
    @pytest.mark.parametrize("index", range(len(cdl.DECISION_PHRASES)), ids=PHRASE_IDS)
    def test_each_entry_catches_its_example_and_only_it_does(self, index):
        phrase = cdl.DECISION_PHRASES[index]
        assert caught(phrase.example) == phrase.category
        without = cdl.build_matchers(cdl.DECISION_PHRASES[:index] + cdl.DECISION_PHRASES[index + 1:])
        assert caught(phrase.example, without) is None, (
            f"{phrase.example!r} is still caught without entry {phrase.pattern!r}; "
            "give the entry an example that only it catches"
        )

    @pytest.mark.parametrize("name, index", ALTERNATIVES, ids=ALT_IDS)
    def test_each_alternative_catches_its_example_and_only_it_does(self, name, index):
        alternative = cdl.FRAGMENTS[name][index]
        assert caught(alternative.example) is not None
        fragments = dict(cdl.FRAGMENTS)
        fragments[name] = fragments[name][:index] + fragments[name][index + 1:]
        assert caught(alternative.example, cdl.build_matchers(fragments=fragments)) is None, (
            f"{alternative.example!r} is still caught without {name} alternative "
            f"{alternative.pattern!r}; give it an example that only it catches"
        )

    def test_every_alternation_is_a_listed_alternative(self):
        """No "|" inside an entry: every alternative must be listed, and so tested."""
        for phrase in cdl.DECISION_PHRASES:
            assert "|" not in phrase.pattern, phrase.pattern
        for name, alts in cdl.FRAGMENTS.items():
            for alt in alts:
                assert "|" not in alt.pattern, (name, alt.pattern)

    def test_every_fragment_is_used_and_every_macro_is_defined(self):
        used = {name for p in cdl.DECISION_PHRASES for name in cdl.MACRO_RE.findall(p.pattern)}
        assert used == set(cdl.FRAGMENTS)

    def test_categories(self):
        assert {p.category for p in cdl.DECISION_PHRASES} == {"approval", "exception", "removal", "date"}

    def test_the_list_is_the_only_list(self):
        source = (REPO_ROOT / "scripts" / "ci" / "check_decision_links.py").read_text(encoding="utf-8")
        assert source.count("DECISION_PHRASES: tuple[Phrase, ...] = (") == 1
        assert source.count("FRAGMENTS: dict[str, tuple[Alt, ...]] = {") == 1
        assert cdl._MATCHERS == cdl.build_matchers()


# ---------------------------------------------------------------------------
# Corpus: written for this check, measured both ways
# ---------------------------------------------------------------------------

def _corpus(name: str) -> list[str]:
    return [
        line for line in (FIXTURES / name).read_text(encoding="utf-8").splitlines()
        if line and not line.startswith("#")
    ]


MUST_FAIL = [tuple(line.split(": ", 1)) for line in _corpus("must_fail.txt")]
MUST_PASS = _corpus("must_pass.txt")


class TestCorpus:
    def test_corpus_sizes(self):
        assert len(MUST_FAIL) >= 100
        assert len(MUST_PASS) >= 60

    @pytest.mark.parametrize("category, sentence", MUST_FAIL)
    def test_decision_sentence_fails(self, category, sentence):
        found = check(sentence + "\n")
        assert [f.category for f in found] == [category]

    @pytest.mark.parametrize("sentence", MUST_PASS)
    def test_ordinary_sentence_passes(self, sentence):
        assert check(sentence + "\n") == []


# ---------------------------------------------------------------------------
# Dates: only a date after the day of the change counts
# ---------------------------------------------------------------------------

class TestDates:
    @pytest.mark.parametrize("groups, end", [
        ({"y": "2027"}, date(2027, 12, 31)),
        ({"y": "2027", "m": "02"}, date(2027, 2, 28)),
        ({"y": "2027", "mn": "Sept."}, date(2027, 9, 30)),
        ({"y": "2027", "mn": "mar", "d": "1"}, date(2027, 3, 1)),
        ({"y": "2027", "q": "2"}, date(2027, 6, 30)),
        ({"y": "2027", "mn": "Build"}, None),
        ({"y": "2027", "m": "13"}, None),
        ({"y": "2027", "m": "2", "d": "30"}, None),
        ({"y": "2027", "mn": "March", "d": "1", "suffix": "xx"}, None),
    ])
    def test_period_end(self, groups, end):
        assert cdl.period_end(groups) == end

    def test_past_and_same_day_dates_are_records(self):
        assert check("Version 2 stays until 2026-11-21.\n") == []
        assert check("Version 2 stayed until 2026-03-01.\n") == []
        assert len(check("Version 2 stays until 2026-11-22.\n")) == 1

    def test_git_mode_uses_the_head_commit_date(self, repo):
        spec = repo / "specs" / "a.md"
        spec.write_text("# A\n\nVersion 2 stays until 2027-03-01.\n", encoding="utf-8")
        _git(repo, "commit", "-q", "-am", "dated", env_date="2027-04-01T00:00:00+00:00")
        assert cdl.main(["--base", "HEAD^", "--head", "HEAD", "--repo-root", str(repo)]) == 0
        assert cdl.main(["--base", "HEAD^", "--head", "HEAD", "--repo-root", str(repo), "--today", "2026-11-21"]) == 1


# ---------------------------------------------------------------------------
# Links and "proposed"
# ---------------------------------------------------------------------------

class TestLinks:
    @pytest.mark.parametrize("link", [
        LINK,
        f"https://github.com/{REPO}/pull/1831#issuecomment-99",
        f"https://github.com/{REPO}/pull/1831#discussion_r42",
        f"https://github.com/{REPO}/pull/1831#pullrequestreview-7",
        f"https://github.com/{REPO}/issues/1791#issue-3001",
        f"https://github.com/JoshSmithXrm/Power-Platform-Developer-Suite/issues/1#issuecomment-5",
    ])
    def test_comment_links_in_this_repository_count(self, link):
        assert check(f"Approved by the maintainer ([decision]({link})).\n") == []

    @pytest.mark.parametrize("text", [
        "Approved by the maintainer (#1791).",
        f"Approved by the maintainer (https://github.com/{REPO}/issues/1791).",
        "Approved by the maintainer (https://github.com/torvalds/linux/issues/1#issuecomment-1).",
        "Approved by the maintainer (https://github.com/joshsmithxrm/ppds-docs/issues/1#issuecomment-1).",
        f"Approved by the maintainer (http://github.com/{REPO}/issues/1#issuecomment-1).",
        f"Approved by the maintainer (https://example.com/?u=https://github.com/{REPO}/issues/1#issuecomment-1).",
        f"Approved by the maintainer (https://github.com/{REPO}/issues/1#issuecomment-1x).",
        f"Approved by the maintainer (https://github.com/{REPO}/pull/1).",
    ])
    def test_other_links_do_not_count(self, text):
        found = check(text + "\n")
        assert len(found) == 1 and found[0].category == "approval"

    @pytest.mark.parametrize("text", [
        "Proposed: `--legacy` will be removed in 2.0.",
        "- **Proposed** — `--legacy` will be removed in 2.0.",
        "`--legacy` will be removed in 2.0 (proposed).",
        "It is proposed that `--legacy` will be removed in 2.0.",
        "The proposed removal: `--legacy` will be removed in 2.0.",
    ])
    def test_proposed_exempts_where_it_qualifies_the_decision(self, text):
        assert check(text + "\n") == []

    @pytest.mark.parametrize("text", [
        "The proposed flag was approved by the maintainer.",
        "We will remove the proposed alias in 3.0.",
    ])
    def test_proposed_elsewhere_does_not_exempt(self, text):
        assert len(check(text + "\n")) == 1

    def test_link_must_be_in_the_same_sentence(self):
        found = check(f"The flag will be removed in 2.0. The decision is at {LINK}.\n")
        assert [f.category for f in found] == ["removal"]

    def test_link_on_a_wrapped_continuation_line_counts(self):
        assert check(f"The flag will be removed in 2.0, as the maintainer\ndecided in {LINK}.\n") == []

    def test_repository_comes_from_the_argument(self, tmp_path):
        other = "https://github.com/someone/fork/issues/1#issuecomment-1"
        path = tmp_path / "changes.json"
        path.write_text(json.dumps({"files": [
            {"path": "specs/a.md", "before": "", "after": f"Approved by the maintainer ({other}).\n"}]}), encoding="utf-8")
        assert cdl.main(["--input-json", str(path), "--today", "2026-11-21"]) == 1
        assert cdl.main(["--input-json", str(path), "--today", "2026-11-21", "--repository", "someone/fork"]) == 0
        assert cdl.main(["--input-json", str(path), "--repository", "noslash"]) == 2


# ---------------------------------------------------------------------------
# Sentences, scope and changed lines
# ---------------------------------------------------------------------------

class TestSentences:
    @pytest.mark.parametrize("text", [
        f"The flag will be removed in 3.0, i.e. the next major ({LINK}).",
        f"The flag will be removed in 3.0, e.g. with the other aliases ({LINK}).",
        f"The flag will be removed in 3.0 (see the review, cf. {LINK}).",
    ])
    def test_abbreviations_do_not_end_a_sentence(self, text):
        assert check(text + "\n") == []

    def test_a_table_row_is_one_unit(self):
        assert check(f"| AC-1 | The flag will be removed in 3.0. | {LINK} |\n") == []
        assert len(check("| AC-1 | The flag will be removed in 3.0. | ok |\n")) == 1

    def test_headings_code_and_comments_are_skipped(self):
        text = (
            "## [1.4.2] - 2026-10-30\n\n"
            "### Approved by nobody\n\n"
            "```\nwill be removed in 2.0\n```\n\n"
            "<!-- approved by the maintainer -->\n"
        )
        assert check(text) == []

    def test_a_blank_line_ends_a_paragraph(self):
        before = "Approved by the maintainer\n\nOld text.\n"
        assert check("Approved by the maintainer\n\nNew text.\n", before) == []

    def test_a_list_item_starts_a_new_unit(self):
        before = "- Approved by the maintainer\n- Old item.\n"
        assert check("- Approved by the maintainer\n- New item.\n", before) == []

    def test_line_numbers_survive_a_multi_line_comment(self):
        found = check("<!--\nnote\n-->\nWe will drop the flag.\n")
        assert [f.line for f in found] == [4]

    def test_finding_reports_the_first_line_of_the_sentence(self):
        found = check("# A\n\nIntro.\n\nThe flag will\nbe removed in 3.0.\n", "# A\n\nIntro.\n")
        assert [f.line for f in found] == [5]
        assert "specs/example.md:5" in found[0].message()
        assert "docs/MERGE-POLICY.md" in found[0].message()
        assert "VERSIONING" not in found[0].message()


class TestScope:
    @pytest.mark.parametrize("path, expected", [
        ("specs/release-cycle.md", True),
        ("specs/sub/thing.md", True),
        ("specs/THING.MD", True),
        ("docs/releases/v1.8.0.md", True),
        ("src/PPDS.Cli/CHANGELOG.md", True),
        ("src/PPDS.Cli/README.md", False),
        ("src/PPDS.Cli/Commands/CHANGELOG.md", False),
        ("docs/RELEASE.md", False),
        ("specs/diagram.png", False),
        ("CHANGELOG.md", False),
    ])
    def test_in_scope(self, path, expected):
        assert cdl.in_scope(path) is expected

    def test_existing_unlinked_sentence_does_not_fail(self):
        before = "# Spec\n\nThis was approved by the maintainer.\n"
        assert check(before + "\nA new neutral sentence.\n", before) == []

    def test_added_unlinked_sentence_fails(self):
        before = "# Spec\n\nIntro.\n"
        found = check(before + "\nThe old flag will be removed in 2.0.\n", before)
        assert [(f.line, f.category) for f in found] == [(5, "removal")]

    def test_only_the_touched_sentence_of_a_paragraph_is_checked(self):
        before = "Approved by the maintainer.\nSecond sentence.\n"
        assert check("Approved by the maintainer.\nSecond sentence, edited.\n", before) == []
        assert len(check("Approved by the maintainer. Edited.\n", "Approved by the maintainer. Second.\n")) == 1

    def test_out_of_scope_and_deleted_files_are_not_checked(self):
        assert check("Approved by the maintainer.\n", path="docs/RELEASE.md") == []
        assert cdl.evaluate([cdl.FileChange(path="specs/x.md", before="Approved by x.\n", after=None)], today=TODAY) == []


# ---------------------------------------------------------------------------
# Git mode: names, renames, merge base, fails closed
# ---------------------------------------------------------------------------

def _git(cwd: Path, *args: str, env_date: str | None = None) -> str:
    import os
    env = dict(os.environ)
    if env_date:
        env["GIT_COMMITTER_DATE"] = env["GIT_AUTHOR_DATE"] = env_date
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True, env=env).stdout.strip()


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    root = tmp_path / "repo"
    root.mkdir()
    _git(root, "init", "-q", "-b", "main")
    _git(root, "config", "user.email", "test@example.invalid")
    _git(root, "config", "user.name", "Test")
    (root / "specs").mkdir()
    (root / "specs" / "a.md").write_text("# A\n\nThis was approved by the maintainer.\n", encoding="utf-8")
    _git(root, "add", ".")
    _git(root, "commit", "-q", "-m", "base", env_date="2026-11-20T00:00:00+00:00")
    return root


def _commit(repo: Path, message: str) -> None:
    _git(repo, "add", "-A")
    _git(repo, "commit", "-q", "-m", message, env_date="2026-11-21T00:00:00+00:00")


def _run(repo: Path, base: str = "main", head: str = "HEAD") -> int:
    return cdl.main(["--base", base, "--head", head, "--repo-root", str(repo)])


class TestGitMode:
    def test_only_the_pull_requests_lines(self, repo, capsys):
        _git(repo, "checkout", "-q", "-b", "feature")
        spec = repo / "specs" / "a.md"
        spec.write_text(spec.read_text() + "\nA neutral line.\n", encoding="utf-8")
        _commit(repo, "neutral")
        assert _run(repo) == 0
        spec.write_text(spec.read_text() + "\n`--x` will be removed in 2.0.\n", encoding="utf-8")
        _commit(repo, "removal")
        assert _run(repo) == 1
        assert "::error file=specs/a.md,line=7,title=Decision links::" in capsys.readouterr().out
        spec.write_text(spec.read_text().replace("in 2.0.", f"in 2.0 ({LINK})."), encoding="utf-8")
        _commit(repo, "linked")
        assert _run(repo) == 0

    def test_non_ascii_file_name_is_checked(self, repo, capsys):
        _git(repo, "checkout", "-q", "-b", "feature")
        for name in ("café.md", "a–b.md"):
            (repo / "specs" / name).write_text("The flag will be removed in 3.0.\n", encoding="utf-8")
        _commit(repo, "non-ascii names")
        assert _run(repo) == 1
        out = capsys.readouterr().out
        assert "specs/café.md:1" in out and "specs/a–b.md:1" in out
        assert "Checked 2 file(s)" in out

    def test_pure_rename_is_not_a_change(self, repo):
        _git(repo, "checkout", "-q", "-b", "feature")
        _git(repo, "mv", "specs/a.md", "specs/b.md")
        _commit(repo, "rename")
        assert _run(repo) == 0

    def test_rename_with_an_edit_checks_only_the_edit(self, repo):
        _git(repo, "checkout", "-q", "-b", "feature")
        _git(repo, "mv", "specs/a.md", "specs/b.md")
        spec = repo / "specs" / "b.md"
        spec.write_text(spec.read_text() + "\nA neutral line.\n", encoding="utf-8")
        _commit(repo, "rename and edit")
        assert _run(repo) == 0
        spec.write_text(spec.read_text() + "\nWe will drop the flag.\n", encoding="utf-8")
        _commit(repo, "decision")
        assert _run(repo) == 1

    def test_move_into_scope_is_checked_whole(self, repo):
        (repo / "notes.md").write_text("We will drop the flag.\n", encoding="utf-8")
        _commit(repo, "notes")
        _git(repo, "checkout", "-q", "-b", "feature")
        _git(repo, "mv", "notes.md", "specs/notes.md")
        _commit(repo, "move into scope")
        assert _run(repo) == 1

    def test_base_that_moved_after_the_branch_point_is_not_the_prs(self, repo):
        _git(repo, "checkout", "-q", "-b", "feature")
        (repo / "specs" / "b.md").write_text("A neutral spec.\n", encoding="utf-8")
        _commit(repo, "feature")
        _git(repo, "checkout", "-q", "main")
        spec = repo / "specs" / "a.md"
        spec.write_text(spec.read_text() + "\nWe will drop the flag.\n", encoding="utf-8")
        _commit(repo, "someone else")
        _git(repo, "checkout", "-q", "feature")
        assert _run(repo, base="main", head="feature") == 0

    def test_file_deleted_on_main_after_the_branch_point_is_not_the_prs(self, repo):
        _git(repo, "checkout", "-q", "-b", "feature")
        (repo / "specs" / "b.md").write_text("A neutral spec.\n", encoding="utf-8")
        _commit(repo, "feature")
        _git(repo, "checkout", "-q", "main")
        _git(repo, "rm", "-q", "specs/a.md")
        _commit(repo, "main deletes a.md")
        _git(repo, "checkout", "-q", "feature")
        assert _run(repo, base="main", head="feature") == 0

    def test_lines_are_compared_with_the_merge_base_not_the_base(self, repo):
        _git(repo, "checkout", "-q", "-b", "feature")
        spec = repo / "specs" / "a.md"
        spec.write_text(spec.read_text() + "\nA neutral line.\n", encoding="utf-8")
        _commit(repo, "feature edit")
        _git(repo, "checkout", "-q", "main")
        spec.write_text(spec.read_text().replace("maintainer.", f"maintainer ({LINK})."), encoding="utf-8")
        _commit(repo, "main links the old sentence")
        _git(repo, "checkout", "-q", "feature")
        assert _run(repo, base="main", head="feature") == 0

    def test_unrelated_histories_exit_2(self, repo):
        _git(repo, "checkout", "-q", "--orphan", "other")
        (repo / "specs" / "b.md").write_text("We will drop the flag.\n", encoding="utf-8")
        _commit(repo, "orphan")
        assert _run(repo, base="main", head="other") == 2

    def test_empty_revision_is_named(self, repo, capsys):
        assert cdl.main(["--base", "", "--head", "HEAD", "--repo-root", str(repo)]) == 2
        assert "must both be given and non-empty" in capsys.readouterr().err

    @pytest.mark.parametrize("base, head", [("", "HEAD"), ("main", ""), ("nope", "HEAD"), ("main", "nope")])
    def test_bad_revisions_exit_2(self, repo, capsys, base, head):
        assert cdl.main(["--base", base, "--head", head, "--repo-root", str(repo)]) == 2
        assert "::error title=Decision links::could not read the input" in capsys.readouterr().err

    def test_missing_head_argument_exits_2(self, repo):
        assert cdl.main(["--base", "main", "--repo-root", str(repo)]) == 2

    def test_not_a_repository_exits_2(self, tmp_path):
        assert cdl.main(["--base", "a", "--head", "b", "--repo-root", str(tmp_path)]) == 2

    @pytest.mark.parametrize("failing", ["merge-base", "diff", "show"])
    def test_any_git_failure_exits_2(self, repo, monkeypatch, failing):
        _git(repo, "checkout", "-q", "-b", "feature")
        (repo / "specs" / "a.md").write_text("# A\n\nchanged\n", encoding="utf-8")
        _commit(repo, "edit")
        real = cdl._git

        def flaky(root, *args):
            if args[0] == failing:
                raise RuntimeError(f"git {failing} failed")
            return real(root, *args)

        monkeypatch.setattr(cdl, "_git", flaky)
        assert _run(repo) == 2

    def test_git_helper_raises_on_failure(self, tmp_path):
        with pytest.raises(RuntimeError):
            cdl._git(tmp_path, "rev-parse", "HEAD")


class TestCommandLine:
    def test_input_json_mode(self, tmp_path):
        path = tmp_path / "changes.json"
        path.write_text(json.dumps({"files": [
            {"path": "docs/releases/v2.0.0.md", "before": "", "after": "Approved by the maintainer.\n"}]}), encoding="utf-8")
        assert cdl.main(["--input-json", str(path)]) == 1
        path.write_text(json.dumps({"files": [
            {"path": "docs/releases/v2.0.0.md", "before": "", "after": f"Approved by the maintainer ({LINK}).\n"}]}), encoding="utf-8")
        assert cdl.main(["--input-json", str(path)]) == 0

    def test_unreadable_input_exits_2(self, tmp_path):
        path = tmp_path / "changes.json"
        path.write_text("nope", encoding="utf-8")
        assert cdl.main(["--input-json", str(path)]) == 2

    def test_whole_tree_reports_and_exits_0(self, tmp_path, capsys):
        (tmp_path / "specs").mkdir()
        (tmp_path / "specs" / "a.md").write_text("Approved by the maintainer.\nNeutral.\n", encoding="utf-8")
        (tmp_path / "docs" / "releases").mkdir(parents=True)
        (tmp_path / "docs" / "releases" / "v1.md").write_text(f"Approved by x ({LINK}).\n", encoding="utf-8")
        (tmp_path / "src" / "PPDS.Cli").mkdir(parents=True)
        (tmp_path / "src" / "PPDS.Cli" / "CHANGELOG.md").write_text("- We will drop the flag.\n", encoding="utf-8")
        assert cdl.main(["--whole-tree", "--repo-root", str(tmp_path), "--today", "2026-11-21"]) == 0
        assert capsys.readouterr().out.strip().endswith(
            '2 sentence(s) would fail: {"approval": 1, "removal": 1}')


# ---------------------------------------------------------------------------
# Workflow contract
# ---------------------------------------------------------------------------

WORKFLOW = REPO_ROOT / ".github" / "workflows" / "decision-link-check.yml"


def _workflow(path: Path = WORKFLOW) -> dict:
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


class TestWorkflow:
    def test_runs_on_every_pull_request_to_main(self):
        trigger = _workflow()["on"]["pull_request"]
        assert {"opened", "synchronize", "reopened", "edited"} <= set(trigger["types"])
        assert trigger["branches"] == ["main"]
        assert "paths" not in trigger and "paths-ignore" not in trigger

    def test_job_cannot_be_skipped_or_soft_failed(self):
        job = _workflow()["jobs"]["decision-links"]
        assert "if" not in job and "continue-on-error" not in job
        for step in job["steps"]:
            assert "if" not in step and "continue-on-error" not in step
            assert "||" not in step.get("run", "")

    def test_runs_the_script_against_the_pull_request_range(self):
        steps = _workflow()["jobs"]["decision-links"]["steps"]
        checkout = next(s for s in steps if s.get("uses", "").startswith("actions/checkout@"))
        assert checkout["with"]["fetch-depth"] == "0"
        assert checkout["with"]["persist-credentials"] == "false"
        step = next(s for s in steps if "check_decision_links.py" in s.get("run", ""))
        assert step["run"] == (
            'python scripts/ci/check_decision_links.py --base "$BASE_SHA" --head "$HEAD_SHA" '
            '--repository "$GITHUB_REPOSITORY"'
        )
        assert step["env"] == {
            "BASE_SHA": "${{ github.event.pull_request.base.sha }}",
            "HEAD_SHA": "${{ github.event.pull_request.head.sha }}",
        }

    def test_reads_only_and_is_not_in_the_pre_merge_aggregate(self):
        assert _workflow()["permissions"] == {"contents": "read"}
        gate = _workflow(REPO_ROOT / ".github" / "workflows" / "pre-merge-gate.yml")
        assert "decision-links" not in gate["jobs"]["pre-merge-gate"]["needs"]
