#!/usr/bin/env python3
"""Informational PR size report for the pre-merge gate.

Reports how large a PR is (changed files, additions, deletions) in the job log
and, when running in GitHub Actions, in the step summary. It NEVER blocks a
merge: the exit code is 0 whatever the size, and whatever happens while reading
the PR. A failure to read the PR is surfaced as a ``::warning::`` annotation
plus a note in the summary, not as a failing job.

A PR is labelled "large" in the report when it exceeds either reference
threshold (more than 50 changed files, or more than 2000 additions+deletions).
The label is advice to reviewers only.

Usage:
    python scripts/ci/check_pr_size.py --pr 123
    python scripts/ci/check_pr_size.py --json '{"additions": 10, ...}'

Exit codes:
    0 — always (invalid command-line usage is rejected by argparse with 2)
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from typing import Optional

# Reference thresholds used only to label a PR "large" in the report.
LARGE_FILES = 50
LARGE_LOC = 2000

INFORMATIONAL = "Informational only; PR size does not block merge."


def fetch_pr_json(pr_number: int) -> dict:
    """Fetch PR data via `gh pr view --json`.

    Kept thin so tests can monkeypatch it.
    """
    cmd = [
        "gh", "pr", "view", str(pr_number),
        "--json", "additions,deletions,changedFiles",
    ]
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, check=True)
    except FileNotFoundError as e:
        raise RuntimeError(f"gh CLI not available: {e}") from e
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"gh pr view failed: {e.stderr.strip()}") from e
    return json.loads(out.stdout)


def describe_size(pr: dict) -> str:
    """Return a one-line size report for `pr` (keys: additions, deletions,
    changedFiles). Never raises on bad data."""
    try:
        additions = int(pr.get("additions") or 0)
        deletions = int(pr.get("deletions") or 0)
        files = int(pr.get("changedFiles") or 0)
    except (TypeError, ValueError, AttributeError) as e:
        return f"PR size: could not read size numbers ({e}). {INFORMATIONAL}"

    loc = additions + deletions
    large = files > LARGE_FILES or loc > LARGE_LOC
    label = "large PR" if large else "PR size"
    note = (
        f" Consider whether reviewers can review it well (reference: more than "
        f"{LARGE_FILES} files or {LARGE_LOC} LoC)."
        if large else ""
    )
    return (
        f"{label}: {files} files, {loc} LoC "
        f"({additions} added, {deletions} deleted). {INFORMATIONAL}{note}"
    )


def write_summary(text: str) -> None:
    """Append `text` to the GitHub step summary if available. Never raises."""
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    try:
        with open(path, "a", encoding="utf-8") as f:
            f.write(text + "\n")
    except OSError as e:
        print(f"::warning::could not write step summary: {e}")


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Informational PR size report (never blocks).",
    )
    src = parser.add_mutually_exclusive_group(required=True)
    src.add_argument("--pr", type=int, help="PR number to check (uses gh pr view)")
    src.add_argument("--json", type=str, help="PR JSON blob (for testing / piped use)")
    args = parser.parse_args(argv)

    try:
        if args.pr is not None:
            pr = fetch_pr_json(args.pr)
        else:
            pr = json.loads(args.json)
    except (RuntimeError, json.JSONDecodeError) as e:
        message = f"PR size could not be read: {e}. {INFORMATIONAL}"
        print(f"::warning::{message}")
        write_summary(f"### PR size\n\n{message}")
        return 0

    message = describe_size(pr)
    print(message)
    write_summary(f"### PR size\n\n{message}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
