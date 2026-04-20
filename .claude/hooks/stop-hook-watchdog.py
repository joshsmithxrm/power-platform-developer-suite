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
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir

WINDOW_SECONDS = 300  # 5 minutes
THRESHOLD = 20


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

    try:
        os.makedirs(state_dir, exist_ok=True)
    except OSError:
        sys.exit(0)  # cannot persist — fail open

    now = time.time()
    counts = _prune(_load_counts(state_path), now)

    session_bucket = counts.setdefault(session_id, {})
    ts_list = session_bucket.setdefault(hook_name, [])
    ts_list.append(now)

    try:
        with open(state_path, "w") as f:
            json.dump(counts, f)
    except OSError:
        pass  # fail open — don't block on disk errors

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
