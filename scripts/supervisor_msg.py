#!/usr/bin/env python3
"""
Supervisor → worker messaging primitive (Option B: file-inbox protocol).

The orchestrator session cannot SendMessage to bg-spawned workers (different
process group, not in any agent team). This script provides the delivery channel:
the supervisor writes inbox files; each worker skill reads and consumes them at
phase transitions.

Inbox location: <worktree>/.workflow/inbox/<timestamp>_<kind>_<rand>.json
Atomic write:   tempfile in same directory + os.replace() (atomic on POSIX;
                near-atomic on Windows — same-volume rename avoids torn reads).

Usage
-----
  # Supervisor side — deliver a directive to a worker
  python scripts/supervisor_msg.py send /abs/path/to/worktree approve
  python scripts/supervisor_msg.py send /abs/path/to/worktree revise --message "Address the layout issue in AC-03"
  python scripts/supervisor_msg.py send /abs/path/to/worktree abort  --payload-file abort_reason.json
  python scripts/supervisor_msg.py send /abs/path/to/worktree note   --message "FYI: CI is slow today"

  # Worker side — consume inbox at each skill phase transition
  python scripts/supervisor_msg.py read              # read from CWD worktree, leave files
  python scripts/supervisor_msg.py read --consume    # read and delete after reading
  python scripts/supervisor_msg.py read --worktree /abs/path --consume

  # Inspection
  python scripts/supervisor_msg.py list [--worktree PATH]

Message kinds
-------------
  approve  — work is accepted; proceed to next phase
  revise   — apply the enclosed feedback before proceeding
  abort    — stop the current skill chain; surface to operator
  note     — informational; worker logs it but does not change state

Exit codes
----------
  0  — success (read may return empty list, that is still 0)
  1  — usage / argument error
  2  — worktree path does not exist
  3  — payload-file not found or invalid JSON
"""
from __future__ import annotations

import json
import os
import sys
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path

VALID_KINDS = ("approve", "revise", "abort", "note")


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _inbox_dir(worktree: Path) -> Path:
    return worktree / ".workflow" / "inbox"


def _resolve_worktree(path_str: str | None) -> Path:
    """Resolve the worktree path, falling back to git toplevel of CWD."""
    if path_str:
        p = Path(path_str).resolve()
    else:
        import subprocess
        try:
            result = subprocess.run(
                ["git", "-c", "core.quotePath=off", "rev-parse", "--show-toplevel"],
                capture_output=True,
                encoding="utf-8",
                errors="replace",
                timeout=5,
            )
            if result.returncode == 0:
                p = Path(result.stdout.strip())
            else:
                p = Path.cwd()
        except (subprocess.TimeoutExpired, FileNotFoundError):
            p = Path.cwd()
    return p


# ---------------------------------------------------------------------------
# Core operations
# ---------------------------------------------------------------------------

def send(worktree_path: str, kind: str, *,
         message: str | None = None,
         payload: dict | None = None) -> Path:
    """Write one inbox message atomically; return the path created."""
    if kind not in VALID_KINDS:
        raise ValueError(f"Invalid kind {kind!r}. Must be one of: {', '.join(VALID_KINDS)}")

    wt = Path(worktree_path).resolve()
    if not wt.exists():
        raise FileNotFoundError(f"Worktree path does not exist: {wt}")

    inbox = _inbox_dir(wt)
    inbox.mkdir(parents=True, exist_ok=True)

    now = datetime.now(timezone.utc)
    ts = now.strftime("%Y%m%dT%H%M%S%fZ")
    rand = uuid.uuid4().hex[:6]
    filename = f"{ts}_{kind}_{rand}.json"
    target = inbox / filename

    msg = {
        "kind": kind,
        "sent_at": now.isoformat(),
    }
    if message is not None:
        msg["message"] = message
    if payload is not None:
        msg["payload"] = payload

    # Atomic write: write to a temp file in the same directory, then rename.
    # os.replace() is atomic on POSIX; on Windows it is best-effort (same volume).
    tmp_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8",
            dir=inbox, suffix=".tmp", delete=False,
        ) as tf:
            tmp_path = Path(tf.name)
            json.dump(msg, tf, indent=2)
            tf.write("\n")
        os.replace(tmp_path, target)
        tmp_path = None
    finally:
        if tmp_path is not None and tmp_path.exists():
            try:
                tmp_path.unlink()
            except OSError:
                pass
    return target


def read_inbox(worktree_path: str | None = None, *,
               consume: bool = False) -> list[dict]:
    """Read all inbox messages; if consume=True, delete files after reading."""
    wt = _resolve_worktree(worktree_path)
    inbox = _inbox_dir(wt)

    if not inbox.exists():
        return []

    files = sorted(inbox.glob("*.json"))
    messages = []
    for f in files:
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
            data["_file"] = str(f)
            messages.append(data)
        except (json.JSONDecodeError, OSError):
            continue
        if consume:
            try:
                f.unlink()
            except OSError:
                pass

    return messages


def list_inbox(worktree_path: str | None = None) -> list[str]:
    """Return sorted list of inbox file paths (no content read)."""
    wt = _resolve_worktree(worktree_path)
    inbox = _inbox_dir(wt)
    if not inbox.exists():
        return []
    return [str(f) for f in sorted(inbox.glob("*.json"))]


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _die(msg: str, code: int = 1) -> None:
    print(f"ERROR: {msg}", file=sys.stderr)
    sys.exit(code)


def _usage() -> None:
    print(__doc__, file=sys.stderr)
    sys.exit(1)


def main(argv: list[str] | None = None) -> None:
    args = (argv if argv is not None else sys.argv)[1:]

    if not args:
        _usage()

    cmd = args[0]

    if cmd == "send":
        # send <worktree> <kind> [--message TEXT] [--payload-file FILE]
        positional = []
        message = None
        payload_file = None
        i = 1
        while i < len(args):
            a = args[i]
            if a == "--message" and i + 1 < len(args):
                message = args[i + 1]; i += 2
            elif a.startswith("--message="):
                message = a.split("=", 1)[1]; i += 1
            elif a == "--payload-file" and i + 1 < len(args):
                payload_file = args[i + 1]; i += 2
            elif a.startswith("--payload-file="):
                payload_file = a.split("=", 1)[1]; i += 1
            else:
                positional.append(a); i += 1

        if len(positional) < 2:
            _die("send requires <worktree> <kind>")

        worktree, kind = positional[0], positional[1]

        payload = None
        if payload_file:
            pf = Path(payload_file)
            if not pf.exists():
                _die(f"Payload file not found: {payload_file}", code=3)
            try:
                payload = json.loads(pf.read_text(encoding="utf-8"))
            except json.JSONDecodeError as exc:
                _die(f"Payload file is not valid JSON: {exc}", code=3)

        try:
            target = send(worktree, kind, message=message, payload=payload)
        except FileNotFoundError as exc:
            _die(str(exc), code=2)
        except ValueError as exc:
            _die(str(exc))

        print(str(target))
        sys.exit(0)

    elif cmd == "read":
        worktree = None
        consume = False
        i = 1
        while i < len(args):
            a = args[i]
            if a == "--consume":
                consume = True; i += 1
            elif a == "--worktree" and i + 1 < len(args):
                worktree = args[i + 1]; i += 2
            elif a.startswith("--worktree="):
                worktree = a.split("=", 1)[1]; i += 1
            else:
                _die(f"Unknown option for read: {a}")

        messages = read_inbox(worktree, consume=consume)
        print(json.dumps(messages, indent=2))
        sys.exit(0)

    elif cmd == "list":
        worktree = None
        i = 1
        while i < len(args):
            a = args[i]
            if a == "--worktree" and i + 1 < len(args):
                worktree = args[i + 1]; i += 2
            elif a.startswith("--worktree="):
                worktree = a.split("=", 1)[1]; i += 1
            else:
                _die(f"Unknown option for list: {a}")

        files = list_inbox(worktree)
        for f in files:
            print(f)
        sys.exit(0)

    else:
        _die(f"Unknown command: {cmd!r}. Expected: send, read, list")


if __name__ == "__main__":
    main()
