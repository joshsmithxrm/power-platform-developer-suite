"""The environment hook (`shakedown-safety.py`) for both shell tools.

Each case is written in Bash syntax and in PowerShell syntax and sent to
the hook in the payload shape recorded from Claude Code for that tool
(`fixtures/shell-tool-payloads.json`). The cases are the forms listed in
#1780 plus the PowerShell reading rules.

Run: ``pytest tests/hooks/test_shakedown_safety_shells.py -v``
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
HOOK_PATH = ROOT / ".claude" / "hooks" / "shakedown-safety.py"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "shell-tool-payloads.json"
SHAPES = json.loads(FIXTURES.read_text(encoding="utf-8"))

DEL = "data delete --entity account"
ENV_BLOCK = "BLOCKED [shakedown-safety/env]"
WRITE_BLOCK = "BLOCKED [shakedown-safety/readonly]"


def _payload(tool: str, command: str, cwd: str | None) -> dict:
    payload = json.loads(json.dumps(SHAPES[tool]))
    payload["tool_input"]["command"] = command
    if cwd is None:
        payload.pop("cwd", None)
    else:
        payload["cwd"] = cwd
    return payload


@pytest.fixture
def run(tmp_path):
    """Run the hook for (tool, command); ``mode`` is "gate" (an allowlist the
    active env is not on) or "shakedown" (active env allowed, write-block on)."""
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
    repo = tmp_path / "repo"  # stands in for the shell's working directory
    (repo / "src" / "PPDS.Cli").mkdir(parents=True)

    def _run(tool, command, mode, cwd=str(repo)):
        env = os.environ.copy()
        for k in ("PPDS_SAFE_ENVS", "PPDS_CONFIG_DIR", "PPDS_SHAKEDOWN"):
            env.pop(k, None)
        env.update({"PPDS_CONFIG_DIR": str(config), "CLAUDE_PROJECT_DIR": str(project)})
        if mode == "gate":
            env["PPDS_SAFE_ENVS"] = "some-other-env"
        else:
            env.update({"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_SHAKEDOWN": "1"})
        return subprocess.run([sys.executable, str(HOOK_PATH)],
                              input=json.dumps(_payload(tool, command, cwd)),
                              capture_output=True, text=True, timeout=30, env=env)

    _run.repo = repo
    return _run


# (id, Bash form, PowerShell form): PPDS writes that must be blocked during a
# shakedown and gated by the allowlist.
WRITES = [
    # #1780 "not detected"
    ("substitution-in-project",
     f"dotnet run --project $(git rev-parse --show-toplevel)/src/PPDS.Cli -- {DEL}",
     f'dotnet run --project "$(git rev-parse --show-toplevel)/src/PPDS.Cli" -- {DEL}'),
    ("substitution-unquoted-ps", None,
     f"dotnet run --project $(git rev-parse --show-toplevel)/src/PPDS.Cli -- {DEL}"),
    ("relative-project-after-cd",
     f"cd src/PPDS.Cli && dotnet run --project . -- {DEL}",
     f"Set-Location src\\PPDS.Cli; dotnet run --project . -- {DEL}"),
    ("relative-project-after-cd-ps-alias", None,
     f"cd src\\PPDS.Cli && dotnet run --project . -- {DEL}"),
    ("watch-without-run",
     f"dotnet watch --project src/PPDS.Cli -- {DEL}",
     f"dotnet watch --project src\\PPDS.Cli -- {DEL}"),
    ("tool-exec", f"dotnet tool exec PPDS.Cli -- {DEL}", f"dotnet tool exec PPDS.Cli@1.0.0 --yes -- {DEL}"),
    ("dnx", f"dnx PPDS.Cli {DEL}", f"dnx PPDS.Cli -- {DEL}"),
    ("continuation-before-project-value",
     f"dotnet run --project \\\n  src/PPDS.Cli -- {DEL}",
     f"dotnet run --project `\n  src/PPDS.Cli -- {DEL}"),
    # #1780 "write-block does not see the verb"
    ("no-separator-after-tl",
     f"dotnet run --project src/PPDS.Cli --tl off {DEL}",
     f"dotnet run --project src\\PPDS.Cli --tl off {DEL}"),
    # PowerShell reading rules (B2), each with its Bash counterpart
    ("statement-separator", f"git status; ppds {DEL}", f"git status; ppds {DEL}"),
    ("pipeline", f"ppds env list | ppds {DEL}", f"ppds env list | ppds {DEL}"),
    ("and-or", f"git status && ppds {DEL} || true", f"git status && ppds {DEL} || Write-Output x"),
    ("call-operator", f"\"ppds\" {DEL}", f"& ppds {DEL}"),
    ("call-operator-quoted-path",
     f"'/c/Program Files/PPDS/ppds.exe' {DEL}",
     f"& 'C:\\Program Files\\PPDS\\ppds.exe' {DEL}"),
    ("backtick-escape", f"p\\pds {DEL}", f"p`pds {DEL}"),
    ("line-continuation", f"ppds data \\\n  delete --entity account", f"ppds data `\n  delete --entity account"),
    ("sub-expression", f"echo $(ppds {DEL})", f"Write-Output $(ppds {DEL})"),
    ("script-block", f"{{ ppds {DEL}; }}", f"& {{ ppds {DEL} }}"),
    ("double-quotes", f'ppds data "delete" --entity "acc\\"ount"', 'ppds data "delete" --entity "acc`"ount"'),
    ("single-quotes", "ppds data 'delete' --entity 'acc'\\''ount'", "ppds data 'delete' --entity 'acc''ount'"),
    ("backslash-path", f"./src/PPDS.Cli/bin/ppds {DEL}", f".\\src\\PPDS.Cli\\bin\\ppds {DEL}"),
    ("exe", f"ppds.exe {DEL}", f"ppds.exe {DEL}"),
    ("path-qualified-exe", f"/c/tools/ppds.exe {DEL}", f"C:\\tools\\ppds.exe {DEL}"),
    ("env-assignment-first", f"PPDS_CONFIG_DIR=x ppds {DEL}", f"$env:PPDS_CONFIG_DIR = 'x'; ppds {DEL}"),
    # wrapped in another shell
    ("bash-c", f"bash -c 'ppds {DEL}'", f"bash -c 'ppds {DEL}'"),
    ("pwsh-command", f"pwsh -NoProfile -Command \"ppds {DEL}\"", f"pwsh -NoProfile -Command \"ppds {DEL}\""),
    ("cmd-c", f"cmd /c \"ppds {DEL}\"", f"cmd /c \"ppds {DEL}\""),
    # cannot tell -> invocation
    ("program-in-variable", f"x=ppds; $x {DEL}", f"$x = 'ppds'; & $x {DEL}"),
    ("alias", f"alias p=ppds; p {DEL}", f"Set-Alias p ppds; p {DEL}"),
    ("project-in-variable", f"dotnet run --project $p -- {DEL}", f"dotnet run --project $p -- {DEL}"),
    ("verb-in-variable", "ppds data $verb --entity account", "ppds data $verb --entity account"),
    ("unterminated-quote", f"ppds {DEL} 'oops", f"ppds {DEL} 'oops"),
]

# Read-only or not PPDS: never blocked during a shakedown.
READS = [
    ("version", "ppds --version", "ppds --version"),
    ("env-list", "ppds env list", "ppds env list"),
    ("dry-run", "ppds plugins deploy --dry-run", "ppds plugins deploy --dry-run"),
    ("mcp-read-only", "ppds-mcp-server --read-only", "ppds-mcp-server.exe --read-only"),
    # #1780 false block, fixed: --read-only before any unknown option reaches the server
    ("mcp-read-only-no-separator",
     "dotnet run --project src/PPDS.Mcp --read-only", "dotnet run --project src\\PPDS.Mcp --read-only"),
    ("other-project", f"dotnet run --project src/PPDS.DocsGen -- {DEL}", f"dotnet run --project src\\PPDS.DocsGen -- {DEL}"),
    ("dotnet-build", "dotnet build src/PPDS.Cli", "dotnet build src\\PPDS.Cli"),
    ("dotnet-watch-test", "dotnet watch test --project tests/PPDS.Cli.Tests",
     "dotnet watch test --project tests\\PPDS.Cli.Tests"),
    ("git", "git status", "git status"),
    ("env-assignment", "FOO=bar python -m pytest -q", None),
]


def _cases(table):
    out = []
    for case_id, bash, ps in table:
        if bash is not None:
            out.append(pytest.param("Bash", bash, id=f"{case_id}-bash"))
        if ps is not None:
            out.append(pytest.param("PowerShell", ps, id=f"{case_id}-powershell"))
    return out


@pytest.mark.parametrize("tool,command", _cases(WRITES))
def test_write_blocked_during_shakedown(run, tool, command):
    r = run(tool, command, "shakedown")
    assert r.returncode == 2, r.stderr
    assert WRITE_BLOCK in r.stderr


@pytest.mark.parametrize("tool,command", _cases(WRITES))
def test_write_gated_by_allowlist(run, tool, command):
    r = run(tool, command, "gate")
    assert r.returncode == 2, r.stderr
    assert ENV_BLOCK in r.stderr


@pytest.mark.parametrize("tool,command", _cases(READS))
def test_read_passes_during_shakedown(run, tool, command):
    r = run(tool, command, "shakedown")
    assert r.returncode == 0, r.stderr
    assert r.stderr == ""


@pytest.mark.parametrize("tool,command", _cases([
    ("unknown-option-before-read-only",
     "dotnet run --project src/PPDS.Mcp --some-option --read-only",
     "dotnet run --project src\\PPDS.Mcp --some-option --read-only"),
]))
def test_read_only_after_an_unknown_option_still_blocks(run, tool, command):
    """An option `dotnet run` does not know might take `--read-only` as its value."""
    r = run(tool, command, "shakedown")
    assert r.returncode == 2, r.stderr
    assert "missing --read-only" in r.stderr


@pytest.mark.parametrize("tool,command", _cases([
    ("commit-message", "git commit -m 'note: ppds data delete is guarded'",
     "git commit -m 'note: ppds data delete is guarded'"),
    ("search", "grep -rn 'ppds data delete' docs/", "Select-String -Path docs\\*.md -Pattern 'ppds data delete'"),
]))
def test_a_mention_is_treated_as_an_invocation(run, tool, command):
    """The maintainer's decision on #1780 (option 1): mentions are accepted
    false positives, in both shells."""
    r = run(tool, command, "shakedown")
    assert r.returncode == 2, r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_dotnet_run_in_the_project_directory(run, tool):
    r = run(tool, f"dotnet run -- {DEL}", "shakedown", cwd=str(run.repo / "src" / "PPDS.Cli"))
    assert r.returncode == 2, r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_dotnet_run_without_a_known_directory(run, tool):
    r = run(tool, f"dotnet run -- {DEL}", "shakedown", cwd=None)
    assert r.returncode == 2, r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_dotnet_run_elsewhere_passes(run, tool):
    r = run(tool, f"dotnet run -- {DEL}", "shakedown")
    assert r.returncode == 0, r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_cd_in_a_subshell_does_not_hide_the_project(run, tool):
    command = {"Bash": f"(cd /tmp) && dotnet run -- {DEL}",
               "PowerShell": f"pwsh -c 'Set-Location C:\\'; dotnet run -- {DEL}"}[tool]
    r = run(tool, command, "shakedown", cwd=str(run.repo / "src" / "PPDS.Cli"))
    assert r.returncode == 2, r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_cap_raised(run, tool):
    """A real `dotnet run` after 499 earlier `dotnet` words is examined."""
    sep = "; " if tool == "PowerShell" else " && "
    command = sep.join(["dotnet --info"] * 499 + [f"dotnet run --project src/PPDS.Cli -- {DEL}"])
    r = run(tool, command, "shakedown")
    assert r.returncode == 2, r.stderr
    assert "Pattern: data delete" in r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_past_the_cap_is_an_invocation(run, tool):
    sep = "; " if tool == "PowerShell" else " && "
    command = sep.join(["dotnet --info"] * 600)
    r = run(tool, command, "shakedown")
    assert r.returncode == 2, r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_encoded_command(run, tool):
    import base64

    good = base64.b64encode(f"ppds {DEL}".encode("utf-16-le")).decode()
    assert run(tool, f"pwsh -EncodedCommand {good}", "shakedown").returncode == 2
    assert run(tool, "pwsh -enc not-base64!", "shakedown").returncode == 2


_VALUED = ["--read-only=false", "--read-only=true", "--read-only="]
_MCP_FORMS = [
    "ppds-mcp-server {flag}",
    "ppds-mcp-server.exe {flag}",
    "dotnet run --project src/PPDS.Mcp {flag}",
    "dotnet run --project src/PPDS.Mcp -- {flag}",
]


def _shell_form(tool, form):
    return form.replace("src/PPDS.Mcp", "src\\PPDS.Mcp") if tool == "PowerShell" else form


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
@pytest.mark.parametrize("flag", _VALUED)
@pytest.mark.parametrize("form", _MCP_FORMS)
def test_read_only_with_a_value_blocks(run, tool, flag, form):
    """The server accepts only the bare flag; any `--read-only=<value>` starts it writable."""
    r = run(tool, _shell_form(tool, form).format(flag=flag), "shakedown")
    assert r.returncode == 2, r.stderr
    assert "only accepts the bare --read-only flag" in r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
@pytest.mark.parametrize("form", _MCP_FORMS)
def test_bare_read_only_passes(run, tool, form):
    r = run(tool, _shell_form(tool, form).format(flag="--read-only"), "shakedown")
    assert r.returncode == 0, r.stderr


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_valued_read_only_beside_the_bare_flag_blocks(run, tool):
    r = run(tool, "ppds-mcp-server --read-only --read-only=false", "shakedown")
    assert r.returncode == 2, r.stderr


def _run_bytes(stdin: bytes, env_extra: dict) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    for k in ("PPDS_SAFE_ENVS", "PPDS_CONFIG_DIR", "PPDS_SHAKEDOWN"):
        env.pop(k, None)
    env.update(env_extra)
    return subprocess.run([sys.executable, str(HOOK_PATH)], input=stdin, capture_output=True,
                          timeout=60, env=env)


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_hook_reads_utf8_bytes_and_writes_non_ascii(run, tool, tmp_path):
    """The input is read as UTF-8 bytes; a block message naming non-ASCII text
    is written without an encoding error (an error would let the command run)."""
    payload = _payload(tool, "ppds data delete --entity café — А", str(tmp_path))
    r = _run_bytes(json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                   {"PPDS_SAFE_ENVS": "x", "PPDS_CONFIG_DIR": str(tmp_path), "CLAUDE_PROJECT_DIR": str(tmp_path)})
    assert r.returncode == 2, r.stderr
    assert "BLOCKED [shakedown-safety/env]" in r.stderr.decode("utf-8")


def test_an_internal_error_blocks_a_ppds_command(monkeypatch, tmp_path):
    import importlib.util

    spec = importlib.util.spec_from_file_location("shakedown_safety_crash", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    sys.path.insert(0, str(HOOK_PATH.parent))
    spec.loader.exec_module(mod)

    def boom(*_args, **_kwargs):
        raise RuntimeError("bug")

    monkeypatch.setattr(mod, "find_ppds_invocations", boom)
    data = json.dumps(_payload("PowerShell", "ppds data delete", str(tmp_path))).encode()
    monkeypatch.setattr(mod.sys, "stdin", type("S", (), {"buffer": type("B", (), {"read": lambda self: data})()})())
    with pytest.raises(SystemExit) as exc:
        mod.main()
    assert exc.value.code == 2


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
def test_a_long_command_answers_in_bounded_time(run, tool):
    import time

    start = time.monotonic()
    r = run(tool, "echo " + "'a' " * 12000 + f"; ppds {DEL}", "shakedown")
    assert r.returncode == 2, r.stderr
    assert time.monotonic() - start < 8


def test_payload_shapes_were_recorded_for_both_tools():
    for tool in ("Bash", "PowerShell"):
        shape = SHAPES[tool]
        assert shape["tool_name"] == tool
        assert isinstance(shape["tool_input"].get("command"), str)
        assert "cwd" in shape
