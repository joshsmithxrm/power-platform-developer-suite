"""Tests for the changelog-entry and breaking-statement check (#1789).

Covers scripts/ci/check_changelog_entry.py, its workflow, and the release-plan
refusal of a non-major version for a surface a `breaking-change` PR changed.

Run with: python -m pytest tests/ci/test_changelog_entry_check.py -q
"""
from __future__ import annotations

import json
import os
import shutil
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


class TestEntryLines:
    @pytest.mark.parametrize("line", ["-", "- ", "*", "<!-- todo -->", "- <!-- todo -->", "### Fixed", "1."])
    def test_a_line_without_content_is_not_an_entry(self, line):
        after = add_under(BASE_CHANGELOG, "## [1.5.0] - Unreleased", line)
        result = run([CODE, changelog_change("PPDS.Cli", after)])
        assert not result.passed

    def test_heading_only_change_is_not_an_entry(self):
        after = BASE_CHANGELOG.replace("### Added", "### Added\n\n### Fixed")
        assert not run([CODE, changelog_change("PPDS.Cli", after)]).passed

    def test_no_version_section_names_the_missing_maintainer_step(self):
        released = "# Changelog\n\n## [Unreleased]\n\n## [1.4.2] - 2026-10-30\n\n- Old fix (#2).\n"
        after = add_under(released, "## [Unreleased]", "- Fixed the thing (#3).")
        result = run([CODE, changelog_change("PPDS.Cli", after, released)])
        assert not result.passed
        message = result.failures[0]
        assert "has no `## [X.Y.Z] - Unreleased` section yet" in message
        assert "A maintainer step is missing" in message and "Open the next version" in message
        assert "Move it" not in message

    def test_revert_that_only_removes_its_entry_says_so(self):
        before = add_under(BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- New flag (#3).")
        result = run([CODE, changelog_change("PPDS.Cli", BASE_CHANGELOG, before)])
        assert not result.passed
        assert "only removes entries" in result.failures[0]
        assert "reverts a change that was never released" in result.failures[0]

    def test_deleting_the_changelog_still_checks_the_package(self):
        deleted = FileChange(path="src/PPDS.Cli/CHANGELOG.md", before=BASE_CHANGELOG, after=None)
        result = run([CODE, deleted])
        assert not result.passed
        assert "deletes src/PPDS.Cli/CHANGELOG.md" in result.failures[0]

    def test_missing_entry_message_does_not_send_authors_to_a_label(self):
        message = run([CODE]).failures[0]
        assert "the maintainer can exempt the pull request" in message
        assert "label the pull request" not in message


class TestKnownTestLocations:
    @pytest.mark.parametrize("path", [
        "src/PPDS.Extension/src/__tests__/panels/QueryPanel.test.ts",
        "src/PPDS.Extension/src/notebooks/__tests__/serializer.test.ts",
        "src/PPDS.Extension/e2e/fixtures.ts",
        "src/PPDS.Extension/tools/bundle-cli.test.mjs",
        "src/PPDS.Extension/tsconfig.test.json",
        "src/PPDS.Extension/vitest.config.ts",
        "src/PPDS.Cli/docs/guide.mdx",
    ])
    def test_known_test_and_doc_locations_are_exempt(self, path):
        assert cce.non_behaviour_reason(path, path.split("/")[1]) is not None

    @pytest.mark.parametrize("path", [
        "src/PPDS.Cli/Commands/Test/RunCommand.cs",
        "src/PPDS.Cli/tests/Helper.cs",
        "src/PPDS.Dataverse/fixtures/Seed.cs",
        "src/PPDS.Cli/Services/RunTests.cs",
        "src/PPDS.Extension/src/panels/Panel.spec.helper.ts",
        "src/PPDS.Extension/tools/bundle-cli.mjs",
    ])
    def test_a_product_folder_or_file_named_like_a_test_is_checked(self, path):
        assert cce.non_behaviour_reason(path, path.split("/")[1]) is None

    def test_every_listed_test_location_matches_a_file_in_the_tree(self):
        tracked = subprocess.run(["git", "ls-files", "src"], cwd=REPO_ROOT, capture_output=True,
                                 text=True, check=True).stdout.splitlines()
        for pattern, _reason in cce.NON_BEHAVIOUR_PATHS[:-1]:
            assert any(pattern.search(path) for path in tracked), pattern.pattern


# ---------------------------------------------------------------------------
# Breaking statement
# ---------------------------------------------------------------------------

class TestBreakingStatement:
    @pytest.mark.parametrize("text", [
        "No", "No.", "no", "None", "None.", "N/A", "n/a.", "Not breaking", "Not breaking.",
        "No: internal refactor", "No; docs only.", "No, only adds a flag.", "None - tests only",
        "No — the output is unchanged", "No. Internal refactor.", "**None**", "- None",
    ])
    def test_whole_no_statement_is_not_breaking(self, text):
        assert cce.classify(text) == "no"
        assert cce.breaking_statement(f"## Breaking changes\n\n{text}\n") == "no"
        assert cce.breaking_statement(f"Breaking: {text}\n") == "no"

    @pytest.mark.parametrize("text", ["Yes", "yes, exit 2 becomes 12", "Yes: removes `--legacy`."])
    def test_yes_statement_is_breaking(self, text):
        assert cce.classify(text) == "yes"

    REVIEWER_WORDINGS = [
        "No longer accepts `--legacy`; scripts must drop it.",
        "No more exit code 2: `plugins diff` exits 12.",
        "None of the old flags work; use `--new`.",
        "- No default for `--environment` any more; pass it.",
    ]
    MISLEADING_BEFORE = ["This is not a breaking change.", "Nothing.", "Not applicable"]

    @pytest.mark.parametrize("text", REVIEWER_WORDINGS + MISLEADING_BEFORE)
    def test_a_no_prefix_or_other_text_is_never_read_as_no(self, text):
        assert cce.classify(text) == "unclear"
        assert cce.breaking_statement(f"## Breaking changes\n\n{text}\n") == "unclear"

    def test_breaking_line_with_no_prefix_is_unclear(self):
        assert cce.breaking_statement("Breaking: no longer returns exit 2, returns 12\n") == "unclear"

    @pytest.mark.parametrize("body", [
        *(f"## Breaking changes\n\n{text}\n" for text in REVIEWER_WORDINGS + MISLEADING_BEFORE),
        "Breaking: no longer returns exit 2, returns 12\n",
    ])
    def test_unclear_statement_without_label_fails_with_a_true_message(self, body):
        result = run([CODE, GOOD_ENTRY], body=body)
        assert not result.passed
        message = result.failures[0]
        assert "The breaking statement is unclear" in message
        assert '"Breaking: no"' in message and '"Breaking: yes"' in message
        assert "says it is not breaking" not in message

    @pytest.mark.parametrize("text", REVIEWER_WORDINGS)
    def test_free_text_with_the_label_is_a_statement_of_the_break(self, text):
        entry = changelog_change("PPDS.Cli", add_under(
            BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- **Breaking:** `--legacy` is gone (#3)."))
        result = run([CODE, entry], labels=["breaking-change"], body=f"## Breaking changes\n\n{text}\n")
        assert result.passed, result.failures
        assert "Breaking statement: yes." in result.notes

    @pytest.mark.parametrize("body, expected", [
        (NOT_BREAKING_BODY, "no"),
        (BREAKING_BODY, "unclear"),
        (TEMPLATE_ONLY_BODY, None),
        ("", None),
        (None, None),
        ("## Breaking changes\n\nNone.\n\n## Notes\n\nmore", "no"),
        ("Breaking: no, only adds a flag.\n", "no"),
        ("- **Breaking:** yes, exit code 2 becomes 12.\n", "yes"),
        ("Breaking: yes\n\n## Breaking changes\n\nNone\n", "yes"),
        ("## Breaking changes\r\n\r\nNone\r\n", "no"),
        ("## Breaking\n\nYes: removes `--legacy`.\n", "yes"),
        ("## Breaking changes\n\n## Testing\n\nran it\n", None),
        ("## Breaking changes\n\n<!-- prompt -->\n\n🤖 Generated with [Claude Code](https://claude.com/claude-code)\n", "unclear"),
    ])
    def test_statement_is_read_from_the_description(self, body, expected):
        assert cce.breaking_statement(body) == expected

    def test_missing_statement_fails_and_says_how(self):
        result = run([CODE, GOOD_ENTRY], body=TEMPLATE_ONLY_BODY)
        assert not result.passed
        assert "no breaking statement" in result.failures[0] and '"Breaking: no"' in result.failures[0]

    def test_yes_without_label_fails(self):
        result = run([CODE, GOOD_ENTRY], body="Breaking: yes, exit 2 becomes 12.\n")
        assert any("not labelled `breaking-change`" in f for f in result.failures)

    def test_label_with_no_statement_fails(self):
        result = run([CODE, GOOD_ENTRY], labels=["breaking-change"])
        assert "says it is not breaking" in result.failures[0]

    def test_breaking_change_entry_must_say_breaking(self):
        result = run([CODE, GOOD_ENTRY], labels=["breaking-change"], body=BREAKING_BODY)
        assert "no changelog entry says so" in result.failures[0]

    def test_non_breaking_in_the_entry_does_not_count(self):
        entry = changelog_change("PPDS.Cli", add_under(
            BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- A non-breaking tweak (#3)."))
        result = run([CODE, entry], labels=["breaking-change"], body=BREAKING_BODY)
        assert "no changelog entry says so" in result.failures[0]

    def test_labelled_breaking_change_with_breaking_entry_passes(self):
        entry = changelog_change("PPDS.Cli", add_under(
            BASE_CHANGELOG, "## [1.5.0] - Unreleased",
            "- **Breaking: exit code 12** - `plugins diff` exits 12 when incomplete (#3).",
        ))
        assert run([CODE, entry], labels=["breaking-change"], body=BREAKING_BODY).passed

    def test_breaking_label_outside_src_still_needs_a_statement(self):
        result = run([FileChange(path="docs/RELEASE.md")], labels=["breaking-change"], body="")
        assert "no breaking statement" in result.failures[0]


# ---------------------------------------------------------------------------
# Command line and event mode (temporary repository)
# ---------------------------------------------------------------------------

def _git(cwd: Path, *args: str) -> str:
    return subprocess.run(
        ["git", *args], cwd=cwd, check=True, capture_output=True, text=True,
    ).stdout.strip()


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    root = tmp_path / "repo"
    root.mkdir()
    _git(root, "init", "-q", "-b", "main")
    _git(root, "config", "user.email", "test@example.invalid")
    _git(root, "config", "user.name", "Test")
    for package in ("PPDS.Cli", "PPDS.Dataverse"):
        (root / "src" / package).mkdir(parents=True)
        (root / "src" / package / "CHANGELOG.md").write_text(BASE_CHANGELOG, encoding="utf-8")
        (root / "src" / package / "Program.cs").write_text("class P {}\n", encoding="utf-8")
    _git(root, "add", ".")
    _git(root, "commit", "-q", "-m", "base")
    _git(root, "checkout", "-q", "-b", "feature")
    return root


def _commit(repo: Path, message: str) -> None:
    _git(repo, "add", "-A")
    _git(repo, "commit", "-q", "-m", message)


def _event(tmp_path: Path, repo: Path, *, labels=(), body=NOT_BREAKING_BODY, author="contributor",
           base="main", head="HEAD") -> Path:
    event = {
        "pull_request": {
            "labels": [{"name": name} for name in labels],
            "body": body,
            "user": {"login": author},
            "base": {"sha": _git(repo, "rev-parse", base)},
            "head": {"sha": _git(repo, "rev-parse", head)},
        }
    }
    path = tmp_path / "event.json"
    path.write_text(json.dumps(event), encoding="utf-8")
    return path


def _main(tmp_path, repo, **event):
    return cce.main(["--event", str(_event(tmp_path, repo, **event)), "--repo-root", str(repo)])


def _edit_code(repo: Path, package: str = "PPDS.Cli") -> None:
    (repo / "src" / package / "Program.cs").write_text("class P { int x; }\n", encoding="utf-8")


def _add_entry(repo: Path, package: str = "PPDS.Cli") -> None:
    (repo / "src" / package / "CHANGELOG.md").write_text(
        add_under(BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- Added x (#3)."), encoding="utf-8")


class TestEventMode:
    def test_fails_without_entry_and_passes_with_it(self, repo, tmp_path, capsys):
        _edit_code(repo)
        _commit(repo, "code only")
        assert _main(tmp_path, repo) == 1
        assert "::error title=Changelog check::PPDS.Cli" in capsys.readouterr().out
        _add_entry(repo)
        _commit(repo, "entry")
        assert _main(tmp_path, repo) == 0
        assert "Changelog check passed." in capsys.readouterr().out

    def test_changes_on_main_after_the_branch_point_are_not_the_prs(self, repo, tmp_path):
        _edit_code(repo)
        _add_entry(repo)
        _commit(repo, "cli change with entry")
        _git(repo, "checkout", "-q", "main")
        _git(repo, "rm", "-q", "src/PPDS.Dataverse/Program.cs")
        _commit(repo, "someone else removes a Dataverse file")
        _git(repo, "checkout", "-q", "feature")
        # A diff from the base tip would show the Dataverse file as added here.
        assert _main(tmp_path, repo) == 0

    def test_labels_reach_the_check_through_the_event(self, repo, tmp_path):
        _edit_code(repo)
        _commit(repo, "code only")
        assert _main(tmp_path, repo) == 1
        assert _main(tmp_path, repo, labels=["internal"]) == 0
        assert _main(tmp_path, repo, labels=["internal", "breaking-change"]) == 1

    def test_breaking_label_reaches_the_check_through_the_event(self, repo, tmp_path):
        (repo / "src" / "PPDS.Cli" / "CHANGELOG.md").write_text(add_under(
            BASE_CHANGELOG, "## [1.5.0] - Unreleased", "- **Breaking:** exit 12 (#3)."), encoding="utf-8")
        _edit_code(repo)
        _commit(repo, "breaking")
        body = "Breaking: yes, exit 2 becomes 12.\n"
        assert _main(tmp_path, repo, body=body) == 1
        assert _main(tmp_path, repo, body=body, labels=["breaking-change"]) == 0

    def test_author_reaches_the_check_through_the_event(self, repo, tmp_path):
        _edit_code(repo)
        _commit(repo, "bump")
        assert _main(tmp_path, repo, author="dependabot[bot]", body="Bumps x.") == 0
        assert _main(tmp_path, repo, author="contributor", body="Bumps x.") == 1

    def test_a_file_moved_between_packages_counts_for_both(self, repo, tmp_path, capsys):
        _git(repo, "mv", "src/PPDS.Dataverse/Program.cs", "src/PPDS.Cli/Moved.cs")
        _add_entry(repo, "PPDS.Cli")
        _commit(repo, "move")
        assert _main(tmp_path, repo) == 1
        assert "PPDS.Dataverse: behaviour changed" in capsys.readouterr().out

    def test_deleting_a_changelog_does_not_hide_the_package(self, repo, tmp_path, capsys):
        _edit_code(repo, "PPDS.Dataverse")
        _git(repo, "rm", "-q", "src/PPDS.Dataverse/CHANGELOG.md")
        _commit(repo, "delete changelog")
        assert _main(tmp_path, repo) == 1
        assert "deletes src/PPDS.Dataverse/CHANGELOG.md" in capsys.readouterr().out

    def test_event_without_shas_exits_2(self, tmp_path):
        path = tmp_path / "event.json"
        path.write_text(json.dumps({"pull_request": {"base": {}, "head": {}}}), encoding="utf-8")
        assert cce.main(["--event", str(path)]) == 2

    def test_event_without_pull_request_exits_2(self, tmp_path):
        path = tmp_path / "event.json"
        path.write_text("{}", encoding="utf-8")
        assert cce.main(["--event", str(path)]) == 2

    def test_unknown_sha_exits_2(self, repo, tmp_path):
        event = json.loads(_event(tmp_path, repo).read_text())
        event["pull_request"]["head"]["sha"] = "f" * 40
        (tmp_path / "event.json").write_text(json.dumps(event), encoding="utf-8")
        assert cce.main(["--event", str(tmp_path / "event.json"), "--repo-root", str(repo)]) == 2


class TestInputJson:
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

    def test_packages_are_discovered_from_src_changelogs(self):
        packages = cce.discover_packages(REPO_ROOT)
        assert {"PPDS.Cli", "PPDS.Dataverse", "PPDS.Extension"} <= set(packages)
        assert "PPDS.Analyzers" not in packages


# ---------------------------------------------------------------------------
# Workflow contracts
# ---------------------------------------------------------------------------

WORKFLOW = REPO_ROOT / ".github" / "workflows" / "changelog-check.yml"
POST_MERGE = REPO_ROOT / ".github" / "workflows" / "post-merge-release-check.yml"


def _workflow(path: Path = WORKFLOW) -> dict:
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


class TestWorkflow:
    def test_runs_on_every_input_change(self):
        trigger = _workflow()["on"]["pull_request"]
        assert {"opened", "synchronize", "edited", "labeled", "unlabeled"} <= set(trigger["types"])
        assert trigger["branches"] == ["main"]
        assert "paths" not in trigger

    def test_runs_the_script_with_the_event_and_full_history(self):
        steps = _workflow()["jobs"]["changelog-entry"]["steps"]
        checkout = next(s for s in steps if s.get("uses", "").startswith("actions/checkout@"))
        assert checkout["with"]["fetch-depth"] == "0"
        assert checkout["with"]["persist-credentials"] == "false"
        runs = [s["run"] for s in steps if "check_changelog_entry.py" in s.get("run", "")]
        assert runs == ['python scripts/ci/check_changelog_entry.py --event "$GITHUB_EVENT_PATH"']

    def test_check_cannot_be_skipped_or_soft_failed(self):
        job = _workflow()["jobs"]["changelog-entry"]
        assert "if" not in job and "continue-on-error" not in job
        for step in job["steps"]:
            assert "if" not in step and "continue-on-error" not in step
            assert "||" not in step.get("run", "")

    def test_reads_only(self):
        assert _workflow()["permissions"] == {"contents": "read"}

    def test_is_not_part_of_the_pre_merge_gate_aggregate(self):
        gate = _workflow(REPO_ROOT / ".github" / "workflows" / "pre-merge-gate.yml")
        assert "changelog-entry" not in gate["jobs"]["pre-merge-gate"]["needs"]


def _post_merge_step(name: str) -> dict:
    steps = _workflow(POST_MERGE)["jobs"]["patch-release-detection"]["steps"]
    return next(s for s in steps if s.get("name") == name)


@pytest.mark.skipif(shutil.which("jq") is None, reason="jq not installed")
class TestPostMergeLabelStep:
    STEP = "Record the breaking-change label for the release plan"

    def _run(self, tmp_path, labels):
        event = {"pull_request": {"number": 42, "title": "t", "merge_commit_sha": "a" * 40,
                                  "labels": [{"name": label} for label in labels]}}
        (tmp_path / "event.json").write_text(json.dumps(event), encoding="utf-8")
        completed = subprocess.run(["bash", "-e", "-c", _post_merge_step(self.STEP)["run"]], cwd=tmp_path,
                                   env={**os.environ, "GITHUB_EVENT_PATH": str(tmp_path / "event.json")},
                                   capture_output=True, text=True)
        assert completed.returncode == 0, completed.stderr
        return json.loads((tmp_path / "breaking-prs.json").read_text())

    def test_labelled_pull_request_is_written(self, tmp_path):
        assert self._run(tmp_path, ["release:patch", "breaking-change"]) == [
            {"number": 42, "title": "t", "mergeCommit": {"oid": "a" * 40}}]

    def test_unlabelled_pull_request_writes_an_empty_list(self, tmp_path):
        assert self._run(tmp_path, ["release:patch"]) == []

    def test_both_plan_runs_read_the_file(self):
        build = _post_merge_step("Build explained release advisory")["run"]
        assert build.count("--breaking-prs-file breaking-prs.json") == 2


class TestRunbook:
    def test_runbook_writes_and_passes_the_breaking_file(self):
        text = (REPO_ROOT / "docs" / "runbooks" / "release.md").read_text(encoding="utf-8")
        assert "--label breaking-change" in text and "--json number,title,mergeCommit > breaking-prs.json" in text
        assert "--breaking-prs-file breaking-prs.json" in text

    def test_runbook_opens_the_next_version_section(self):
        text = (REPO_ROOT / "docs" / "runbooks" / "release.md").read_text(encoding="utf-8")
        assert "Open the next version" in text
        assert "`## [X.Y.Z] - Unreleased`" in text


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
            {"pr": 1605, "title": "exit 12 for incomplete checks", "surfaces": ["PPDS.Cli"]}]
        assert len(plan["refusals"]) == 1
        assert plan["refusals"][0].startswith(f"PPDS.Cli: #1605 is labelled breaking-change; a {kind} version is refused.")

    def test_major_plan_with_a_new_major_passes(self, graph):
        plan = build_release_plan(graph, RUNTIME, release_kind="major", breaking_changes=[BREAKING_CLI],
                                  tags=["Cli-v1.4.2"], planned_versions={"PPDS.Cli": "2.0.0"})
        assert plan["refusals"] == []

    @pytest.mark.parametrize("planned, tags", [
        ("1.5.0", ["Cli-v1.4.2"]),
        ("1.4.3", ["Cli-v1.4.2"]),
        ("2.1.0", ["Cli-v2.0.0", "Cli-v1.4.2"]),
    ])
    def test_major_plan_refuses_a_package_planned_as_minor_or_patch(self, graph, planned, tags):
        plan = build_release_plan(graph, RUNTIME, release_kind="major", breaking_changes=[BREAKING_CLI],
                                  tags=tags, planned_versions={"PPDS.Cli": planned})
        assert len(plan["refusals"]) == 1 and f"planned version {planned} is not a new major" in plan["refusals"][0]

    def test_major_plan_prerelease_tags_do_not_count_as_the_latest_major(self, graph):
        plan = build_release_plan(graph, RUNTIME, release_kind="major", breaking_changes=[BREAKING_CLI],
                                  tags=["Cli-v1.4.2", "Cli-v2.0.0-beta.1"], planned_versions={"PPDS.Cli": "2.0.0"})
        assert plan["refusals"] == []

    def test_major_plan_without_a_planned_version_refuses(self, graph):
        plan = build_release_plan(graph, RUNTIME, release_kind="major", breaking_changes=[BREAKING_CLI],
                                  tags=["Cli-v1.4.2"], planned_versions={})
        assert "has no planned version" in plan["refusals"][0]

    def test_first_release_of_a_package_is_not_refused(self, graph):
        plan = build_release_plan(graph, RUNTIME, release_kind="major", breaking_changes=[BREAKING_CLI],
                                  tags=[], planned_versions={"PPDS.Cli": "1.0.0"})
        assert plan["refusals"] == []

    def test_planned_versions_come_from_the_changelogs(self, graph):
        from release_model import planned_versions_from_changelogs
        planned = planned_versions_from_changelogs(REPO_ROOT, graph)
        assert planned["PPDS.Cli"] == cce.VERSION_UNRELEASED_RE.search(
            next(line for line in (REPO_ROOT / "src/PPDS.Cli/CHANGELOG.md").read_text().splitlines()
                 if cce.VERSION_UNRELEASED_RE.match(line))).group("version")

    def test_no_breaking_change_means_no_refusal(self, graph):
        plan = build_release_plan(graph, RUNTIME, release_kind="patch")
        assert plan["refusals"] == [] and plan["breaking_check"] == "checked"

    def test_surface_not_released_is_not_refused(self, graph):
        dataverse = BreakingChange(number=9, title="d", paths=("src/PPDS.Dataverse/Query/X.cs",))
        plan = build_release_plan(graph, RUNTIME, release_kind="patch", breaking_changes=[dataverse])
        assert "PPDS.Dataverse" not in plan["release_targets"]
        assert plan["refusals"] == []

    def test_breaking_change_with_no_surface_is_refused_in_a_patch(self, graph):
        docs_only = BreakingChange(number=9, title="docs", paths=("docs/RELEASE.md",))
        plan = build_release_plan(graph, RUNTIME, release_kind="patch", breaking_changes=[docs_only])
        assert "changes no release surface" in plan["refusals"][0]

    def test_unchecked_breaking_data_refuses_a_plan_that_releases(self, graph):
        plan = build_release_plan(graph, RUNTIME, release_kind="minor", breaking_unchecked="no file")
        assert "Breaking changes were not checked (no file)" in plan["refusals"][0]
        assert plan["breaking_check"] == "not checked: no file"
        nothing = build_release_plan(graph, [], release_kind="patch", breaking_unchecked="no file")
        assert nothing["release_targets"] == [] and nothing["refusals"] == []

    def test_markdown_shows_the_refusal_and_outside_range(self, graph):
        from release_model import render_markdown
        plan = build_release_plan(graph, RUNTIME, release_kind="patch", breaking_changes=[BREAKING_CLI],
                                  breaking_outside_range=[1200])
        text = render_markdown(plan)
        assert "### Breaking Changes in Range" in text and "### Refused" in text
        assert "### Breaking Pull Requests Outside the Range" in text and "#1200" in text


class TestReleasePlanCommandLine:
    def _files(self, tmp_path):
        files = tmp_path / "files.txt"
        files.write_text("src/PPDS.Cli/Commands/Plugins/DiffCommand.cs\n", encoding="utf-8")
        return ["--repo-root", str(REPO_ROOT), "--files-file", str(files), "--format", "title"]

    def _breaking(self, tmp_path, items):
        path = tmp_path / "breaking.json"
        path.write_text(json.dumps(items), encoding="utf-8")
        return str(path)

    def test_refusal_exits_1_and_a_new_major_exits_0(self, tmp_path, capsys, monkeypatch):
        breaking = self._breaking(tmp_path, [{
            "number": 1605, "title": "exit 12",
            "files": [{"path": "src/PPDS.Cli/Commands/Plugins/DiffCommand.cs"}]}])
        base = self._files(tmp_path)
        assert rp.main([*base, "--release-kind", "patch", "--breaking-prs-file", breaking]) == 1
        out = capsys.readouterr()
        assert out.out.startswith("Release plan refused")
        assert "::error title=Release plan refused::PPDS.Cli" in out.err
        monkeypatch.setattr(rp, "planned_versions_from_changelogs", lambda *_: {"PPDS.Cli": "99.0.0"})
        assert rp.main([*base, "--release-kind", "major", "--breaking-prs-file", breaking]) == 0

    def test_without_breaking_data_a_plan_says_so_and_exits_1(self, tmp_path, capsys):
        assert rp.main([*self._files(tmp_path), "--release-kind", "minor"]) == 1
        assert "Breaking changes were not checked" in capsys.readouterr().err

    def test_checked_elsewhere_is_explicit_and_recorded(self, tmp_path, capsys):
        args = [*self._files(tmp_path)[:-2], "--format", "json", "--release-kind", "minor"]
        assert rp.main([*args, "--breaking-checked-elsewhere", "reviewed by hand"]) == 0
        assert json.loads(capsys.readouterr().out)["breaking_check"] == "checked elsewhere: reviewed by hand"

    def test_file_and_checked_elsewhere_are_exclusive(self, tmp_path):
        with pytest.raises(SystemExit) as exit_info:
            rp.main([*self._files(tmp_path), "--breaking-prs-file", self._breaking(tmp_path, []),
                     "--breaking-checked-elsewhere", "x"])
        assert exit_info.value.code == 2

    @pytest.mark.parametrize("content", ["{}", "nope", '[{"title": "no number"}]'])
    def test_bad_breaking_file_exits_2(self, tmp_path, capsys, content):
        path = tmp_path / "breaking.json"
        path.write_text(content, encoding="utf-8")
        assert rp.main([*self._files(tmp_path), "--breaking-prs-file", str(path)]) == 2
        assert "could not read --breaking-prs-file" in capsys.readouterr().err

    def test_missing_breaking_file_exits_2(self, tmp_path):
        assert rp.main([*self._files(tmp_path), "--breaking-prs-file", str(tmp_path / "none.json")]) == 2


class TestBreakingFileInRange:
    @pytest.fixture
    def history(self, repo):
        base = _git(repo, "rev-parse", "HEAD")
        _edit_code(repo)
        _commit(repo, "breaking (#7)")
        merge = _git(repo, "rev-parse", "HEAD")
        return repo, base, merge

    def _file(self, tmp_path, items):
        path = tmp_path / "breaking.json"
        path.write_text(json.dumps(items), encoding="utf-8")
        return path

    def test_mapped_from_its_merge_commit(self, history, tmp_path):
        repo, base, merge = history
        path = self._file(tmp_path, [{"number": 7, "title": "t", "mergeCommit": {"oid": merge}}])
        assert rp.breaking_changes_from_file(path, repo, base, merge) == (
            [BreakingChange(number=7, title="t", paths=("src/PPDS.Cli/Program.cs",))], [])

    @pytest.mark.parametrize("form", ["short", "upper"])
    def test_short_and_upper_case_ids_are_resolved(self, history, tmp_path, form):
        repo, base, merge = history
        oid = merge[:12] if form == "short" else merge.upper()
        path = self._file(tmp_path, [{"number": 7, "title": "t", "mergeCommit": {"oid": oid}}])
        changes, outside = rp.breaking_changes_from_file(path, repo, base, merge)
        assert [c.number for c in changes] == [7] and outside == []

    def test_outside_the_range_is_reported_not_dropped(self, history, tmp_path):
        repo, base, merge = history
        path = self._file(tmp_path, [{"number": 7, "title": "t", "mergeCommit": {"oid": merge}}])
        assert rp.breaking_changes_from_file(path, repo, merge, merge) == ([], [7])

    @pytest.mark.parametrize("item, message", [
        ({"number": 7, "title": "t"}, "no merge commit"),
        ({"number": 7, "title": "t", "mergeCommit": None}, "no merge commit"),
        ({"number": 7, "title": "t", "mergeCommit": {"oid": "zz"}}, "is not a commit id"),
        ({"number": 7, "title": "t", "mergeCommit": {"oid": "b" * 40}}, "is not in this repository"),
    ])
    def test_unusable_merge_commit_is_an_error(self, history, tmp_path, item, message):
        repo, base, merge = history
        with pytest.raises(ValueError, match=message):
            rp.breaking_changes_from_file(self._file(tmp_path, [item]), repo, base, merge)

    def test_cherry_picked_fix_on_a_stabilization_branch_counts(self, history, tmp_path):
        """docs/runbooks/release.md: fixes reach release/X.Y with `git cherry-pick -x`."""
        repo, base, merge = history
        _git(repo, "checkout", "-q", "-b", "release/1.5", base)
        (repo / "src" / "PPDS.Dataverse" / "Other.cs").write_text("class O {}\n", encoding="utf-8")
        _commit(repo, "stabilization fix")
        _git(repo, "cherry-pick", "-x", merge)
        picked_head = _git(repo, "rev-parse", "HEAD")
        path = self._file(tmp_path, [{"number": 7, "title": "t", "mergeCommit": {"oid": merge}}])
        changes, outside = rp.breaking_changes_from_file(path, repo, base, picked_head)
        assert outside == [] and changes == [
            BreakingChange(number=7, title="t", paths=("src/PPDS.Cli/Program.cs",))]

    def test_a_pick_without_x_is_outside_the_range(self, history, tmp_path):
        repo, base, merge = history
        _git(repo, "checkout", "-q", "-b", "release/1.5", base)
        (repo / "src" / "PPDS.Dataverse" / "Other.cs").write_text("class O {}\n", encoding="utf-8")
        _commit(repo, "stabilization fix")
        _git(repo, "cherry-pick", merge)
        path = self._file(tmp_path, [{"number": 7, "title": "t", "mergeCommit": {"oid": merge}}])
        assert rp.breaking_changes_from_file(path, repo, base, _git(repo, "rev-parse", "HEAD")) == ([], [7])


class TestPullRequestTemplate:
    """The template asks for the line the check reads (#1789)."""

    TEMPLATE = REPO_ROOT / ".github" / "pull_request_template.md"

    def test_template_asks_for_a_breaking_yes_or_no_line(self):
        section = self.TEMPLATE.read_text(encoding="utf-8").split("## Breaking changes", 1)[1]
        assert '"Breaking: yes" or "Breaking: no", and why' in section
        assert "`breaking-change`" in section

    def test_unfilled_template_is_no_statement(self):
        assert cce.breaking_statement(self.TEMPLATE.read_text(encoding="utf-8")) is None

    @pytest.mark.parametrize("line, expected", [
        ("Breaking: no, adds an optional flag only.", "no"),
        ("Breaking: yes, exit code 2 becomes 12 for incomplete checks.", "yes"),
        ("Breaking: no longer returns exit 2", "unclear"),
    ])
    def test_filled_template_is_read(self, line, expected):
        text = self.TEMPLATE.read_text(encoding="utf-8").rstrip() + "\n" + line + "\n"
        assert cce.breaking_statement(text) == expected
