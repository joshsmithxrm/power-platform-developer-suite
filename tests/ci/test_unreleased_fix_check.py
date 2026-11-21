"""Tests for the release risk rule that replaced the 8-week cadence timer (#1791).

Covers scripts/ci/check_unreleased_fixes.py and .github/workflows/release-risk-check.yml.

Run with: python -m pytest tests/ci/test_unreleased_fix_check.py -q
"""
from __future__ import annotations

import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_unreleased_fixes as cuf  # noqa: E402

TARGETS_TABLE = f"""# Security Policy

{cuf.TARGETS_BEGIN}
| Fix | Released within (days after merge) |
|-----|------------------------------------|
| data-safety | 7 |
| `security` | 14 |
{cuf.TARGETS_END}
"""
NOW = datetime(2026, 11, 30, 9, 0, tzinfo=timezone.utc)


def fix(number=1700, *, labels=("data-safety",), merged="2026-11-01T12:00:00Z", title="stop writing the wrong rows"):
    return cuf.Fix(
        number=number, title=title, merged_at=cuf.parse_utc(merged),
        merge_oid="a" * 40, labels=frozenset(labels),
    )


def decide(fixes, *, unreleased=("PPDS.Cli",), recorded=frozenset(), targets=None, now=NOW):
    return cuf.evaluate(
        fixes, targets=targets or {"data-safety": 7, "security": 14}, now=now,
        recorded=set(recorded), unreleased_surfaces=lambda _fix: tuple(unreleased),
    )


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

    @pytest.mark.parametrize("value", ["seven", "7.5", "", "-1"])
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
        assert cuf.record_marker(1700) in body

    def test_fix_within_target_opens_nothing(self):
        decisions = decide([fix(merged="2026-11-25T00:00:00Z")])
        assert decisions[0].open_issue is False
        assert decisions[0].reason == "within target"

    def test_target_day_itself_is_within_target(self):
        decisions = decide([fix(merged="2026-11-23T09:00:00Z")])
        assert decisions[0].age_days == 7 and decisions[0].open_issue is False

    def test_released_fix_opens_nothing(self):
        decisions = decide([fix()], unreleased=())
        assert decisions[0].open_issue is False
        assert decisions[0].reason == "released or changes no release surface"

    def test_fix_with_an_issue_gets_no_second_one(self):
        decisions = decide([fix()], recorded={1700})
        assert decisions[0].open_issue is False
        assert decisions[0].reason == "already recorded"
        issues = [{"number": 9, "state": "CLOSED", "body": "text\n" + cuf.record_marker(1700)}]
        assert cuf.recorded_prs(issues) == {1700}

    def test_security_fix_uses_its_own_target(self):
        decisions = decide([fix(labels=("security",), merged="2026-11-20T09:00:00Z")])
        assert decisions[0].target_days == 14 and decisions[0].open_issue is False
        decisions = decide([fix(labels=("security",), merged="2026-11-10T09:00:00Z")])
        assert decisions[0].open_issue is True

    def test_fix_with_both_labels_uses_the_shorter_target(self):
        decisions = decide([fix(labels=("security", "data-safety"), merged="2026-11-20T09:00:00Z")])
        assert decisions[0].target_days == 7 and decisions[0].open_issue is True
        assert cuf.issue_title(decisions[0]).startswith("Unreleased data-safety and security fix #1700")

    def test_one_issue_per_fix(self):
        decisions = decide([fix(1), fix(2), fix(3, merged="2026-11-29T00:00:00Z")])
        assert [d.fix.number for d in decisions if d.open_issue] == [1, 2]

    def test_elapsed_time_alone_opens_nothing(self):
        """An unlabelled change, however old and unreleased, is not a fix."""
        items = [{"number": 5, "title": "old feature", "mergedAt": "2025-01-01T00:00:00Z",
                  "mergeCommit": {"oid": "b" * 40}, "labels": [{"name": "type:enhancement"}]}]
        assert cuf.read_fixes(items) == []
        assert decide(cuf.read_fixes(items)) == []

    def test_read_fixes_merges_duplicates_and_requires_merge_data(self):
        item = {"number": 7, "title": "t", "mergedAt": "2026-11-01T00:00:00+02:00",
                "mergeCommit": {"oid": "c" * 40}, "labels": [{"name": "data-safety"}, {"name": "security"}]}
        fixes = cuf.read_fixes([item, item])
        assert len(fixes) == 1
        assert fixes[0].merged_at == datetime(2026, 10, 31, 22, 0, tzinfo=timezone.utc)
        with pytest.raises(ValueError, match="#8"):
            cuf.read_fixes([{"number": 8, "labels": [{"name": "data-safety"}]}])


# ---------------------------------------------------------------------------
# What counts as released
# ---------------------------------------------------------------------------

class TestReleaseStatus:
    @pytest.mark.parametrize("surface, tags, released", [
        ("PPDS.Cli", ["Cli-v1.4.2"], "Cli-v1.4.2"),
        ("PPDS.Cli", ["Cli-v1.5.0-beta.1"], None),
        ("PPDS.Cli", ["Cli-v01.4.2", "Cli-vnot-a-version"], None),
        ("PPDS.Cli", ["Dataverse-v1.4.0"], None),
        ("PPDS.Extension", ["Extension-v1.7.0"], None),
        ("PPDS.Extension", ["Extension-v1.8.0"], "Extension-v1.8.0"),
    ])
    def test_only_a_stable_tag_of_the_surface_counts(self, surface, tags, released):
        prefix = {"PPDS.Cli": "Cli-v", "PPDS.Extension": "Extension-v"}[surface]
        assert cuf.stable_release_containing(surface, prefix, tags) == released


def _git(cwd: Path, *args: str) -> str:
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    root = tmp_path / "repo"
    project = root / "src" / "PPDS.Legacy" / "PPDS.Legacy.csproj"
    project.parent.mkdir(parents=True)
    project.write_text(
        "<Project>\n  <PropertyGroup>\n    <PackageId>PPDS.Legacy</PackageId>\n"
        "    <MinVerTagPrefix>Legacy-v</MinVerTagPrefix>\n  </PropertyGroup>\n</Project>\n",
        encoding="utf-8",
    )
    (root / "src" / "PPDS.Legacy" / "Writer.cs").write_text("class Writer {}\n", encoding="utf-8")
    (root / "SECURITY.md").write_text(TARGETS_TABLE, encoding="utf-8")
    _git(root, "init", "-q", "-b", "main")
    _git(root, "config", "user.email", "test@example.invalid")
    _git(root, "config", "user.name", "Test")
    _git(root, "add", ".")
    _git(root, "commit", "-q", "-m", "base")
    _git(root, "tag", "Legacy-v1.0.0")
    (root / "src" / "PPDS.Legacy" / "Writer.cs").write_text("class Writer { /* guarded */ }\n", encoding="utf-8")
    _git(root, "commit", "-q", "-am", "fix wrong-target write (#42)")
    return root


def _inputs(tmp_path: Path, repo: Path, *, issues=()) -> list[str]:
    merged = tmp_path / "merged.json"
    merged.write_text(json.dumps([{
        "number": 42, "title": "guard the write", "mergedAt": "2026-11-01T00:00:00Z",
        "mergeCommit": {"oid": _git(repo, "rev-parse", "HEAD")},
        "labels": [{"name": "data-safety"}],
    }]), encoding="utf-8")
    existing = tmp_path / "issues.json"
    existing.write_text(json.dumps(list(issues)), encoding="utf-8")
    return [
        "--merged-prs", str(merged), "--existing-issues", str(existing),
        "--security-policy", str(repo / "SECURITY.md"), "--repo-root", str(repo),
        "--current-date", "2026-11-30T00:00:00Z", "--issue-dir", str(tmp_path / "out"),
    ]


class TestCommandLine:
    def test_unreleased_fix_writes_one_issue(self, repo, tmp_path, capsys):
        assert cuf.main(_inputs(tmp_path, repo)) == 0
        result = json.loads(capsys.readouterr().out)
        assert result["issues_to_open"] == [42]
        assert result["decisions"][0]["unreleased_surfaces"] == ["PPDS.Legacy"]
        assert (tmp_path / "out" / "42.title").read_text() == "Unreleased data-safety fix #42: guard the write"
        assert cuf.record_marker(42) in (tmp_path / "out" / "42.md").read_text()

    def test_prerelease_tag_does_not_release_the_fix(self, repo, tmp_path, capsys):
        _git(repo, "tag", "Legacy-v1.0.1-beta.1")
        assert cuf.main(_inputs(tmp_path, repo)) == 0
        assert json.loads(capsys.readouterr().out)["issues_to_open"] == [42]

    def test_stable_tag_containing_the_merge_releases_the_fix(self, repo, tmp_path, capsys):
        _git(repo, "tag", "Legacy-v1.0.1")
        assert cuf.main(_inputs(tmp_path, repo)) == 0
        result = json.loads(capsys.readouterr().out)
        assert result["issues_to_open"] == []
        assert not (tmp_path / "out").exists() or not list((tmp_path / "out").iterdir())

    def test_existing_issue_suppresses_a_second(self, repo, tmp_path, capsys):
        issues = [{"number": 3, "state": "CLOSED", "body": cuf.record_marker(42)}]
        assert cuf.main(_inputs(tmp_path, repo, issues=issues)) == 0
        assert json.loads(capsys.readouterr().out)["issues_to_open"] == []

    def test_missing_targets_exit_2_and_open_nothing(self, repo, tmp_path, capsys):
        (repo / "SECURITY.md").write_text("# Security Policy\n\nNo targets.\n", encoding="utf-8")
        assert cuf.main(_inputs(tmp_path, repo)) == 2
        assert "states no release targets" in capsys.readouterr().err
        assert not (tmp_path / "out").exists()

    def test_unreadable_input_exits_2(self, repo, tmp_path):
        args = _inputs(tmp_path, repo)
        (tmp_path / "merged.json").write_text("nope", encoding="utf-8")
        assert cuf.main(args) == 2


# ---------------------------------------------------------------------------
# Workflow contract
# ---------------------------------------------------------------------------

WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release-risk-check.yml"


def _workflow() -> dict:
    return yaml.load(WORKFLOW.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def _run_text() -> str:
    steps = _workflow()["jobs"]["risk-check"]["steps"]
    return "\n".join(step.get("run", "") for step in steps)


class TestWorkflow:
    def test_cadence_workflow_and_script_are_gone(self):
        assert not (REPO_ROOT / ".github" / "workflows" / "release-cadence-check.yml").exists()
        assert not (REPO_ROOT / "scripts" / "ci" / "check_release_cadence.py").exists()

    def test_no_elapsed_time_trigger(self):
        # Executable parts only; the header comment names what this replaced.
        steps = _workflow()["jobs"]["risk-check"]["steps"]
        text = "\n".join(f"{step.get('name', '')}\n{step.get('run', '')}" for step in steps).lower()
        for removed in ("weeks", "unreleased-commits", "last-release-date", "rev-list", "cadence"):
            assert removed not in text, removed

    def test_lists_both_fix_labels_and_existing_issues_of_its_label(self):
        text = _run_text()
        assert "--label data-safety" in text
        assert "--label security" in text
        assert '--label "release:unreleased-fix" --state all' in text

    def test_targets_come_from_the_security_policy(self):
        assert "--security-policy SECURITY.md" in _run_text()

    def test_opens_issues_only_from_the_script_output(self):
        text = _run_text()
        assert text.count("gh issue create") == 1
        assert 'for body in issues-to-open/*.md' in text
        assert '--label "release:unreleased-fix"' in text

    def test_schedule_and_permissions(self):
        workflow = _workflow()
        assert workflow["on"]["schedule"] == [{"cron": "0 9 * * 1"}]
        assert workflow["permissions"] == {"contents": "read", "issues": "write"}

    def test_label_manifest_owns_the_new_label(self):
        manifest = json.loads((REPO_ROOT / "scripts" / "ci" / "release_labels.json").read_text(encoding="utf-8"))
        names = {label["name"] for label in manifest["labels"]}
        assert cuf.ISSUE_LABEL in names
        assert "release:cadence-check" not in names
