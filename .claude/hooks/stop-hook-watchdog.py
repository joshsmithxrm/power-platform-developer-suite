#!/usr/bin/env python3
"""Stop-hook watchdog (circuit breaker).

Meta-retro #17 (2026-04-19): PR #830 ran 509 turns with 553 errors because a
Stop hook fired repeatedly in a loop. No circuit breaker existed. This hook
tracks per-(session, hook_name) Stop firings in a rolling 5-minute window and
denies (exit 2) when any single hook exceeds 20 firings in that window.

State file: .claude/state/hook-counts.json
    { "<session_id>": { "<hook_name>": [epoch_ts, epoch_ts, ...], ... }, ... }

Entries older than WINDOW_SECONDS are pruned on every invocation. Corrupt
state files are silently reset (graceful fallback — must never block Stop on
bookkeeping errors).

Concurrency: read-modify-write is guarded by a sidecar lock file acquired via
O_CREAT|O_EXCL (cross-platform) and the write itself uses write-to-temp +
os.replace for atomicity. If lock acquisition times out, we fail open rather
than block a Stop hook on bookkeeping.
"""
import errno
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir

WINDOW_SECONDS = 300  # 5 minutes
THRESHOLD = 20
LOCK_TIMEOUT_SECONDS = 2.0  # max wait for lock — beyond this, fail open
LOCK_STALE_SECONDS = 30.0  # reclaim orphaned locks older than this


def _load_counts(state_path):
    """Load counts file; return {} on any error (graceful fallback)."""
    try:
        with open(state_path, "r") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            return {}
        return data
    except (OSError, json.JSONDecodeError, ValueError):
        return {}


def _acquire_lock(lock_path, timeout=LOCK_TIMEOUT_SECONDS):
    """Acquire sidecar lock via O_CREAT|O_EXCL. Returns fd or None on timeout.

    Stale locks (older than LOCK_STALE_SECONDS) are reclaimed — guards against
    orphaned locks from crashed processes.
    """
    deadline = time.time() + timeout
    while True:
        try:
            return os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
        except OSError as e:
            if e.errno != errno.EEXIST:
                return None
            # Lock exists — check if stale and reclaim.
            try:
                age = time.time() - os.path.getmtime(lock_path)
                if age > LOCK_STALE_SECONDS:
                    os.unlink(lock_path)
                    continue
            except OSError:
                pass
            if time.time() >= deadline:
                return None
            time.sleep(0.02)


def _release_lock(fd, lock_path):
    """Release the lock file; swallow errors (best effort)."""
    try:
        os.close(fd)
    except OSError:
        pass
    try:
        os.unlink(lock_path)
    except OSError:
        pass


def _atomic_write(state_path, counts):
    """Write counts to state_path atomically (temp file + os.replace)."""
    tmp_path = state_path + ".tmp"
    with open(tmp_path, "w") as f:
        json.dump(counts, f)
        f.flush()
        try:
            os.fsync(f.fileno())
        except OSError:
            pass
    os.replace(tmp_path, state_path)


def _prune(counts, now):
    """Drop timestamps older than WINDOW_SECONDS; drop empty entries."""
    cutoff = now - WINDOW_SECONDS
    pruned = {}
    for sid, hooks in list(counts.items()):
        if not isinstance(hooks, dict):
            continue
        kept_hooks = {}
        for hname, ts_list in hooks.items():
            if not isinstance(ts_list, list):
                continue
            fresh = [t for t in ts_list if isinstance(t, (int, float)) and t >= cutoff]
            if fresh:
                kept_hooks[hname] = fresh
        if kept_hooks:
            pruned[sid] = kept_hooks
    return pruned


def main():
    try:
        hook_input = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError, OSError):
        hook_input = {}

    # Re-entry guard: never count/deny when stop_hook_active is set.
    if hook_input.get("stop_hook_active"):
        sys.exit(0)

    session_id = str(hook_input.get("session_id") or "unknown")
    hook_name = str(hook_input.get("hook_event_name") or "Stop")

    project_dir = get_project_dir()
    state_dir = os.path.join(project_dir, ".claude", "state")
    state_path = os.path.join(state_dir, "hook-counts.json")
    lock_path = state_path + ".lock"

    try:
        os.makedirs(state_dir, exist_ok=True)
    except OSError:
        sys.exit(0)  # cannot persist — fail open

    # Serialize read-modify-write across concurrent hook invocations.
    lock_fd = _acquire_lock(lock_path)
    if lock_fd is None:
        sys.exit(0)  # couldn't lock — fail open (never block Stop on bookkeeping)

    try:
        now = time.time()
        counts = _prune(_load_counts(state_path), now)

        session_bucket = counts.setdefault(session_id, {})
        ts_list = session_bucket.setdefault(hook_name, [])
        ts_list.append(now)

        try:
            _atomic_write(state_path, counts)
        except OSError:
            pass  # fail open — don't block on disk errors
    finally:
        _release_lock(lock_fd, lock_path)

    if len(ts_list) > THRESHOLD:
        msg = (
            f"Stop hook '{hook_name}' fired >{THRESHOLD}x in 5min — "
            "circuit breaker engaged. Investigate `.workflow/state.json` "
            "phase and hook registration in settings.json."
        )
        print(json.dumps({"decision": "block", "reason": msg}))
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
