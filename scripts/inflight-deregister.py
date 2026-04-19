#!/usr/bin/env python3
"""Deregister a session's in-flight entry.

Called when work completes (PR merged, branch deleted, session abandoned).
The ``/cleanup`` skill invokes this for each removed branch; ``/start``
invokes it on resume of an existing worktree.

Usage:
    python scripts/inflight-deregister.py --branch feat/something
    python scripts/inflight-deregister.py --session abc12345

Exactly one of ``--branch`` or ``--session`` is required. Exits 0 even
if no matching entry exists (deregister is idempotent).
"""
from __future__ import annotations

import argparse
import json
import sys

from inflight_common import locked_state, prune_stale, write_locked_state


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Deregister an in-flight session entry.")
    p.add_argument("--branch", help="Branch name to remove.")
    p.add_argument("--session", help="Session ID to remove.")
    return p.parse_args(argv)


def deregister(*, branch: str | None = None, session: str | None = None) -> list[dict]:
    """Remove matching entries; returns the removed entries (may be empty)."""
    if not branch and not session:
        raise ValueError("must supply --branch or --session")
    removed: list[dict] = []
    with locked_state() as (fp, state):
        prune_stale(state)
        kept: list[dict] = []
        for entry in state.get("open_work", []):
            match = (
                (branch and entry.get("branch") == branch)
                or (session and entry.get("session_id") == session)
            )
            if match:
                removed.append(entry)
            else:
                kept.append(entry)
        state["open_work"] = kept
        write_locked_state(fp, state)
    return removed


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if not args.branch and not args.session:
        print("error: --branch or --session required", file=sys.stderr)
        return 2
    removed = deregister(branch=args.branch, session=args.session)
    json.dump({"removed": removed}, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
