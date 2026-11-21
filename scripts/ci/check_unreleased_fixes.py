#!/usr/bin/env python3
"""Risk rule: a merged data-safety or security fix that stays unreleased (#1791).

Replaces the 8-week release-cadence timer. Time since the last release says
nothing about whether users are exposed; an unreleased fix for wrong data, lost
data or a security flaw does. ``release-risk-check.yml`` runs this weekly.

A fix is a pull request merged into ``main`` with the label ``data-safety`` or
``security``. It is **unreleased** while some release surface its merge commit
changes has no stable release tag that contains that commit. (The surfaces come
from the release model, the same mapping the release plan uses. A prerelease
tag, or an odd-minor Extension tag, does not count: users on the stable channel
are still exposed.) An unreleased fix older than its target opens one issue that
names the fix. Nothing opens an issue because of elapsed time alone.

The targets come from the security policy (``SECURITY.md``), from a table
between ``<!-- RELEASE_TARGETS:BEGIN -->`` and ``<!-- RELEASE_TARGETS:END -->``
with one row per label and the number of days after merge::

    | Fix | Released within (days after merge) |
    |-----|------------------------------------|
    | data-safety | <days> |
    | security | <days> |

When the policy states no targets, the check stops with exit 2 and opens
nothing: what is overdue is the maintainer's to set, not this script's.

Each opened issue carries a marker for its pull request number. A fix with an
issue, open or closed, does not get another.

Usage (workflow)::

    python scripts/ci/check_unreleased_fixes.py \\
        --merged-prs merged-fixes.json \\
        --existing-issues existing-issues.json \\
        --security-policy SECURITY.md \\
        --issue-dir issues-to-open

``--merged-prs``: ``gh pr list --state merged --label <label> --json
number,title,mergedAt,mergeCommit,labels`` output (one or both labels, merged).
``--existing-issues``: ``gh issue list --label release:unreleased-fix --state
all --json number,state,body`` output.

Exit codes:
    0 - evaluated (issues to open, if any, are written to --issue-dir)
    2 - an input could not be read, or the security policy states no targets
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterable, Optional, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_model import FileChange, ReleaseGraph, SemVer, _detect_direct_changes  # noqa: E402

FIX_LABELS = ("data-safety", "security")
ISSUE_LABEL = "release:unreleased-fix"
TARGETS_BEGIN = "<!-- RELEASE_TARGETS:BEGIN -->"
TARGETS_END = "<!-- RELEASE_TARGETS:END -->"
EXTENSION_SURFACE = "PPDS.Extension"
PUBLIC_RELEASE_RUNBOOK = (
    "https://github.com/joshsmithxrm/power-platform-developer-suite/"
    "blob/main/docs/runbooks/release.md"
)


class TargetsMissing(ValueError):
    """The security policy states no release target for a fix label."""


@dataclass(frozen=True)
class Fix:
    number: int
    title: str
    merged_at: datetime
    merge_oid: str
    labels: frozenset[str]


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
            "labels": sorted(self.fix.labels & set(FIX_LABELS)),
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
            targets[cells[0]] = int(cells[1])
    missing = [label for label in FIX_LABELS if label not in targets]
    if missing:
        raise TargetsMissing(
            "The security policy's release-target table has no row for: " + ", ".join(missing)
        )
    return targets


def read_fixes(items: Iterable[dict]) -> list[Fix]:
    fixes: dict[int, Fix] = {}
    for item in items:
        labels = frozenset(label.get("name", "") for label in item.get("labels") or [])
        if not labels & set(FIX_LABELS):
            continue
        oid = str((item.get("mergeCommit") or {}).get("oid") or "")
        merged_at = item.get("mergedAt")
        if not oid or not merged_at:
            raise ValueError(f"#{item.get('number')}: merged pull request without mergeCommit or mergedAt")
        number = int(item["number"])
        fixes[number] = Fix(
            number=number, title=str(item.get("title") or ""),
            merged_at=parse_utc(str(merged_at)), merge_oid=oid, labels=labels,
        )
    return [fixes[number] for number in sorted(fixes)]


def record_marker(pr_number: int) -> str:
    return f"<!-- ppds-unreleased-fix pr={pr_number} -->"


def recorded_prs(issues: Iterable[dict]) -> set[int]:
    found: set[int] = set()
    for issue in issues:
        for match in re.finditer(r"<!-- ppds-unreleased-fix pr=(\d+) -->", str(issue.get("body") or "")):
            found.add(int(match.group(1)))
    return found


# ---------------------------------------------------------------------------
# Decision
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


def evaluate(
    fixes: Sequence[Fix],
    *,
    targets: dict[str, int],
    now: datetime,
    recorded: set[int],
    unreleased_surfaces: Callable[[Fix], tuple[str, ...]],
) -> list[Decision]:
    decisions: list[Decision] = []
    for fix in fixes:
        surfaces = unreleased_surfaces(fix)
        if not surfaces:
            decisions.append(Decision(fix, False, "released or changes no release surface"))
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


def issue_title(decision: Decision) -> str:
    labels = " and ".join(sorted(decision.fix.labels & set(FIX_LABELS)))
    return f"Unreleased {labels} fix #{decision.fix.number}: {decision.fix.title}"


def issue_body(decision: Decision) -> str:
    fix = decision.fix
    surfaces = ", ".join(f"`{surface}`" for surface in decision.unreleased_surfaces)
    labels = ", ".join(f"`{label}`" for label in sorted(fix.labels & set(FIX_LABELS)))
    return f"""\
## Unreleased fix past its release target

#{fix.number} ({labels}) was merged {decision.age_days} days ago and is still not in a stable release of {surfaces}. The security policy's target for this kind of fix is {decision.target_days} days after merge.

| Field | Value |
|-------|-------|
| Fix | #{fix.number} {fix.title} |
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


def git_unreleased_surfaces(repo_root: Path, graph: ReleaseGraph) -> Callable[[Fix], tuple[str, ...]]:
    def unreleased(fix: Fix) -> tuple[str, ...]:
        paths = [
            line.strip()
            for line in _git(repo_root, "diff", "--name-only", "--no-renames", f"{fix.merge_oid}^", fix.merge_oid).splitlines()
            if line.strip()
        ]
        surfaces = sorted(_detect_direct_changes(graph, [FileChange(path=path) for path in paths]).direct_reasons)
        if not surfaces:
            return ()
        containing = _git(repo_root, "tag", "--contains", fix.merge_oid).split()
        return tuple(
            surface for surface in surfaces
            if stable_release_containing(surface, graph.surfaces[surface].tag_prefix, containing) is None
        )
    return unreleased


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("--merged-prs", type=Path, required=True)
    parser.add_argument("--existing-issues", type=Path, required=True)
    parser.add_argument("--security-policy", type=Path, required=True)
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--current-date", help="ISO-8601 date/time to treat as now (default: now, UTC)")
    parser.add_argument("--issue-dir", type=Path, help="write <pr>.title and <pr>.md for each issue to open")
    args = parser.parse_args(argv)

    try:
        targets = read_targets(args.security_policy.read_text(encoding="utf-8"))
        fixes = read_fixes(json.loads(args.merged_prs.read_text(encoding="utf-8")))
        recorded = recorded_prs(json.loads(args.existing_issues.read_text(encoding="utf-8")))
        now = parse_utc(args.current_date) if args.current_date else datetime.now(timezone.utc)
        repo_root = args.repo_root.resolve()
        decisions = evaluate(
            fixes, targets=targets, now=now, recorded=recorded,
            unreleased_surfaces=git_unreleased_surfaces(repo_root, ReleaseGraph.discover(repo_root)),
        )
    except TargetsMissing as error:
        print(f"::error title=Unreleased-fix check::{error}", file=sys.stderr)
        return 2
    except (OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
        print(f"::error title=Unreleased-fix check::could not read the input: {error}", file=sys.stderr)
        return 2

    if args.issue_dir:
        args.issue_dir.mkdir(parents=True, exist_ok=True)
        for decision in decisions:
            if decision.open_issue:
                (args.issue_dir / f"{decision.fix.number}.title").write_text(issue_title(decision), encoding="utf-8")
                (args.issue_dir / f"{decision.fix.number}.md").write_text(issue_body(decision), encoding="utf-8")
    print(json.dumps({
        "targets": targets,
        "decisions": [decision.as_dict() for decision in decisions],
        "issues_to_open": [decision.fix.number for decision in decisions if decision.open_issue],
    }, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
