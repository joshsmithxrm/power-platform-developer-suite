"""The environment hook (`shakedown-safety.py`): command forms that must count
as a PPDS invocation, each written in the syntax of the shell it concerns
and sent to the hook as a real process in the recorded payload shape.

Every case writes to Dataverse if it runs, so each must be blocked both by
the allowlist gate (active env not on the list) and during a shakedown.
The second half pins what each part of the hook contributes: the
PowerShell reading, the POSIX reading, and the accounting of mentions the
readings do not place.

Run: ``pytest tests/hooks/test_shakedown_safety_forms.py -v``
"""
from __future__ import annotations

import base64
import importlib.util
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
HOOK_PATH = ROOT / ".claude" / "hooks" / "shakedown-safety.py"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "shell-tool-payloads.json"
SHAPES = json.loads(FIXTURES.read_text(encoding="utf-8"))
SETTINGS = ROOT / ".claude" / "settings.json"

sys.path.insert(0, str(HOOK_PATH.parent))
_spec = importlib.util.spec_from_file_location("shakedown_safety_forms", str(HOOK_PATH))
hook = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hook)

DEL = "data delete --entity account"
B64 = base64.b64encode(f"ppds {DEL}".encode("utf-16-le")).decode()
ENV_BLOCK = "BLOCKED [shakedown-safety/env]"
WRITE_BLOCK = "BLOCKED [shakedown-safety/readonly]"


@pytest.fixture(scope="module")
def run(tmp_path_factory):
    """Run the hook for (tool, command) in ``mode`` "gate" (an allowlist the
    active env is not on) or "shakedown" (active env allowed, write-block on)."""
    base = tmp_path_factory.mktemp("forms")
    config = base / "ppds-config"
    config.mkdir()
    (config / "profiles.json").write_text(json.dumps({
        "version": 2, "activeProfileIndex": 1,
        "profiles": [{"index": 1, "name": "p", "environment": {
            "displayName": "ppds-dev", "uniqueName": "ppds-dev",
            "url": "https://orgxyz.crm.dynamics.com/"}}],
    }), encoding="utf-8")
    project = base / "project"
    (project / ".claude" / "state").mkdir(parents=True)
    (project / ".claude" / "settings.json").write_text("{}", encoding="utf-8")
    repo = base / "repo"
    (repo / "src" / "PPDS.Cli").mkdir(parents=True)

    def _run(tool, command, mode, cwd=str(repo)):
        payload = json.loads(json.dumps(SHAPES.get(tool, SHAPES["Bash"])))
        payload["tool_name"] = tool
        payload["tool_input"]["command"] = command
        payload["cwd"] = cwd
        env = os.environ.copy()
        for k in ("PPDS_SAFE_ENVS", "PPDS_CONFIG_DIR", "PPDS_SHAKEDOWN", "PPDS_HOOK_BUDGET_SECONDS"):
            env.pop(k, None)
        env.update({"PPDS_CONFIG_DIR": str(config), "CLAUDE_PROJECT_DIR": str(project)})
        if mode == "gate":
            env["PPDS_SAFE_ENVS"] = "some-other-env"
        else:
            env.update({"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_SHAKEDOWN": "1"})
        r = subprocess.run([sys.executable, str(HOOK_PATH)],
                           input=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                           capture_output=True, timeout=60, env=env)
        return r.returncode, r.stderr.decode("utf-8", errors="replace")

    return _run


PS = "PowerShell"
BASH = "Bash"

# (id, tool, command)
FORMS = [
    # A script block glued to the word before it.
    ("ps-foreach-glued", PS, f"1..3|%{{ppds {DEL}}}"),
    ("ps-foreach-object-glued", PS, f"1..3 | ForEach-Object{{ppds {DEL}}}"),
    ("ps-if-glued", PS, f"if($true){{ppds {DEL}}}"),
    ("ps-else-glued", PS, f"if($false){{1}}else{{ppds {DEL}}}"),
    ("ps-try-glued", PS, f"try{{ppds {DEL}}}catch{{}}"),
    ("ps-finally-glued", PS, f"try{{1}}finally{{ppds {DEL}}}"),
    ("ps-do-glued", PS, f"do{{ppds {DEL}}}while($false)"),
    ("ps-while-glued", PS, f"while($true){{ppds {DEL};break}}"),
    ("ps-foreach-statement-glued", PS, f"foreach($i in 1..2){{ppds {DEL}}}"),
    ("ps-switch-glued", PS, f"switch(1){{1{{ppds {DEL}}}}}"),
    ("ps-function-glued", PS, f"function f{{ppds {DEL}}};f"),
    ("ps-icm-glued", PS, f"icm{{ppds {DEL}}}"),
    ("ps-dot-glued", PS, f".{{ppds {DEL}}}"),
    ("ps-start-job-glued", PS, f"Start-Job{{ppds {DEL}}}"),
    ("ps-scriptblock-variable", PS, f"$sb={{ppds {DEL}}};$sb.Invoke()"),
    ("ps-if-glued-dotnet", PS, f"if($true){{dotnet run --project src\\PPDS.Cli -- {DEL}}}"),
    # An assignment written without spaces, and other forms that glue the name.
    ("ps-assign", PS, f"$r=ppds {DEL}"),
    ("ps-assign-exe", PS, f"$r=ppds.exe {DEL}"),
    ("ps-assign-null", PS, f"$null=ppds {DEL}"),
    ("ps-assign-plus", PS, f"$r+=ppds {DEL}"),
    ("ps-assign-env", PS, f"$env:OUT=ppds {DEL}"),
    ("ps-assign-typed", PS, f"[string]$r=ppds {DEL}"),
    ("ps-assign-member", PS, f"$x.y=ppds {DEL}"),
    ("ps-assign-scope", PS, f"$script:r=ppds {DEL}"),
    ("ps-assign-space-before", PS, f"$r =ppds {DEL}"),
    ("ps-hashtable", PS, f"@{{a=ppds {DEL}}}"),
    ("ps-assign-dotnet", PS, f"$r=dotnet run --project src\\PPDS.Cli -- {DEL}"),
    ("ps-assign-server", PS, "$p=ppds-mcp-server"),
    ("ps-paren-arguments", PS, "ppds('data','delete','--entity','account')"),
    ("ps-paren-arguments-exe", PS, "ppds.exe('data','delete','--entity','account')"),
    # Unicode separators.
    ("ps-nbsp-after-separator", PS, f"ppds env list;\u00a0ppds {DEL}"),
    ("ps-em-space-after-separator", PS, f"ppds env list;\u2003ppds {DEL}"),
    ("ps-nbsp-words", PS, f"ppds env list\u00a0; ppds\u00a0{DEL}"),
    ("ps-nel", PS, f"ppds env list\u0085ppds {DEL}"),
    ("ps-line-separator", PS, f"ppds env list\u2028ppds {DEL}"),
    # --% (stop parsing).
    ("ps-stop-parsing-quoted", PS, f'cmd --% /c "ppds" {DEL}'),
    ("ps-stop-parsing-ampersand", PS, f"ppds env list; cmd --% /c echo x&ppds {DEL}"),
    ("ps-stop-parsing-pipe", PS, f"ppds env list; cmd --% /c echo x|ppds {DEL}"),
    # Start-Process with the arguments as one string, or a working directory.
    ("ps-start-process-argument-string", PS,
     f'Start-Process dotnet -ArgumentList "run --project src/PPDS.Cli -- {DEL}"'),
    ("ps-start-process-positional-string", PS, f'Start-Process dotnet "run --project src/PPDS.Cli -- {DEL}"'),
    ("ps-start-process-working-directory", PS,
     "Start-Process dotnet -WorkingDirectory src\\PPDS.Cli -ArgumentList 'run','--','data','delete'"),
    ("ps-start-process-ppds", PS, f"Start-Process ppds -ArgumentList '{DEL}'"),
    ("ps-start-process-file-path", PS, f"Start-Process -FilePath ppds.exe -ArgumentList '{DEL}'"),
    # Encoded commands.
    ("ps-encoded-slash", PS, f"powershell /EncodedCommand {B64}"),
    ("ps-encoded-slash-short", PS, f"powershell /enc {B64}"),
    ("ps-encoded-double-dash", PS, f"pwsh --EncodedCommand {B64}"),
    ("ps-encoded-slash-e", PS, f"pwsh /e {B64}"),
    ("ps-encoded-via-start-process", PS, f'Start-Process pwsh "-enc {B64}"'),
    # dotnet tool execute.
    ("ps-tool-execute", PS, f"dotnet tool execute PPDS.Cli -- {DEL}"),
    ("bash-tool-execute", BASH, f"dotnet tool execute PPDS.Cli -- {DEL}"),
    # Escaped quotes inside a wrapper string.
    ("bash-pwsh-escaped-quotes", BASH, f'pwsh -Command "& \\"C:\\\\Program Files\\\\PPDS\\\\ppds.exe\\" {DEL}"'),
    ("bash-bash-escaped-quotes", BASH, f'bash -c "\\"ppds\\" {DEL}"'),
    ("bash-bash-escaped-quoted-path", BASH, f'bash -c "\\"/c/Program Files/PPDS/ppds\\" {DEL}"'),
    ("bash-cmd-escaped-quotes", BASH, f'cmd //c "\\"C:\\\\tools\\\\ppds.exe\\" {DEL}"'),
    ("bash-bash-escaped-backslash", BASH, f'bash -c "p\\\\pds {DEL}"'),
    # Wrapper code only known at run time.
    ("ps-pwsh-command-variable", PS, "$c='pp'+'ds data delete'; pwsh -Command $c"),
    ("ps-pwsh-c-variable", PS, "pwsh -c $c"),
    ("ps-cmd-variable", PS, "cmd /c $c"),
    ("ps-cmd-program-variable", PS, f"cmd /c $exe {DEL}"),
    ("ps-bash-variable", PS, "bash -c $c"),
    ("ps-pipe-to-iex", PS, "$c | iex"),
    ("ps-joined-string-to-iex", PS, "'pp'+'ds data delete' | iex"),
    ("bash-bash-c-variable", BASH, "bash -c $x"),
    ("bash-pipe-to-sh", BASH, "echo $c | sh"),
    ("bash-here-string-to-sh", BASH, 'sh <<< "$c"'),
    ("ps-scriptblock-create", PS, "[scriptblock]::Create($c).Invoke()"),
    ("ps-process-start", PS, "[Diagnostics.Process]::Start($exe,$a)"),
    # cmd syntax inside cmd /c.
    ("ps-cmd-cd-d", PS, f'cmd /c "cd /d src\\PPDS.Cli && dotnet run -- {DEL}"'),
    ("bash-cmd-cd-d", BASH, f'cmd /c "cd /d src\\\\PPDS.Cli && dotnet run -- {DEL}"'),
    ("ps-cmd-caret-quoted", PS, f'cmd /c "p^pds {DEL}"'),
    ("ps-cmd-caret", PS, f"cmd /c p^pds {DEL}"),
    # Brace, glob and escape forms.
    ("bash-brace-program", BASH, f"{{ppds,}} {DEL}"),
    ("bash-brace-inside-name", BASH, f"pp{{d,}}s {DEL}"),
    ("bash-brace-path", BASH, f"/usr/bin/{{ppds,}} {DEL}"),
    ("bash-glob-program", BASH, f"/usr/local/bin/ppd? {DEL}"),
    ("bash-glob-project", BASH, f"dotnet run --project src/PPDS.Cl? -- {DEL}"),
    ("bash-ansi-c-hex", BASH, f"$'\\x70pds' {DEL}"),
    ("ps-backtick-u", PS, f'& "`u{{70}}pds" {DEL}'),
    ("ps-alias-wildcard", PS, f"Set-Alias p (gcm pp*s).Source; p {DEL}"),
    # The verb behind an option, a redirect or Start-Process.
    ("bash-redirect-before-verb", BASH, "ppds data 2>&1 delete --entity account"),
    ("ps-redirect-before-verb", PS, "ppds data 2>&1 delete --entity account"),
    ("bash-option-before-verb", BASH, f"ppds --verbose {DEL}"),
    ("ps-option-before-verb", PS, f"ppds --verbose {DEL}"),
    # Working directories the hook cannot resolve.
    ("ps-set-location-colon", PS, f"Set-Location -Path:src\\PPDS.Cli; dotnet run -- {DEL}"),
    ("ps-push-location-stack", PS, f"Push-Location -StackName s src\\PPDS.Cli; dotnet run -- {DEL}"),
    ("bash-env-chdir", BASH, f"env -C src/PPDS.Cli dotnet run -- {DEL}"),
    # A program after a prefix that takes values.
    ("bash-timeout-variable", BASH, f"timeout 5 $x {DEL}"),
    ("bash-nice-variable", BASH, f"nice -n 5 $x {DEL}"),
    ("bash-find-exec-variable", BASH, f"find . -exec $x {DEL} \\;"),
    # Bash syntax sent through the PowerShell tool.
    ("ps-tool-builtin-cd", PS, f"builtin cd src/PPDS.Cli && dotnet run -- {DEL}"),
    ("ps-tool-if-cd", PS, f"if cd src/PPDS.Cli; then dotnet run -- {DEL}; fi"),
    ("ps-tool-command-v", PS, f'"$(command -v ppds)" {DEL}'),
]

@pytest.mark.parametrize("tool,command", [pytest.param(t, c, id=i) for i, t, c in FORMS])
def test_blocked_during_shakedown(run, tool, command):
    code, err = run(tool, command, "shakedown")
    assert code == 2, err
    assert WRITE_BLOCK in err


@pytest.mark.parametrize("tool,command", [pytest.param(t, c, id=i) for i, t, c in FORMS])
def test_gated_by_the_allowlist(run, tool, command):
    code, err = run(tool, command, "gate")
    assert code == 2, err
    assert ENV_BLOCK in err


# ---------------------------------------------------------------------------
# What each part of the hook contributes
# ---------------------------------------------------------------------------

_FAR = time.monotonic() + 3600


def _reading(command: str, dialect: str, cwd=hook._UNKNOWN_CWD) -> list:
    return hook._read_invocations(command, dialect, cwd, hook._mentions(command), _FAR)


_DELV = ["ppds", "data", "delete", "--entity", "account"]

# The PowerShell reading on its own finds the verb in each PowerShell rule.
PS_READINGS = [
    ("statement-separator", f"git status; ppds {DEL}", _DELV),
    ("pipeline", f"ppds env list | ppds {DEL}", _DELV),
    ("and-or", f"git status && ppds {DEL} || Write-Output x", _DELV),
    ("call-operator-quoted-path", f"& 'C:\\Program Files\\PPDS\\ppds.exe' {DEL}",
     ["ppds"] + DEL.split()),
    ("backtick-escape", f"p`pds {DEL}", _DELV),
    ("line-continuation", "ppds data `\n  delete --entity account", _DELV),
    ("sub-expression", f"Write-Output $(ppds {DEL})", _DELV),
    ("script-block", f"& {{ ppds {DEL} }}", _DELV),
    ("script-block-glued", f"if($true){{ppds {DEL}}}", _DELV),
    ("double-quotes-backtick", 'ppds data "delete" --entity "acc`"ount"', ["ppds", "data", "delete", "--entity", 'acc"ount']),
    ("single-quotes-doubled", "ppds data 'delete' --entity 'acc''ount'", ["ppds", "data", "delete", "--entity", "acc'ount"]),
    ("backtick-u-escape", 'ppds data "del`u{65}te" --entity account', _DELV),
    ("exe", f"C:\\tools\\ppds.exe {DEL}", ["ppds"] + DEL.split()),
    ("env-assignment-first", f"$env:PPDS_CONFIG_DIR = 'x'; ppds {DEL}", _DELV),
    ("assignment-glued", f"$r=ppds {DEL}", _DELV),
    ("nbsp-separator", f"ppds\u00a0data\u00a0delete --entity account", _DELV),
    ("command-rest", f"pwsh -NoProfile -Command ppds {DEL}", _DELV),
]


@pytest.mark.parametrize("command,argv", [pytest.param(c, a, id=i) for i, c, a in PS_READINGS])
def test_the_powershell_reading_finds_the_verb(command, argv):
    found = _reading(command, "ps")
    assert [a.lower() if k == 0 else a for k, a in enumerate(argv)] in [
        [w.lower() if k == 0 else w for k, w in enumerate(hit)] for hit in found]
    assert ["ppds", hook.UNREADABLE] not in found


# The POSIX reading finds these, whatever the tool name says.
POSIX_ONLY = [
    ("builtin-cd", f"builtin cd src/PPDS.Cli && dotnet run -- {DEL}"),
    ("if-cd", f"if cd src/PPDS.Cli; then dotnet run -- {DEL}; fi"),
    ("command-v", f'"$(command -v ppds)" {DEL}'),
]


@pytest.mark.parametrize("command", [pytest.param(c, id=i) for i, c in POSIX_ONLY])
def test_the_posix_reading_runs_for_the_powershell_tool(command):
    found = hook.find_ppds_invocations(command, "PowerShell", hook._UNKNOWN_CWD)
    assert any(hook.is_mutation(argv)[0] for argv in found), found


# Only the accounting of unplaced mentions finds these: no reading puts the
# name in a word it can classify.
ACCOUNTED = [
    ("ps-hashtable", f"@{{a=ppds {DEL}}}"),
    ("cmd-caret", f'cmd /c "p^pds {DEL}"'),
    ("python-list", "python -c \"import subprocess; subprocess.run(['ppds','data','delete'])\""),
    ("glued-option-string", f'foo --run="ppds {DEL}"'),
]


@pytest.mark.parametrize("command", [pytest.param(c, id=i) for i, c in ACCOUNTED])
@pytest.mark.parametrize("dialect", ["posix", "ps"])
def test_an_unplaced_mention_is_an_invocation(command, dialect):
    assert ["ppds", hook.UNREADABLE] in _reading(command, dialect)


@pytest.mark.parametrize("command", [
    "dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0",
    "dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter \"Category!=Integration\"",
    "ppds env select --environment=ppds-dev",
    "$env:PPDS_CONFIG_DIR = 'x'",
    "PPDS_CONFIG_DIR=x git status",
    "Get-ChildItem src\\PPDS.Cli",
    "ls ~/.dotnet/tools",
])
@pytest.mark.parametrize("dialect", ["posix", "ps"])
def test_a_mention_inside_another_name_is_placed(command, dialect):
    assert ["ppds", hook.UNREADABLE] not in _reading(command, dialect)


@pytest.mark.parametrize("command", ["$x = 1; Write-Output $x", "$env:FOO = 'bar'; python -m pytest -q"])
def test_a_statement_led_by_a_variable_blocks_during_a_shakedown(run, command):
    """Documented false block: the POSIX reading of `$x = ...` is a program
    only known at run time, and both readings always count."""
    code, err = run(PS, command, "shakedown")
    assert code == 2, err
