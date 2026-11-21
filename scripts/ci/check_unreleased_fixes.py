#!/usr/bin/env python3
"""Risk rule: a merged data-safety or security fix that stays unreleased (#1791).

Replaces the 8-week release-cadence timer. Time since the last release says
nothing about whether users are exposed; an unreleased fix for wrong data, lost
data or a security flaw does. ``release-risk-check.yml`` runs this weekly.

**A fix** is a pull request merged into ``main`` that carries the label
``data-safety`` or ``security``, or that closes an issue in this repository
carrying one of them (the pull request's closing-issue references). In this
repository the ``data-safety`` label is put on issues, so the closing reference
is how most fixes are found.

**Unreleased** means: some deliverable that ships the changed code has no
stable release tag containing the merge commit. The deliverables are the
release plan's own patch targets for the merge commit's diff
(``release_model.build_release_plan``): the directly changed packages plus every
downstream deliverable that consumes or bundles them. A change in a library is
unreleased until the CLI, MCP server and Extension that carry it have shipped
too. A prerelease tag, or an odd-minor Extension tag, does not count: users on
the stable channel are still exposed.

An unreleased fix older than its target opens one issue that names the fix.
Nothing opens an issue because of elapsed time alone.

**Accounting from the issues.** Every closed issue that carries a fix label
ends in exactly one state, and the output counts them:

- (a) ``not planned``: closed as not planned; ignored.
- (b) ``released``: closed as completed, covered by at least one merged pull
  request's closing reference, and every covering fix shipped in every
  deliverable (or changes nothing that ships).
- (c) ``unreleased past target`` (an issue is opened, once) or ``pending``
  (still within the target): covered, but not all shipped.
- (d) ``unattributed``: closed as completed (or any reason other than not
  planned) and covered by no merged pull request, for example closed by hand,
  by a commit, or named without a closing keyword. It gets its own once-only
  issue asking for the pull request to be linked.

The script checks that the states add up to the number of closed labelled
issues and fails with a named error if they do not. A pull request that
carries a fix label itself is a fix whether or not an issue is labelled.

**Targets** come from the security policy (``SECURITY.md``), from a table
between ``<!-- RELEASE_TARGETS:BEGIN -->`` and ``<!-- RELEASE_TARGETS:END -->``
with one row per label and the number of days after merge::

    | Fix | Released within (days after merge) |
    |-----|------------------------------------|
    | data-safety | <days> |
    | security | <days> |

**Fails loudly, never silently.** The check exits 2 and opens nothing when the
policy states no targets (or a target twice), when a label it relies on does
not exist in the repository, when a list it was given may be truncated, or
when a list is empty that cannot be: no merged pull requests at all, or no
issue for a fix label that exists. Each error is named.

Each opened issue carries a marker for its pull request number. A fix with an
issue, open or closed, does not get another.

Usage (workflow)::

    python scripts/ci/check_unreleased_fixes.py \\
        --merged-prs merged-prs.json --fix-issues fix-issues.json \\
        --repo-labels repo-labels.json --existing-issues existing-issues.json \\
        --security-policy SECURITY.md --repository owner/name \\
        --list-limit 3000 --issue-dir issues-to-open

Inputs (all ``gh ... --json`` output):

- ``--merged-prs``: ``gh pr list --state merged --base main --json
  number,title,mergedAt,mergeCommit,labels,closingIssuesReferences``
- ``--fix-issues``: ``gh issue list --state all --label <label> --json
  number,title,labels,state,stateReason`` for each fix label, merged
- ``--repo-labels``: ``gh label list --json name``
- ``--existing-issues``: ``gh issue list --label release:unreleased-fix
  --state all --json number,state,body``

Exit codes:
    0 - evaluated (issues to open, if any, are written to --issue-dir)
    2 - an input could not be read or is incomplete, a required label does not
        exist, or the security policy states no targets
"""
from __future__ import annotations

import argparse
import html
import json
import re
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterable, Mapping, Optional, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_model import FileChange, ReleaseGraph, SemVer, build_release_plan  # noqa: E402

FIX_LABELS = ("data-safety", "security")
ISSUE_LABEL = "release:unreleased-fix"
REQUIRED_LABELS = (*FIX_LABELS, ISSUE_LABEL)
TARGETS_BEGIN = "<!-- RELEASE_TARGETS:BEGIN -->"
TARGETS_END = "<!-- RELEASE_TARGETS:END -->"
EXTENSION_SURFACE = "PPDS.Extension"
MAX_ISSUE_TITLE = 256
PUBLIC_RELEASE_RUNBOOK = (
    "https://github.com/joshsmithxrm/power-platform-developer-suite/"
    "blob/main/docs/runbooks/release.md"
)


class CheckError(ValueError):
    """The check cannot decide; it must fail rather than report nothing."""


class TargetsMissing(CheckError):
    """The security policy states no release target for a fix label."""


@dataclass(frozen=True)
class Fix:
    number: int
    title: str
    merged_at: datetime
    merge_oid: str
    labels: frozenset[str]
    via_issues: tuple[int, ...] = ()


@dataclass(frozen=True)
class Decision:
    fix: Fix
    open_issue: bool
    reason: str
    unreleased_surfaces: tuple[str, ...] = ()
    age_days: int = 0
    target_days: Optional[int] = None

    def as_dict(self) -> dict:
        return {
            "pr": self.fix.number,
            "title": self.fix.title,
            "labels": sorted(self.fix.labels),
            "via_issues": list(self.fix.via_issues),
            "open_issue": self.open_issue,
            "reason": self.reason,
            "unreleased_surfaces": list(self.unreleased_surfaces),
            "age_days": self.age_days,
            "target_days": self.target_days,
        }


# ---------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------

def parse_utc(value: str) -> datetime:
    normalized = value[:-1] + "+00:00" if value.endswith(("Z", "z")) else value
    parsed = datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        return parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def read_targets(policy_text: str) -> dict[str, int]:
    """Read the release targets (days after merge) from the security policy."""
    begin = policy_text.find(TARGETS_BEGIN)
    end = policy_text.find(TARGETS_END)
    if begin < 0 or end < begin:
        raise TargetsMissing(
            "The security policy (SECURITY.md) states no release targets for merged "
            f"data-safety and security fixes (no {TARGETS_BEGIN} table). The check "
            "cannot decide what is overdue; the maintainer sets the targets."
        )
    targets: dict[str, int] = {}
    for line in policy_text[begin + len(TARGETS_BEGIN):end].splitlines():
        cells = [cell.strip().strip("`") for cell in line.strip().strip("|").split("|")]
        if len(cells) >= 2 and cells[0] in FIX_LABELS:
            if not re.fullmatch(r"\d+", cells[1]):
                raise TargetsMissing(f"Release target for {cells[0]} is not a whole number of days: {cells[1]!r}")
            if cells[0] in targets:
                raise TargetsMissing(f"The security policy's release-target table lists {cells[0]} twice.")
            targets[cells[0]] = int(cells[1])
    missing = [label for label in FIX_LABELS if label not in targets]
    if missing:
        raise TargetsMissing(
            "The security policy's release-target table has no row for: " + ", ".join(missing)
        )
    return targets


LABEL_PURPOSE = {
    "data-safety": "it marks a fix for wrong or lost data; without it no data-safety fix is found",
    "security": "it marks a security fix; without it no security fix is found",
    ISSUE_LABEL: ("it is the label of the issues this check opens, and how it finds them again "
                  "next week; without it the check would open the same issue every week"),
}


def check_labels_exist(repo_labels: Iterable[dict]) -> None:
    names = {str(label.get("name", "")) for label in repo_labels}
    missing = [label for label in REQUIRED_LABELS if label not in names]
    if missing:
        raise CheckError(
            "The repository has no label "
            + "; ".join(f"`{label}` ({LABEL_PURPOSE[label]})" for label in missing)
            + ". The maintainer creates it."
        )


def check_not_empty(merged: Sequence, fix_issues: Sequence, repo_labels: Iterable[dict]) -> None:
    """A successful query that returns nothing where the repository has something."""
    if not merged:
        raise CheckError(
            "The merged pull request list is empty, but this repository has merged pull "
            "requests; the query returned nothing it should have."
        )
    existing = {str(label.get("name", "")) for label in repo_labels}
    for label in FIX_LABELS:
        if label in existing and not any(
            label in {entry.get("name") for entry in issue.get("labels") or []} for issue in fix_issues
        ):
            raise CheckError(
                f"The label `{label}` exists but the issue list has no issue with it; the query "
                "returned nothing. If no issue carries the label yet, that is the cause: label "
                "the first such issue, or remove the label until it is used."
            )


def check_complete(name: str, items: Sequence, limit: int) -> None:
    if len(items) >= limit:
        raise CheckError(
            f"{name} has {len(items)} entries, the list limit; it may be truncated. "
            "Raise the limit in the workflow."
        )


def issue_fix_labels(issues: Iterable[dict]) -> dict[int, frozenset[str]]:
    """Map each issue number to the fix labels it carries."""
    found: dict[int, frozenset[str]] = {}
    for issue in issues:
        labels = frozenset(label.get("name", "") for label in issue.get("labels") or []) & set(FIX_LABELS)
        if labels:
            number = int(issue["number"])
            found[number] = found.get(number, frozenset()) | labels
    return found


def _same_repository(reference: dict, repository: str) -> bool:
    repo = reference.get("repository")
    if not isinstance(repo, dict):
        return True  # gh lists only this repository's issues when none is named
    owner = (repo.get("owner") or {}).get("login", "")
    return f"{owner}/{repo.get('name', '')}".lower() == repository.lower()


def read_fixes(
    items: Iterable[dict],
    issue_labels: dict[int, frozenset[str]],
    repository: str,
) -> list[Fix]:
    """Merged pull requests that are fixes, by their own label or a closed issue's."""
    fixes: dict[int, Fix] = {}
    for item in items:
        labels = set(label.get("name", "") for label in item.get("labels") or []) & set(FIX_LABELS)
        via: list[int] = []
        for reference in item.get("closingIssuesReferences") or []:
            if not _same_repository(reference, repository):
                continue
            number = int(reference["number"])
            if number in issue_labels:
                labels |= issue_labels[number]
                via.append(number)
        if not labels:
            continue
        oid = str((item.get("mergeCommit") or {}).get("oid") or "")
        merged_at = item.get("mergedAt")
        if not oid or not merged_at:
            raise CheckError(f"#{item.get('number')}: merged pull request without mergeCommit or mergedAt")
        number = int(item["number"])
        fixes[number] = Fix(
            number=number, title=str(item.get("title") or ""),
            merged_at=parse_utc(str(merged_at)), merge_oid=oid,
            labels=frozenset(labels), via_issues=tuple(sorted(via)),
        )
    return [fixes[number] for number in sorted(fixes)]


@dataclass(frozen=True)
class FixIssue:
    number: int
    title: str
    labels: frozenset[str]
    state: str
    reason: str


def read_fix_issues(items: Iterable[dict]) -> dict[int, FixIssue]:
    issues: dict[int, FixIssue] = {}
    for item in items:
        labels = frozenset(label.get("name", "") for label in item.get("labels") or []) & set(FIX_LABELS)
        if not labels:
            continue
        number = int(item["number"])
        state = str(item.get("state") or "").upper()
        if state not in {"OPEN", "CLOSED"}:
            raise CheckError(f"Issue #{number} has no state; the issue query must ask for state,stateReason")
        previous = issues.get(number)
        issues[number] = FixIssue(
            number=number, title=str(item.get("title") or ""),
            labels=labels | (previous.labels if previous else frozenset()),
            state=state, reason=str(item.get("stateReason") or "").upper(),
        )
    return issues


STATES = ("not planned", "released", "unreleased past target", "pending", "unattributed")


@dataclass(frozen=True)
class IssueState:
    issue: FixIssue
    state: str
    prs: tuple[int, ...] = ()


def account(
    fix_issues: Mapping[int, FixIssue], fixes: Sequence[Fix], decisions: Sequence[Decision],
) -> list[IssueState]:
    """Put every closed fix issue in exactly one state (see the module docstring)."""
    by_pr = {decision.fix.number: decision for decision in decisions}
    covering: dict[int, list[int]] = {}
    for fix in fixes:
        for number in fix.via_issues:
            covering.setdefault(number, []).append(fix.number)
    closed = sorted(number for number, issue in fix_issues.items() if issue.state == "CLOSED")
    states: list[IssueState] = []
    for number in closed:
        issue = fix_issues[number]
        prs = tuple(sorted(covering.get(number, ())))
        if issue.reason == "NOT_PLANNED":
            states.append(IssueState(issue, "not planned", prs))
            continue
        if not prs:
            states.append(IssueState(issue, "unattributed"))
            continue
        missing = [pr for pr in prs if pr not in by_pr]
        if missing:
            raise CheckError(f"Issue #{number}: covering pull request(s) {missing} were not evaluated")
        reasons = {by_pr[pr].reason for pr in prs}
        if reasons <= {"released", "changes no release surface"}:
            state = "released"
        elif reasons & {"unreleased past target", "already recorded"}:
            state = "unreleased past target"
        else:
            state = "pending"
        states.append(IssueState(issue, state, prs))
    counts = {name: sum(1 for item in states if item.state == name) for name in STATES}
    if sum(counts.values()) != len(closed) or {item.issue.number for item in states} != set(closed):
        raise CheckError(
            f"Accounting does not add up: {len(closed)} closed fix issues, states {counts}"
        )
    return states


def record_marker(pr_number: int) -> str:
    return f"<!-- ppds-unreleased-fix pr={pr_number} -->"


def issue_marker(issue_number: int) -> str:
    return f"<!-- ppds-unreleased-fix issue={issue_number} -->"


def _markers(issues: Iterable[dict], kind: str) -> set[int]:
    found: set[int] = set()
    for issue in issues:
        for match in re.finditer(rf"<!-- ppds-unreleased-fix {kind}=(\d+) -->", str(issue.get("body") or "")):
            found.add(int(match.group(1)))
    return found


def recorded_prs(issues: Iterable[dict]) -> set[int]:
    return _markers(issues, "pr")


def recorded_issues(issues: Iterable[dict]) -> set[int]:
    return _markers(issues, "issue")


def unattributed_title(issue: FixIssue) -> str:
    labels = " and ".join(sorted(issue.labels))
    title = f"#{issue.number} ({labels}) was closed as fixed but no merged pull request closes it"
    return title[:MAX_ISSUE_TITLE]


def unattributed_body(issue: FixIssue) -> str:
    labels = ", ".join(f"`{label}`" for label in sorted(issue.labels))
    return f"""\
## Fix issue closed without a linked pull request

#{issue.number} ({labels}, "{_plain(issue.title)}") was closed as fixed, but no merged pull request closes it. The release risk check cannot tell whether the fix has shipped, so users may still be exposed without any alarm.

## Options for the maintainer

- **Link the pull request**: in #{issue.number}'s Development section, link the merged pull request that fixed it (or add `Closes #{issue.number}` to that pull request's description). The check then follows that pull request to its release.
- **If it was not fixed**: reopen #{issue.number}, or close it as not planned.

Close this issue once that is done. No further issue opens for #{issue.number}.

{issue_marker(issue.number)}
"""


# ---------------------------------------------------------------------------
# Release status
# ---------------------------------------------------------------------------

def is_stable_release(surface: str, version: SemVer) -> bool:
    if version.is_prerelease:
        return False
    if surface == EXTENSION_SURFACE and version.minor % 2 == 1:
        return False
    return True


def stable_release_containing(
    surface: str, tag_prefix: str, containing_tags: Iterable[str],
) -> Optional[str]:
    """Return a stable release tag of ``surface`` among ``containing_tags``."""
    for tag in sorted(containing_tags):
        if not tag.startswith(tag_prefix):
            continue
        try:
            version = SemVer.parse(tag[len(tag_prefix):])
        except ValueError:
            continue
        if is_stable_release(surface, version):
            return tag
    return None


def deliverables_for(graph: ReleaseGraph, paths: Sequence[str]) -> list[str]:
    """The release plan's patch targets for a diff: direct plus downstream."""
    plan = build_release_plan(
        graph, [FileChange(path=path) for path in paths],
        release_kind="patch", channel="stable",
    )
    return list(plan["release_targets"])


def unreleased_in(
    graph: ReleaseGraph, paths: Sequence[str], containing_tags: Iterable[str],
) -> tuple[tuple[str, ...], bool]:
    """Return (deliverables without a stable tag containing the fix, any deliverable)."""
    tags = list(containing_tags)
    deliverables = deliverables_for(graph, paths)
    unreleased = tuple(
        surface for surface in deliverables
        if stable_release_containing(surface, graph.surfaces[surface].tag_prefix, tags) is None
    )
    return unreleased, bool(deliverables)


# ---------------------------------------------------------------------------
# Decision
# ---------------------------------------------------------------------------

ReleaseStatus = Callable[[Fix], tuple[tuple[str, ...], bool]]


def evaluate(
    fixes: Sequence[Fix],
    *,
    targets: dict[str, int],
    now: datetime,
    recorded: set[int],
    release_status: ReleaseStatus,
) -> list[Decision]:
    decisions: list[Decision] = []
    for fix in fixes:
        surfaces, ships = release_status(fix)
        if not ships:
            decisions.append(Decision(fix, False, "changes no release surface"))
            continue
        if not surfaces:
            decisions.append(Decision(fix, False, "released"))
            continue
        age_days = (now - fix.merged_at).days
        target = min(targets[label] for label in fix.labels if label in targets)
        if age_days <= target:
            decisions.append(Decision(fix, False, "within target", surfaces, age_days, target))
            continue
        if fix.number in recorded:
            decisions.append(Decision(fix, False, "already recorded", surfaces, age_days, target))
            continue
        decisions.append(Decision(fix, True, "unreleased past target", surfaces, age_days, target))
    return decisions


def _plain(text: str) -> str:
    """A pull request title as inert text: one line, no HTML comment, no table break."""
    return html.escape(" ".join(text.split()), quote=False).replace("|", "\\|")


def _days(count: Optional[int]) -> str:
    return "1 day" if count == 1 else f"{count} days"


def issue_title(decision: Decision) -> str:
    labels = " and ".join(sorted(decision.fix.labels))
    prefix = f"Unreleased {labels} fix #{decision.fix.number}: "
    title = " ".join(decision.fix.title.split())
    room = MAX_ISSUE_TITLE - len(prefix)
    if len(title) > room:
        title = title[: room - 1].rstrip() + "…"
    return prefix + title


def issue_body(decision: Decision) -> str:
    fix = decision.fix
    title = _plain(fix.title)
    surfaces = ", ".join(f"`{surface}`" for surface in decision.unreleased_surfaces)
    labels = ", ".join(f"`{label}`" for label in sorted(fix.labels))
    via = (
        " (closes " + ", ".join(f"#{number}" for number in fix.via_issues) + ")"
        if fix.via_issues else ""
    )
    return f"""\
## Unreleased fix past its release target

#{fix.number} ({labels}){via} was merged {_days(decision.age_days)} ago and is still not in a stable release of {surfaces}. The security policy's target for this kind of fix is {_days(decision.target_days)} after merge.

| Field | Value |
|-------|-------|
| Fix | #{fix.number} {title} |
| Merged | {fix.merged_at.date().isoformat()} (`{fix.merge_oid[:12]}`) |
| Unreleased in | {surfaces} |
| Days since merge | {decision.age_days} |
| Target (days) | {decision.target_days} |

Users of a released version of these packages are still exposed to what #{fix.number} fixes.

## Options for the maintainer

- **Release** the affected packages: follow the public [release procedure]({PUBLIC_RELEASE_RUNBOOK}) (patch release procedure).
- **Record why not**: comment with the reason and close this issue. No further issue opens for #{fix.number}.

{record_marker(fix.number)}
"""


# ---------------------------------------------------------------------------
# Git-backed release status
# ---------------------------------------------------------------------------

def _git(repo_root: Path, *args: str) -> str:
    completed = subprocess.run(
        ["git", *args], cwd=repo_root, capture_output=True, text=True,
        encoding="utf-8", errors="replace",
    )
    if completed.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {completed.stderr.strip()}")
    return completed.stdout


def git_release_status(repo_root: Path, graph: ReleaseGraph) -> ReleaseStatus:
    def status(fix: Fix) -> tuple[tuple[str, ...], bool]:
        names = _git(repo_root, "-c", "core.quotepath=off", "diff", "--name-only", "--no-renames",
                     f"{fix.merge_oid}^", fix.merge_oid)
        paths = [line.strip() for line in names.splitlines() if line.strip()]
        containing = _git(repo_root, "tag", "--contains", fix.merge_oid).split()
        return unreleased_in(graph, paths, containing)
    return status


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _load(path: Path) -> list:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise CheckError(f"{path}: expected a JSON list")
    return data


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--merged-prs", type=Path, required=True)
    parser.add_argument("--fix-issues", type=Path, required=True)
    parser.add_argument("--repo-labels", type=Path, required=True)
    parser.add_argument("--existing-issues", type=Path, required=True)
    parser.add_argument("--security-policy", type=Path, required=True)
    parser.add_argument("--repository", required=True, help="owner/name of this repository")
    parser.add_argument("--list-limit", type=int, required=True,
                        help="the --limit the lists were fetched with; a list that reaches it is an error")
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--current-date", help="ISO-8601 date/time to treat as now (default: now, UTC)")
    parser.add_argument("--issue-dir", type=Path,
                        help="write <pr>.title/.md and issue-<n>.title/.md for each issue to open")
    args = parser.parse_args(argv)

    errors: list[str] = []
    targets: dict[str, int] = {}
    try:
        targets = read_targets(args.security_policy.read_text(encoding="utf-8"))
    except (CheckError, OSError) as error:
        errors.append(str(error))
    try:
        check_labels_exist(_load(args.repo_labels))
    except (CheckError, OSError, ValueError) as error:
        errors.append(str(error))
    if errors:
        for error in errors:
            print(f"::error title=Unreleased-fix check::{error}", file=sys.stderr)
        return 2

    try:
        repo_labels = _load(args.repo_labels)
        merged = _load(args.merged_prs)
        fix_issue_items = _load(args.fix_issues)
        existing = _load(args.existing_issues)
        check_complete("The merged pull request list", merged, args.list_limit)
        check_complete("The fix issue list", fix_issue_items, args.list_limit)
        check_complete("The existing issue list", existing, args.list_limit)
        check_not_empty(merged, fix_issue_items, repo_labels)
        fix_issues = read_fix_issues(fix_issue_items)
        fixes = read_fixes(merged, {n: issue.labels for n, issue in fix_issues.items()}, args.repository)
        now = parse_utc(args.current_date) if args.current_date else datetime.now(timezone.utc)
        repo_root = args.repo_root.resolve()
        decisions = evaluate(
            fixes, targets=targets, now=now, recorded=recorded_prs(existing),
            release_status=git_release_status(repo_root, ReleaseGraph.discover(repo_root)),
        )
        states = account(fix_issues, fixes, decisions)
    except (CheckError, OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
        print(f"::error title=Unreleased-fix check::could not evaluate: {error}", file=sys.stderr)
        return 2

    recorded_unattributed = recorded_issues(existing)
    unattributed = [item.issue for item in states
                    if item.state == "unattributed" and item.issue.number not in recorded_unattributed]
    if args.issue_dir:
        args.issue_dir.mkdir(parents=True, exist_ok=True)
        for decision in decisions:
            if decision.open_issue:
                (args.issue_dir / f"{decision.fix.number}.title").write_text(issue_title(decision), encoding="utf-8")
                (args.issue_dir / f"{decision.fix.number}.md").write_text(issue_body(decision), encoding="utf-8")
        for issue in unattributed:
            (args.issue_dir / f"issue-{issue.number}.title").write_text(unattributed_title(issue), encoding="utf-8")
            (args.issue_dir / f"issue-{issue.number}.md").write_text(unattributed_body(issue), encoding="utf-8")
    counts = {name: sum(1 for item in states if item.state == name) for name in STATES}
    print(json.dumps({
        "targets": targets,
        "accounting": {
            "closed_fix_issues": len(states),
            "open_fix_issues": sum(1 for issue in fix_issues.values() if issue.state == "OPEN"),
            **counts,
        },
        "issue_states": [
            {"issue": item.issue.number, "state": item.state, "prs": list(item.prs)} for item in states
        ],
        "decisions": [decision.as_dict() for decision in decisions],
        "issues_to_open": [decision.fix.number for decision in decisions if decision.open_issue],
        "unattributed_to_open": [issue.number for issue in unattributed],
    }, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
