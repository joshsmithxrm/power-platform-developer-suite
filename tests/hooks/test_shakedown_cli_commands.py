"""The shakedown write-block knows every command of the ppds CLI.

During a shakedown the environment hook passes a ppds command only when its
command path is one the hook classifies as read-only (``CLI_COMMANDS`` in
``.claude/hooks/shakedown-safety.py``). These tests derive the command tree
from the CLI's source (``cli_command_tree.py``, read only) and fail when the
CLI has a command the hook does not classify, or the hook lists one the CLI
no longer has. Then they run the hook as a process, in Bash and PowerShell
syntax, for write commands, unknown commands and one read command per group.

Run: ``pytest tests/hooks/test_shakedown_cli_commands.py -v``
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cli_command_tree  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
HOOK_PATH = ROOT / ".claude" / "hooks" / "shakedown-safety.py"
CLI = ROOT / "src" / "PPDS.Cli"

sys.path.insert(0, str(HOOK_PATH.parent))
_spec = importlib.util.spec_from_file_location("shakedown_safety_cli_commands", str(HOOK_PATH))
hook = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hook)

PATHS, PROBLEMS = cli_command_tree.command_paths()


def _canonical(path: tuple) -> tuple:
    return (hook._CLI_ALIASES.get(path[0], path[0]),) + path[1:]


# -- the hook's table matches the CLI -------------------------------------------


def test_the_command_tree_is_derived_without_problems():
    assert PROBLEMS == []
    assert len(PATHS) > 250


def test_every_cli_command_is_classified():
    missing = sorted(" ".join(p) for p in {_canonical(p) for p in PATHS} - set(hook.CLI_COMMANDS))
    assert missing == [], "classify these in CLI_COMMANDS (shakedown-safety.py): " + ", ".join(missing)


def test_the_table_lists_no_command_the_cli_does_not_have():
    derived = {_canonical(p) for p in PATHS} | {()}
    stale = sorted(" ".join(p) for p in set(hook.CLI_COMMANDS) - derived)
    assert stale == []


def test_every_alias_the_cli_defines_is_known():
    aliases = {p[0] for p in PATHS if len(p) == 1} - {p[0] for p in hook.CLI_COMMANDS if p}
    assert aliases <= set(hook._CLI_ALIASES), sorted(aliases - set(hook._CLI_ALIASES))


def test_every_command_name_in_the_source_is_in_the_tree():
    """A command built in a way the tree walk does not follow fails here."""
    names, files = set(), []
    for path in CLI.rglob("*.cs"):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        found = re.findall(r'new\s+Command\(\s*"([^"]+)"', path.read_text(encoding="utf-8-sig"))
        names |= set(found)
        if found:
            files.append(path)
    lasts = {p[-1] for p in PATHS}
    assert sorted(names - lasts) == []
    assert len(files) > 100


def test_every_group_has_children_and_every_leaf_none():
    for path, cls in hook.CLI_COMMANDS.items():
        children = [p for p in hook.CLI_COMMANDS if len(p) == len(path) + 1 and p[:len(path)] == path]
        if cls == "g":
            assert children, path
        elif cls not in ("r", "list"):
            assert not children, path


# -- in-process classification ----------------------------------------------------


def _argv(text: str) -> list:
    return ["ppds"] + text.split()


@pytest.mark.parametrize("text", [
    "metadata optionset add-option --name x", "metadata optionset update-option --name x",
    "metadata optionset remove-option --name x", "metadata optionset reorder --name x",
    "metadata entity add-statusreason --entity account", "metadata entity update-statusreason --entity account",
    "metadata entity remove-statusreason --entity account", "metadata attribute add-option --entity a",
    "metadata choice reorder --name x", "metadata key reactivate --entity a",
    "forms add-field --form f", "views set-filter --view v", "model-driven-app add-table --app a",
    "custom-apis set-plugin --name n", "flows enable --name n", "query history execute 3",
    "deployment-settings sync --file f", "env config --label x", "auth select --name p", "api request --path x",
    "serve", "interactive", "publish --type entity",
])
def test_writes_are_blocked(text):
    assert hook.is_mutation(_argv(text))[0], text


@pytest.mark.parametrize("text", ["frobnicate", "data frobnicate", "metadata optionset-x", "logs anything",
                                  "plugins download frobnicate", "--verbose data list", "data $x",
                                  "env current", "env switch x", "help"])
def test_unknown_commands_are_blocked(text):
    assert hook.is_mutation(_argv(text.replace("$x", hook.sp.DYN)))[0], text


@pytest.mark.parametrize("text,blocked", [
    ("query sql SELECT name FROM account", False),
    ("query sql DELETE FROM account", True),
    ("query sql UPDATE account SET name = 'x'", True),
    ("query sql INSERT INTO account (name) VALUES ('x')", True),
    ("query sql SELECT name INTO #t FROM account", True),
    ("query sql --file q.sql", True),
    ("query sql --stdin", True),
    ("query sql", True),
    ("plugins deploy --dry-run", False),
    ("plugins deploy -n", False),
    ("plugins deploy", True),
    ("plugins deploy --dry-run false", True),
    ("plugins deploy --dry-run=false", True),
    ("metadata entity account", False),
    ("metadata --help", False),
    ("--help", False),
    ("env config --list", False),
    ("env config --show", False),
    ("env config --label x", True),
    ("env config", True),
    ("env type --list", False),
    ("env type --remove x", True),
    ("", False),
])
def test_conditions(text, blocked):
    assert hook.is_mutation(_argv(text))[0] is blocked, text


def _read_paths() -> list:
    """One read-only command per group, the first in the table's order."""
    out = []
    for group in sorted(p for p, cls in hook.CLI_COMMANDS.items() if cls == "g"):
        reads = sorted(p for p, cls in hook.CLI_COMMANDS.items()
                       if cls == "r" and len(p) == len(group) + 1 and p[:len(group)] == group)
        if reads:
            out.append(reads[0])
    return out


READ_PATHS = _read_paths()


def test_read_paths_cover_most_groups():
    assert len(READ_PATHS) >= 30


@pytest.mark.parametrize("path", READ_PATHS, ids=[" ".join(p) or "root" for p in READ_PATHS])
def test_reads_pass(path):
    assert not hook.is_mutation(["ppds", *path])[0]


# -- the hook as a process, in both shells' syntax ------------------------------------


@pytest.fixture(scope="module")
def run(tmp_path_factory):
    base = tmp_path_factory.mktemp("cli")
    config = base / "ppds-config"
    config.mkdir()
    (config / "profiles.json").write_text(json.dumps({
        "version": 2, "activeProfileIndex": 1,
        "profiles": [{"index": 1, "name": "p", "environment": {"displayName": "ppds-dev"}}],
    }), encoding="utf-8")
    project = base / "project"
    (project / ".claude").mkdir(parents=True)
    (project / ".claude" / "settings.json").write_text("{}", encoding="utf-8")

    def _run(tool, command):
        payload = {"tool_name": tool, "tool_input": {"command": command}, "cwd": str(base)}
        env = os.environ.copy()
        for k in ("PPDS_SAFE_ENVS", "PPDS_CONFIG_DIR", "PPDS_SHAKEDOWN", "PPDS_HOOK_BUDGET_SECONDS"):
            env.pop(k, None)
        env.update({"PPDS_CONFIG_DIR": str(config), "CLAUDE_PROJECT_DIR": str(project),
                    "PPDS_SAFE_ENVS": "ppds-dev", "PPDS_SHAKEDOWN": "1"})
        r = subprocess.run([sys.executable, str(HOOK_PATH)], input=json.dumps(payload).encode("utf-8"),
                           capture_output=True, timeout=60, env=env)
        return r.returncode, r.stderr.decode("utf-8", errors="replace")

    return _run


def _shells(text: str) -> list:
    """The command in Bash syntax and in PowerShell syntax."""
    return [
        pytest.param("Bash", f"ppds {text} --name 'x'", id=f"bash-{text}"),
        pytest.param("PowerShell", f"& ppds.exe {text} --name 'x'", id=f"ps-{text}"),
    ]


_WRITE_TEXTS = [
    "metadata optionset add-option", "metadata optionset update-option", "metadata optionset remove-option",
    "metadata optionset reorder", "metadata entity add-statusreason", "metadata entity update-statusreason",
    "metadata entity remove-statusreason",
]
_UNKNOWN_TEXTS = ["data frobnicate"]


@pytest.mark.parametrize("tool,command", [p for t in _WRITE_TEXTS + _UNKNOWN_TEXTS for p in _shells(t)])
def test_write_and_unknown_commands_are_blocked_during_a_shakedown(run, tool, command):
    code, err = run(tool, command)
    assert code == 2, err
    assert "BLOCKED [shakedown-safety/readonly]" in err


@pytest.mark.parametrize("tool,command", [p for path in READ_PATHS for p in _shells(" ".join(path))])
def test_one_read_command_per_group_passes_during_a_shakedown(run, tool, command):
    code, err = run(tool, command)
    assert code == 0, err


@pytest.mark.parametrize("tool,command", [
    ("Bash", "ppds query sql 'SELECT name FROM account'"),
    ("PowerShell", "& ppds query sql 'SELECT name FROM account'"),
])
def test_sql_that_only_reads_passes(run, tool, command):
    assert run(tool, command)[0] == 0


@pytest.mark.parametrize("tool,command", [
    ("Bash", "ppds query sql 'DELETE FROM account'"),
    ("PowerShell", "& ppds query sql 'DELETE FROM account'"),
    ("Bash", "ppds query sql --file q.sql"),
    ("PowerShell", "ppds query sql --stdin"),
])
def test_sql_that_writes_or_is_not_in_the_text_is_blocked(run, tool, command):
    assert run(tool, command)[0] == 2
