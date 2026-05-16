#!/usr/bin/env python3
"""resume-with-answer helper: deliver operator's choice to a paused worker.

Operator-side companion to scripts/await_operator.py. Two writes:

  1. `.workflow/operator-answer.json` in the target worktree — the answer
     the resumed worker reads on its next turn.
  2. `~/.claude/jobs/<short>/state.json` — clear `state` back to `working`
     and blank `needs`, so the supervisor's poll stops treating the
     session as escalated.

Usage:
  python scripts/resume_with_answer.py <short> <choice> \
      [--operator <handle>] [--worktree <path>]

`<choice>` is the freeform answer (label string). `--worktree` defaults to
the worktree recorded in the daemon state.json `cwd` field; pass it
explicitly when the daemon state is stale.

Exits 0 on success, 1 on bad arguments / missing state files.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path


PENDING_REL = ".workflow/pending-ratification.json"
ANSWER_REL = ".workflow/operator-answer.json"


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _jobs_dir() -> Path:
    """Resolve the Claude jobs directory (~/.claude/jobs)."""
    home = Path(os.environ.get("USERPROFILE") or os.path.expanduser("~"))
    return home / ".claude" / "jobs"


def _resolve_worktree(state_path: Path, override: str | None) -> Path | None:
    if override:
        return Path(override)
    try:
        data = json.loads(state_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    cwd = data.get("cwd")
    if not cwd:
        return None
    return Path(cwd)


def _write_answer(worktree: Path, *, choice: str, operator: str) -> Path:
    target = worktree / ANSWER_REL
    target.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "choice": choice,
        "answered_at": _now_iso(),
        "operator": operator,
    }
    target.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    return target


def _clear_blocked(state_path: Path) -> None:
    try:
        data = json.loads(state_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        data = {}
    data["state"] = "working"
    data["needs"] = ""
    data["tempo"] = "active"
    data["updatedAt"] = _now_iso()
    state_path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(description="Deliver operator answer.")
    p.add_argument("short", help="8-char daemon session short.")
    p.add_argument("choice", help="Freeform answer (label or sentence).")
    p.add_argument("--operator", default=os.environ.get("USER")
                   or os.environ.get("USERNAME") or "operator",
                   help="Operator handle to stamp on the answer.")
    p.add_argument("--worktree", default=None,
                   help="Override worktree path (default: read from daemon state).")
    args = p.parse_args(argv)

    state_path = _jobs_dir() / args.short / "state.json"
    if not state_path.exists():
        sys.stderr.write(
            f"resume_with_answer: no daemon state at {state_path}.\n"
            f"  Check the short ({args.short!r}) and that the session "
            f"exists in `claude` Agent View.\n"
        )
        return 1

    worktree = _resolve_worktree(state_path, args.worktree)
    if worktree is None or not worktree.exists():
        sys.stderr.write(
            f"resume_with_answer: cannot resolve worktree (state cwd missing "
            f"or stale). Pass --worktree explicitly.\n"
        )
        return 1

    pending = worktree / PENDING_REL
    if not pending.exists():
        sys.stderr.write(
            f"resume_with_answer: warning — no pending-ratification.json at "
            f"{pending}. The worker may not actually be paused via "
            f"/await-operator. Continuing anyway.\n"
        )

    answer = _write_answer(worktree, choice=args.choice, operator=args.operator)
    _clear_blocked(state_path)

    sys.stdout.write(
        f"resume_with_answer: answer delivered to {args.short}\n"
        f"  answer:   {answer}\n"
        f"  worktree: {worktree}\n"
        f"  state:    cleared (state=working, needs='')\n"
        f"  next:     claude attach {args.short}  # or let supervisor re-poll\n"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
