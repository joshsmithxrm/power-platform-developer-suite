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

import base64
import json
import re
from dataclasses import dataclass
from pathlib import Path

from _harness import LISTED, profile_environment

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


def _profile_harm(value, mode: str, cause: str, what: str):
    env = profile_environment(value, mode)
    if env is not None and _listed(env):
        return None
    return Harm(cause, "requirement", f"{what} {value!r} selects {env!r}")


def judge(oracle, program: str, argv: list, mode: str, overrides: dict | None = None) -> tuple:
    """(truth, harms) for one invocation. ``truth`` is the oracle's answer.

    ``overrides`` (oracle 2 only) describes the process environment the stand-in saw:
    ``config_dir_changed`` (PPDS_CONFIG_DIR differs from the one the hook read) and ``profile``
    (a PPDS_PROFILE value)."""
    overrides = overrides or {}
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
            harms.append(Harm("mcp-environment-override", "requirement", f"--environment {env!r}"))
        if t.get("profile") is not None:
            h = _profile_harm(t["profile"], mode, "mcp-profile-override", "--profile")
            if h:
                harms.append(h)
        bad = [e for e in t.get("allowed_env", []) if not _listed(e)]
        if bad:
            harms.append(Harm("mcp-allowed-env-unlisted", "requirement", f"--allowed-env {bad!r}"))
        _process_overrides(overrides, mode, harms)
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
    if is_env and path[1] in ENV_SWITCHES:
        target = _opt_value(opts, "--environment")
        if not isinstance(target, str) or not target.strip():
            # src/PPDS.Auth/Discovery/EnvironmentResolver.cs: a blank identifier resolves to nothing,
            # and env select then fails without saving anything.
            return t, harms
    if mode == "gate-unlisted":
        if is_env and path[1] in ENV_SWITCHES and _listed(_opt_value(opts, "--environment")):
            return t, harms  # switching INTO a listed environment is allowed (R4)
        if is_env and path[1] in ENV_READ_DIAGNOSTICS and not (
                path[1] in ("config", "type") and cli_can_write(t)[0]):
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
            harms.append(Harm("environment-override", "requirement", f"--environment binds {env!r}"))
    if path == ("auth", "select"):
        idx = _opt_value(opts, "--index")
        name = _opt_value(opts, "--name")
        chosen = str(idx) if idx is not None else name
        h = _profile_harm(chosen, mode, "profile-switch", "auth select")
        if h:
            harms.append(h)
    prof = _opt_value(opts, "--profile")
    if prof is not None:
        h = _profile_harm(prof, mode, "profile-override", "--profile")
        if h:
            harms.append(h)
    _process_overrides(overrides, mode, harms)

    if mode == "shakedown":
        writes, why = cli_can_write(t)
        if writes:
            harms.append(Harm("write-in-shakedown", "requirement", f"{' '.join(path) or '<root>'}: {why}"))
    return t, harms


def _process_overrides(overrides: dict, mode: str, harms: list) -> None:
    if overrides.get("config_dir_changed"):
        harms.append(Harm("config-folder-override", "requirement",
                          "PPDS_CONFIG_DIR differs from the profile store the hook read"))
    if overrides.get("profile") is not None:
        h = _profile_harm(overrides["profile"], mode, "profile-override", "PPDS_PROFILE")
        if h:
            harms.append(h)


# Cause families of the environment gate (listed active environment). For these, R4 asks for a refusal
# of an invocation the hook cannot fully place only when the command text itself shows switch words or
# a target option (coordinator decision on round 3); otherwise a pass is acceptable and is reported as
# policy severity.
ENV_GATE_CAUSES = {"switch-to-unlisted", "environment-override", "profile-override", "profile-switch",
                   "mcp-environment-override", "mcp-profile-override", "mcp-allowed-env-unlisted",
                   "config-folder-override"}
_SWITCH_GROUP = re.compile(r"(?i)(?<![\w-])(env|org|auth)(?![\w-])")
_SWITCH_VERB = re.compile(r"(?i)(?<![\w-])(select|switch|use)(?![\w-])")
_TARGET_OPT = re.compile(r"(?i)(--environment|(?<![\w-])-env|(?<![\w-])-e(?![\w-])|--profile|(?<![\w-])-p(?![\w-])|--allowed-env|"
                         r"PPDS_CONFIG_DIR|PPDS_PROFILE)")


_ENCODED = re.compile(r"(?i)-(?:e|ec|encodedcommand)\s+([A-Za-z0-9+/=]{8,})")


def _readable(text: str) -> str:
    """The text with quoting and escape characters removed and -EncodedCommand payloads decoded, so
    that a word the shells see (``au""th``, ``--environm''ent``) counts as shown."""
    extra = []
    for m in _ENCODED.finditer(text):
        try:
            extra.append(base64.b64decode(m.group(1) + "==").decode("utf-16-le", "replace"))
        except ValueError:
            pass
    return re.sub(r"[\'\"`\\]", "", "\n".join([text] + extra))


def text_shows_target(text: str) -> bool:
    t = _readable(text)
    return bool((_SWITCH_GROUP.search(t) and _SWITCH_VERB.search(t)) or _TARGET_OPT.search(t))


def apply_text_rule(harms: list, text: str) -> list:
    """Downgrade env-gate harms of a text that shows no switch words and no target option."""
    if text_shows_target(text):
        return harms
    return [Harm(h.cause + "-not-in-text", "policy", h.detail) if h.cause in ENV_GATE_CAUSES else h for h in harms]
