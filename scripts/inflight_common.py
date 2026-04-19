#!/usr/bin/env python3
"""Shared helpers for in-flight session state coordination.

The in-flight state file at ``.claude/state/in-flight-issues.json`` records
which sessions are currently working on which issues / code areas across
parallel worktrees. Sessions register on start, deregister on completion,
and check before filing new issues / starting new work to detect overlap.

Locking strategy
----------------

We open the JSON file in r+ mode and acquire an exclusive OS-level lock
using ``fcntl`` on POSIX or ``msvcrt`` on Windows. The lock is held for the
read-modify-write window, so two concurrent ``register`` invocations
serialize at the OS layer rather than racing.

Race acceptance: simultaneous registrations for the *same* issue still
produce two entries — the file is consistent JSON but both sessions
appear. Detection happens on the *check* side, so v1 is "last writer
wins, both visible". A future iteration could de-dup on register, but
that requires deciding whose claim is canonical.
"""
from __future__ import annotations

import json
import os
import sys
import time
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator

SCHEMA_VERSION = 1
STALE_AFTER_SECONDS = 24 * 60 * 60  # 24h

IS_WINDOWS = sys.platform.startswith("win")


def repo_root() -> Path:
    """Locate the repository root (the directory that contains .claude/)."""
    here = Path(__file__).resolve().parent
    for candidate in [here, *here.parents]:
        if (candidate / ".claude").is_dir():
            return candidate
    # Fallback: parent of scripts/
    return here.parent


def state_path() -> Path:
    """Absolute path to the in-flight state file."""
    p = repo_root() / ".claude" / "state" / "in-flight-issues.json"
    p.parent.mkdir(parents=True, exist_ok=True)
    return p


def now_utc_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def empty_state() -> dict[str, Any]:
    return {
        "version": SCHEMA_VERSION,
        "updated": now_utc_iso(),
        "open_work": [],
    }


# ---------------------------------------------------------------------------
# Cross-platform file locking
# ---------------------------------------------------------------------------

if IS_WINDOWS:
    import msvcrt

    def _lock(fileobj) -> None:
        # msvcrt.locking locks bytes from the current file position. Lock the
        # whole logical file by seeking to 0 and locking a non-zero length.
        # Retry briefly if another process holds the lock.
        fileobj.seek(0)
        for _ in range(50):  # ~5 seconds
            try:
                msvcrt.locking(fileobj.fileno(), msvcrt.LK_NBLCK, 0x7FFFFFFF)
                return
            except OSError:
                time.sleep(0.1)
        raise OSError("Could not acquire lock on in-flight state file")

    def _unlock(fileobj) -> None:
        try:
            fileobj.seek(0)
            msvcrt.locking(fileobj.fileno(), msvcrt.LK_UNLCK, 0x7FFFFFFF)
        except OSError:
            pass
else:
    import fcntl

    def _lock(fileobj) -> None:
        fcntl.flock(fileobj.fileno(), fcntl.LOCK_EX)

    def _unlock(fileobj) -> None:
        try:
            fcntl.flock(fileobj.fileno(), fcntl.LOCK_UN)
        except OSError:
            pass


@contextmanager
def locked_state(path: Path | None = None) -> Iterator[tuple[Any, dict[str, Any]]]:
    """Open the state file under an exclusive lock.

    Yields ``(fileobj, state)`` where ``state`` is the parsed JSON. On
    exit, callers should call :func:`write_locked_state` if they want to
    persist changes; the lock is released when the context manager exits.
    """
    if path is None:
        path = state_path()
    # Ensure file exists so we can open r+; create with empty state first.
    if not path.exists():
        path.write_text(json.dumps(empty_state(), indent=2) + "\n", encoding="utf-8")

    with open(path, "r+", encoding="utf-8") as fp:
        _lock(fp)
        try:
            fp.seek(0)
            raw = fp.read()
            try:
                state = json.loads(raw) if raw.strip() else empty_state()
            except json.JSONDecodeError:
                state = empty_state()
            if "open_work" not in state:
                state["open_work"] = []
            if "version" not in state:
                state["version"] = SCHEMA_VERSION
            yield fp, state
        finally:
            _unlock(fp)


def write_locked_state(fileobj, state: dict[str, Any]) -> None:
    """Persist ``state`` to ``fileobj``; assumes ``locked_state`` lock is held."""
    state["updated"] = now_utc_iso()
    serialized = json.dumps(state, indent=2) + "\n"
    fileobj.seek(0)
    fileobj.truncate()
    fileobj.write(serialized)
    fileobj.flush()
    try:
        os.fsync(fileobj.fileno())
    except OSError:
        pass


# ---------------------------------------------------------------------------
# Domain helpers
# ---------------------------------------------------------------------------


def _parse_iso(ts: str) -> datetime | None:
    try:
        # Accept both "...Z" and "+00:00" forms.
        if ts.endswith("Z"):
            ts = ts[:-1] + "+00:00"
        return datetime.fromisoformat(ts)
    except (ValueError, TypeError):
        return None


def _branch_exists(branch: str) -> bool:
    """Return True if a local branch exists; tolerate missing git."""
    if not branch:
        return False
    try:
        import subprocess

        result = subprocess.run(
            ["git", "branch", "--list", branch],
            capture_output=True,
            text=True,
            timeout=5,
        )
        return result.returncode == 0 and bool(result.stdout.strip())
    except Exception:
        # If git is unavailable, don't aggressively prune.
        return True


def prune_stale(state: dict[str, Any], *, now: datetime | None = None,
                branch_exists=_branch_exists) -> list[dict[str, Any]]:
    """Remove entries older than ``STALE_AFTER_SECONDS`` whose branch is gone.

    Returns the list of pruned entries (for reporting). Mutates ``state``.
    """
    if now is None:
        now = datetime.now(timezone.utc)
    keep: list[dict[str, Any]] = []
    pruned: list[dict[str, Any]] = []
    for entry in state.get("open_work", []):
        started = _parse_iso(entry.get("started", "")) or now
        age = (now - started).total_seconds()
        if age > STALE_AFTER_SECONDS and not branch_exists(entry.get("branch", "")):
            pruned.append(entry)
            continue
        keep.append(entry)
    state["open_work"] = keep
    return pruned


def find_conflicts(state: dict[str, Any], *, area: str | None = None,
                   issue: int | None = None,
                   exclude_session: str | None = None) -> list[dict[str, Any]]:
    """Return entries that conflict with the given ``area`` or ``issue``.

    Conflict rules (v1):
      * Same issue number anywhere in another entry's ``issues``.
      * ``area`` is a path prefix of an existing entry's area (or vice
        versa) — handles the common case where one session works on
        ``src/PPDS.Cli/`` and another on ``src/PPDS.Cli/Plugins/Foo.cs``.
    """
    conflicts: list[dict[str, Any]] = []
    norm_area = _normalize_area(area) if area else None
    for entry in state.get("open_work", []):
        if exclude_session and entry.get("session_id") == exclude_session:
            continue
        if issue is not None and issue in (entry.get("issues") or []):
            conflicts.append(entry)
            continue
        if norm_area:
            for existing in entry.get("areas") or []:
                if _areas_overlap(norm_area, _normalize_area(existing)):
                    conflicts.append(entry)
                    break
    return conflicts


def _normalize_area(path: str) -> str:
    p = (path or "").strip().replace("\\", "/")
    while p.endswith("/"):
        p = p[:-1]
    return p


def _areas_overlap(a: str, b: str) -> bool:
    if not a or not b:
        return False
    if a == b:
        return True
    return a.startswith(b + "/") or b.startswith(a + "/")
