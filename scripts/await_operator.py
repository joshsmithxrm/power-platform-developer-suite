#!/usr/bin/env python3
"""await-operator helper: pause a bg/headless worker for operator input.

Invoked by the `/await-operator` skill. Performs two writes atomically from
the worker's perspective:

  1. `.workflow/pending-ratification.json` in the worktree (the operator-
     facing artifact describing the question and the draft under review).
  2. `$CLAUDE_JOB_DIR/state.json` — flip `state` to `blocked` and populate
     `needs` so the supervisor's `BgHandle.poll` / `goal_supervisor._evaluate_entry`
     escalate on the next poll (see scripts/claude_dispatch.py L295-321 and
     scripts/goal_supervisor.py L502-522).

Idempotent: re-invoking overwrites pending-ratification.json and re-applies
the same state flip.

Usage:
  python scripts/await_operator.py \
      --artifact-path .plans/draft.md \
      --question "Approve the draft?" \
      --option "Approve" --option "Reject" --option "Revise"

Exits 0 on success, 1 on failure (e.g., CLAUDE_JOB_DIR not set / not a bg
session — the skill should not have been invoked from an interactive
session).
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path


PENDING_REL = ".workflow/pending-ratification.json"


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _worktree_root() -> Path:
    """Resolve the worktree root via git, falling back to CWD."""
    try:
        out = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True, text=True, timeout=5,
        )
        if out.returncode == 0 and out.stdout.strip():
            return Path(out.stdout.strip())
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass
    return Path(os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd()))


def _session_short_from_job_dir(job_dir: Path) -> str:
    """The daemon-side `short` is the basename of CLAUDE_JOB_DIR."""
    return job_dir.name


def _write_pending(
    worktree: Path,
    *,
    question: str,
    artifact_path: str,
    options: list[str],
    session_short: str,
) -> Path:
    payload = {
        "question": question,
        "artifact_path": artifact_path,
        "options": options,
        "created_at": _now_iso(),
        "session_short": session_short,
    }
    target = worktree / PENDING_REL
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    return target


def _flip_daemon_state(state_path: Path, needs: str) -> None:
    """Set state=blocked and needs=<needs> in the daemon state.json.

    Preserves all other fields. The daemon may rewrite the file between
    our read and write — this is best-effort; if the write races and gets
    clobbered, the operator can re-invoke the skill.
    """
    try:
        data = json.loads(state_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        data = {}
    data["state"] = "blocked"
    data["needs"] = needs
    data["tempo"] = "idle"
    data["updatedAt"] = _now_iso()
    state_path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(description="Pause worker for operator input.")
    p.add_argument("--artifact-path", required=True,
                   help="Path to the draft artifact the operator should review.")
    p.add_argument("--question", required=True,
                   help="The question for the operator (one sentence).")
    p.add_argument("--option", action="append", default=[],
                   help="Repeatable. Suggested choice label.")
    p.add_argument("--rationale", default="",
                   help="Optional short rationale prefix for the needs string.")
    args = p.parse_args(argv)

    job_dir_str = os.environ.get("CLAUDE_JOB_DIR", "").strip()
    if not job_dir_str:
        sys.stderr.write(
            "await_operator: CLAUDE_JOB_DIR is not set. /await-operator is "
            "only meaningful inside a bg or headless Claude session. In an "
            "interactive session, call AskUserQuestion instead.\n"
        )
        return 1
    job_dir = Path(job_dir_str)
    state_path = job_dir / "state.json"
    if not state_path.exists():
        sys.stderr.write(
            f"await_operator: daemon state.json not found at {state_path}.\n"
        )
        return 1

    worktree = _worktree_root()
    short = _session_short_from_job_dir(job_dir)

    pending = _write_pending(
        worktree,
        question=args.question,
        artifact_path=args.artifact_path,
        options=list(args.option),
        session_short=short,
    )

    rationale = args.rationale.strip() or args.question.strip()
    rationale = rationale.rstrip(";.")
    needs = f"{rationale}; review .workflow/pending-ratification.json"
    _flip_daemon_state(state_path, needs)

    sys.stdout.write(
        f"await_operator: paused session {short}\n"
        f"  pending: {pending}\n"
        f"  needs:   {needs}\n"
        f"  resume:  /resume-with-answer {short} <choice>\n"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
