#!/usr/bin/env python3
"""Check whether another active session conflicts with proposed work.

Called BEFORE filing a new issue (``/backlog`` skill) or starting work
(``/start`` skill) to prevent the duplicate-work pattern that produced
issue #802 (session A filed a bug for a feature that session B had
already shipped 5h earlier in another worktree).

Usage:
    python scripts/inflight-check.py --area src/PPDS.Cli/Plugins/
    python scripts/inflight-check.py --issue 802
    python scripts/inflight-check.py --area path/ --issue 5 --session self-id

Exit codes:
    0  no conflict (still prints empty conflict report on stdout)
    1  one or more conflicting sessions (full report on stdout as JSON)
    2  bad arguments

The caller (skill) is expected to surface the conflict to the operator
with an actionable prompt: "session ce9a2a05 is already working on
this area — coordinate before proceeding."
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone

from inflight_common import find_conflicts, locked_state, prune_stale, write_locked_state

STALE_TTL_DAYS = 7


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Check for sibling sessions claiming overlapping work."
    )
    p.add_argument("--area", help="Code area path to check (e.g., src/PPDS.Cli/).")
    p.add_argument("--issue", type=int, help="Issue number to check.")
    p.add_argument(
        "--session",
        help=(
            "Session ID to exclude (so a session checking before its own "
            "/start does not collide with itself if it pre-registered)."
        ),
    )
    p.add_argument(
        "--no-prune", action="store_true",
        help="Skip the stale-entry sweep (useful for unit tests).",
    )
    return p.parse_args(argv)


def check(*, area: str | None = None, issue: int | None = None,
          exclude_session: str | None = None,
          do_prune: bool = True) -> list[dict]:
    """Return list of conflicting entries; empty list means no conflict."""
    with locked_state() as (fp, state):
        if do_prune:
            pruned = prune_stale(state)
            if pruned:
                write_locked_state(fp, state)
        return find_conflicts(
            state, area=area, issue=issue, exclude_session=exclude_session
        )


def _live_worktree_branches() -> set[str]:
    """Branches that currently have a checked-out worktree (`git worktree list`)."""
    try:
        result = subprocess.run(
            ["git", "worktree", "list", "--porcelain"],
            capture_output=True, text=True, timeout=10,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return set()
    if result.returncode != 0:
        return set()
    branches: set[str] = set()
    for line in (result.stdout or "").splitlines():
        if line.startswith("branch "):
            ref = line.split(None, 1)[1].strip()
            branches.add(ref.removeprefix("refs/heads/"))
    return branches


def _is_stale(entry: dict, live_branches: set[str]) -> bool:
    """AC-180: registered > 7 days ago AND no live worktree on the branch."""
    started = entry.get("started")
    if not started:
        return False
    try:
        ts = datetime.fromisoformat(started.replace("Z", "+00:00"))
    except ValueError:
        return False
    age = (datetime.now(timezone.utc) - ts).total_seconds()
    if age <= STALE_TTL_DAYS * 86400:
        return False
    branch = entry.get("branch", "")
    return branch and branch not in live_branches


def annotate_stale(conflicts: list[dict]) -> list[dict]:
    """Append ' [stale]' to the printable summary of stale entries.
    Informational only — does not change exit code."""
    if not conflicts:
        return conflicts
    live = _live_worktree_branches()
    annotated: list[dict] = []
    for entry in conflicts:
        copy = dict(entry)
        if _is_stale(copy, live):
            copy["stale"] = True
        annotated.append(copy)
    return annotated


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if args.area is None and args.issue is None:
        print("error: at least one of --area or --issue required", file=sys.stderr)
        return 2
    conflicts = check(
        area=args.area,
        issue=args.issue,
        exclude_session=args.session,
        do_prune=not args.no_prune,
    )
    conflicts = annotate_stale(conflicts)
    payload = {
        "area": args.area,
        "issue": args.issue,
        "conflicts": conflicts,
    }
    json.dump(payload, sys.stdout, indent=2)
    sys.stdout.write("\n")
    # Emit a one-line annotated form for human-readable inspection (does not
    # change exit code; informational per AC-180).
    for entry in conflicts:
        suffix = " [stale]" if entry.get("stale") else ""
        line = f"  {entry.get('session_id', '?')[:8]} {entry.get('branch', '?')}{suffix}"
        print(line, file=sys.stderr)
    return 1 if conflicts else 0


if __name__ == "__main__":
    sys.exit(main())
