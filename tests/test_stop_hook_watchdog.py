"""Tests for .claude/hooks/stop-hook-watchdog.py (meta-retro #17).

Covers: under-threshold allow, over-threshold deny (message + exit 2),
corrupt-state graceful fallback, cross-session isolation, concurrent-write
race safety (atomic rename + sidecar lock).
"""
import json
import os
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor

HOOK_PATH = os.path.normpath(os.path.join(
    os.path.dirname(__file__), os.pardir, ".claude", "hooks", "stop-hook-watchdog.py"
))


def _run(payload, project_dir):
    env = os.environ.copy()
    env["CLAUDE_PROJECT_DIR"] = project_dir
    env.pop("PPDS_PIPELINE", None)
    env.pop("PPDS_SHAKEDOWN", None)
    return subprocess.run(
        [sys.executable, HOOK_PATH], input=json.dumps(payload),
        capture_output=True, text=True, timeout=10, env=env,
    )


def _counts(project_dir):
    with open(os.path.join(project_dir, ".claude", "state", "hook-counts.json")) as f:
        return json.load(f)


def _seed(project_dir, data):
    path = os.path.join(project_dir, ".claude", "state", "hook-counts.json")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(data, f)


# --- Normal fire (under threshold, allows) ---

def test_under_threshold_allows_20_fires(tmp_path):
    """20 firings is still allowed (threshold is strictly >20)."""
    payload = {"session_id": "sess-A", "hook_event_name": "Stop"}
    for _ in range(20):
        assert _run(payload, str(tmp_path)).returncode == 0
    assert len(_counts(str(tmp_path))["sess-A"]["Stop"]) == 20


def test_stop_hook_active_is_noop(tmp_path):
    """Re-entry guard short-circuits without counting or touching disk."""
    r = _run({"session_id": "s", "hook_event_name": "Stop", "stop_hook_active": True},
             str(tmp_path))
    assert r.returncode == 0
    assert not os.path.exists(os.path.join(str(tmp_path), ".claude", "state", "hook-counts.json"))


# --- Threshold breach (denies with right message) ---

def test_21st_fire_denies_with_message(tmp_path):
    now = time.time()
    _seed(str(tmp_path), {"sess-A": {"Stop": [now - i for i in range(20)]}})
    r = _run({"session_id": "sess-A", "hook_event_name": "Stop"}, str(tmp_path))
    assert r.returncode == 2
    out = json.loads(r.stdout)
    assert out["decision"] == "block"
    reason = out["reason"]
    assert "Stop hook 'Stop' fired >20x in 5min" in reason
    assert "circuit breaker engaged" in reason
    assert ".workflow/state.json" in reason and "settings.json" in reason


def test_old_entries_pruned_before_threshold_check(tmp_path):
    """Firings older than 5 minutes do NOT count toward threshold."""
    now = time.time()
    _seed(str(tmp_path), {"sess-A": {"Stop": [now - 400 - i for i in range(20)]}})
    r = _run({"session_id": "sess-A", "hook_event_name": "Stop"}, str(tmp_path))
    assert r.returncode == 0
    assert len(_counts(str(tmp_path))["sess-A"]["Stop"]) == 1


# --- State-file corruption (graceful fallback) ---

def test_corrupt_json_falls_back_gracefully(tmp_path):
    path = os.path.join(str(tmp_path), ".claude", "state", "hook-counts.json")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write("{not valid json~~~")
    r = _run({"session_id": "sess-A", "hook_event_name": "Stop"}, str(tmp_path))
    assert r.returncode == 0
    assert len(_counts(str(tmp_path))["sess-A"]["Stop"]) == 1


def test_non_dict_root_resets(tmp_path):
    _seed(str(tmp_path), ["garbage"])  # wrong shape
    r = _run({"session_id": "sess-A", "hook_event_name": "Stop"}, str(tmp_path))
    assert r.returncode == 0


# --- Cross-session isolation ---

def test_session_a_high_count_does_not_affect_session_b(tmp_path):
    now = time.time()
    _seed(str(tmp_path), {"sess-A": {"Stop": [now - i for i in range(25)]}})
    r = _run({"session_id": "sess-B", "hook_event_name": "Stop"}, str(tmp_path))
    assert r.returncode == 0
    data = _counts(str(tmp_path))
    assert "sess-A" in data and len(data["sess-B"]["Stop"]) == 1


# --- Concurrency: atomic write + sidecar lock prevent lost updates ---

def test_concurrent_invocations_do_not_lose_updates(tmp_path):
    """N concurrent Stop hooks across distinct sessions must persist all N
    timestamps — the lock + atomic rename eliminate read-modify-write races.
    """
    project_dir = str(tmp_path)
    n = 10

    def fire(i):
        return _run(
            {"session_id": f"sess-{i}", "hook_event_name": "Stop"}, project_dir
        ).returncode

    with ThreadPoolExecutor(max_workers=n) as pool:
        results = list(pool.map(fire, range(n)))

    assert all(rc == 0 for rc in results)
    data = _counts(project_dir)
    # Every distinct session must be present — no lost updates.
    assert len(data) == n
    for i in range(n):
        assert len(data[f"sess-{i}"]["Stop"]) == 1
    # No stray lock or temp file left behind.
    state_dir = os.path.join(project_dir, ".claude", "state")
    assert not os.path.exists(os.path.join(state_dir, "hook-counts.json.lock"))
    assert not os.path.exists(os.path.join(state_dir, "hook-counts.json.tmp"))
