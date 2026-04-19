#!/usr/bin/env python3
"""Register a session as actively working on issues / code areas.

Called by the ``/start`` skill after worktree creation. Writes an entry
to ``.claude/state/in-flight-issues.json`` so other concurrent sessions
can detect overlap before filing duplicate issues or starting parallel
work in the same area.

Usage:
    python scripts/inflight-register.py \\
        --session abc12345 \\
        --branch feat/something \\
        --worktree .worktrees/something \\
        --issue 801 --issue 802 \\
        --area src/PPDS.Cli/Plugins/ \\
        --intent "audit-capture pipeline implementation"

If ``--session`` is omitted, a random 8-char hex ID is generated. If an
entry with the same ``branch`` already exists it is replaced (idempotent
re-register on resume).
"""
from __future__ import annotations

import argparse
import json
import secrets
import sys

from inflight_common import (
    locked_state,
    now_utc_iso,
    prune_stale,
    write_locked_state,
)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Register an in-flight session entry.")
    p.add_argument("--session", help="Session ID (8 hex chars). Random if omitted.")
    p.add_argument("--branch", required=True, help="Branch name being worked on.")
    p.add_argument("--worktree", default="", help="Worktree path (relative to repo root).")
    p.add_argument(
        "--issue", action="append", type=int, default=[],
        help="Issue number(s) being worked on (repeatable).",
    )
    p.add_argument(
        "--area", action="append", default=[],
        help=(
            "Code area path(s) (repeatable, or comma-separated). "
            "Used by inflight-check to detect overlap with sibling sessions."
        ),
    )
    p.add_argument("--intent", default="", help="Short human-readable description.")
    return p.parse_args(argv)


def _flatten_areas(raw: list[str]) -> list[str]:
    out: list[str] = []
    for item in raw:
        for chunk in (item or "").split(","):
            chunk = chunk.strip()
            if chunk:
                out.append(chunk)
    # de-dup, preserve order
    seen: set[str] = set()
    return [a for a in out if not (a in seen or seen.add(a))]


def register(args: argparse.Namespace) -> dict:
    session_id = args.session or secrets.token_hex(4)
    entry = {
        "session_id": session_id,
        "started": now_utc_iso(),
        "branch": args.branch,
        "worktree": args.worktree,
        "issues": list(args.issue),
        "areas": _flatten_areas(args.area),
        "intent": args.intent,
    }
    with locked_state() as (fp, state):
        prune_stale(state)
        # Replace any existing entry on the same branch (idempotent re-register).
        state["open_work"] = [
            e for e in state.get("open_work", [])
            if e.get("branch") != args.branch
        ]
        state["open_work"].append(entry)
        write_locked_state(fp, state)
    return entry


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    entry = register(args)
    json.dump(entry, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
