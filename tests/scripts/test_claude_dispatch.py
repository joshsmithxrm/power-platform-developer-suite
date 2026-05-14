"""Unit tests for scripts/claude_dispatch.py — ACs 01-10 + mode resolution + output."""
from __future__ import annotations

import importlib.util
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import MagicMock

import pytest

_REPO = Path(__file__).resolve().parents[2]
_SCRIPTS = _REPO / "scripts"
if str(_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS))

_MOD_PATH = _SCRIPTS / "claude_dispatch.py"
_spec = importlib.util.spec_from_file_location("claude_dispatch", str(_MOD_PATH))
_mod = importlib.util.module_from_spec(_spec)
sys.modules["claude_dispatch"] = _mod
_spec.loader.exec_module(_mod)

claude_dispatch = _mod
spawn = _mod.spawn
require_min_version = _mod.require_min_version
_resolve_mode = _mod._resolve_mode
_emit_headless_warning = _mod._emit_headless_warning
_reset_version_cache = _mod._reset_version_cache
_parse_banner = _mod._parse_banner
BgHandle = _mod.BgHandle
HeadlessHandle = _mod.HeadlessHandle
DispatchError = _mod.DispatchError
BlockedSessionError = _mod.BlockedSessionError
DispatchFallbackError = _mod.DispatchFallbackError
WARNING_TEMPLATE = _mod.WARNING_TEMPLATE

# AC-03 stderr template — em-dash is U+2014 (—), not hyphen-minus.
AC03_REGEX = re.compile(
    r"^WARN SDK pool: claude -p invoked from .+ \(model=.+, agent=.+\) — "
    r"counts against monthly Agent SDK credit, not subscription\.$"
)


@pytest.fixture(autouse=True)
def _reset_caches(monkeypatch):
    _reset_version_cache()
    yield
    _reset_version_cache()


class _FakeProc:
    def __init__(self, exit_code=0):
        self._exit_code = exit_code
        self._polled = False

    def poll(self):
        if self._polled:
            return self._exit_code
        return None

    def wait(self, timeout=None):
        self._polled = True
        return self._exit_code

    def terminate(self):
        self._polled = True


def _patch_min_version(monkeypatch, version_str="2.1.141"):
    def fake_run(argv, **kw):
        if argv[:2] == ["claude", "--version"]:
            return CompletedProcess(argv, 0, f"{version_str} (Claude Code)\n", "")
        return CompletedProcess(argv, 0, "", "")
    monkeypatch.setattr(subprocess, "run", fake_run)


def _seed_state(jobs_dir: Path, short: str, *, cwd: str, state: str = "working",
                session_id: str = "00000000-0000-0000-0000-000000000001",
                link_scan_path: str | None = None) -> Path:
    d = jobs_dir / short
    d.mkdir(parents=True, exist_ok=True)
    p = d / "state.json"
    payload = {
        "sessionId": session_id,
        "cwd": cwd.replace("\\", "/"),
        "state": state,
        "linkScanPath": link_scan_path or str(jobs_dir / short / "transcript.jsonl"),
    }
    p.write_text(json.dumps(payload), encoding="utf-8")
    return p


# ---------------------------------------------------------------------------
# AC-05, AC-06: version gate
# ---------------------------------------------------------------------------

def test_require_min_version_rejects_old(monkeypatch):
    """AC-05: < 2.1.139 raises DispatchError."""
    _patch_min_version(monkeypatch, "2.1.138")
    with pytest.raises(DispatchError) as exc_info:
        require_min_version()
    assert exc_info.value.exit_code == 1
    msg = str(exc_info.value)
    assert "2.1.139" in msg


def test_require_min_version_accepts_boundary(monkeypatch):
    """AC-06: exactly 2.1.139 must be accepted."""
    _patch_min_version(monkeypatch, "2.1.139")
    require_min_version()  # must not raise


def test_require_min_version_caches_after_success(monkeypatch):
    """Second call does not re-invoke claude --version."""
    calls = []
    def fake_run(argv, **kw):
        calls.append(list(argv))
        return CompletedProcess(argv, 0, "2.1.141 (Claude Code)\n", "")
    monkeypatch.setattr(subprocess, "run", fake_run)
    require_min_version()
    require_min_version()
    version_calls = [c for c in calls if c[:2] == ["claude", "--version"]]
    assert len(version_calls) == 1


# ---------------------------------------------------------------------------
# Mode resolution
# ---------------------------------------------------------------------------

def test_resolve_mode_flag_wins_over_env():
    """CLI flag overrides env when both are set."""
    mode = _resolve_mode("interactive", env={"PPDS_DISPATCH_MODE": "headless"})
    assert mode == "interactive"


def test_resolve_mode_env_default_interactive():
    """No flag, no env, default is interactive."""
    assert _resolve_mode(None, env={}) == "interactive"


def test_resolve_mode_env_headless():
    """Env value 'headless' resolves to headless when no flag."""
    assert _resolve_mode(None, env={"PPDS_DISPATCH_MODE": "headless"}) == "headless"


def test_resolve_mode_invalid_env_raises_dispatch_error():
    """PPDS_DISPATCH_MODE=banana raises DispatchError."""
    with pytest.raises(DispatchError) as exc_info:
        _resolve_mode(None, env={"PPDS_DISPATCH_MODE": "banana"})
    assert "PPDS_DISPATCH_MODE" in str(exc_info.value)
    assert "banana" in str(exc_info.value)


def test_resolve_mode_invalid_flag_value_raises():
    """Invalid --mode at the dispatcher layer raises DispatchError. argparse handles CLI side."""
    with pytest.raises(DispatchError):
        _resolve_mode("yelling", env={})


def test_resolve_mode_empty_env_string_treated_as_unset():
    """PPDS_DISPATCH_MODE='' falls through to default."""
    assert _resolve_mode(None, env={"PPDS_DISPATCH_MODE": ""}) == "interactive"


# ---------------------------------------------------------------------------
# AC-01: interactive argv shape
# ---------------------------------------------------------------------------

def test_dispatch_interactive_argv(monkeypatch, tmp_path):
    """AC-01: interactive mode spawns claude --bg --name <n> --dangerously-skip-permissions -- <prompt>."""
    jobs_dir = tmp_path / "jobs"
    jobs_dir.mkdir()
    seeded_state_path = _seed_state(jobs_dir, "abc12345",
                                    cwd=str(tmp_path),
                                    link_scan_path=str(tmp_path / "tr.jsonl"))
    captured = []

    def fake_run(argv, **kw):
        captured.append({"argv": list(argv), "shell": kw.get("shell", False)})
        if argv[:2] == ["claude", "--version"]:
            return CompletedProcess(argv, 0, "2.1.141 (Claude Code)\n", "")
        if argv[:2] == ["claude", "--bg"]:
            return CompletedProcess(argv, 0, "backgrounded · abc12345\n", "")
        return CompletedProcess(argv, 0, "", "")

    monkeypatch.setattr(subprocess, "run", fake_run)

    handle = spawn(
        mode="interactive",
        prompt="do thing",
        caller="test",
        name="stage",
        dangerous=True,
        cwd=str(tmp_path),
        jobs_dir=jobs_dir,
    )
    bg_call = next(c for c in captured if c["argv"][:2] == ["claude", "--bg"])
    assert bg_call["argv"] == [
        "claude", "--bg", "--name", "stage",
        "--dangerously-skip-permissions", "--", "do thing",
    ]
    assert bg_call["shell"] is False
    assert isinstance(handle, BgHandle)
    assert handle.short == "abc12345"
    assert handle.transcript_path == Path(tmp_path / "tr.jsonl")


def test_dispatch_interactive_without_dangerous(monkeypatch, tmp_path):
    """When dangerous=False the flag is absent (start-bg-spawn-style)."""
    jobs_dir = tmp_path / "jobs"
    jobs_dir.mkdir()
    _seed_state(jobs_dir, "abc12345", cwd=str(tmp_path))
    captured = []

    def fake_run(argv, **kw):
        captured.append(list(argv))
        if argv[:2] == ["claude", "--version"]:
            return CompletedProcess(argv, 0, "2.1.141\n", "")
        if argv[:2] == ["claude", "--bg"]:
            return CompletedProcess(argv, 0, "backgrounded · abc12345\n", "")
        return CompletedProcess(argv, 0, "", "")
    monkeypatch.setattr(subprocess, "run", fake_run)

    spawn(mode="interactive", prompt="x", caller="test", name="stage",
          dangerous=False, cwd=str(tmp_path), jobs_dir=jobs_dir)
    bg_call = [c for c in captured if c[:2] == ["claude", "--bg"]][0]
    assert "--dangerously-skip-permissions" not in bg_call


def test_dispatch_interactive_requires_name(monkeypatch, tmp_path):
    _patch_min_version(monkeypatch)
    with pytest.raises(DispatchError):
        spawn(mode="interactive", prompt="x", caller="t", name=None,
              cwd=str(tmp_path), jobs_dir=tmp_path)


def test_dispatch_interactive_validates_name(monkeypatch, tmp_path):
    _patch_min_version(monkeypatch)
    with pytest.raises(DispatchError):
        spawn(mode="interactive", prompt="x", caller="t",
              name="invalid name with spaces!",
              cwd=str(tmp_path), jobs_dir=tmp_path)


# ---------------------------------------------------------------------------
# AC-02, AC-03, AC-04: headless argv + warning + JSONL
# ---------------------------------------------------------------------------

def _patch_headless(monkeypatch, popen_capture):
    def fake_popen(argv, **kw):
        popen_capture.append({"argv": list(argv), "shell": kw.get("shell", False)})
        return _FakeProc(exit_code=0)
    monkeypatch.setattr(subprocess, "Popen", fake_popen)
    _patch_min_version(monkeypatch)


def test_dispatch_headless_argv(monkeypatch, tmp_path):
    """AC-02: headless emits claude -p <prompt> --verbose --output-format stream-json [model][agent]."""
    captured = []
    _patch_headless(monkeypatch, captured)
    stage_log = tmp_path / "stage.jsonl"
    spend = tmp_path / "spend.jsonl"
    handle = spawn(
        mode="headless",
        prompt="do thing",
        caller="test",
        model="sonnet",
        agent="reviewer",
        stage_log=stage_log,
        spend_log_path=spend,
    )
    assert captured, "Popen was not called"
    argv = captured[0]["argv"]
    expected_prefix = ["claude", "-p", "do thing", "--verbose", "--output-format", "stream-json"]
    assert argv[:len(expected_prefix)] == expected_prefix
    assert "--model" in argv and argv[argv.index("--model") + 1] == "sonnet"
    assert "--agent" in argv and argv[argv.index("--agent") + 1] == "reviewer"
    assert captured[0]["shell"] is False
    assert isinstance(handle, HeadlessHandle)
    assert handle.transcript_path == stage_log


def test_dispatch_headless_no_model_no_agent(monkeypatch, tmp_path):
    """When model/agent are omitted, the argv must not include them."""
    captured = []
    _patch_headless(monkeypatch, captured)
    spawn(
        mode="headless",
        prompt="hi",
        caller="t",
        stage_log=tmp_path / "log.jsonl",
        spend_log_path=tmp_path / "spend.jsonl",
    )
    argv = captured[0]["argv"]
    assert "--model" not in argv
    assert "--agent" not in argv


def test_dispatch_headless_stderr_warning_format(monkeypatch, capsys, tmp_path):
    """AC-03: stderr matches the AC-03 regex with em-dash literal."""
    _patch_headless(monkeypatch, [])
    spawn(
        mode="headless",
        prompt="hi",
        caller="pipeline.run_claude",
        model="sonnet",
        agent="implementer",
        stage_log=tmp_path / "log.jsonl",
        spend_log_path=tmp_path / "spend.jsonl",
    )
    err = capsys.readouterr().err.strip().splitlines()
    matching = [l for l in err if AC03_REGEX.match(l)]
    assert matching, f"AC-03 regex didn't match any stderr line. Got: {err!r}"
    line = matching[0]
    assert "pipeline.run_claude" in line
    assert "model=sonnet" in line
    assert "agent=implementer" in line
    # Em-dash, not hyphen-minus.
    assert " — " in line


def test_dispatch_headless_warning_uses_none_default(monkeypatch, capsys, tmp_path):
    """AC-03: missing model/agent default to literal 'none'."""
    _patch_headless(monkeypatch, [])
    spawn(
        mode="headless",
        prompt="hi",
        caller="x",
        stage_log=tmp_path / "log.jsonl",
        spend_log_path=tmp_path / "spend.jsonl",
    )
    err = capsys.readouterr().err
    assert "model=none" in err
    assert "agent=none" in err


def test_dispatch_headless_appends_sdk_spend_jsonl(monkeypatch, tmp_path):
    """AC-04: every headless spawn appends a JSONL row to spend log."""
    _patch_headless(monkeypatch, [])
    spend = tmp_path / "spend.jsonl"
    spawn(
        mode="headless",
        prompt="hi",
        caller="triage_common.dispatch_subagent",
        model="sonnet",
        agent="reviewer",
        stage_log=tmp_path / "log.jsonl",
        spend_log_path=spend,
    )
    rows = [json.loads(l) for l in spend.read_text(encoding="utf-8").strip().splitlines()]
    assert len(rows) == 1
    row = rows[0]
    assert set(row.keys()) >= {"ts", "caller", "model", "agent", "est_input_tokens"}
    assert row["caller"] == "triage_common.dispatch_subagent"
    assert row["model"] == "sonnet"
    assert row["agent"] == "reviewer"


def test_dispatch_headless_requires_stage_log(monkeypatch, tmp_path):
    _patch_headless(monkeypatch, [])
    with pytest.raises(DispatchError):
        spawn(mode="headless", prompt="x", caller="t",
              stage_log=None, spend_log_path=tmp_path / "s.jsonl")


def test_spawn_invalid_mode_raises(monkeypatch):
    _patch_min_version(monkeypatch)
    with pytest.raises(DispatchError):
        spawn(mode="banana", prompt="x", caller="t")  # type: ignore[arg-type]


def test_spawn_requires_caller(monkeypatch, tmp_path):
    _patch_min_version(monkeypatch)
    with pytest.raises(DispatchError):
        spawn(mode="headless", prompt="x", caller="",
              stage_log=tmp_path / "x.jsonl",
              spend_log_path=tmp_path / "s.jsonl")


# ---------------------------------------------------------------------------
# AC-07, AC-08, AC-09a, AC-09b: BgHandle behaviour
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("raw,expected", [
    ("working", "working"),
    ("done", "done"),
    ("blocked", "blocked"),
    ("error", "error"),
    ("future_unknown_value", "error"),
])
def test_bg_handle_poll_state_mapping(tmp_path, raw, expected):
    """AC-07: state.json state values map; unknown -> error."""
    state_path = tmp_path / "state.json"
    state_path.write_text(json.dumps({
        "sessionId": "s1", "cwd": "/x", "state": raw,
        "linkScanPath": str(tmp_path / "tr.jsonl"),
    }), encoding="utf-8")
    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path,
                 transcript_path=tmp_path / "tr.jsonl")
    assert h.poll() == expected


def test_bg_handle_transcript_path(tmp_path):
    """AC-08: transcript_path is the absolute path read from state.json's linkScanPath."""
    expected = tmp_path / "deep" / "session.jsonl"
    state_path = tmp_path / "state.json"
    state_path.write_text(json.dumps({
        "sessionId": "s1", "cwd": "/x", "state": "done",
        "linkScanPath": str(expected),
    }), encoding="utf-8")
    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path,
                 transcript_path=Path(expected))
    assert h.transcript_path == expected


def test_bg_handle_wait_raises_blocked_with_needs(monkeypatch, tmp_path):
    """AC-09a: wait() raises BlockedSessionError with needs text."""
    state_path = tmp_path / "state.json"
    state_path.write_text(json.dumps({
        "sessionId": "s1", "cwd": "/x", "state": "blocked",
        "needs": "what env should I deploy to?",
        "linkScanPath": str(tmp_path / "tr.jsonl"),
    }), encoding="utf-8")
    # No-op claude stop.
    monkeypatch.setattr(subprocess, "run", lambda *a, **k: CompletedProcess([], 0, "", ""))
    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path,
                 transcript_path=tmp_path / "tr.jsonl")
    with pytest.raises(BlockedSessionError) as exc_info:
        h.wait(timeout=1)
    err = exc_info.value
    assert err.short == "abc12345"
    assert err.needs == "what env should I deploy to?"


def test_bg_handle_wait_runs_claude_stop_on_blocked(monkeypatch, tmp_path):
    """AC-09b: before raising on blocked, ['claude', 'stop', <short>] is invoked."""
    state_path = tmp_path / "state.json"
    state_path.write_text(json.dumps({
        "sessionId": "s1", "cwd": "/x", "state": "blocked",
        "needs": "n",
        "linkScanPath": str(tmp_path / "tr.jsonl"),
    }), encoding="utf-8")
    captured = []
    def fake_run(argv, **kw):
        captured.append(list(argv))
        return CompletedProcess(argv, 0, "", "")
    monkeypatch.setattr(subprocess, "run", fake_run)
    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path,
                 transcript_path=tmp_path / "tr.jsonl")
    with pytest.raises(BlockedSessionError):
        h.wait(timeout=1)
    assert ["claude", "stop", "abc12345"] in captured


def test_bg_handle_wait_returns_zero_on_done(tmp_path):
    state_path = tmp_path / "state.json"
    state_path.write_text(json.dumps({
        "sessionId": "s1", "cwd": "/x", "state": "done",
        "linkScanPath": str(tmp_path / "tr.jsonl"),
    }), encoding="utf-8")
    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path,
                 transcript_path=tmp_path / "tr.jsonl")
    assert h.wait(timeout=1) == 0


def test_bg_handle_output_reads_transcript(tmp_path):
    """BgHandle.output() returns bg_transcript.parse_outcome on its transcript_path."""
    transcript = tmp_path / "tr.jsonl"
    transcript.write_text(
        json.dumps({"type": "result", "result": "the-output"}) + "\n",
        encoding="utf-8",
    )
    state_path = tmp_path / "state.json"
    state_path.write_text(json.dumps({
        "sessionId": "s1", "cwd": "/x", "state": "done",
        "linkScanPath": str(transcript),
    }), encoding="utf-8")
    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path, transcript_path=transcript)
    assert h.output() == "the-output"


# ---------------------------------------------------------------------------
# AC-10: HeadlessHandle behaviour
# ---------------------------------------------------------------------------

class _PollProc:
    def __init__(self, *, exit_code=0, poll_sequence=None):
        self._exit_code = exit_code
        self._seq = list(poll_sequence or [exit_code])

    def poll(self):
        if not self._seq:
            return self._exit_code
        return self._seq.pop(0)

    def wait(self, timeout=None):
        return self._exit_code

    def terminate(self):
        pass


def test_headless_handle_poll_exit_mapping(tmp_path):
    """AC-10: working (None), done (0), error (non-zero)."""
    p = tmp_path / "log.jsonl"
    p.write_text("", encoding="utf-8")

    # working
    h1 = HeadlessHandle(proc=_PollProc(poll_sequence=[None]),
                        transcript_path=p)
    assert h1.poll() == "working"

    # done
    h2 = HeadlessHandle(proc=_PollProc(poll_sequence=[0]),
                        transcript_path=p)
    assert h2.poll() == "done"

    # error
    h3 = HeadlessHandle(proc=_PollProc(poll_sequence=[2]),
                        transcript_path=p)
    assert h3.poll() == "error"


def test_headless_handle_output_assembles_partial(tmp_path):
    """HeadlessHandle.output reads stage_log JSONL the same way BgHandle does."""
    p = tmp_path / "log.jsonl"
    p.write_text(
        json.dumps({"type": "assistant", "message": {"content": [
            {"type": "text", "text": "partial answer"}]}}) + "\n",
        encoding="utf-8",
    )
    h = HeadlessHandle(proc=_PollProc(poll_sequence=[0]),
                       transcript_path=p)
    assert h.output() == "partial answer"


def test_headless_handle_wait_returns_exit_code(tmp_path):
    h = HeadlessHandle(proc=_PollProc(exit_code=42),
                       transcript_path=tmp_path / "log.jsonl")
    assert h.wait(timeout=1) == 42



# ---------------------------------------------------------------------------
# Regression: state.json populates sessionId before linkScanPath
# ---------------------------------------------------------------------------

def test_spawn_polls_for_linkscanpath_after_sessionid(monkeypatch, tmp_path):
    """state.json is written in stages: sessionId+cwd first, linkScanPath
    later. spawn() must re-poll for linkScanPath, not bail on the first read."""
    jobs_dir = tmp_path / "jobs"
    jobs_dir.mkdir()
    short = "abc12345"
    state_dir = jobs_dir / short
    state_dir.mkdir()
    state_path = state_dir / "state.json"
    final_lsp = str(tmp_path / "transcript.jsonl")

    # First read: sessionId present, linkScanPath empty.
    import json as _json
    state_path.write_text(_json.dumps({
        "sessionId": "s1", "cwd": str(tmp_path).replace("\\", "/"),
        "state": "working", "linkScanPath": "",
    }), encoding="utf-8")

    # _identify_bg_session returns on sessionId, so the first read succeeds.
    # The spawn() linkScanPath poll loop then keeps reading until it appears.
    # Simulate the daemon writing the field after a short delay.
    real_sleep = time.sleep
    sleep_count = [0]
    def fake_sleep(s):
        sleep_count[0] += 1
        if sleep_count[0] == 2:
            state_path.write_text(_json.dumps({
                "sessionId": "s1", "cwd": str(tmp_path).replace("\\", "/"),
                "state": "working", "linkScanPath": final_lsp,
            }), encoding="utf-8")
        # don't actually sleep in the test
    monkeypatch.setattr(time, "sleep", fake_sleep)

    def fake_run(argv, **kw):
        if argv[:2] == ["claude", "--version"]:
            return CompletedProcess(argv, 0, "2.1.141\n", "")
        if argv[:2] == ["claude", "--bg"]:
            return CompletedProcess(argv, 0, "backgrounded · abc12345\n", "")
        return CompletedProcess(argv, 0, "", "")
    monkeypatch.setattr(subprocess, "run", fake_run)

    handle = spawn(
        mode="interactive",
        prompt="hi",
        caller="regression-test",
        name="stage",
        cwd=str(tmp_path),
        jobs_dir=jobs_dir,
    )
    assert isinstance(handle, BgHandle)
    assert handle.transcript_path == Path(final_lsp)


def test_spawn_falls_back_to_slug_derived_path_when_linkscanpath_absent(monkeypatch, tmp_path):
    """template=bg sessions never write linkScanPath. spawn() must fall back
    to ~/.claude/projects/<slug>/<sessionId>.jsonl rather than raise."""
    jobs_dir = tmp_path / "jobs"
    jobs_dir.mkdir()
    short = "abc12345"
    state_dir = jobs_dir / short
    state_dir.mkdir()
    state_path = state_dir / "state.json"
    import json as _json
    state_path.write_text(_json.dumps({
        "sessionId": "abc12345-0000-0000-0000-000000000000",
        "cwd": str(tmp_path).replace("\\\\", "/"),
        "state": "working",
        # linkScanPath intentionally absent (template=bg behavior)
    }), encoding="utf-8")

    monkeypatch.setattr(time, "sleep", lambda _s: None)

    def fake_run(argv, **kw):
        if argv[:2] == ["claude", "--version"]:
            return CompletedProcess(argv, 0, "2.1.141\n", "")
        if argv[:2] == ["claude", "--bg"]:
            return CompletedProcess(argv, 0, "backgrounded · abc12345\n", "")
        return CompletedProcess(argv, 0, "", "")
    monkeypatch.setattr(subprocess, "run", fake_run)

    handle = spawn(
        mode="interactive",
        prompt="hi",
        caller="slug-fallback-test",
        name="stage",
        cwd=str(tmp_path),
        jobs_dir=jobs_dir,
    )
    assert isinstance(handle, BgHandle)
    # Slug-derived path lives under ~/.claude/projects/<slug>/<sid>.jsonl.
    p = str(handle.transcript_path)
    assert p.endswith("abc12345-0000-0000-0000-000000000000.jsonl"), p
    assert ".claude" in p and "projects" in p, p


def test_derive_transcript_path_slug_format():
    """Slug replaces every non-[A-Za-z0-9-] char with -."""
    import claude_dispatch as _m
    p = _m._derive_transcript_path(
        r"C:\Users\josh_\source\repos\ppdsw\ppds\.worktrees\dual-mode-dispatch",
        "abc12345-1111-2222-3333-444444444444",
    )
    s = str(p)
    assert "C--Users-josh--source-repos-ppdsw-ppds--worktrees-dual-mode-dispatch" in s
    assert s.endswith("abc12345-1111-2222-3333-444444444444.jsonl")



def test_bg_handle_wait_tolerates_empty_needs_blocked_transition(monkeypatch, tmp_path):
    """state=blocked with empty needs is treated as a startup transient.
    The wait() loop keeps polling until state moves to done/error or the
    timeout budget elapses. Real "stage asked a question" blocks (needs
    populated) still hard-fail immediately."""
    state_path = tmp_path / "state.json"
    monkeypatch.setattr(time, "sleep", lambda _s: None)

    seq = iter(["blocked", "blocked", "done"])
    def fake_read_state(self_arg):
        return {"sessionId": "s1", "cwd": "/x",
                "state": next(seq, "done"),
                "needs": "",
                "linkScanPath": str(tmp_path / "tr.jsonl")}
    monkeypatch.setattr(BgHandle, "_read_state", fake_read_state)

    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path,
                 transcript_path=tmp_path / "tr.jsonl")
    assert h.wait(timeout=10) == 0


def test_bg_handle_wait_still_hard_fails_when_needs_populated(monkeypatch, tmp_path):
    """A blocked state with populated needs is the real failure case
    (AC-09a) and must still raise BlockedSessionError immediately."""
    import json as _json
    state_path = tmp_path / "state.json"
    state_path.write_text(_json.dumps({
        "sessionId": "s1", "cwd": "/x",
        "state": "blocked",
        "needs": "what env should I deploy to?",
        "linkScanPath": str(tmp_path / "tr.jsonl"),
    }), encoding="utf-8")
    monkeypatch.setattr(subprocess, "run", lambda *a, **k: CompletedProcess([], 0, "", ""))
    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path,
                 transcript_path=tmp_path / "tr.jsonl")
    with pytest.raises(BlockedSessionError) as exc:
        h.wait(timeout=5)
    assert exc.value.needs == "what env should I deploy to?"


def test_bg_handle_wait_empty_needs_blocked_eventually_times_out(monkeypatch, tmp_path):
    """If state stays blocked-with-empty-needs forever, the timeout budget
    eventually triggers DispatchError (not BlockedSessionError)."""
    import json as _json
    state_path = tmp_path / "state.json"
    state_path.write_text(_json.dumps({
        "sessionId": "s1", "cwd": "/x",
        "state": "blocked",
        "needs": "",
        "linkScanPath": str(tmp_path / "tr.jsonl"),
    }), encoding="utf-8")
    monkeypatch.setattr(time, "sleep", lambda _s: None)
    # Make time advance so the budget elapses on first iteration.
    times = iter([1000.0, 1000.0, 9999.0, 9999.0, 9999.0])
    monkeypatch.setattr(time, "time", lambda: next(times, 9999.0))
    h = BgHandle(short="abc12345", session_id="s1",
                 state_path=state_path,
                 transcript_path=tmp_path / "tr.jsonl")
    with pytest.raises(DispatchError) as exc:
        h.wait(timeout=1)
    msg = str(exc.value)
    assert "timed out" in msg
    assert "no needs text" in msg
