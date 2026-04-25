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

Exit codes
----------
Always 0 — the caller reads the JSON ``should_open_issue`` field to decide
whether to create an issue.  A non-zero exit would prevent ``$()`` capture
in bash; we surface errors via stderr and still exit 0 so the workflow can
fall through to the JSON parsing step gracefully.
"""
from __future__ import annotations

import argparse
import json
import sys
from datetime import date, datetime
from typing import Optional


# ---------------------------------------------------------------------------
# Core decision logic
# ---------------------------------------------------------------------------

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
    weeks_since_release = (current_date - last_release_date).days // 7

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

- **Release now** — run `/release` to cut a patch or minor release
- **Defer with reason** — comment on this issue explaining the deferral and close it; a new issue will open next week if the condition persists
- **Close as not-needed** — if the unreleased commits are housekeeping/docs that don't warrant a release, close this issue with a note

## Checklist

- [ ] Review unreleased commits since `{last_release_tag}` — are any user-facing?
- [ ] Decide: release now, defer, or close as not-needed
- [ ] If releasing: run `/release` (see the skill for the full ceremony)
- [ ] Close this issue once the decision is actioned
"""


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Evaluate whether a release cadence check-in issue should be opened. "
            "Outputs a JSON object to stdout; always exits 0."
        ),
    )
    parser.add_argument(
        "--last-release-date",
        required=True,
        help="ISO date string (YYYY-MM-DD) of the most recent release tag.",
    )
    parser.add_argument(
        "--current-date",
        default=None,
        help=(
            "ISO date string (YYYY-MM-DD) to treat as today. "
            "Defaults to the actual current date."
        ),
    )
    parser.add_argument(
        "--unreleased-commits",
        type=int,
        required=True,
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

    try:
        last_release_date = datetime.fromisoformat(args.last_release_date)
    except ValueError as exc:
        print(f"error: --last-release-date: {exc}", file=sys.stderr)
        return 0  # still exit 0; JSON won't be valid but workflow reads stderr

    if args.current_date is not None:
        try:
            current_date = datetime.fromisoformat(args.current_date)
        except ValueError as exc:
            print(f"error: --current-date: {exc}", file=sys.stderr)
            return 0
    else:
        current_date = datetime.combine(date.today(), datetime.min.time())

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
