#!/usr/bin/env python3
"""Milestone completion detection for the PPDS release cycle.

When a GitHub Milestone is closed, this script determines whether a release
issue should be opened.  It is invoked by ``milestone-release-check.yml`` and
is designed to be fully testable without GitHub Actions.

Public API (called by the workflow and by unit tests):
    should_open_release_issue(merged_prs) -> bool
    build_issue_body(milestone_title, milestone_description, merged_prs, deferred_count) -> str

CLI usage:
    python scripts/ci/check_milestone_completion.py \\
        --milestone "v1.1.0" \\
        --description "First minor release" \\
        --merged-prs merged_prs.json \\
        [--deferred-count 2] \\
        [--out issue_body.md]

Exit codes:
    0 — success (issue body written to stdout/file, or nothing written if no issue needed)
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Optional


def should_open_release_issue(merged_prs: list[dict]) -> bool:
    """Return True if a release issue should be opened.

    An issue is warranted only when the milestone contained at least one
    merged PR.  A milestone closed with zero merged PRs was likely deferred
    entirely; no release action is needed.

    Args:
        merged_prs: List of dicts with at least ``number`` and ``title`` keys.

    Returns:
        True if len(merged_prs) > 0, else False.
    """
    return len(merged_prs) > 0


def build_issue_body(
    milestone_title: str,
    milestone_description: str,
    merged_prs: list[dict],
    deferred_count: int = 0,
) -> str:
    """Build the markdown body for the GitHub release readiness issue.

    Args:
        milestone_title: The milestone name, e.g. "v1.1.0".
        milestone_description: The milestone description text (may be empty).
        merged_prs: List of dicts with ``number`` and ``title`` keys.
        deferred_count: Number of issues/PRs that were deferred (moved out).

    Returns:
        A markdown string suitable for use as a GitHub issue body.
    """
    lines: list[str] = []

    lines.append(f"## Milestone {milestone_title} — Release Readiness")
    lines.append("")

    if milestone_description:
        lines.append(milestone_description)
        lines.append("")

    lines.append(f"### Merged PRs ({len(merged_prs)})")
    lines.append("")
    for pr in merged_prs:
        number = pr.get("number", "?")
        title = pr.get("title", "")
        lines.append(f"- #{number} — {title}")
    lines.append("")

    if deferred_count > 0:
        lines.append(f"**Deferred:** {deferred_count} item(s) moved to the next milestone.")
        lines.append("")

    lines.append("### Release Checklist")
    lines.append("")
    lines.append("- [ ] Run `/release` ceremony (CHANGELOGs, version bumps, tag push)")
    lines.append("- [ ] Verify all packages published successfully")
    lines.append("- [ ] Close this milestone")
    lines.append("")

    return "\n".join(lines)


def main(argv: Optional[list[str]] = None) -> int:
    """Entry point.

    Reads merged PRs from the JSON file, decides whether to open an issue,
    and if so writes the issue body to stdout (default) or to ``--out``.
    """
    parser = argparse.ArgumentParser(
        description="Decide whether to open a release issue after milestone closure.",
    )
    parser.add_argument(
        "--milestone",
        required=True,
        help="Milestone title, e.g. 'v1.1.0'",
    )
    parser.add_argument(
        "--description",
        default="",
        help="Milestone description text",
    )
    parser.add_argument(
        "--merged-prs",
        required=True,
        dest="merged_prs",
        help="Path to JSON file containing merged PRs: [{\"number\": N, \"title\": \"...\"}]",
    )
    parser.add_argument(
        "--deferred-count",
        type=int,
        default=0,
        dest="deferred_count",
        help="Number of items deferred to the next milestone (default: 0)",
    )
    parser.add_argument(
        "--out",
        default=None,
        help="Write issue body to this file instead of stdout",
    )

    args = parser.parse_args(argv)

    try:
        prs_path = Path(args.merged_prs)
        raw = prs_path.read_text(encoding="utf-8")
        merged_prs: list[dict] = json.loads(raw) if raw.strip() else []
    except (OSError, json.JSONDecodeError) as exc:
        print(f"error: could not read merged PRs from {args.merged_prs!r}: {exc}", file=sys.stderr)
        return 1

    if not should_open_release_issue(merged_prs):
        # Milestone closed with no merged PRs — nothing to release.
        return 0

    body = build_issue_body(
        milestone_title=args.milestone,
        milestone_description=args.description,
        merged_prs=merged_prs,
        deferred_count=args.deferred_count,
    )

    if args.out:
        Path(args.out).write_text(body, encoding="utf-8")
    else:
        print(body, end="")

    return 0


if __name__ == "__main__":
    sys.exit(main())
