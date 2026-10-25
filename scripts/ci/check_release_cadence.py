#!/usr/bin/env python3
"""Phase 4 — cadence floor workflow helper.

Encapsulates the release cadence decision so that the GitHub Actions workflow
``release-cadence-check.yml`` can call this script and parse its JSON output,
and so that unit tests can exercise the decision logic without touching git,
GitHub Actions, or the ``gh`` CLI.

Public API
----------
evaluate_cadence(...)  — pure function, returns a decision dict
build_issue_title(...)  — formats the check-in issue title
build_issue_body(...)   — formats the check-in issue body (Markdown)
main(argv=None)         — argparse entry point; prints JSON to stdout

Usage (workflow)
----------------
    python scripts/ci/check_release_cadence.py \\
        --last-release-date 2026-01-01 \\
        --current-date      2026-04-24 \\
        --unreleased-commits 12 \\
        --last-release-tag   Cli-v1.0.0

    # Optionally add --has-open-check-in-issue if an open issue already exists.

Timestamps are normalized to UTC before subtraction. Date-only and otherwise
timezone-naive inputs are interpreted as UTC for backward compatibility; git's
production ``creatordate:iso-strict`` offset is preserved and converted.

Invalid inputs return non-zero so a broken cadence check cannot silently skip.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from release_model import ReleaseGraph, SemVer


PUBLIC_RELEASE_RUNBOOK = (
    "https://github.com/joshsmithxrm/power-platform-developer-suite/"
    "blob/main/docs/RELEASE.md"
)


# ---------------------------------------------------------------------------
# Core decision logic
# ---------------------------------------------------------------------------

def as_utc(value: datetime) -> datetime:
    """Normalize a datetime to UTC, treating a missing offset as UTC."""
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def parse_utc_timestamp(value: str) -> datetime:
    """Parse an ISO-8601 date/timestamp into an aware UTC datetime."""
    normalized = value[:-1] + "+00:00" if value.endswith(("Z", "z")) else value
    return as_utc(datetime.fromisoformat(normalized))


@dataclass(frozen=True)
class ReleaseTagWithDate:
    name: str
    created_at: datetime


def select_latest_cadence_release(
    tags: list[ReleaseTagWithDate],
    tag_prefixes: set[str],
) -> dict:
    """Select the most recently created strict release tag.

    Package versions are independent, so cadence ranks valid releases by UTC
    creation time rather than comparing versions across package lines. SemVer
    is still validated through the shared strict implementation.
    """
    valid: list[ReleaseTagWithDate] = []
    diagnostics: list[str] = []
    for tag in tags:
        prefix = next(
            (
                candidate for candidate in sorted(tag_prefixes, key=len, reverse=True)
                if tag.name.startswith(candidate)
            ),
            None,
        )
        if prefix is None:
            continue
        try:
            SemVer.parse(tag.name[len(prefix):])
        except ValueError as exc:
            diagnostics.append(f"Malformed release tag {tag.name!r}: {exc}")
            continue
        valid.append(tag)

    selected = max(
        valid,
        key=lambda tag: (as_utc(tag.created_at), tag.name),
        default=None,
    )
    return {
        "latest_tag": selected.name if selected else None,
        "latest_tag_date": (
            as_utc(selected.created_at).isoformat() if selected else None
        ),
        "diagnostics": sorted(diagnostics),
    }


def collect_release_tags() -> list[ReleaseTagWithDate]:
    result = subprocess.run(
        [
            "git",
            "for-each-ref",
            "--format=%(refname:short)%09%(creatordate:iso-strict)",
            "refs/tags/",
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"git for-each-ref failed (exit {result.returncode}): "
            f"{result.stderr.strip()}"
        )
    tags: list[ReleaseTagWithDate] = []
    for line in result.stdout.splitlines():
        name, separator, raw_date = line.partition("\t")
        if not separator:
            continue
        try:
            created_at = parse_utc_timestamp(raw_date.strip())
        except ValueError:
            continue
        tags.append(ReleaseTagWithDate(name=name.strip(), created_at=created_at))
    return tags


def discover_release_prefixes(repo_root: Path) -> set[str]:
    graph = ReleaseGraph.discover(repo_root)
    return {surface.tag_prefix for surface in graph.surfaces.values()} | {"v"}

def evaluate_cadence(
    *,
    last_release_date: datetime,
    current_date: datetime,
    unreleased_commits: int,
    has_open_check_in_issue: bool,
    threshold_weeks: int = 8,
) -> dict:
    """Decide whether a cadence check-in issue should be opened.

    Parameters
    ----------
    last_release_date:
        The date/datetime of the most recent release tag.
    current_date:
        The date/datetime to treat as "now" (injectable for tests).
    unreleased_commits:
        Number of commits on main since the last release tag.
    has_open_check_in_issue:
        True if a GitHub issue with the ``release:cadence-check`` label is
        already open — prevents duplicates.
    threshold_weeks:
        Minimum number of *complete* weeks that must have elapsed before the
        check-in issue is opened (exclusive: > threshold, not >=).

    Returns
    -------
    dict with keys:
        should_open_issue   bool
        reason              str  ("duplicate" | "recent release" |
                                  "no unreleased commits" | "overdue")
        weeks_since_release int  (floor of days / 7)
        unreleased_commits  int  (echoed from input)
    """
    weeks_since_release = (
        as_utc(current_date) - as_utc(last_release_date)
    ).days // 7

    if has_open_check_in_issue:
        return {
            "should_open_issue": False,
            "reason": "duplicate",
            "weeks_since_release": weeks_since_release,
            "unreleased_commits": unreleased_commits,
        }

    if weeks_since_release <= threshold_weeks:
        return {
            "should_open_issue": False,
            "reason": "recent release",
            "weeks_since_release": weeks_since_release,
            "unreleased_commits": unreleased_commits,
        }

    if unreleased_commits == 0:
        return {
            "should_open_issue": False,
            "reason": "no unreleased commits",
            "weeks_since_release": weeks_since_release,
            "unreleased_commits": unreleased_commits,
        }

    return {
        "should_open_issue": True,
        "reason": "overdue",
        "weeks_since_release": weeks_since_release,
        "unreleased_commits": unreleased_commits,
    }


# ---------------------------------------------------------------------------
# Issue formatting helpers
# ---------------------------------------------------------------------------

def build_issue_title(weeks: int, commits: int) -> str:
    """Return the GitHub issue title for a cadence check-in issue."""
    return (
        f"Release check-in: {commits} commits unreleased, "
        f"{weeks} weeks since last release"
    )


def build_issue_body(weeks: int, commits: int, last_release_tag: str) -> str:
    """Return the GitHub issue body (Markdown) for a cadence check-in issue."""
    return f"""\
## Release Check-in

The release cadence floor has been triggered: no release has shipped in the last **{weeks} weeks**, and there are **{commits} unreleased commits** on `main` since the last release tag.

| Field | Value |
|-------|-------|
| Weeks since last release | {weeks} |
| Unreleased commits on main | {commits} |
| Last release tag | `{last_release_tag}` |

## Options for the Maintainer

- **Release now** — follow the public [release procedure]({PUBLIC_RELEASE_RUNBOOK}) to cut a patch or minor release
- **Defer with reason** — comment on this issue explaining the deferral and close it; a new issue will open next week if the condition persists
- **Close as not-needed** — if the unreleased commits are housekeeping/docs that don't warrant a release, close this issue with a note

## Checklist

- [ ] Review unreleased commits since `{last_release_tag}` — are any user-facing?
- [ ] Decide: release now, defer, or close as not-needed
- [ ] If releasing: follow the public [release procedure]({PUBLIC_RELEASE_RUNBOOK})
- [ ] Close this issue once the decision is actioned
"""


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Evaluate whether a release cadence check-in issue should be opened. "
            "Outputs a JSON object to stdout and fails on invalid input."
        ),
    )
    parser.add_argument(
        "--find-latest-release",
        action="store_true",
        help="Discover the most recent strict release tag and print JSON.",
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path.cwd(),
        help="Repository root used to discover release tag prefixes.",
    )
    parser.add_argument(
        "--last-release-date",
        required=False,
        help="ISO-8601 date/timestamp of the most recent release tag.",
    )
    parser.add_argument(
        "--current-date",
        default=None,
        help=(
            "ISO-8601 date/timestamp to treat as now. "
            "Defaults to the current UTC time."
        ),
    )
    parser.add_argument(
        "--unreleased-commits",
        type=int,
        required=False,
        help="Number of commits on main since the last release tag.",
    )
    parser.add_argument(
        "--has-open-check-in-issue",
        action="store_true",
        default=False,
        help="Pass this flag if an open release:cadence-check issue already exists.",
    )
    parser.add_argument(
        "--last-release-tag",
        default="",
        help="Tag name of the most recent release (used in issue body if opened).",
    )
    parser.add_argument(
        "--format",
        choices=["json", "title", "body"],
        default="json",
        help=(
            "Output format. 'json' (default) emits the evaluate_cadence result. "
            "'title' emits the issue title string. 'body' emits the issue body markdown. "
            "The 'title' and 'body' modes are used by the workflow to build gh issue create args."
        ),
    )

    args = parser.parse_args(argv)

    if args.find_latest_release:
        selection = select_latest_cadence_release(
            collect_release_tags(),
            discover_release_prefixes(args.repo_root),
        )
        print(json.dumps(selection, sort_keys=True))
        return 0

    if args.last_release_date is None:
        parser.error("--last-release-date is required unless --find-latest-release is used")
    if args.unreleased_commits is None:
        parser.error("--unreleased-commits is required unless --find-latest-release is used")

    try:
        last_release_date = parse_utc_timestamp(args.last_release_date)
    except ValueError as exc:
        print(f"error: --last-release-date: {exc}", file=sys.stderr)
        return 2

    if args.current_date is not None:
        try:
            current_date = parse_utc_timestamp(args.current_date)
        except ValueError as exc:
            print(f"error: --current-date: {exc}", file=sys.stderr)
            return 2
    else:
        current_date = datetime.now(timezone.utc)

    result = evaluate_cadence(
        last_release_date=last_release_date,
        current_date=current_date,
        unreleased_commits=args.unreleased_commits,
        has_open_check_in_issue=args.has_open_check_in_issue,
    )

    if args.format == "title":
        print(build_issue_title(
            result["weeks_since_release"],
            result["unreleased_commits"],
        ))
    elif args.format == "body":
        print(build_issue_body(
            result["weeks_since_release"],
            result["unreleased_commits"],
            args.last_release_tag,
        ))
    else:
        print(json.dumps(result))

    return 0


if __name__ == "__main__":
    sys.exit(main())
