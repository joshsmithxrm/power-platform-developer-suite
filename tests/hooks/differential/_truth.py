"""Ground truth: is an invocation harmful in a mode, according to the product's own parsers?

``judge(oracle, program, argv, mode)`` returns a list of ``Harm`` (empty = harmless). Every fact
about binding comes from the parse oracle (the CLI's real root command, ``McpSessionOptions.Parse``,
the product's ``QueryParser``); the only hand-written facts are which command paths only read
(``read_paths.json``, frozen) and which SQL statement kinds only read (below).

Severities:
* ``requirement`` -- R4 says the hook must refuse it (a pass is a defect).
* ``policy``      -- harmful by the brief's property but outside R4's letter or documented as allowed
  (per-command target overrides, the env read-only diagnostics). Reported and counted; a pass fails the
  suite only with ``PPDS_DIFF_STRICT=1``. Whether they should block is the maintainer's decision.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from _harness import LISTED

_READ = {tuple(p.split()) for p in json.loads((Path(__file__).with_name("read_paths.json")).read_text("utf-8"))["read"]}

# Statement kinds that only read or only touch session variables. Everything else counts as a write,
# including statement kinds that could not be classified. Containers (IF, WHILE, BEGIN/END) are
# neutral because the collector also reports every statement nested in them.
READ_SQL_KINDS = {"SelectStatement", "PrintStatement", "DeclareVariableStatement", "SetVariableStatement",
                  "IfStatement", "WhileStatement", "BeginEndBlockStatement"}

ENV_GROUPS = {"env", "org"}
ENV_READ_DIAGNOSTICS = {"list", "current", "who", "config", "type", "show"}
ENV_SWITCHES = {"select", "switch", "use"}


@dataclass(frozen=True)
class Harm:
    cause: str
    severity: str  # "requirement" | "policy"
    detail: str = ""


def _listed(value) -> bool:
    return isinstance(value, str) and value.strip().lower() in LISTED


def _explicit(opts: dict) -> dict:
    return {k: v for k, v in opts.items() if not v.get("implicit")}


def _opt_value(opts: dict, name: str):
    o = opts.get(name)
    return None if o is None or o.get("implicit") else o.get("value")


def cli_can_write(t: dict) -> tuple:
    """(writes?, why) for a parse answer that runs."""
    if t.get("tui"):
        return True, "no arguments starts the TUI (same as `interactive`)"
    path = tuple(t["path"])
    opts = t.get("options", {})
    if path == ("plugins", "deploy"):
        dry = _opt_value(opts, "--dry-run")
        return (dry is not True), f"--dry-run binds {dry!r}"
    if path == ("query", "sql"):
        if _opt_value(opts, "--file") is not None or _opt_value(opts, "--stdin") is True:
            return True, "SQL read from a file or stdin"
        sql = t.get("sql")
        if sql is None:
            return True, "no inline SQL bound"
        if not sql.get("parsed"):
            return False, "SQL does not parse (the command fails before executing)"
        kinds = sql.get("kinds", [])
        writes = [k for k in kinds if k not in READ_SQL_KINDS]
        return bool(writes), "statement kinds " + ",".join(kinds)
    if len(path) == 2 and path[0] in ENV_GROUPS and path[1] in ("config", "type"):
        given = set(_explicit(opts)) - {"--list", "--show", "--help"}
        args = [a for a in t.get("arguments", {}).values() if a.get("tokens")]
        return bool(given or args), "local settings change" if (given or args) else "--list/--show only"
    if path == ("version",):
        upd = _opt_value(opts, "--update")
        return (upd is True), f"--update binds {upd!r}"
    if len(path) >= 1 and path[0] == "org":
        path = ("env",) + path[1:]
    if path in _READ:
        return False, "read-only path"
    return True, "path not classified read-only"


def judge(oracle, program: str, argv: list, mode: str) -> tuple:
    """(truth, harms) for one invocation. ``truth`` is the oracle's answer."""
    harms = []
    if program == "ppds-mcp-server":
        t = oracle.mcp(argv)
        if not t.get("runs"):
            return t, harms
        if mode == "gate-unlisted":
            harms.append(Harm("mcp-starts-on-unlisted-active", "requirement"))
            return t, harms
        if mode == "shakedown" and not t.get("read_only"):
            harms.append(Harm("mcp-writable-in-shakedown", "requirement", "McpSessionOptions.ReadOnly is false"))
        env = t.get("environment")
        if env is not None and not _listed(env):
            harms.append(Harm("mcp-environment-override", "policy", f"--environment {env!r}"))
        if t.get("profile") is not None:
            harms.append(Harm("mcp-profile-override", "policy", f"--profile {t['profile']!r}"))
        bad = [e for e in t.get("allowed_env", []) if not _listed(e)]
        if bad:
            harms.append(Harm("mcp-allowed-env-unlisted", "policy", f"--allowed-env {bad!r}"))
        return t, harms

    t = oracle.cli(argv)
    if t.get("oracle_error"):
        harms.append(Harm("oracle-error", "requirement", t["oracle_error"]))
        return t, harms
    if not t.get("runs"):
        return t, harms
    path = tuple(t.get("path", []))
    opts = t.get("options", {})
    is_env = len(path) == 2 and path[0] in ENV_GROUPS
    if mode == "gate-unlisted":
        if is_env and path[1] in ENV_SWITCHES and _listed(_opt_value(opts, "--environment")):
            return t, harms  # switching INTO a listed environment is allowed (R4)
        if is_env and path[1] in ENV_READ_DIAGNOSTICS:
            harms.append(Harm("env-diagnostic-on-unlisted-active", "policy", " ".join(path)))
        else:
            harms.append(Harm("runs-on-unlisted-active", "requirement", " ".join(path) or "<root>"))
        return t, harms

    # Environment gate with a listed active environment (also applies during the shakedown,
    # because the allowlist is configured in every mode).
    if is_env and path[1] in ENV_SWITCHES:
        target = _opt_value(opts, "--environment")
        if not _listed(target):
            harms.append(Harm("switch-to-unlisted", "requirement", f"--environment binds {target!r}"))
    else:
        env = _opt_value(opts, "--environment")
        if env is not None and not _listed(env):
            harms.append(Harm("environment-override", "policy", f"--environment binds {env!r}"))
    if path == ("auth", "select"):
        harms.append(Harm("profile-switch", "policy", "auth select changes the active environment"))
    prof = _opt_value(opts, "--profile")
    if prof is not None:
        harms.append(Harm("profile-override", "policy", f"--profile binds {prof!r}"))

    if mode == "shakedown":
        writes, why = cli_can_write(t)
        if writes:
            harms.append(Harm("write-in-shakedown", "requirement", f"{' '.join(path) or '<root>'}: {why}"))
    return t, harms
