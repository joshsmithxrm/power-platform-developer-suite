"""Tests for the release risk rule that replaced the 8-week cadence timer (#1791).

Covers scripts/ci/check_unreleased_fixes.py and .github/workflows/release-risk-check.yml.
The workflow's shell steps are run against a stub `gh` so that what they
pass on, and how they fail, is tested and not only their text.

Run with: python -m pytest tests/ci/test_unreleased_fix_check.py -q
"""
from __future__ import annotations

import json
import os
import shutil
import stat
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_unreleased_fixes as cuf  # noqa: E402
from release_model import ReleaseGraph  # noqa: E402

REPOSITORY = "joshsmithxrm/power-platform-developer-suite"
TARGETS_TABLE = f"""# Security Policy

{cuf.TARGETS_BEGIN}
| Fix | Released within (days after merge) |
|-----|------------------------------------|
| data-safety | 7 |
| `security` | 14 |
{cuf.TARGETS_END}
"""
NOW = datetime(2026, 11, 30, 9, 0, tzinfo=timezone.utc)
ALL_LABELS = [{"name": name} for name in (*cuf.REQUIRED_LABELS, "type:bug")]


@pytest.fixture(scope="module")
def graph() -> ReleaseGraph:
    return ReleaseGraph.discover(REPO_ROOT)


def fix(number=1700, *, labels=("data-safety",), merged="2026-11-01T12:00:00Z", title="stop writing the wrong rows",
        via=()):
    return cuf.Fix(
        number=number, title=title, merged_at=cuf.parse_utc(merged),
        merge_oid="a" * 40, labels=frozenset(labels), via_issues=tuple(via),
    )


def decide(fixes, *, unreleased=("PPDS.Cli",), ships=True, recorded=frozenset(), targets=None, now=NOW):
    return cuf.evaluate(
        fixes, targets=targets or {"data-safety": 7, "security": 14}, now=now,
        recorded=set(recorded), release_status=lambda _fix: (tuple(unreleased), ships),
    )


# ---------------------------------------------------------------------------
# The real case: #1783 fixed data-safety issue #1731 and is not released
# ---------------------------------------------------------------------------

# Recorded 2026-11-21 with `gh pr view 1783 --json ...`, `gh issue view 1731
# --json number,labels`, `git diff --name-only 266a2a81^ 266a2a81` and
# `git tag --contains 266a2a81` (no tag contains it; newest Extension tag is
# Extension-v1.6.2, which predates it).
PR_1783 = {
    "number": 1783,
    "title": "fix(extension): discard stale panel responses after an environment switch",
    "mergedAt": "2026-11-19T18:21:04Z",
    "mergeCommit": {"oid": "266a2a817d3168634f45c7456006d4838d02343c"},
    "labels": [],
    "closingIssuesReferences": [{
        "number": 1731,
        "repository": {"name": "power-platform-developer-suite", "owner": {"login": "joshsmithxrm"}},
        "url": "https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1731",
    }],
}
# `gh issue view 1731 --json number,state,stateReason`: CLOSED, COMPLETED.
ISSUE_1731 = {"number": 1731, "title": "Stale panel responses after an environment switch",
              "state": "CLOSED", "stateReason": "COMPLETED", "labels": [
    {"name": "type:bug"}, {"name": "priority:low"}, {"name": "area:extension"}, {"name": "data-safety"},
]}
PATHS_1783 = [
    "src/PPDS.Extension/CHANGELOG.md",
    "src/PPDS.Extension/DEVELOPMENT.md",
    "src/PPDS.Extension/src/__tests__/panels/staleResponseGuard.test.ts",
    "src/PPDS.Extension/src/__tests__/panels/staleResponseGuardWrites.test.ts",
    "src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts",
    "src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts",
    "src/PPDS.Extension/src/panels/ImportJobsPanel.ts",
    "src/PPDS.Extension/src/panels/MetadataBrowserPanel.ts",
    "src/PPDS.Extension/src/panels/PluginTracesPanel.ts",
    "src/PPDS.Extension/src/panels/PluginsPanel.ts",
    "src/PPDS.Extension/src/panels/SolutionsPanel.ts",
    "src/PPDS.Extension/src/panels/WebResourcesPanel.ts",
    "src/PPDS.Extension/src/panels/WebviewPanelBase.ts",
]
TAGS_CONTAINING_1783: list[str] = []


class TestRealCase1783:
    def test_unlabelled_pull_request_closing_a_data_safety_issue_is_a_fix(self):
        fixes = cuf.read_fixes([PR_1783], cuf.issue_fix_labels([ISSUE_1731]), REPOSITORY)
        assert [(f.number, f.labels, f.via_issues) for f in fixes] == [(1783, frozenset({"data-safety"}), (1731,))]

    def test_1783_is_reported_unreleased_for_the_extension(self, graph):
        fixes = cuf.read_fixes([PR_1783], cuf.issue_fix_labels([ISSUE_1731]), REPOSITORY)
        decisions = cuf.evaluate(
            fixes, targets={"data-safety": 0, "security": 0},
            now=datetime(2026, 11, 21, 12, 0, tzinfo=timezone.utc), recorded=set(),
            release_status=lambda _fix: cuf.unreleased_in(graph, PATHS_1783, TAGS_CONTAINING_1783),
        )
        assert len(decisions) == 1 and decisions[0].open_issue
        assert decisions[0].unreleased_surfaces == ("PPDS.Extension",)
        body = cuf.issue_body(decisions[0])
        assert "#1783 (`data-safety`) (closes #1731)" in body
        assert "`PPDS.Extension`" in body

    def test_a_stable_extension_tag_containing_it_would_release_it(self, graph):
        assert cuf.unreleased_in(graph, PATHS_1783, ["Extension-v1.8.0"]) == ((), True)
        assert cuf.unreleased_in(graph, PATHS_1783, ["Extension-v1.7.0"]) == (("PPDS.Extension",), True)


class TestReadFixes:
    def _pr(self, number, *, labels=(), closes=(), repo=REPOSITORY):
        owner, name = repo.split("/")
        return {
            "number": number, "title": f"t{number}", "mergedAt": "2026-11-01T00:00:00Z",
            "mergeCommit": {"oid": f"{number:040d}"},
            "labels": [{"name": label} for label in labels],
            "closingIssuesReferences": [
                {"number": issue, "repository": {"name": name, "owner": {"login": owner}}} for issue in closes
            ],
        }

    def test_label_on_the_pull_request_or_on_a_closed_issue(self):
        issues = cuf.issue_fix_labels([
            {"number": 10, "labels": [{"name": "data-safety"}]},
            {"number": 11, "labels": [{"name": "security"}]},
            {"number": 12, "labels": [{"name": "type:bug"}]},
        ])
        prs = [
            self._pr(1, labels=("security",)),
            self._pr(2, closes=(10,)),
            self._pr(3, closes=(11, 10)),
            self._pr(4, closes=(12,)),
            self._pr(5),
            self._pr(6, labels=("type:bug",), closes=(99,)),
        ]
        fixes = {f.number: f for f in cuf.read_fixes(prs, issues, REPOSITORY)}
        assert set(fixes) == {1, 2, 3}
        assert fixes[1].labels == {"security"} and fixes[1].via_issues == ()
        assert fixes[2].labels == {"data-safety"} and fixes[2].via_issues == (10,)
        assert fixes[3].labels == {"data-safety", "security"} and fixes[3].via_issues == (10, 11)

    def test_an_issue_in_another_repository_does_not_count(self):
        issues = cuf.issue_fix_labels([{"number": 10, "labels": [{"name": "data-safety"}]}])
        prs = [self._pr(1, closes=(10,), repo="someone/else")]
        assert cuf.read_fixes(prs, issues, REPOSITORY) == []

    def test_elapsed_time_alone_is_not_a_fix(self):
        old = self._pr(5, labels=("type:enhancement",))
        old["mergedAt"] = "2025-01-01T00:00:00Z"
        assert cuf.read_fixes([old], {}, REPOSITORY) == []

    def test_duplicates_merge_and_merge_data_is_required(self):
        item = self._pr(7, labels=("data-safety", "security"))
        item["mergedAt"] = "2026-11-01T00:00:00+02:00"
        fixes = cuf.read_fixes([item, item], {}, REPOSITORY)
        assert len(fixes) == 1
        assert fixes[0].merged_at == datetime(2026, 10, 31, 22, 0, tzinfo=timezone.utc)
        with pytest.raises(cuf.CheckError, match="#8"):
            cuf.read_fixes([{"number": 8, "labels": [{"name": "data-safety"}]}], {}, REPOSITORY)


# ---------------------------------------------------------------------------
# Missing labels and truncated lists are errors, not empty results
# ---------------------------------------------------------------------------

class TestFailsLoudly:
    @pytest.mark.parametrize("missing", list(cuf.REQUIRED_LABELS))
    def test_a_missing_label_is_named(self, missing):
        labels = [label for label in ALL_LABELS if label["name"] != missing]
        with pytest.raises(cuf.CheckError, match=f"`{missing}`"):
            cuf.check_labels_exist(labels)

    def test_all_labels_present_passes(self):
        cuf.check_labels_exist(ALL_LABELS)

    def test_repository_today_lacks_security_label(self):
        """Recorded `gh label list` 2026-11-21: data-safety exists, security does not."""
        today = [{"name": n} for n in ("data-safety", "breaking-change", "release:patch", "release:cadence-check")]
        with pytest.raises(cuf.CheckError) as error:
            cuf.check_labels_exist(today)
        assert "`security`" in str(error.value) and "`release:unreleased-fix`" in str(error.value)
        assert "`data-safety`" not in str(error.value)

    def test_a_list_at_the_limit_may_be_truncated(self):
        cuf.check_complete("x", [1, 2], 3)
        with pytest.raises(cuf.CheckError, match="may be truncated"):
            cuf.check_complete("x", [1, 2, 3], 3)


# ---------------------------------------------------------------------------
# Targets come from the security policy
# ---------------------------------------------------------------------------

class TestTargets:
    def test_targets_are_read_from_the_marked_table(self):
        assert cuf.read_targets(TARGETS_TABLE) == {"data-safety": 7, "security": 14}

    def test_security_policy_on_main_states_no_targets_yet(self):
        """The policy on main has no targets; the check must not invent them."""
        policy = (REPO_ROOT / "SECURITY.md").read_text(encoding="utf-8")
        if cuf.TARGETS_BEGIN in policy:
            pytest.skip("SECURITY.md now states release targets")
        with pytest.raises(cuf.TargetsMissing, match="states no release targets"):
            cuf.read_targets(policy)

    def test_missing_row_is_an_error(self):
        policy = TARGETS_TABLE.replace("| `security` | 14 |\n", "")
        with pytest.raises(cuf.TargetsMissing, match="no row for: security"):
            cuf.read_targets(policy)

    @pytest.mark.parametrize("value", ["seven", "7.5", "", "-1", "7 days"])
    def test_non_integer_target_is_an_error(self, value):
        policy = TARGETS_TABLE.replace("| data-safety | 7 |", f"| data-safety | {value} |")
        with pytest.raises(cuf.TargetsMissing):
            cuf.read_targets(policy)


# ---------------------------------------------------------------------------
# Decision
# ---------------------------------------------------------------------------

class TestDecision:
    def test_unreleased_fix_past_target_opens_one_issue_naming_it(self):
        decisions = decide([fix()])
        assert [d.open_issue for d in decisions] == [True]
        decision = decisions[0]
        assert decision.reason == "unreleased past target"
        assert decision.age_days == 28 and decision.target_days == 7
        title, body = cuf.issue_title(decision), cuf.issue_body(decision)
        assert title == "Unreleased data-safety fix #1700: stop writing the wrong rows"
        assert "#1700" in body and "`PPDS.Cli`" in body
        assert "28 days ago" in body and "7 days after merge" in body
        assert body.count("<!-- ppds-unreleased-fix") == 1 and cuf.record_marker(1700) in body

    def test_fix_within_target_opens_nothing(self):
        decisions = decide([fix(merged="2026-11-25T00:00:00Z")])
        assert decisions[0].open_issue is False and decisions[0].reason == "within target"

    def test_target_day_itself_is_within_target(self):
        decisions = decide([fix(merged="2026-11-23T09:00:00Z")])
        assert decisions[0].age_days == 7 and decisions[0].open_issue is False

    def test_released_and_no_surface_are_reported_apart(self):
        assert decide([fix()], unreleased=())[0].reason == "released"
        assert decide([fix()], unreleased=(), ships=False)[0].reason == "changes no release surface"
        assert not decide([fix()], unreleased=())[0].open_issue

    def test_fix_with_an_issue_gets_no_second_one(self):
        decisions = decide([fix()], recorded={1700})
        assert decisions[0].open_issue is False and decisions[0].reason == "already recorded"
        issues = [{"number": 9, "state": "CLOSED", "body": "text\n" + cuf.record_marker(1700)}]
        assert cuf.recorded_prs(issues) == {1700}

    def test_security_fix_uses_its_own_target(self):
        assert decide([fix(labels=("security",), merged="2026-11-20T09:00:00Z")])[0].open_issue is False
        assert decide([fix(labels=("security",), merged="2026-11-10T09:00:00Z")])[0].open_issue is True

    def test_fix_with_both_labels_uses_the_shorter_target(self):
        decisions = decide([fix(labels=("security", "data-safety"), merged="2026-11-20T09:00:00Z")])
        assert decisions[0].target_days == 7 and decisions[0].open_issue is True
        assert cuf.issue_title(decisions[0]).startswith("Unreleased data-safety and security fix #1700")

    def test_one_issue_per_fix(self):
        decisions = decide([fix(1), fix(2), fix(3, merged="2026-11-29T00:00:00Z")])
        assert [d.fix.number for d in decisions if d.open_issue] == [1, 2]

    def test_long_title_is_cut_to_the_github_limit(self):
        decision = decide([fix(title="x" * 300)])[0]
        title = cuf.issue_title(decision)
        assert len(title) == cuf.MAX_ISSUE_TITLE == 256
        assert title.startswith("Unreleased data-safety fix #1700: xxx") and title.endswith("…")

    def test_title_cannot_plant_a_marker_or_break_the_table(self):
        planted = "fix <!-- ppds-unreleased-fix pr=43 --> | done"
        body = cuf.issue_body(decide([fix(title=planted)])[0])
        assert cuf.recorded_prs([{"body": body}]) == {1700}
        assert "&lt;!-- ppds-unreleased-fix pr=43 --&gt; \\| done" in body


# ---------------------------------------------------------------------------
# What counts as released: every deliverable that ships the change
# ---------------------------------------------------------------------------

class TestReleaseStatus:
    @pytest.mark.parametrize("surface, tags, released", [
        ("PPDS.Cli", ["Cli-v1.4.2"], "Cli-v1.4.2"),
        ("PPDS.Cli", ["Cli-v1.5.0-beta.1"], None),
        ("PPDS.Cli", ["Cli-v01.4.2", "Cli-vnot-a-version"], None),
        # Same prefix length and a valid version: only the prefix tells them apart.
        ("PPDS.Cli", ["Mcp-v1.2.2"], None),
        ("PPDS.Extension", ["Extension-v1.7.0"], None),
        ("PPDS.Extension", ["Extension-v1.8.0"], "Extension-v1.8.0"),
    ])
    def test_only_a_stable_tag_of_the_surface_counts(self, surface, tags, released):
        prefix = {"PPDS.Cli": "Cli-v", "PPDS.Extension": "Extension-v"}[surface]
        assert cuf.stable_release_containing(surface, prefix, tags) == released

    def test_library_fix_is_unreleased_until_its_consumers_ship(self, graph):
        paths = ["src/PPDS.Dataverse/Query/QueryExecutor.cs"]
        assert cuf.deliverables_for(graph, paths) == \
            ["PPDS.Cli", "PPDS.Dataverse", "PPDS.Extension", "PPDS.Mcp", "PPDS.Migration", "PPDS.Query"]
        unreleased, ships = cuf.unreleased_in(graph, paths, ["Dataverse-v1.4.1"])
        assert ships and unreleased == ("PPDS.Cli", "PPDS.Extension", "PPDS.Mcp", "PPDS.Migration", "PPDS.Query")
        everything = ["Cli-v1.5.0", "Dataverse-v1.4.1", "Extension-v1.8.0", "Mcp-v1.5.0", "Migration-v1.5.0", "Query-v1.1.0"]
        assert cuf.unreleased_in(graph, paths, everything) == ((), True)

    def test_every_deliverable_is_checked_not_only_the_first(self, graph):
        paths = ["src/PPDS.Cli/Program.cs"]
        assert cuf.unreleased_in(graph, paths, ["Cli-v1.5.0", "Extension-v1.8.0"]) == (("PPDS.Mcp",), True)

    def test_docs_only_change_ships_nothing(self, graph):
        assert cuf.unreleased_in(graph, ["docs/RELEASE.md"], []) == ((), False)

    def test_git_failure_raises(self, tmp_path):
        with pytest.raises(RuntimeError, match="git"):
            cuf._git(tmp_path, "rev-parse", "HEAD")


# ---------------------------------------------------------------------------
# End to end against a temporary repository with a library and a tool
# ---------------------------------------------------------------------------

def _git(cwd: Path, *args: str) -> str:
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


def _project(root: Path, name: str, prefix: str, references=()) -> None:
    project = root / "src" / name / f"{name}.csproj"
    project.parent.mkdir(parents=True)
    refs = "".join(
        f'    <ProjectReference Include="..\\{ref}\\{ref}.csproj" />\n' for ref in references
    )
    project.write_text(
        "<Project>\n  <PropertyGroup>\n"
        f"    <PackageId>{name}</PackageId>\n    <MinVerTagPrefix>{prefix}</MinVerTagPrefix>\n"
        "  </PropertyGroup>\n"
        + (f"  <ItemGroup>\n{refs}  </ItemGroup>\n" if refs else "")
        + "</Project>\n",
        encoding="utf-8",
    )
    (project.parent / "Code.cs").write_text("class C {}\n", encoding="utf-8")


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    root = tmp_path / "repo"
    _project(root, "PPDS.Lib", "Lib-v")
    _project(root, "PPDS.Tool", "Tool-v", references=("PPDS.Lib",))
    (root / "SECURITY.md").write_text(TARGETS_TABLE, encoding="utf-8")
    _git(root, "init", "-q", "-b", "main")
    _git(root, "config", "user.email", "test@example.invalid")
    _git(root, "config", "user.name", "Test")
    _git(root, "add", ".")
    _git(root, "commit", "-q", "-m", "base")
    _git(root, "tag", "Lib-v1.0.0")
    _git(root, "tag", "Tool-v1.0.0")
    (root / "src" / "PPDS.Lib" / "Code.cs").write_text("class C { /* guarded */ }\n", encoding="utf-8")
    _git(root, "commit", "-q", "-am", "fix wrong-target write (#42)")
    return root


def _inputs(tmp_path: Path, repo: Path, *, issues=(), labels=ALL_LABELS, limit=3000, pr_labels=(), closes=(42000,)) -> list[str]:
    files = {
        "merged.json": [{
            "number": 42, "title": "guard the write", "mergedAt": "2026-11-01T00:00:00Z",
            "mergeCommit": {"oid": _git(repo, "rev-parse", "HEAD")},
            "labels": [{"name": label} for label in pr_labels],
            "closingIssuesReferences": [{"number": n} for n in closes],
        }],
        "fix-issues.json": [
            {"number": 42000, "title": "wrong-target write", "labels": [{"name": "data-safety"}],
             "state": "CLOSED", "stateReason": "COMPLETED"},
            {"number": 43000, "title": "token leak", "labels": [{"name": "security"}],
             "state": "OPEN", "stateReason": ""},
        ],
        "labels.json": list(labels),
        "issues.json": list(issues),
    }
    for name, data in files.items():
        (tmp_path / name).write_text(json.dumps(data), encoding="utf-8")
    return [
        "--merged-prs", str(tmp_path / "merged.json"), "--fix-issues", str(tmp_path / "fix-issues.json"),
        "--repo-labels", str(tmp_path / "labels.json"), "--existing-issues", str(tmp_path / "issues.json"),
        "--security-policy", str(repo / "SECURITY.md"), "--repository", REPOSITORY,
        "--list-limit", str(limit), "--repo-root", str(repo),
        "--current-date", "2026-11-30T00:00:00Z", "--issue-dir", str(tmp_path / "out"),
    ]


class TestCommandLine:
    def _run(self, capsys, args):
        code = cuf.main(args)
        captured = capsys.readouterr()
        return code, (json.loads(captured.out) if code == 0 else None), captured.err

    def test_unreleased_library_fix_opens_one_issue_for_both_deliverables(self, repo, tmp_path, capsys):
        code, result, _ = self._run(capsys, _inputs(tmp_path, repo))
        assert code == 0 and result["issues_to_open"] == [42]
        assert result["decisions"][0]["unreleased_surfaces"] == ["PPDS.Lib", "PPDS.Tool"]
        assert result["decisions"][0]["via_issues"] == [42000]
        assert (tmp_path / "out" / "42.title").read_text() == "Unreleased data-safety fix #42: guard the write"

    def test_library_tagged_but_tool_not_is_still_unreleased(self, repo, tmp_path, capsys):
        _git(repo, "tag", "Lib-v1.0.1")
        code, result, _ = self._run(capsys, _inputs(tmp_path, repo))
        assert code == 0 and result["issues_to_open"] == [42]
        assert result["decisions"][0]["unreleased_surfaces"] == ["PPDS.Tool"]

    def test_prerelease_tags_do_not_release_the_fix(self, repo, tmp_path, capsys):
        _git(repo, "tag", "Lib-v1.0.1-beta.1")
        _git(repo, "tag", "Tool-v1.0.1-beta.1")
        assert self._run(capsys, _inputs(tmp_path, repo))[1]["issues_to_open"] == [42]

    def test_every_deliverable_tagged_releases_the_fix(self, repo, tmp_path, capsys):
        _git(repo, "tag", "Lib-v1.0.1")
        _git(repo, "tag", "Tool-v1.0.1")
        code, result, _ = self._run(capsys, _inputs(tmp_path, repo))
        assert code == 0 and result["issues_to_open"] == []
        assert result["decisions"][0]["reason"] == "released"
        assert not (tmp_path / "out").exists() or not list((tmp_path / "out").iterdir())

    def test_label_on_the_pull_request_alone_also_counts(self, repo, tmp_path, capsys):
        args = _inputs(tmp_path, repo, pr_labels=("security",), closes=())
        result = self._run(capsys, args)[1]
        assert result["issues_to_open"] == [42] and result["decisions"][0]["labels"] == ["security"]

    def test_existing_issue_suppresses_a_second(self, repo, tmp_path, capsys):
        issues = [{"number": 3, "state": "CLOSED", "body": cuf.record_marker(42)}]
        assert self._run(capsys, _inputs(tmp_path, repo, issues=issues))[1]["issues_to_open"] == []

    def test_missing_targets_exit_2_and_open_nothing(self, repo, tmp_path, capsys):
        (repo / "SECURITY.md").write_text("# Security Policy\n\nNo targets.\n", encoding="utf-8")
        code, _, err = self._run(capsys, _inputs(tmp_path, repo))
        assert code == 2 and "states no release targets" in err
        assert not (tmp_path / "out").exists()

    def test_missing_label_exit_2_and_open_nothing(self, repo, tmp_path, capsys):
        labels = [label for label in ALL_LABELS if label["name"] != "security"]
        code, _, err = self._run(capsys, _inputs(tmp_path, repo, labels=labels))
        assert code == 2 and "no label `security`" in err
        assert not (tmp_path / "out").exists()

    def test_missing_targets_and_label_are_both_named(self, repo, tmp_path, capsys):
        (repo / "SECURITY.md").write_text("# Security Policy\n", encoding="utf-8")
        labels = [label for label in ALL_LABELS if label["name"] != "data-safety"]
        code, _, err = self._run(capsys, _inputs(tmp_path, repo, labels=labels))
        assert code == 2 and "states no release targets" in err and "`data-safety`" in err

    def test_a_list_at_the_limit_exits_2(self, repo, tmp_path, capsys):
        code, _, err = self._run(capsys, _inputs(tmp_path, repo, limit=1))
        assert code == 2 and "may be truncated" in err

    def test_unknown_merge_commit_exits_2(self, repo, tmp_path, capsys):
        args = _inputs(tmp_path, repo)
        merged = json.loads((tmp_path / "merged.json").read_text())
        merged[0]["mergeCommit"]["oid"] = "f" * 40
        (tmp_path / "merged.json").write_text(json.dumps(merged), encoding="utf-8")
        assert self._run(capsys, args)[0] == 2

    def test_unreadable_input_exits_2(self, repo, tmp_path, capsys):
        args = _inputs(tmp_path, repo)
        (tmp_path / "merged.json").write_text("nope", encoding="utf-8")
        assert self._run(capsys, args)[0] == 2


# ---------------------------------------------------------------------------
# Workflow: contract and behaviour of its shell steps against a stub gh
# ---------------------------------------------------------------------------

WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release-risk-check.yml"


def _workflow() -> dict:
    return yaml.load(WORKFLOW.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def _job() -> dict:
    return _workflow()["jobs"]["risk-check"]


def _step(name: str) -> dict:
    return next(step for step in _job()["steps"] if step.get("name") == name)


STUB_GH = r'''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ["STUB_LOG"], "a", encoding="utf-8") as log:
    log.write(json.dumps(args) + "\n")
fail = os.environ.get("STUB_FAIL", "")
key = " ".join(args[:2])
if fail and fail == key:
    print("HTTP 502", file=sys.stderr)
    sys.exit(1)
fixtures = json.loads(os.environ.get("STUB_OUTPUT", "{}"))
label = args[args.index("--label") + 1] if "--label" in args else ""
print(fixtures.get(f"{key} {label}".strip(), fixtures.get(key, "[]")))
'''


@pytest.fixture
def stub(tmp_path: Path):
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    gh = bin_dir / "gh"
    gh.write_text(STUB_GH, encoding="utf-8")
    gh.chmod(gh.stat().st_mode | stat.S_IEXEC)
    work = tmp_path / "work"
    work.mkdir()
    (work / "scripts").symlink_to(REPO_ROOT / "scripts")
    log = tmp_path / "gh.log"

    def run(step_name: str, *, outputs=None, fail="", extra_env=None):
        env = dict(os.environ)
        env.update(_job()["env"])
        env.update({
            "PATH": f"{bin_dir}{os.pathsep}{env['PATH']}", "STUB_LOG": str(log),
            "STUB_FAIL": fail, "STUB_OUTPUT": json.dumps(outputs or {}),
            "GITHUB_REPOSITORY": REPOSITORY, "GH_TOKEN": "x",
        })
        env.update(extra_env or {})
        # GitHub's default `run` shell on Linux is `bash -e {0}`.
        completed = subprocess.run(
            ["bash", "-e", "-c", _step(step_name)["run"]], cwd=work, env=env,
            capture_output=True, text=True,
        )
        calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
        return completed, calls

    run.work = work
    return run


pytestmark_jq = pytest.mark.skipif(shutil.which("jq") is None, reason="jq not installed")


@pytestmark_jq
class TestWorkflowSteps:
    LIST = "List merged pull requests and fix issues"

    def test_lists_merged_main_prs_with_closing_references_up_to_the_limit(self, stub):
        completed, calls = stub(self.LIST)
        assert completed.returncode == 0, completed.stderr
        pr_call = next(c for c in calls if c[:2] == ["pr", "list"])
        assert pr_call[pr_call.index("--state") + 1] == "merged"
        assert pr_call[pr_call.index("--base") + 1] == "main"
        assert pr_call[pr_call.index("--limit") + 1] == _job()["env"]["LIST_LIMIT"] == "3000"
        assert "closingIssuesReferences" in pr_call[pr_call.index("--json") + 1].split(",")
        issue_labels = sorted(c[c.index("--label") + 1] for c in calls if c[:2] == ["issue", "list"])
        assert issue_labels == ["data-safety", "security"]
        for call in calls:
            assert call[call.index("--limit") + 1] == "3000"

    def test_both_fix_issue_lists_are_merged(self, stub):
        outputs = {
            "issue list data-safety": json.dumps([{"number": 1, "labels": [{"name": "data-safety"}]}]),
            "issue list security": json.dumps([{"number": 2, "labels": [{"name": "security"}]},
                                               {"number": 1, "labels": [{"name": "data-safety"}]}]),
        }
        completed, _ = stub(self.LIST, outputs=outputs)
        assert completed.returncode == 0, completed.stderr
        merged = json.loads((stub.work / "fix-issues.json").read_text())
        assert sorted(issue["number"] for issue in merged) == [1, 2]

    @pytest.mark.parametrize("failing", ["pr list", "issue list"])
    def test_a_failing_query_fails_the_step(self, stub, failing):
        completed, _ = stub(self.LIST, fail=failing)
        assert completed.returncode != 0

    def test_failing_label_or_existing_issue_query_fails(self, stub):
        assert stub("List repository labels", fail="label list")[0].returncode != 0
        assert stub("List existing unreleased-fix issues", fail="issue list")[0].returncode != 0

    def test_existing_issue_list_reads_all_states_of_the_issue_label(self, stub):
        _, calls = stub("List existing unreleased-fix issues")
        call = calls[-1]
        assert call[call.index("--label") + 1] == cuf.ISSUE_LABEL
        assert call[call.index("--state") + 1] == "all"

    def test_evaluate_step_fails_when_the_script_refuses(self, stub):
        for name, data in {"merged-prs.json": [{"number": 1}], "fix-issues.json": [], "existing-issues.json": [],
                           "repo-labels.json": ALL_LABELS}.items():
            (stub.work / name).write_text(json.dumps(data), encoding="utf-8")
        (stub.work / "SECURITY.md").write_text("# Security Policy\n", encoding="utf-8")
        completed, _ = stub("Evaluate unreleased fixes against the security policy targets")
        assert completed.returncode == 2
        assert "states no release targets" in completed.stderr

    def test_evaluate_step_passes_the_limit_and_repository(self, stub):
        run = _step("Evaluate unreleased fixes against the security policy targets")["run"]
        assert '--list-limit "$LIST_LIMIT"' in run and '--repository "$GITHUB_REPOSITORY"' in run

    def test_each_issue_is_created_labelled_with_its_title_verbatim(self, stub):
        out = stub.work / "issues-to-open"
        out.mkdir()
        hostile = 'fix $(touch pwned) `touch pwned2` "quoted"'
        (out / "7.title").write_text(hostile, encoding="utf-8")
        (out / "7.md").write_text("body 7", encoding="utf-8")
        (out / "8.title").write_text("second", encoding="utf-8")
        (out / "8.md").write_text("body 8", encoding="utf-8")
        completed, calls = stub("Open one issue per overdue fix")
        assert completed.returncode == 0, completed.stderr
        creates = [c for c in calls if c[:2] == ["issue", "create"]]
        assert len(creates) == 2
        for call in creates:
            assert call[call.index("--label") + 1] == cuf.ISSUE_LABEL
        assert creates[0][creates[0].index("--title") + 1] == hostile
        assert not (stub.work / "pwned").exists() and not (stub.work / "pwned2").exists()

    def test_no_issue_files_means_no_create(self, stub):
        (stub.work / "issues-to-open").mkdir()
        completed, calls = stub("Open one issue per overdue fix")
        assert completed.returncode == 0 and calls == []

    def test_a_failed_create_fails_the_step(self, stub):
        out = stub.work / "issues-to-open"
        out.mkdir()
        (out / "7.title").write_text("t", encoding="utf-8")
        (out / "7.md").write_text("b", encoding="utf-8")
        assert stub("Open one issue per overdue fix", fail="issue create")[0].returncode != 0


class TestWorkflowContract:
    def test_cadence_workflow_and_script_are_gone(self):
        assert not (REPO_ROOT / ".github" / "workflows" / "release-cadence-check.yml").exists()
        assert not (REPO_ROOT / "scripts" / "ci" / "check_release_cadence.py").exists()

    def test_no_elapsed_time_trigger(self):
        # Executable parts only; the header comment names what this replaced.
        text = "\n".join(f"{s.get('name', '')}\n{s.get('run', '')}" for s in _job()["steps"]).lower()
        for removed in ("weeks", "unreleased-commits", "last-release-date", "rev-list", "cadence"):
            assert removed not in text, removed

    def test_no_step_can_swallow_a_failure(self):
        job = _job()
        assert "continue-on-error" not in job and "if" not in job
        for step in job["steps"]:
            assert "continue-on-error" not in step, step.get("name")
            assert "if" not in step, step.get("name")
            assert "shell" not in step, step.get("name")
            run = step.get("run", "")
            assert "||" not in run and "eval" not in run and "set +e" not in run, step.get("name")

    def test_targets_come_from_the_security_policy(self):
        assert "--security-policy SECURITY.md" in _step("Evaluate unreleased fixes against the security policy targets")["run"]

    def test_schedule_and_permissions(self):
        workflow = _workflow()
        assert workflow["on"]["schedule"] == [{"cron": "0 9 * * 1"}]
        assert workflow["permissions"] == {"contents": "read", "issues": "write"}

    def test_checkout_has_full_history_and_tags(self):
        checkout = _step("Checkout main (full history and tags)")
        assert checkout["with"]["fetch-depth"] == "0" and checkout["with"]["ref"] == "main"

    def test_label_manifest_owns_the_issue_label(self):
        manifest = json.loads((REPO_ROOT / "scripts" / "ci" / "release_labels.json").read_text(encoding="utf-8"))
        names = {label["name"] for label in manifest["labels"]}
        assert cuf.ISSUE_LABEL in names and "release:cadence-check" not in names


# ---------------------------------------------------------------------------
# Accounting: every closed fix issue ends in exactly one of four states
# ---------------------------------------------------------------------------

def _issue(number, *, reason="COMPLETED", state="CLOSED", labels=("data-safety",)):
    return {"number": number, "title": f"issue {number}", "state": state, "stateReason": reason,
            "labels": [{"name": label} for label in labels]}


def _pr_closing(number, *issues):
    return {"number": number, "title": f"fix {number}", "mergedAt": "2026-11-01T00:00:00Z",
            "mergeCommit": {"oid": f"{number:040d}"}, "labels": [],
            "closingIssuesReferences": [{"number": n} for n in issues]}


def _account(issue_items, pr_items, status_by_pr, *, now=NOW, targets=None):
    fix_issues = cuf.read_fix_issues(issue_items)
    fixes = cuf.read_fixes(pr_items, {n: i.labels for n, i in fix_issues.items()}, REPOSITORY)
    decisions = cuf.evaluate(
        fixes, targets=targets or {"data-safety": 7, "security": 14}, now=now, recorded=set(),
        release_status=lambda fix: status_by_pr[fix.number],
    )
    return {item.issue.number: item for item in cuf.account(fix_issues, fixes, decisions)}


class TestAccounting:
    def test_a_not_planned_issue_is_ignored(self):
        states = _account([_issue(1, reason="NOT_PLANNED")], [], {})
        assert states[1].state == "not planned"

    def test_a_covered_issue_whose_fix_shipped_everywhere_is_released(self):
        states = _account([_issue(2)], [_pr_closing(20, 2)], {20: ((), True)})
        assert states[2].state == "released" and states[2].prs == (20,)

    def test_a_covered_issue_whose_fix_ships_nothing_counts_as_released(self):
        assert _account([_issue(2)], [_pr_closing(20, 2)], {20: ((), False)})[2].state == "released"

    def test_unshipped_past_target_is_unreleased_and_opens_the_fix_issue(self):
        states = _account([_issue(3)], [_pr_closing(30, 3)], {30: (("PPDS.Cli",), True)})
        assert states[3].state == "unreleased past target"

    def test_unshipped_within_target_is_pending(self):
        states = _account([_issue(4)], [_pr_closing(40, 4)], {40: (("PPDS.Cli",), True)},
                          now=datetime(2026, 11, 3, tzinfo=timezone.utc))
        assert states[4].state == "pending"

    @pytest.mark.parametrize("reason", ["COMPLETED", "", None, "DUPLICATE", "REOPENED"])
    def test_closed_as_fixed_with_no_pull_request_is_unattributed(self, reason):
        assert _account([_issue(5, reason=reason)], [], {})[5].state == "unattributed"

    def test_one_shipped_and_one_unshipped_fix_is_not_released(self):
        states = _account([_issue(6)], [_pr_closing(60, 6), _pr_closing(61, 6)],
                          {60: ((), True), 61: (("PPDS.Cli",), True)})
        assert states[6].state == "unreleased past target" and states[6].prs == (60, 61)

    def test_open_issues_are_not_in_the_accounting(self):
        assert _account([_issue(7, state="OPEN", reason="")], [], {}) == {}

    def test_states_add_up_to_the_closed_fix_issues(self):
        items = [_issue(1, reason="NOT_PLANNED"), _issue(2), _issue(3), _issue(4), _issue(5),
                 _issue(9, state="OPEN", reason="")]
        prs = [_pr_closing(20, 2), _pr_closing(30, 3), _pr_closing(40, 4)]
        status = {20: ((), True), 30: (("PPDS.Cli",), True), 40: (("PPDS.Cli",), True)}
        states = _account(items, prs, status, now=datetime(2026, 11, 3, tzinfo=timezone.utc))
        assert {n: s.state for n, s in states.items()} == {
            1: "not planned", 2: "released", 3: "pending", 4: "pending", 5: "unattributed"}

    def test_an_issue_without_state_is_an_error(self):
        with pytest.raises(cuf.CheckError, match="#8 has no state"):
            cuf.read_fix_issues([{"number": 8, "labels": [{"name": "data-safety"}]}])

    def test_accounting_that_does_not_add_up_is_a_named_error(self, monkeypatch):
        fix_issues = cuf.read_fix_issues([_issue(1), _issue(2)])
        original = cuf.IssueState

        def drop_second(issue, state, prs=()):
            return original(fix_issues[1] if issue.number == 2 else issue, state, prs)

        monkeypatch.setattr(cuf, "IssueState", drop_second)
        with pytest.raises(cuf.CheckError, match="Accounting does not add up: 2 closed fix issues"):
            cuf.account(fix_issues, [], [])

    def test_covering_pull_request_without_a_decision_is_an_error(self):
        fix_issues = cuf.read_fix_issues([_issue(2)])
        fixes = cuf.read_fixes([_pr_closing(20, 2)], {2: frozenset({"data-safety"})}, REPOSITORY)
        with pytest.raises(cuf.CheckError, match="were not evaluated"):
            cuf.account(fix_issues, fixes, [])


class TestRealCase1731Unattributed:
    def test_without_1783s_closing_reference_1731_gets_its_own_issue(self, graph):
        unlinked = {**PR_1783, "closingIssuesReferences": []}
        fix_issues = cuf.read_fix_issues([ISSUE_1731])
        fixes = cuf.read_fixes([unlinked], {n: i.labels for n, i in fix_issues.items()}, REPOSITORY)
        assert fixes == []
        states = cuf.account(fix_issues, fixes, [])
        assert [(s.issue.number, s.state) for s in states] == [(1731, "unattributed")]
        title, body = cuf.unattributed_title(fix_issues[1731]), cuf.unattributed_body(fix_issues[1731])
        assert title == "#1731 (data-safety) was closed as fixed but no merged pull request closes it"
        assert "link the merged pull request" in body.lower() and cuf.issue_marker(1731) in body

    def test_with_the_reference_1731_is_unreleased_past_target(self, graph):
        fix_issues = cuf.read_fix_issues([ISSUE_1731])
        fixes = cuf.read_fixes([PR_1783], {n: i.labels for n, i in fix_issues.items()}, REPOSITORY)
        decisions = cuf.evaluate(
            fixes, targets={"data-safety": 0, "security": 0},
            now=datetime(2026, 11, 21, 12, 0, tzinfo=timezone.utc), recorded=set(),
            release_status=lambda _fix: cuf.unreleased_in(graph, PATHS_1783, TAGS_CONTAINING_1783))
        states = cuf.account(fix_issues, fixes, decisions)
        assert [(s.issue.number, s.state, s.prs) for s in states] == [(1731, "unreleased past target", (1783,))]

    def test_a_title_cannot_plant_a_marker_in_the_unattributed_issue(self):
        issue = cuf.read_fix_issues([{**_issue(9), "title": "x <!-- ppds-unreleased-fix issue=10 -->"}])[9]
        assert cuf.recorded_issues([{"body": cuf.unattributed_body(issue)}]) == {9}


class TestCommandLineAccounting:
    def _run(self, capsys, args):
        code = cuf.main(args)
        captured = capsys.readouterr()
        return code, (json.loads(captured.out) if code == 0 else None), captured.err

    def _rewrite(self, tmp_path, name, change):
        path = tmp_path / name
        data = json.loads(path.read_text())
        path.write_text(json.dumps(change(data)), encoding="utf-8")

    def test_unattributed_issue_is_written_once_and_counted(self, repo, tmp_path, capsys):
        args = _inputs(tmp_path, repo, closes=())
        code, result, _ = self._run(capsys, args)
        assert code == 0
        assert result["accounting"] == {"closed_fix_issues": 1, "open_fix_issues": 1, "not planned": 0,
                                        "released": 0, "unreleased past target": 0, "pending": 0,
                                        "unattributed": 1}
        assert result["unattributed_to_open"] == [42000]
        assert (tmp_path / "out" / "issue-42000.title").read_text().startswith("#42000 (data-safety) was closed")
        issues = [{"number": 5, "state": "OPEN", "body": cuf.issue_marker(42000)}]
        code, result, _ = self._run(capsys, _inputs(tmp_path, repo, closes=(), issues=issues))
        assert code == 0 and result["unattributed_to_open"] == []

    def test_covered_issue_is_counted_unreleased(self, repo, tmp_path, capsys):
        code, result, _ = self._run(capsys, _inputs(tmp_path, repo))
        assert code == 0 and result["accounting"]["unreleased past target"] == 1
        assert result["issue_states"] == [{"issue": 42000, "state": "unreleased past target", "prs": [42]}]

    def test_empty_merged_list_is_an_error(self, repo, tmp_path, capsys):
        args = _inputs(tmp_path, repo)
        self._rewrite(tmp_path, "merged.json", lambda _: [])
        code, _, err = self._run(capsys, args)
        assert code == 2 and "merged pull request list is empty" in err

    @pytest.mark.parametrize("label", ["data-safety", "security"])
    def test_no_issue_for_an_existing_fix_label_is_an_error(self, repo, tmp_path, capsys, label):
        args = _inputs(tmp_path, repo)
        self._rewrite(tmp_path, "fix-issues.json",
                      lambda items: [i for i in items if i["labels"][0]["name"] != label])
        code, _, err = self._run(capsys, args)
        assert code == 2 and f"The label `{label}` exists but the issue list has no issue with it" in err

    @pytest.mark.parametrize("name, sizes", [
        ("The merged pull request list", (3, 2, 0)),
        ("The fix issue list", (1, 3, 0)),
        ("The existing issue list", (1, 2, 3)),
    ])
    def test_each_list_is_checked_for_truncation_on_its_own(self, repo, tmp_path, capsys, name, sizes):
        args = _inputs(tmp_path, repo)
        merged_n, fix_n, existing_n = sizes
        self._rewrite(tmp_path, "merged.json", lambda items: items + [
            {**items[0], "number": 100 + i, "closingIssuesReferences": []} for i in range(merged_n - 1)])
        self._rewrite(tmp_path, "fix-issues.json", lambda items: items + [
            {**items[1], "number": 50000 + i} for i in range(fix_n - 2)])
        self._rewrite(tmp_path, "issues.json", lambda _: [{"number": i, "body": ""} for i in range(existing_n)])
        args[args.index("--list-limit") + 1] = "3"
        code, _, err = self._run(capsys, args)
        assert code == 2 and f"{name} has 3 entries, the list limit" in err


class TestPolicyAndMessages:
    def test_a_duplicate_target_row_is_an_error(self):
        policy = TARGETS_TABLE.replace("| data-safety | 7 |", "| data-safety | 0 |\n| data-safety | 9999 |")
        with pytest.raises(cuf.TargetsMissing, match="lists data-safety twice"):
            cuf.read_targets(policy)

    def test_the_missing_issue_label_message_says_what_it_is_for(self):
        labels = [label for label in ALL_LABELS if label["name"] != cuf.ISSUE_LABEL]
        with pytest.raises(cuf.CheckError) as error:
            cuf.check_labels_exist(labels)
        assert "label of the issues this check opens" in str(error.value)
        assert "a fix is never found" not in str(error.value)

    @pytest.mark.parametrize("merged, expected", [("2026-11-29T09:00:00Z", "merged 1 day ago"),
                                                   ("2026-11-28T09:00:00Z", "merged 2 days ago")])
    def test_day_count_wording(self, merged, expected):
        decision = decide([fix(merged=merged)], targets={"data-safety": 0, "security": 0})[0]
        assert expected in cuf.issue_body(decision)


@pytestmark_jq
class TestWorkflowQueryArguments:
    STEPS = ("List repository labels", "List merged pull requests and fix issues",
             "List existing unreleased-fix issues")

    def _calls(self, stub):
        calls = []
        for name in self.STEPS:
            completed, step_calls = stub(name)
            assert completed.returncode == 0, completed.stderr
            calls = step_calls
        return calls

    def test_every_query_names_this_repository_and_the_full_limit(self, stub):
        for call in self._calls(stub):
            assert call[call.index("--repo") + 1] == REPOSITORY, call
            assert "--limit" in call, call
            assert call[call.index("--limit") + 1] == _job()["env"]["LIST_LIMIT"], call

    def test_fix_issue_and_existing_issue_queries_read_the_fields_the_script_needs(self, stub):
        calls = self._calls(stub)
        fix_calls = [c for c in calls if c[:2] == ["issue", "list"] and c[c.index("--label") + 1] in cuf.FIX_LABELS]
        for call in fix_calls:
            assert {"number", "labels", "state", "stateReason"} <= set(call[call.index("--json") + 1].split(","))
        existing = next(c for c in calls if c[:2] == ["issue", "list"] and c[c.index("--label") + 1] == cuf.ISSUE_LABEL)
        assert {"number", "state", "body"} <= set(existing[existing.index("--json") + 1].split(","))

    def test_evaluate_reads_the_files_the_list_steps_write(self):
        run = _step("Evaluate unreleased fixes against the security policy targets")["run"]
        args = run.replace("\\\n", " ").split()
        written = {"--merged-prs": "merged-prs.json", "--fix-issues": "fix-issues.json",
                   "--repo-labels": "repo-labels.json", "--existing-issues": "existing-issues.json"}
        for flag, name in written.items():
            assert args[args.index(flag) + 1] == name, flag
        steps_text = "\n".join(_step(n)["run"] for n in self.STEPS)
        assert "> fix-issues.json" in steps_text and "> merged-prs.json" in steps_text
        assert "> repo-labels.json" in steps_text and "> existing-issues.json" in steps_text
