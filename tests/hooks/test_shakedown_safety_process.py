"""The environment hook (`shakedown-safety.py`) as a process: input shapes,
caps, the time budget and internal errors.

Claude Code runs a command whose PreToolUse hook exits 1 or exceeds its
registered timeout, in both shell tools. So the hook must answer within
its budget, and any failure while a gate applies must end in exit 2 for a
command that mentions ppds, dotnet or dnx. Every test here runs the hook
as a real subprocess and writes its input as UTF-8 bytes.

Run: ``pytest tests/hooks/test_shakedown_safety_process.py -v``
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
HOOK_PATH = ROOT / ".claude" / "hooks" / "shakedown-safety.py"
SETTINGS = ROOT / ".claude" / "settings.json"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "shell-tool-payloads.json"
SHAPES = json.loads(FIXTURES.read_text(encoding="utf-8"))

DEL = "data delete --entity account"


@pytest.fixture
def env_for(tmp_path):
    """Process environment for ``mode``: "gate" (allowlist the active env is
    not on), "shakedown" (active env allowed, write-block on), "safe" (active
    env allowed, no shakedown) or "off" (no allowlist, no shakedown)."""
    config = tmp_path / "ppds-config"
    config.mkdir()
    (config / "profiles.json").write_text(json.dumps({
        "version": 2, "activeProfileIndex": 1,
        "profiles": [{"index": 1, "name": "p", "environment": {
            "displayName": "ppds-dev", "uniqueName": "ppds-dev",
            "url": "https://orgxyz.crm.dynamics.com/"}}],
    }), encoding="utf-8")
    project = tmp_path / "project"
    (project / ".claude" / "state").mkdir(parents=True)
    (project / ".claude" / "settings.json").write_text("{}", encoding="utf-8")

    def _env(mode, **extra):
        env = os.environ.copy()
        for k in ("PPDS_SAFE_ENVS", "PPDS_CONFIG_DIR", "PPDS_SHAKEDOWN", "PPDS_HOOK_BUDGET_SECONDS"):
            env.pop(k, None)
        env.update({"PPDS_CONFIG_DIR": str(config), "CLAUDE_PROJECT_DIR": str(project)})
        env.update({
            "gate": {"PPDS_SAFE_ENVS": "some-other-env"},
            "shakedown": {"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_SHAKEDOWN": "1"},
            "safe": {"PPDS_SAFE_ENVS": "ppds-dev"},
            "off": {},
        }[mode])
        env.update(extra)
        return env

    return _env


def _run(stdin: bytes, env: dict, argv=None) -> subprocess.CompletedProcess:
    return subprocess.run(argv or [sys.executable, str(HOOK_PATH)], input=stdin,
                          capture_output=True, timeout=60, env=env)


def _payload(tool, command, **changes) -> bytes:
    payload = json.loads(json.dumps(SHAPES.get(tool, SHAPES["Bash"])))
    payload["tool_input"]["command"] = command
    payload["cwd"] = "C:/work/repo"
    if tool is None:
        payload.pop("tool_name")
    else:
        payload["tool_name"] = tool
    payload.update(changes)
    return json.dumps(payload, ensure_ascii=False).encode("utf-8")


# -- tool names and payload shapes ----------------------------------------------


@pytest.mark.parametrize("tool", ["Shell", "bash", "pwsh", None])
@pytest.mark.parametrize("mode", ["gate", "shakedown"])
def test_an_unknown_or_missing_tool_name_is_read_in_both_dialects(env_for, tool, mode):
    for command in (f"ppds {DEL}", f"$r=ppds {DEL}", f"if($true){{ppds {DEL}}}"):
        r = _run(_payload(tool, command), env_for(mode))
        assert r.returncode == 2, (command, r.stderr)


@pytest.mark.parametrize("stdin", [
    json.dumps([{"tool_input": {"command": f"ppds {DEL}"}}]),
    json.dumps(f"ppds {DEL}"),
    json.dumps({"tool_name": "Bash", "tool_input": f"ppds {DEL}"}),
    json.dumps({"tool_name": "PowerShell", "tool_input": [f"ppds {DEL}"]}),
    json.dumps({"tool_name": "Bash", "tool_input": {"script": f"ppds {DEL}"}}),
    json.dumps({"tool_name": "Bash", "tool_input": {"command": [f"ppds {DEL}"]}}),
    json.dumps({"command": f"ppds {DEL}"}),
    "{not json ppds " + DEL,
], ids=["list", "string", "tool-input-string", "tool-input-list", "other-key", "command-list",
        "top-level-command", "invalid-json"])
def test_an_unexpected_shape_that_mentions_ppds_blocks(env_for, stdin):
    r = _run(stdin.encode("utf-8"), env_for("gate"))
    assert r.returncode == 2, r.stderr


@pytest.mark.parametrize("stdin", [
    json.dumps(["git status"]),
    json.dumps({"tool_name": "Bash", "tool_input": "git status"}),
    "",
    "{not json",
])
def test_an_unexpected_shape_without_a_mention_passes(env_for, stdin):
    assert _run(stdin.encode("utf-8"), env_for("gate")).returncode == 0


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_non_ascii_input_is_read_as_utf8(env_for, tool):
    """Typographic quotes are quotes in PowerShell; read as UTF-8 bytes, the
    name between them is found, and a message with non-ASCII text is written."""
    for command in ("& ‘ppds’ data delete --entity account", "ppds “data” delete --entity café"):
        r = _run(_payload(tool, command), env_for("shakedown"))
        assert r.returncode == 2, (command, r.stderr)
        assert "BLOCKED" in r.stderr.decode("utf-8")


def test_a_utf8_byte_order_mark_is_accepted(env_for):
    r = _run(b"\xef\xbb\xbf" + _payload("Bash", f"ppds {DEL}"), env_for("gate"))
    assert r.returncode == 2, r.stderr


# -- gate off ---------------------------------------------------------------------


@pytest.mark.parametrize("command", [f"ppds {DEL}", "a" * 300_000, "& $x data delete"],
                         ids=["write", "past-the-length-cap", "run-time-program"])
def test_no_allowlist_and_no_shakedown_passes_everything(env_for, command):
    r = _run(_payload("PowerShell", command), env_for("off"))
    assert r.returncode == 0, r.stderr
    assert r.stderr == b""


# -- caps ---------------------------------------------------------------------------


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
@pytest.mark.parametrize("command", [
    "echo " + "$(" * 10 + "git status" + ")" * 10,
], ids=["depth"])
def test_past_a_cap_is_an_invocation_without_a_mention(env_for, tool, command):
    r = _run(_payload(tool, command), env_for("shakedown"))
    assert r.returncode == 2, r.stderr
    r = _run(_payload(tool, command), env_for("gate"))
    assert r.returncode == 2, r.stderr


# Above the length cap the hook does not read the text; constant-cost checks
# decide: a program name or a letter escape refuses in every gated mode,
# run-time text refuses during a shakedown and with an unlisted active env.
_LONG = "a" * 200_050


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
@pytest.mark.parametrize("command,shakedown,gate,listed", [
    ("Write-Output " + _LONG + "; git status", 0, 0, 0),
    ("echo " + _LONG, 0, 0, 0),
    ("echo " + _LONG + "; ppds data delete --entity account", 2, 2, 2),
    ("echo " + _LONG + "; p'p'd's env list", 2, 2, 2),
    ("echo " + _LONG + "; bash -c x", 2, 2, 2),
    ("echo " + _LONG + "; Invoke-Expression $c", 2, 2, 2),
    ("echo " + _LONG + "; $'\\x70pds' env list", 2, 2, 2),
    ("echo " + _LONG + "; $X data delete", 2, 2, 0),
    ("echo " + _LONG + "; `$x` data delete", 2, 2, 0),
], ids=["ps-plain", "posix-plain", "names-ppds", "quoted-name", "shell", "code-word", "escape",
        "run-time", "backtick"])
def test_past_the_length_cap(env_for, tool, command, shakedown, gate, listed):
    for mode, expected in (("shakedown", shakedown), ("gate", gate), ("safe", listed)):
        r = _run(_payload(tool, command), env_for(mode))
        assert r.returncode == expected, (mode, r.stderr)
        if expected:
            assert b"longer than the hook reads" in r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_far_past_the_length_cap_answers_in_time(env_for, tool):
    command = "git status " + "'" * 20_000_000
    t = time.monotonic()
    r = _run(_payload(tool, command), env_for("safe"))
    assert r.returncode == 0, r.stderr
    # well inside the registered 10 s (about 1 s on an idle Linux machine)
    assert time.monotonic() - t < 8


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_below_the_caps_a_plain_command_passes(env_for, tool):
    r = _run(_payload(tool, "echo " + "a" * 150_000), env_for("shakedown"))
    assert r.returncode == 0, r.stderr


# -- the time budget ------------------------------------------------------------------


def _registered_timeout() -> float:
    settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
    for entry in settings["hooks"]["PreToolUse"]:
        for h in entry.get("hooks", []):
            if "shakedown-safety.py" in h.get("command", ""):
                return float(h["timeout"])
    raise AssertionError("shakedown-safety.py is not registered")


def _load_for_budget():
    import importlib.util

    sys.path.insert(0, str(HOOK_PATH.parent))
    spec = importlib.util.spec_from_file_location("shakedown_safety_budget", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def test_the_budget_is_well_inside_the_registered_timeout():
    """Interpreter start-up plus the hook's budget and the watchdog's grace
    (the latest the hook answers) must leave at least half of the registered
    timeout unused."""
    mod = _load_for_budget()
    startups = []
    for _ in range(3):
        start = time.monotonic()
        subprocess.run([sys.executable, "-c", "import json, re, shlex, bisect, unicodedata, base64"],
                       check=True, timeout=60, stdin=subprocess.DEVNULL, capture_output=True)
        startups.append(time.monotonic() - start)
    startup = max(startups)
    timeout = _registered_timeout()
    latest = mod._BUDGET_SECONDS + mod._WATCHDOG_GRACE_SECONDS
    assert latest + 2 * startup <= timeout / 2, (latest, startup, timeout)


@pytest.mark.parametrize("mode", ["gate", "shakedown"])
def test_the_deadline_is_an_invocation(env_for, mode):
    """Out of time, a read-only command counts as an invocation with an
    unknown verb (blocked during a shakedown and by the gate)."""
    for command in ("ppds env list", "git status"):
        r = _run(_payload("PowerShell", command), env_for(mode, PPDS_HOOK_BUDGET_SECONDS="0"))
        assert r.returncode == 2, (command, r.stderr)


def test_the_deadline_refuses_with_a_safe_active_env_too(env_for):
    """Out of time the hook cannot tell which environment a command reaches:
    refused even when the active env is listed."""
    r = _run(_payload("Bash", "ppds env list"), env_for("safe", PPDS_HOOK_BUDGET_SECONDS="0"))
    assert r.returncode == 2, r.stderr


def test_the_budget_override_cannot_raise_the_budget(env_for):
    code = ("import importlib.util, sys; sys.path.insert(0, sys.argv[1]);"
            "s = importlib.util.spec_from_file_location('h', sys.argv[2]);"
            "m = importlib.util.module_from_spec(s); s.loader.exec_module(m); print(m._budget())")
    out = subprocess.run([sys.executable, "-c", code, str(HOOK_PATH.parent), str(HOOK_PATH)],
                         capture_output=True, text=True, timeout=60, stdin=subprocess.DEVNULL,
                         env=env_for("off", PPDS_HOOK_BUDGET_SECONDS="999")).stdout
    assert float(out) <= 3.5


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
@pytest.mark.parametrize("command", [
    f"ppds {DEL} " + " ".join(f"'f{i}.txt'" for i in range(6000)),
    f"ppds {DEL} " + " ".join(f'"f{i}.txt"' for i in range(6000)),
    f"ppds {DEL} --filter {{" + "," * 100_000,
    "echo " + "'a' " * 12000 + f"; ppds {DEL}",
    # Many words naming the program in one statement (each one an argument
    # list of the words after it), then many short words (round 2, A1).
    f"ppds {DEL} ; echo " + "ppds " * 8797 + "a " * 32988,
    f"ppds {DEL} ; echo " + "ppds " * 13096 + "a," * 32000,
    "echo " + "ppds " * 40000,
    "echo " + "dotnet run --project src/PPDS.Cli " * 4000,
], ids=["single-quoted-words", "double-quoted-words", "brace-commas", "many-strings",
        "program-words-110k", "program-words-131k", "program-words-200k", "dotnet-words"])
@pytest.mark.parametrize("mode", ["shakedown", "gate"])
def test_large_inputs_answer_inside_the_budget(env_for, tool, command, mode):
    """Every input is answered (exit 2 here) within the budget plus start-up
    and the watchdog's grace, far inside the registered timeout."""
    start = time.monotonic()
    r = _run(_payload(tool, command), env_for(mode))
    elapsed = time.monotonic() - start
    assert r.returncode == 2, r.stderr
    mod = _load_for_budget()
    assert elapsed < mod._BUDGET_SECONDS + mod._WATCHDOG_GRACE_SECONDS + 1.0, elapsed
    assert elapsed < _registered_timeout() / 2, elapsed


# -- internal errors -------------------------------------------------------------------


def _with_failing_reader(stdin: bytes, env: dict) -> subprocess.CompletedProcess:
    """Run the hook as __main__ with ``_shellparse.read`` raising."""
    code = ("import runpy, sys; sys.path.insert(0, sys.argv[1]); import _shellparse;"
            "_shellparse.read = lambda *a, **k: (_ for _ in ()).throw(RuntimeError('injected'));"
            "runpy.run_path(sys.argv[2], run_name='__main__')")
    return _run(stdin, env, [sys.executable, "-c", code, str(HOOK_PATH.parent), str(HOOK_PATH)])


@pytest.mark.parametrize("mode", ["gate", "shakedown", "safe"])
@pytest.mark.parametrize("command", [f"ppds {DEL}", "ppds env list", "dotnet build", "d`n`x x"])
def test_an_internal_error_refuses_a_mention(env_for, mode, command):
    r = _with_failing_reader(_payload("PowerShell", command), env_for(mode))
    assert r.returncode == 2, r.stderr
    assert "internal error" in r.stderr.decode("utf-8")


def test_an_internal_error_without_a_mention_refuses(env_for):
    """Any internal error refuses, a command with no mention included."""
    for command in ("git status", "& $x data delete"):
        r = _with_failing_reader(_payload("Bash", command), env_for("gate"))
        assert r.returncode == 2, (command, r.stderr)
        assert "internal error" in r.stderr.decode("utf-8")


@pytest.mark.parametrize("mode", ["gate", "shakedown", "safe"])
def test_a_failing_stdin_read_refuses(env_for, mode):
    """The payload cannot be read: refused (exit 2), not a crash or a pass."""
    code = ("import runpy, sys; sys.path.insert(0, sys.argv[1]);"
            "sys.stdin = type('S', (), {'buffer': type('B', (), {'read': lambda self: 1 / 0})()})();"
            "runpy.run_path(sys.argv[2], run_name='__main__')")
    r = _run(_payload("Bash", f"ppds {DEL}"), env_for(mode),
             [sys.executable, "-c", code, str(HOOK_PATH.parent), str(HOOK_PATH)])
    assert r.returncode == 2, r.stderr
    assert "internal error" in r.stderr.decode("utf-8")


def test_an_internal_error_with_the_gate_off_passes(env_for):
    r = _with_failing_reader(_payload("Bash", f"ppds {DEL}"), env_for("off"))
    assert r.returncode == 0, r.stderr


@pytest.mark.parametrize("missing", ["_shellparse.py", "_pathfix.py"])
def test_a_hook_copy_without_its_helpers_refuses(env_for, tmp_path, missing):
    # the helper imports sit inside the guard: a missing helper is exit 2, not 1
    copy = tmp_path / "hooks"
    copy.mkdir()
    for name in ("shakedown-safety.py", "_shellparse.py", "_pathfix.py"):
        if name != missing:
            (copy / name).write_bytes((HOOK_PATH.parent / name).read_bytes())
    r = _run(_payload("Bash", "ppds env list"), env_for("safe"), argv=[sys.executable, str(copy / "shakedown-safety.py")])
    assert r.returncode == 2, r.stderr
    assert b"could not load its helper modules" in r.stderr
