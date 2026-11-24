"""The environment hook's launcher (`shakedown-safety-launcher.py`) and the
registered command around it: every abnormal end of the hook refuses.

Claude Code runs a command whose PreToolUse hook exits with anything but 2,
or outlives its registered timeout. The launcher runs the hook as a child
and passes on exit 0 and exit 2; any other end of the hook (a signal, a
crash, exit 1 or another code, a hang, a missing file) becomes a refusal,
exit 2. The registered command ends in ``|| exit 2`` so that a launcher
which cannot start (no interpreter) or is killed refuses as well.

Each abnormal end is produced for real: a stand-in hook placed next to a
copy of the launcher crashes, exits, hangs or is killed from outside while
it runs; the real hook is killed while it waits for its input.

Run: ``pytest tests/hooks/test_shakedown_safety_launcher.py -v``
"""
from __future__ import annotations

import ast
import json
import os
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
HOOKS = ROOT / ".claude" / "hooks"
LAUNCHER = HOOKS / "shakedown-safety-launcher.py"
HOOK = HOOKS / "shakedown-safety.py"
SETTINGS = ROOT / ".claude" / "settings.json"
POSIX = os.name == "posix"

BLOCK_PAYLOAD = json.dumps({"tool_name": "Bash", "tool_input": {"command": "ppds data delete --entity account"}})
PASS_PAYLOAD = json.dumps({"tool_name": "Bash", "tool_input": {"command": "git status"}})


def _launcher_constant(name: str) -> float:
    tree = ast.parse(LAUNCHER.read_text(encoding="utf-8"))
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(getattr(t, "id", None) == name for t in node.targets):
            return float(ast.literal_eval(node.value))
    raise AssertionError(f"{name} not found in the launcher")


TIMEOUT = _launcher_constant("_TIMEOUT_SECONDS")


def _registered():
    """The one registered hook entry that runs the launcher: (command, timeout, matcher)."""
    settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
    found = [(h["command"], h.get("timeout"), entry.get("matcher"))
             for entry in settings["hooks"]["PreToolUse"] for h in entry.get("hooks", [])
             if "shakedown-safety" in h.get("command", "")]
    assert len(found) == 1, found
    return found[0]


def _gate_env(tmp_path: Path, **extra) -> dict:
    """An allowlist the active environment is not on (so the real hook reads its input and refuses
    a ppds command) and an empty project folder."""
    config = tmp_path / "ppds-config"
    config.mkdir(exist_ok=True)
    (config / "profiles.json").write_text(json.dumps({
        "version": 2, "activeProfileIndex": 1,
        "profiles": [{"index": 1, "name": "p", "environment": {
            "displayName": "zz-prod-x", "uniqueName": "zz-prod-x", "url": "https://zz-prod-x.invalid/"}}],
    }), encoding="utf-8")
    project = tmp_path / "project"
    (project / ".claude").mkdir(parents=True, exist_ok=True)
    (project / ".claude" / "settings.json").write_text("{}", encoding="utf-8")
    env = {k: v for k, v in os.environ.items() if not k.startswith("PPDS_")}
    env.update({"PPDS_SAFE_ENVS": "zz-safe-a", "PPDS_CONFIG_DIR": str(config),
                "CLAUDE_PROJECT_DIR": str(project)})
    env.update(extra)
    return env


def _stand_in(tmp_path: Path, body: str) -> Path:
    """A copy of the launcher with a stand-in hook next to it; returns the launcher copy."""
    folder = tmp_path / "hooks"
    folder.mkdir(exist_ok=True)
    shutil.copy2(LAUNCHER, folder / LAUNCHER.name)
    (folder / HOOK.name).write_text(body, encoding="utf-8")
    return folder / LAUNCHER.name


def _launch(launcher: Path, payload: str, env=None, timeout: float = 60):
    start = time.monotonic()
    r = subprocess.run([sys.executable, str(launcher)], input=payload.encode("utf-8"),
                       capture_output=True, env=env, timeout=timeout)
    return r.returncode, r.stdout.decode("utf-8", "replace"), r.stderr.decode("utf-8", "replace"), \
        time.monotonic() - start


# -- the real hook through the launcher ------------------------------------------------


def test_a_refusal_stays_a_refusal(tmp_path):
    code, out, err, _ = _launch(LAUNCHER, BLOCK_PAYLOAD, _gate_env(tmp_path))
    assert code == 2 and "BLOCKED [shakedown-safety/env]" in err, err
    assert out == ""


def test_a_pass_stays_a_pass(tmp_path):
    code, out, err, _ = _launch(LAUNCHER, PASS_PAYLOAD, _gate_env(tmp_path))
    assert (code, out, err) == (0, "", ""), err


def test_a_large_input_reaches_the_hook_whole(tmp_path):
    """The launcher hands its own input on, not a copy: a payload far above a pipe buffer is read in
    full (the refusal names the last word of a 1 MB command)."""
    command = "echo " + "a " * 500_000 + "; ppds data delete --entity zzlastword"
    payload = json.dumps({"tool_name": "Bash", "tool_input": {"command": command}})
    code, _, err, _ = _launch(LAUNCHER, payload, _gate_env(tmp_path))
    assert code == 2, err


# -- abnormal ends of the hook ---------------------------------------------------------


@pytest.mark.parametrize("body, shows", [
    ("import faulthandler\nfaulthandler._sigsegv()\n", "ended abnormally"),
    ("raise RuntimeError('internal error')\n", "exit code 1"),
    ("import os\nos._exit(3)\n", "exit code 3"),
    ("import os\nos._exit(255)\n", "exit code 255"),
    ("def broken(:\n", "exit code 1"),
], ids=["segfault", "uncaught-exception", "exit-3", "exit-255", "syntax-error"])
def test_an_abnormal_end_refuses(tmp_path, body, shows):
    code, _, err, _ = _launch(_stand_in(tmp_path, body), BLOCK_PAYLOAD)
    assert code == 2, err
    assert "BLOCKED [shakedown-safety]" in err and shows in err, err


@pytest.mark.skipif(not POSIX, reason="signals other than kill exist on POSIX only")
@pytest.mark.parametrize("sig", ["SIGSEGV", "SIGABRT", "SIGTERM", "SIGKILL"])
def test_a_hook_ended_by_a_signal_refuses(tmp_path, sig):
    body = f"import os, signal\nos.kill(os.getpid(), signal.{sig})\nimport time\ntime.sleep(30)\n"
    code, _, err, _ = _launch(_stand_in(tmp_path, body), BLOCK_PAYLOAD)
    assert code == 2 and f"signal {sig}" in err, err


def test_a_hook_killed_mid_run_refuses(tmp_path):
    """The hook is killed from outside while it runs (it has started and is working)."""
    pid_file = tmp_path / "hook.pid"
    body = ("import os, sys, time\n"
            f"open({str(pid_file)!r}, 'w').write(str(os.getpid()))\n"
            "sys.stdin.buffer.read()\n"
            "time.sleep(30)\n")
    launcher = _stand_in(tmp_path, body)
    proc = subprocess.Popen([sys.executable, str(launcher)], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    proc.stdin.write(BLOCK_PAYLOAD.encode("utf-8"))
    proc.stdin.close()
    deadline = time.monotonic() + 30
    while not (pid_file.exists() and pid_file.read_text()) and time.monotonic() < deadline:
        time.sleep(0.02)
    os.kill(int(pid_file.read_text()), signal.SIGKILL if POSIX else signal.SIGTERM)
    out, err = proc.communicate(timeout=30)
    assert proc.returncode == 2, err
    assert b"ended abnormally" in err, err


@pytest.mark.skipif(not sys.platform.startswith("linux"), reason="finds the child through /proc")
def test_the_real_hook_killed_mid_run_refuses(tmp_path):
    """The real hook, killed while it waits for its input (it has loaded its settings and the
    allowlist and is reading stdin)."""
    proc = subprocess.Popen([sys.executable, str(LAUNCHER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, env=_gate_env(tmp_path))
    children = Path(f"/proc/{proc.pid}/task/{proc.pid}/children")
    deadline = time.monotonic() + 30
    child = ""
    while not child and time.monotonic() < deadline:
        child = children.read_text().strip() if children.exists() else ""
        time.sleep(0.02)
    assert child, "the launcher started no hook"
    time.sleep(0.3)  # let it get as far as reading its input
    os.kill(int(child.split()[0]), signal.SIGKILL)
    out, err = proc.communicate(timeout=30)
    assert proc.returncode == 2, err
    assert b"ended abnormally (signal SIGKILL)" in err, err


def test_a_hook_that_does_not_answer_is_killed_and_refused(tmp_path):
    marker = tmp_path / "still-running"
    body = ("import time\ntime.sleep(" + str(TIMEOUT + 3) + ")\n"
            f"open({str(marker)!r}, 'w').write('the hook outlived its launcher')\n")
    code, _, err, elapsed = _launch(_stand_in(tmp_path, body), BLOCK_PAYLOAD)
    assert code == 2 and f"did not answer within {TIMEOUT:g} seconds" in err, err
    assert elapsed < TIMEOUT + 3, elapsed
    time.sleep(4)
    assert not marker.exists(), "the hook was not killed"


def test_a_missing_hook_refuses(tmp_path):
    launcher = _stand_in(tmp_path, "")
    (launcher.parent / HOOK.name).unlink()
    code, _, err, _ = _launch(launcher, BLOCK_PAYLOAD)
    assert code == 2 and "missing" in err, err


@pytest.mark.skipif(not POSIX, reason="SIGINT to one process is POSIX only")
def test_an_interrupted_launcher_refuses(tmp_path):
    launcher = _stand_in(tmp_path, "import time\ntime.sleep(30)\n")
    proc = subprocess.Popen([sys.executable, str(launcher)], stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    time.sleep(1.0)
    proc.send_signal(signal.SIGINT)
    out, err = proc.communicate(timeout=30)
    assert proc.returncode == 2 and b"interrupted" in err, err


# -- the registered command ------------------------------------------------------------


def test_the_registered_command_is_the_launcher_and_refuses_on_its_failure():
    command, timeout, matcher = _registered()
    assert command == 'python "$CLAUDE_PROJECT_DIR/.claude/hooks/shakedown-safety-launcher.py" || exit 2'
    assert matcher == "Bash|PowerShell"
    # The launcher's limit stays at half of the registered timeout or less.
    assert timeout and TIMEOUT <= timeout / 2, (TIMEOUT, timeout)


def _sh():
    return shutil.which("sh") if POSIX else None


def _run_registered(env: dict, payload: str, path=None, project=ROOT):
    command, _, _ = _registered()
    env = dict(env, CLAUDE_PROJECT_DIR=str(project))
    if path is not None:
        env["PATH"] = path
    return subprocess.run([_sh(), "-c", command], input=payload.encode("utf-8"), capture_output=True,
                          env=env, timeout=60)


@pytest.mark.skipif(not _sh(), reason="needs a POSIX sh, as Claude Code uses for hook commands here")
def test_the_registered_command_passes_and_refuses(tmp_path):
    env = _gate_env(tmp_path)
    assert _run_registered(env, PASS_PAYLOAD).returncode == 0
    r = _run_registered(env, BLOCK_PAYLOAD)
    assert r.returncode == 2 and b"BLOCKED [shakedown-safety/env]" in r.stderr, r.stderr


@pytest.mark.skipif(not _sh(), reason="needs a POSIX sh")
def test_the_registered_command_refuses_without_an_interpreter(tmp_path):
    empty = tmp_path / "empty-path"
    empty.mkdir()
    r = _run_registered(_gate_env(tmp_path), BLOCK_PAYLOAD, path=str(empty))
    assert r.returncode == 2, r.stderr


@pytest.mark.skipif(not _sh(), reason="needs a POSIX sh")
def test_the_registered_command_refuses_when_the_launcher_is_killed(tmp_path):
    """The launcher itself ended by a signal: the shell's ``|| exit 2`` refuses."""
    project = tmp_path / "proj"
    hooks = project / ".claude" / "hooks"
    hooks.mkdir(parents=True)
    # A launcher stand-in that is killed by a signal as soon as it starts.
    (hooks / LAUNCHER.name).write_text("import os, signal\nos.kill(os.getpid(), signal.SIGKILL)\n",
                                       encoding="utf-8")
    bindir = tmp_path / "bin"
    bindir.mkdir()
    (bindir / "python").symlink_to(sys.executable)
    r = _run_registered(_gate_env(tmp_path), BLOCK_PAYLOAD, path=f"{bindir}{os.pathsep}{os.environ.get('PATH', '')}",
                        project=project)
    assert r.returncode == 2, r.stderr


# -- no thread at interpreter shutdown --------------------------------------------------

_THREAD_MODULES = {"threading", "_thread", "concurrent", "multiprocessing", "asyncio", "sched"}


@pytest.mark.parametrize("path", sorted(p.name for p in HOOKS.glob("*.py")
                                        if p.name.startswith(("shakedown-safety", "_"))))
def test_the_hook_files_import_no_thread_module(path):
    """No thread can be alive at interpreter shutdown if none is started: the hook, its helpers and
    the launcher import no module that starts one."""
    tree = ast.parse((HOOKS / path).read_text(encoding="utf-8"))
    names = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            names |= {a.name.split(".")[0] for a in node.names}
        elif isinstance(node, ast.ImportFrom) and node.module:
            names.add(node.module.split(".")[0])
    assert not names & _THREAD_MODULES, names & _THREAD_MODULES


@pytest.mark.parametrize("payload, expected", [(BLOCK_PAYLOAD, 2), (PASS_PAYLOAD, 0)], ids=["refuse", "pass"])
def test_only_the_main_thread_is_alive_when_the_hook_ends(tmp_path, payload, expected):
    """The real hook, run as __main__: at its exit only the main thread is alive, and no thread
    module was imported by it (``threading`` is imported here only after the hook has ended)."""
    code = ("import runpy, sys\n"
            "sys.argv = [sys.argv[1]]\n"
            "try:\n"
            "    runpy.run_path(sys.argv[0], run_name='__main__')\n"
            "except SystemExit as e:\n"
            "    loaded = sorted(m for m in ('threading', 'concurrent.futures', 'multiprocessing')\n"
            "                    if m in sys.modules)\n"
            "    import threading\n"
            "    sys.stderr.write('THREADS %d LOADED %s\\n' % (threading.active_count(), loaded))\n"
            "    raise\n")
    r = subprocess.run([sys.executable, "-c", code, str(HOOK)], input=payload.encode("utf-8"),
                       capture_output=True, env=_gate_env(tmp_path), timeout=60)
    err = r.stderr.decode("utf-8", "replace")
    assert r.returncode == expected, err
    assert "THREADS 1 LOADED []" in err, err
