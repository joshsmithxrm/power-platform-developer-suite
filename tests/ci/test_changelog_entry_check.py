"""Tests for the changelog-entry and breaking-statement check (#1789).

Covers scripts/ci/check_changelog_entry.py, its workflow, and the release-plan
refusal of a non-major version for a surface a `breaking-change` PR changed.

Run with: python -m pytest tests/ci/test_changelog_entry_check.py -q
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_changelog_entry as cce  # noqa: E402
import release_plan as rp  # noqa: E402
from release_model import BreakingChange, FileChange, ReleaseGraph, build_release_plan  # noqa: E402

PACKAGES = ["PPDS.Cli", "PPDS.Dataverse", "PPDS.Extension"]

BASE_CHANGELOG = """# Changelog - PPDS.Cli

## [Unreleased]

## [1.5.0] - Unreleased

### Added

- Existing entry (#1).

## [1.4.2] - 2026-10-30

### Fixed

- Old fix (#2).
"""

NOT_BREAKING_BODY = """## Summary

Fixes a thing.

## Breaking changes

<!-- List any breaking changes and migration steps, or write "None". -->
None
"""

BREAKING_BODY = """## Summary

Changes an exit code.

## Breaking changes

<!-- List any breaking changes and migration steps, or write "None". -->
`plugins diff` exits 12 instead of 2 when the check is incomplete; scripts must test for 12.
"""

TEMPLATE_ONLY_BODY = """## Summary

## Breaking changes

<!-- List any breaking changes and migration steps, or write "None". -->
"""


def add_under(changelog: str, heading: str, line: str) -> str:
    """Insert ``line`` after ``heading`` (a ``## `` line) in ``changelog``."""
    head, sep, tail = changelog.partition(heading + "\n")
    assert sep, heading
    return head + sep + "\n" + line + "\n" + tail


def changelog_change(package: str, after: str, before: str = BASE_CHANGELOG) -> FileChange:
    return FileChange(path=f"src/{package}/CHANGELOG.md", before=before, after=after)


def run(changes, *, labels=(), body=NOT_BREAKING_BODY, author="contributor"):
    return cce.evaluate(
        changes, labels=labels, body=body, author=author,
        packages_with_changelog=PACKAGES,
    )


CODE = FileChange(path="src/PPDS.Cli/Commands/Plugins/DiffCommand.cs")
GOOD_ENTRY = changelog_change(
    "PPDS.Cli", add_under(BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- Fixed the thing (#3).")
)


# ---------------------------------------------------------------------------
# Changelog entry
# ---------------------------------------------------------------------------

class TestChangelogEntry:
    def test_src_change_without_changelog_fails(self):
        result = run([CODE])
        assert not result.passed
        assert "src/PPDS.Cli/CHANGELOG.md was not changed" in result.failures[0]

    def test_entry_under_version_unreleased_section_passes(self):
        result = run([CODE, GOOD_ENTRY])
        assert result.passed, result.failures
        assert "PPDS.Cli: changelog entry found." in result.notes

    def test_entry_under_empty_unreleased_section_fails(self):
        after = add_under(BASE_CHANGELOG, "## [Unreleased]", "- Fixed the thing (#3).")
        result = run([CODE, changelog_change("PPDS.Cli", after)])
        assert not result.passed
        assert "empty `## [Unreleased]`" in result.failures[0]

    def test_entry_under_released_section_fails(self):
        after = add_under(BASE_CHANGELOG, "## [1.4.2] - 2026-10-30", "- Late entry (#3).")
        result = run([CODE, changelog_change("PPDS.Cli", after)])
        assert not result.passed
        assert "## [1.4.2] - 2026-10-30" in result.failures[0]

    def test_changelog_edit_without_new_entry_line_fails(self):
        after = BASE_CHANGELOG.replace("\n\n## [1.4.2]", "\n\n\n## [1.4.2]")
        result = run([CODE, changelog_change("PPDS.Cli", after)])
        assert not result.passed
        assert "no entry line was added" in result.failures[0]

    def test_reworded_existing_entry_counts_as_an_entry(self):
        after = BASE_CHANGELOG.replace("- Existing entry (#1).", "- Existing entry, now with the new flag (#1, #3).")
        assert run([CODE, changelog_change("PPDS.Cli", after)]).passed

    def test_new_changelog_file_with_version_section_passes(self):
        new = "# Changelog\n\n## [Unreleased]\n\n## [0.1.0] - Unreleased\n\n- First (#3).\n"
        change = FileChange(path="src/PPDS.Cli/CHANGELOG.md", before=None, after=new)
        assert run([CODE, change]).passed

    def test_prerelease_version_section_is_accepted(self):
        before = BASE_CHANGELOG.replace("[1.5.0] - Unreleased", "[2.0.0-beta.1] - Unreleased")
        after = add_under(before, "## [2.0.0-beta.1] - Unreleased", "- Fixed (#3).")
        assert run([CODE, changelog_change("PPDS.Cli", after, before)]).passed

    def test_two_packages_each_need_their_own_entry(self):
        dataverse_code = FileChange(path="src/PPDS.Dataverse/Query/QueryExecutor.cs")
        result = run([CODE, GOOD_ENTRY, dataverse_code])
        assert not result.passed
        assert len(result.failures) == 1
        assert result.failures[0].startswith("PPDS.Dataverse:")

        dataverse_entry = changelog_change(
            "PPDS.Dataverse",
            add_under(BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- Fixed (#3)."),
        )
        assert run([CODE, GOOD_ENTRY, dataverse_code, dataverse_entry]).passed

    def test_entry_in_another_packages_changelog_does_not_count(self):
        other = changelog_change(
            "PPDS.Dataverse",
            add_under(BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- Fixed (#3)."),
        )
        result = run([CODE, other])
        assert not result.passed
        assert result.failures[0].startswith("PPDS.Cli:")


# ---------------------------------------------------------------------------
# What is and is not a behaviour change
# ---------------------------------------------------------------------------

class TestScope:
    @pytest.mark.parametrize("path", [
        "src/PPDS.Cli/README.md",
        "src/PPDS.Extension/src/__tests__/panels/QueryPanel.test.ts",
        "src/PPDS.Extension/src/notebooks/__tests__/serializer.test.ts",
        "src/PPDS.Extension/e2e/smoke.spec.ts",
        "src/PPDS.Cli/CHANGELOG.md",
    ])
    def test_docs_tests_and_changelog_only_changes_pass(self, path):
        result = run([FileChange(path=path)], body="")
        assert result.passed, result.failures
        assert "No behaviour change under src/: not checked." in result.notes

    def test_generated_code_is_a_behaviour_change(self):
        generated = FileChange(path="src/PPDS.Dataverse/Generated/Entities/PluginType.cs")
        result = run([generated])
        assert not result.passed
        assert result.failures[0].startswith("PPDS.Dataverse:")

    def test_package_without_changelog_is_not_checked(self):
        result = run([FileChange(path="src/PPDS.Analyzers/Rules/NoSyncOverAsync.cs")], body="")
        assert result.passed
        assert any("no package CHANGELOG" in note for note in result.notes)

    def test_changes_outside_src_are_not_checked(self):
        result = run([FileChange(path="scripts/ci/check_pr_size.py"),
                      FileChange(path="docs/RELEASE.md")], body="")
        assert result.passed

    def test_internal_label_skips_the_check(self):
        result = run([CODE], labels=["internal"], body="")
        assert result.passed
        assert "Labelled `internal`: not checked." in result.notes

    def test_internal_and_breaking_labels_together_fail(self):
        result = run([CODE], labels=["internal", "breaking-change"])
        assert not result.passed
        assert "both `internal` and `breaking-change`" in result.failures[0]

    @pytest.mark.parametrize("author", ["dependabot[bot]", "app/dependabot"])
    def test_dependabot_pull_requests_are_not_checked(self, author):
        bump = FileChange(path="src/PPDS.Extension/package.json")
        result = run([bump], author=author, body="Bumps esbuild from 0.24 to 0.25.")
        assert result.passed
        assert "Dependabot pull request: not checked." in result.notes


# ---------------------------------------------------------------------------
# Breaking statement
# ---------------------------------------------------------------------------

class TestBreakingStatement:
    @pytest.mark.parametrize("body, expected", [
        (NOT_BREAKING_BODY, "no"),
        (BREAKING_BODY, "yes"),
        (TEMPLATE_ONLY_BODY, None),
        ("", None),
        (None, None),
        ("## Breaking changes\n\nNone.\n\n## Notes\n\nmore", "no"),
        ("## Breaking changes\n\n- None\n", "no"),
        ("## Breaking changes\n\nN/A\n", "no"),
        ("Breaking: no, only adds a flag.\n", "no"),
        ("- **Breaking:** yes, exit code 2 becomes 12.\n", "yes"),
        ("Breaking: yes\n\n## Breaking changes\n\nNone\n", "yes"),
        ("## Breaking changes\r\n\r\nNone\r\n", "no"),
        ("## Breaking\n\nYes: removes `--legacy`.\n", "yes"),
        ("## Breaking changes\n\n## Testing\n\nran it\n", None),
    ])
    def test_statement_is_read_from_the_description(self, body, expected):
        assert cce.breaking_statement(body) == expected

    def test_missing_statement_fails(self):
        result = run([CODE, GOOD_ENTRY], body=TEMPLATE_ONLY_BODY)
        assert not result.passed
        assert "no breaking statement" in result.failures[0]

    def test_breaking_statement_without_label_fails(self):
        result = run([CODE, GOOD_ENTRY], body=BREAKING_BODY)
        assert not result.passed
        assert any("not labelled `breaking-change`" in f for f in result.failures)

    def test_label_without_breaking_statement_fails(self):
        result = run([CODE, GOOD_ENTRY], labels=["breaking-change"])
        assert not result.passed
        assert "says it is not breaking" in result.failures[0]

    def test_breaking_change_entry_must_say_breaking(self):
        result = run([CODE, GOOD_ENTRY], labels=["breaking-change"], body=BREAKING_BODY)
        assert not result.passed
        assert "no changelog entry says so" in result.failures[0]

    def test_labelled_breaking_change_with_breaking_entry_passes(self):
        entry = changelog_change("PPDS.Cli", add_under(
            BASE_CHANGELOG, "## [1.5.0] - Unreleased",
            "- **Breaking: exit code 12** - `plugins diff` exits 12 when incomplete (#3).",
        ))
        result = run([CODE, entry], labels=["breaking-change"], body=BREAKING_BODY)
        assert result.passed, result.failures

    def test_breaking_label_outside_src_still_needs_a_statement(self):
        result = run([FileChange(path="docs/RELEASE.md")], labels=["breaking-change"], body="")
        assert not result.passed
        assert "no breaking statement" in result.failures[0]


# ---------------------------------------------------------------------------
# Command line
# ---------------------------------------------------------------------------

def _git(cwd: Path, *args: str) -> str:
    return subprocess.run(
        ["git", *args], cwd=cwd, check=True, capture_output=True, text=True,
    ).stdout.strip()


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    _git(tmp_path, "init", "-q", "-b", "main")
    _git(tmp_path, "config", "user.email", "test@example.invalid")
    _git(tmp_path, "config", "user.name", "Test")
    changelog = tmp_path / "src" / "PPDS.Cli" / "CHANGELOG.md"
    changelog.parent.mkdir(parents=True)
    changelog.write_text(BASE_CHANGELOG, encoding="utf-8")
    (tmp_path / "src" / "PPDS.Cli" / "Program.cs").write_text("class P {}\n", encoding="utf-8")
    _git(tmp_path, "add", ".")
    _git(tmp_path, "commit", "-q", "-m", "base")
    return tmp_path


def _event(tmp_path: Path, repo: Path, *, labels=(), body=NOT_BREAKING_BODY) -> Path:
    event = {
        "pull_request": {
            "labels": [{"name": name} for name in labels],
            "body": body,
            "user": {"login": "contributor"},
            "base": {"sha": _git(repo, "rev-parse", "main")},
            "head": {"sha": _git(repo, "rev-parse", "HEAD")},
        }
    }
    path = tmp_path / "event.json"
    path.write_text(json.dumps(event), encoding="utf-8")
    return path


class TestCommandLine:
    def test_event_mode_fails_without_entry_and_passes_with_it(self, repo, tmp_path, capsys):
        _git(repo, "checkout", "-q", "-b", "feature")
        (repo / "src" / "PPDS.Cli" / "Program.cs").write_text("class P { int x; }\n", encoding="utf-8")
        _git(repo, "commit", "-q", "-am", "code only")
        event = _event(tmp_path, repo)
        assert cce.main(["--event", str(event), "--repo-root", str(repo)]) == 1
        assert "::error title=Changelog check::PPDS.Cli" in capsys.readouterr().out

        changelog = repo / "src" / "PPDS.Cli" / "CHANGELOG.md"
        changelog.write_text(
            add_under(BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- Added x (#3)."),
            encoding="utf-8",
        )
        _git(repo, "commit", "-q", "-am", "entry")
        event = _event(tmp_path, repo)
        assert cce.main(["--event", str(event), "--repo-root", str(repo)]) == 0
        assert "Changelog check passed." in capsys.readouterr().out

    def test_event_mode_uses_the_merge_base(self, repo, tmp_path):
        """A changelog entry that landed on main after the branch point is not this PR's."""
        _git(repo, "checkout", "-q", "-b", "feature")
        (repo / "src" / "PPDS.Cli" / "Program.cs").write_text("class P { int x; }\n", encoding="utf-8")
        _git(repo, "commit", "-q", "-am", "code only")
        _git(repo, "checkout", "-q", "main")
        (repo / "src" / "PPDS.Cli" / "CHANGELOG.md").write_text(
            add_under(BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- Someone else (#4)."),
            encoding="utf-8",
        )
        _git(repo, "commit", "-q", "-am", "other PR")
        _git(repo, "checkout", "-q", "feature")
        event = _event(tmp_path, repo)
        assert cce.main(["--event", str(event), "--repo-root", str(repo)]) == 1

    def test_input_json_mode(self, tmp_path):
        data = {
            "files": [{"path": CODE.path},
                      {"path": GOOD_ENTRY.path, "before": GOOD_ENTRY.before, "after": GOOD_ENTRY.after}],
            "labels": [], "body": NOT_BREAKING_BODY, "author": "contributor",
        }
        path = tmp_path / "pr.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        assert cce.main(["--input-json", str(path), "--repo-root", str(REPO_ROOT)]) == 0
        data["files"] = data["files"][:1]
        path.write_text(json.dumps(data), encoding="utf-8")
        assert cce.main(["--input-json", str(path), "--repo-root", str(REPO_ROOT)]) == 1

    def test_unreadable_input_exits_2(self, tmp_path):
        path = tmp_path / "pr.json"
        path.write_text("not json", encoding="utf-8")
        assert cce.main(["--input-json", str(path)]) == 2

    def test_event_without_pull_request_exits_2(self, tmp_path):
        path = tmp_path / "event.json"
        path.write_text("{}", encoding="utf-8")
        assert cce.main(["--event", str(path)]) == 2

    def test_packages_are_discovered_from_src_changelogs(self):
        packages = cce.discover_packages(REPO_ROOT)
        assert {"PPDS.Cli", "PPDS.Dataverse", "PPDS.Extension"} <= set(packages)
        assert "PPDS.Analyzers" not in packages


# ---------------------------------------------------------------------------
# Workflow contract
# ---------------------------------------------------------------------------

WORKFLOW = REPO_ROOT / ".github" / "workflows" / "changelog-check.yml"


def _workflow(path: Path = WORKFLOW) -> dict:
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


class TestWorkflow:
    def test_runs_on_every_input_change(self):
        trigger = _workflow()["on"]["pull_request"]
        assert {"opened", "synchronize", "edited", "labeled", "unlabeled"} <= set(trigger["types"])
        assert trigger["branches"] == ["main"]

    def test_runs_the_script_with_the_event_and_full_history(self):
        steps = _workflow()["jobs"]["changelog-entry"]["steps"]
        checkout = next(s for s in steps if s.get("uses", "").startswith("actions/checkout@"))
        assert checkout["with"]["fetch-depth"] == "0"
        assert checkout["with"]["persist-credentials"] == "false"
        runs = [s["run"] for s in steps if "check_changelog_entry.py" in s.get("run", "")]
        assert runs == ['python scripts/ci/check_changelog_entry.py --event "$GITHUB_EVENT_PATH"']

    def test_reads_only(self):
        assert _workflow()["permissions"] == {"contents": "read"}

    def test_is_not_part_of_the_pre_merge_gate_aggregate(self):
        gate = _workflow(REPO_ROOT / ".github" / "workflows" / "pre-merge-gate.yml")
        assert "changelog-entry" not in gate["jobs"]["pre-merge-gate"]["needs"]


# ---------------------------------------------------------------------------
# Release plan refuses a non-major version for a breaking-change surface
# ---------------------------------------------------------------------------

@pytest.fixture(scope="module")
def graph() -> ReleaseGraph:
    return ReleaseGraph.discover(REPO_ROOT)


BREAKING_CLI = BreakingChange(
    number=1605, title="exit 12 for incomplete checks",
    paths=("src/PPDS.Cli/Commands/Plugins/DiffCommand.cs",),
)
RUNTIME = [FileChange(path="src/PPDS.Cli/Commands/Plugins/DiffCommand.cs", before="a", after="b")]


class TestReleasePlanRefusal:
    @pytest.mark.parametrize("kind", ["patch", "minor"])
    def test_non_major_plan_with_breaking_change_is_refused(self, graph, kind):
        plan = build_release_plan(graph, RUNTIME, release_kind=kind, breaking_changes=[BREAKING_CLI])
        assert plan["breaking_changes"] == [
            {"pr": 1605, "title": "exit 12 for incomplete checks", "surfaces": ["PPDS.Cli"]}
        ]
        assert len(plan["refusals"]) == 1
        assert plan["refusals"][0].startswith(f"PPDS.Cli: #1605 is labelled breaking-change; a {kind} version is refused.")

    def test_major_plan_lists_but_does_not_refuse(self, graph):
        plan = build_release_plan(graph, RUNTIME, release_kind="major", breaking_changes=[BREAKING_CLI])
        assert plan["refusals"] == []
        assert plan["breaking_changes"][0]["surfaces"] == ["PPDS.Cli"]

    def test_no_breaking_change_means_no_refusal(self, graph):
        plan = build_release_plan(graph, RUNTIME, release_kind="patch")
        assert plan["refusals"] == []
        assert plan["breaking_changes"] == []

    def test_breaking_change_with_no_surface_is_refused_in_a_patch(self, graph):
        docs_only = BreakingChange(number=9, title="docs", paths=("docs/RELEASE.md",))
        plan = build_release_plan(graph, RUNTIME, release_kind="patch", breaking_changes=[docs_only])
        assert plan["refusals"] and "changes no release surface" in plan["refusals"][0]

    def test_markdown_shows_the_refusal(self, graph):
        from release_model import render_markdown
        plan = build_release_plan(graph, RUNTIME, release_kind="patch", breaking_changes=[BREAKING_CLI])
        text = render_markdown(plan)
        assert "### Breaking Changes in Range" in text
        assert "### Refused" in text

    def test_cli_exits_1_on_refusal_and_0_without(self, tmp_path, capsys):
        files = tmp_path / "files.txt"
        files.write_text("src/PPDS.Cli/Commands/Plugins/DiffCommand.cs\n", encoding="utf-8")
        breaking = tmp_path / "breaking.json"
        breaking.write_text(json.dumps([{
            "number": 1605, "title": "exit 12",
            "mergeCommit": {"oid": "a" * 40},
            "files": [{"path": "src/PPDS.Cli/Commands/Plugins/DiffCommand.cs"}],
        }]), encoding="utf-8")
        base = ["--repo-root", str(REPO_ROOT), "--files-file", str(files), "--format", "title"]
        assert rp.main([*base, "--release-kind", "patch", "--breaking-prs-file", str(breaking)]) == 1
        out = capsys.readouterr()
        assert out.out.startswith("Release plan refused")
        assert "::error title=Release plan refused::PPDS.Cli" in out.err
        assert rp.main([*base, "--release-kind", "major", "--breaking-prs-file", str(breaking)]) == 0
        assert rp.main([*base, "--release-kind", "patch"]) == 0

    def test_only_pull_requests_merged_in_the_range_count(self, monkeypatch, tmp_path):
        in_range, out_of_range = "1" * 40, "2" * 40

        def fake_git(_root, *args, **_kwargs):
            if args[0] == "rev-list":
                assert args[1] == "base..head"
                return in_range + "\n"
            if args[0] == "diff":
                assert args[-1] == in_range
                return "src/PPDS.Cli/Program.cs\n"
            raise AssertionError(args)

        monkeypatch.setattr(rp, "_git", fake_git)
        path = tmp_path / "breaking.json"
        path.write_text(json.dumps([
            {"number": 1, "title": "in", "mergeCommit": {"oid": in_range}},
            {"number": 2, "title": "out", "mergeCommit": {"oid": out_of_range}},
        ]), encoding="utf-8")
        changes = rp.breaking_changes_from_file(path, REPO_ROOT, "base", "head")
        assert changes == [BreakingChange(number=1, title="in", paths=("src/PPDS.Cli/Program.cs",))]

    def test_pull_request_without_files_or_merge_commit_is_an_error(self, tmp_path):
        path = tmp_path / "breaking.json"
        path.write_text(json.dumps([{"number": 3, "title": "x"}]), encoding="utf-8")
        with pytest.raises(ValueError, match="#3"):
            rp.breaking_changes_from_file(path, REPO_ROOT, None, None)

    def test_post_merge_workflow_passes_the_label_to_both_plan_runs(self):
        wf = _workflow(REPO_ROOT / ".github" / "workflows" / "post-merge-release-check.yml")
        steps = wf["jobs"]["patch-release-detection"]["steps"]
        names = [s.get("name") for s in steps]
        record = names.index("Record the breaking-change label for the release plan")
        build = names.index("Build explained release advisory")
        assert record < build
        assert '.name == "breaking-change"' in steps[record]["run"]
        assert "$GITHUB_EVENT_PATH" in steps[record]["run"]
        assert steps[build]["run"].count("--breaking-prs-file breaking-prs.json") == 2


def test_breaking_pull_request_is_mapped_from_its_merge_commit(repo, tmp_path):
    base = _git(repo, "rev-parse", "HEAD")
    (repo / "src" / "PPDS.Cli" / "Program.cs").write_text("class P { int y; }\n", encoding="utf-8")
    _git(repo, "commit", "-q", "-am", "breaking (#7)")
    merge = _git(repo, "rev-parse", "HEAD")
    path = tmp_path / "breaking.json"
    path.write_text(json.dumps([{"number": 7, "title": "t", "mergeCommit": {"oid": merge}}]), encoding="utf-8")
    assert rp.breaking_changes_from_file(path, repo, base, merge) == [
        BreakingChange(number=7, title="t", paths=("src/PPDS.Cli/Program.cs",))
    ]
    assert rp.breaking_changes_from_file(path, repo, merge, merge) == []


class TestPullRequestTemplate:
    """The template asks for the line the check reads (#1789)."""

    TEMPLATE = REPO_ROOT / ".github" / "pull_request_template.md"

    def test_template_asks_for_a_breaking_yes_or_no_line(self):
        text = self.TEMPLATE.read_text(encoding="utf-8")
        section = text.split("## Breaking changes", 1)[1]
        assert '"Breaking: yes" or "Breaking: no", and why' in section
        assert "`breaking-change`" in section

    def test_unfilled_template_is_no_statement(self):
        assert cce.breaking_statement(self.TEMPLATE.read_text(encoding="utf-8")) is None

    @pytest.mark.parametrize("line, expected", [
        ("Breaking: no, adds an optional flag only.", "no"),
        ("Breaking: yes, exit code 2 becomes 12 for incomplete checks.", "yes"),
    ])
    def test_filled_template_is_read(self, line, expected):
        text = self.TEMPLATE.read_text(encoding="utf-8").rstrip() + "\n" + line + "\n"
        assert cce.breaking_statement(text) == expected
