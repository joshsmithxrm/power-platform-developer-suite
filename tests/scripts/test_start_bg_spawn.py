"""Unit tests for scripts/start-bg-spawn.py — AC-01 through AC-07, AC-12."""
from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import time
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import MagicMock, patch

import pytest

_REPO = Path(__file__).resolve().parents[2]
_MOD_PATH = _REPO / "scripts" / "start-bg-spawn.py"

_spec = importlib.util.spec_from_file_location("start_bg_spawn", str(_MOD_PATH))
_mod = importlib.util.module_from_spec(_spec)
sys.modules["start_bg_spawn"] = _mod
_spec.loader.exec_module(_mod)

spawn = _mod.spawn
require_min_version = _mod.require_min_version
parse_banner = _mod.parse_banner
identify_session = _mod.identify_session
SpawnError = _mod.SpawnError
SpawnResult = _mod.SpawnResult
main = _mod.main


# ---------------------------------------------------------------------------
# AC-01: rejects old claude version
# ---------------------------------------------------------------------------

def test_rejects_old_version(monkeypatch, capsys):
    """AC-01: exit 1 when claude --version < 2.1.139; stderr has version + install cmd."""
    def fake_run(argv, **kw):
        return CompletedProcess(argv, 0, "2.1.138 (Claude Code)\n", "")

    monkeypatch.setattr(subprocess, "run", fake_run)
    with pytest.raises(SpawnError) as exc_info:
        require_min_version()
    err = exc_info.value
    assert err.code == 1
    msg = str(err)
    assert "2.1.139" in msg
    assert "npm i -g @anthropic-ai/claude-code" in msg


def test_accepts_exact_min_version(monkeypatch):
    """AC-01 boundary: version exactly 2.1.139 must be accepted (not rejected)."""
    def fake_run(argv, **kw):
        return CompletedProcess(argv, 0, "2.1.139 (Claude Code)\n", "")

    monkeypatch.setattr(subprocess, "run", fake_run)
    require_min_version()  # must not raise


# ---------------------------------------------------------------------------
# AC-02: spawn argv + cwd, never shell=True
# ---------------------------------------------------------------------------

def test_spawn_argv(monkeypatch, tmp_path):
    """AC-02: subprocess.run called with exact argv and cwd, shell not True."""
    calls = []

    def fake_run(argv, **kw):
        calls.append({"argv": list(argv), "cwd": kw.get("cwd"), "shell": kw.get("shell", False)})
        if argv == ["claude", "--version"]:
            return CompletedProcess(argv, 0, "2.1.140 (Claude Code)\n", "")
        if argv[0:2] == ["claude", "--bg"]:
            return CompletedProcess(argv, 0, "backgrounded · abc12345\n", "")
        return CompletedProcess(argv, 0, "", "")

    fake_jobs_dir = tmp_path / "jobs"
    fake_jobs_dir.mkdir()
    state_dir = fake_jobs_dir / "abc12345"
    state_dir.mkdir()
    state_file = state_dir / "state.json"
    state_file.write_text(
        json.dumps({"sessionId": "abc12345-0000-0000-0000-000000000000", "cwd": str(tmp_path).replace("\\", "/")}),
        encoding="utf-8",
    )

    monkeypatch.setattr(subprocess, "run", fake_run)

    result = spawn(
        worktree_abs=str(tmp_path),
        branch="feat/x",
        prompt="hello",
        jobs_dir=fake_jobs_dir,
    )

    bg_call = next(c for c in calls if c["argv"][:2] == ["claude", "--bg"])
    assert bg_call["argv"] == ["claude", "--bg", "--name", "feat/x", "--", "hello"]
    assert bg_call["cwd"] == str(tmp_path)
    assert bg_call["shell"] is False


# ---------------------------------------------------------------------------
# AC-03: banner parser, plain and ANSI
# ---------------------------------------------------------------------------

def test_parse_banner_plain():
    """AC-03a: plain banner yields short ID."""
    raw = "backgrounded · abc12345\n  claude attach abc12345...\n"
    assert parse_banner(raw) == "abc12345"


def test_parse_banner_ansi():
    """AC-03b: ANSI-decorated banner yields short ID via strip-then-match."""
    raw = "backgrounded · \x1b[36mabc12345\x1b[39m\n  claude attach abc12345...\n"
    assert parse_banner(raw) == "abc12345"


# ---------------------------------------------------------------------------
# AC-04: fallback cwd scan with fixture state.json files
# ---------------------------------------------------------------------------

def test_fallback_id_lookup_mtime(tmp_path, monkeypatch):
    """AC-04 mtime-fallback path: when state.json omits createdAt, mtime gates recency."""
    jobs_dir = tmp_path / "jobs"
    jobs_dir.mkdir()

    target_cwd = str(tmp_path / "worktree").replace("\\", "/")
    now = time.time()

    # Old entry — same cwd but too old (>10s ago) — should NOT be chosen first
    # Actually per spec: within last 10s. Let's make one old and one current.
    old_dir = jobs_dir / "oldold00"
    old_dir.mkdir()
    old_state = old_dir / "state.json"
    old_state.write_text(
        json.dumps({"sessionId": "old-uuid", "cwd": target_cwd}),
        encoding="utf-8",
    )
    # Backdate mtime by 20s
    old_time = now - 20
    import os
    os.utime(str(old_state), (old_time, old_time))

    # Wrong cwd entry (recent)
    wrong_dir = jobs_dir / "wrongcwd"
    wrong_dir.mkdir()
    wrong_state = wrong_dir / "state.json"
    wrong_state.write_text(
        json.dumps({"sessionId": "wrong-uuid", "cwd": "/some/other/path"}),
        encoding="utf-8",
    )

    # Correct entry — matching cwd, recent
    good_dir = jobs_dir / "goodgood"
    good_dir.mkdir()
    good_state = good_dir / "state.json"
    good_state.write_text(
        json.dumps({"sessionId": "good-uuid-0000-0000-0000-000000000000", "cwd": target_cwd}),
        encoding="utf-8",
    )

    # monkeypatch time so spawn_floor = now - 10 (includes good, excludes old).
    # Return now for early calls so the loop enters; expire after many calls so
    # a scan failure raises SpawnError rather than hanging forever.
    call_count = [0]

    def patched_time():
        call_count[0] += 1
        if call_count[0] > 20:
            return now + 6.0  # past deadline — prevents hang on bad test setup
        return now

    monkeypatch.setattr(time, "time", patched_time)
    monkeypatch.setattr(time, "sleep", lambda _: None)

    short, data = identify_session(None, target_cwd, jobs_dir=jobs_dir)
    assert short == "goodgood"
    assert data["sessionId"] == "good-uuid-0000-0000-0000-000000000000"


def test_fallback_id_lookup_created_at(tmp_path, monkeypatch):
    """AC-04 createdAt-ISO path: state["createdAt"] within last 10s gates recency.

    Exercises the branch in `_scan_for_cwd` that parses `state["createdAt"]`
    (ISO-8601) — the spec's canonical recency signal. The mtime pre-filter
    is bypassed by setting mtimes recent on all fixtures, so the createdAt
    parser must do the work of rejecting the stale entry.
    """
    import os
    from datetime import datetime, timezone

    jobs_dir = tmp_path / "jobs"
    jobs_dir.mkdir()

    target_cwd = str(tmp_path / "worktree").replace("\\", "/")
    now = time.time()
    now_iso = datetime.fromtimestamp(now, tz=timezone.utc).isoformat().replace("+00:00", "Z")
    stale_iso = datetime.fromtimestamp(now - 30, tz=timezone.utc).isoformat().replace("+00:00", "Z")

    # Stale entry: matching cwd, recent mtime, but createdAt is 30s old (>10s window)
    stale_dir = jobs_dir / "stalests"
    stale_dir.mkdir()
    stale_state = stale_dir / "state.json"
    stale_state.write_text(
        json.dumps({
            "sessionId": "stale-uuid",
            "cwd": target_cwd,
            "createdAt": stale_iso,
        }),
        encoding="utf-8",
    )
    os.utime(str(stale_state), (now, now))  # recent mtime bypasses mtime pre-filter

    # Fresh entry: matching cwd, createdAt within window
    fresh_dir = jobs_dir / "freshfre"
    fresh_dir.mkdir()
    fresh_state = fresh_dir / "state.json"
    fresh_state.write_text(
        json.dumps({
            "sessionId": "fresh-uuid-0000-0000-0000-000000000000",
            "cwd": target_cwd,
            "createdAt": now_iso,
        }),
        encoding="utf-8",
    )
    os.utime(str(fresh_state), (now, now))

    call_count = [0]

    def patched_time():
        call_count[0] += 1
        if call_count[0] > 20:
            return now + 6.0
        return now

    monkeypatch.setattr(time, "time", patched_time)
    monkeypatch.setattr(time, "sleep", lambda _: None)

    short, data = identify_session(None, target_cwd, jobs_dir=jobs_dir)
    assert short == "freshfre"
    assert data["sessionId"] == "fresh-uuid-0000-0000-0000-000000000000"


# ---------------------------------------------------------------------------
# AC-05: state.json poll timeout
# ---------------------------------------------------------------------------

def test_state_json_poll_timeout(tmp_path, monkeypatch, capsys):
    """AC-05: exhausting 5s budget raises SpawnError(code=2) with timeout message."""
    jobs_dir = tmp_path / "jobs"
    jobs_dir.mkdir()

    # Advance time past the 5s budget after one loop iteration.
    # Call sequence with spawn_floor computed inside loop:
    #   call 1: deadline setup (time.time() + POLL_BUDGET_SEC)
    #   call 2: while-check → enters loop body
    #   call 3: spawn_floor inside loop (time.time() - FALLBACK_AGE_SEC)
    #   call 4: while-check → past deadline → exits loop
    t_start = 1000.0
    call_n = [0]

    def fast_time():
        call_n[0] += 1
        if call_n[0] <= 3:
            return t_start
        return t_start + 6.0

    monkeypatch.setattr(time, "time", fast_time)
    monkeypatch.setattr(time, "sleep", lambda _: None)

    with pytest.raises(SpawnError) as exc_info:
        identify_session(None, str(tmp_path / "wt"), jobs_dir=jobs_dir)
    err = exc_info.value
    assert err.code == 2
    assert "daemon state file did not appear" in str(err)


# ---------------------------------------------------------------------------
# AC-06: cwd mismatch → claude stop called, exit 2
# ---------------------------------------------------------------------------

def test_cwd_mismatch_stops_session(monkeypatch, tmp_path):
    """AC-06: cwd mismatch after spawn triggers claude stop and SpawnError(code=2)."""
    stop_calls = []
    real_worktree = str(tmp_path).replace("\\", "/")
    wrong_cwd = "/completely/different/path"

    def fake_run(argv, **kw):
        if argv == ["claude", "--version"]:
            return CompletedProcess(argv, 0, "2.1.140 (Claude Code)\n", "")
        if argv[:2] == ["claude", "--bg"]:
            return CompletedProcess(argv, 0, "backgrounded · abc12345\n", "")
        if argv[:2] == ["claude", "stop"]:
            stop_calls.append(argv)
            return CompletedProcess(argv, 0, "", "")
        return CompletedProcess(argv, 0, "", "")

    fake_jobs = tmp_path / "jobs"
    fake_jobs.mkdir()
    sd = fake_jobs / "abc12345"
    sd.mkdir()
    (sd / "state.json").write_text(
        json.dumps({"sessionId": "abc12345-uuid", "cwd": wrong_cwd}),
        encoding="utf-8",
    )

    monkeypatch.setattr(subprocess, "run", fake_run)

    with pytest.raises(SpawnError) as exc_info:
        spawn(real_worktree, "feat/x", "hi", jobs_dir=fake_jobs)

    err = exc_info.value
    assert err.code == 2
    assert "daemon cwd mismatch" in str(err)
    assert any(c[:3] == ["claude", "stop", "abc12345"] for c in stop_calls)


# ---------------------------------------------------------------------------
# AC-07: stdout is exactly one JSON line; all other output to stderr
# ---------------------------------------------------------------------------

def test_stdout_is_json_only(monkeypatch, tmp_path, capsys):
    """AC-07: stdout is exactly one line, JSON with {short, sessionId, cwd}."""
    norm_cwd = str(tmp_path).replace("\\", "/")

    def fake_run(argv, **kw):
        if argv == ["claude", "--version"]:
            return CompletedProcess(argv, 0, "2.1.140 (Claude Code)\n", "")
        if argv[:2] == ["claude", "--bg"]:
            return CompletedProcess(argv, 0, "backgrounded · deadbeef\n", "")
        return CompletedProcess(argv, 0, "", "")

    fake_jobs = tmp_path / "jobs"
    fake_jobs.mkdir()
    sd = fake_jobs / "deadbeef"
    sd.mkdir()
    (sd / "state.json").write_text(
        json.dumps({"sessionId": "deadbeef-0000-0000-0000-000000000000", "cwd": norm_cwd}),
        encoding="utf-8",
    )

    monkeypatch.setattr(subprocess, "run", fake_run)

    prompt_file = tmp_path / "prompt.txt"
    prompt_file.write_text("hello world", encoding="utf-8")

    # Patch the module-level JOBS_DIR so spawn() picks up our fake jobs dir
    monkeypatch.setattr(_mod, "JOBS_DIR", fake_jobs)

    rc = main(["--worktree-abs", str(tmp_path), "--branch", "feat/x", "--prompt-file", str(prompt_file)])
    assert rc == 0

    captured = capsys.readouterr()
    lines = [l for l in captured.out.splitlines() if l.strip()]
    assert len(lines) == 1, f"Expected 1 stdout line, got: {captured.out!r}"
    obj = json.loads(lines[0])
    assert set(obj.keys()) == {"short", "sessionId", "cwd"}
    assert obj["short"] == "deadbeef"


# ---------------------------------------------------------------------------
# AC-12: claude not on PATH → exit 1 with install instructions
# ---------------------------------------------------------------------------

def test_claude_not_on_path(monkeypatch):
    """AC-12: FileNotFoundError from version probe → SpawnError(code=1) with install info."""
    def fake_run(argv, **kw):
        if argv == ["claude", "--version"]:
            raise FileNotFoundError("claude not found")
        return CompletedProcess(argv, 0, "", "")

    monkeypatch.setattr(subprocess, "run", fake_run)

    with pytest.raises(SpawnError) as exc_info:
        require_min_version()
    err = exc_info.value
    assert err.code == 1
    msg = str(err)
    assert "Claude Code" in msg
    assert "npm i -g @anthropic-ai/claude-code" in msg


# ---------------------------------------------------------------------------
# Spec InvalidArg: argparse missing-required-arg → exit 1 (not argparse default 2)
# ---------------------------------------------------------------------------

def test_argparse_missing_args_exits_1(capsys):
    """Spec Error Types: InvalidArg → exit 1. argparse defaults to 2; helper overrides."""
    with pytest.raises(SystemExit) as exc_info:
        main([])  # no args at all → all three required flags missing
    assert exc_info.value.code == 1
    err = capsys.readouterr().err
    assert "--worktree-abs" in err or "required" in err.lower()
