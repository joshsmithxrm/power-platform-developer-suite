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

from _harness import LISTED, profile_environment, truth_mode

_READ = {tuple(p.split()) for p in json.loads((Path(__file__).with_name("read_paths.json")).read_text("utf-8"))["read"]}

# Statement kinds that only read or only touch session variables. Everything else counts as a write,
# including statement kinds that could not be classified. Containers (IF, WHILE, BEGIN/END) are
# neutral because the collector also reports every statement nested in them.
READ_SQL_KINDS = {"SelectStatement", "PrintStatement", "DeclareVariableStatement", "SetVariableStatement",
                  "IfStatement", "WhileStatement", "BeginEndBlockStatement"}

ENV_GROUPS = {"env", "org"}
# Pure local or discovery reads on an unlisted active environment (policy severity). `env who` is not one:
# it sends WhoAmI to the active environment (EnvCommandGroup.ExecuteWhoAsync), so it is a requirement harm,
# unless --environment names another environment (then WhoAmI goes there; judged in judge()).
# `env list` asks Global Discovery, not the active environment; `env config` / `env type` in their --list /
# --show forms only read local settings.
ENV_READ_DIAGNOSTICS = {"list", "config", "type"}
ENV_OPTIONS = ("--environment", "--source-env", "--target-env")
PROFILE_OPTIONS = ("--profile", "--source-profile", "--target-profile")
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
    """(truth, harms) for one invocation in a hook mode. ``truth`` is the oracle's answer.

    The mode's rule is ``truth_mode(mode)``: ``gate-unlisted`` (the active environment is not listed or
    cannot be determined), ``gate-listed``, ``shakedown`` (listed, write-block on) or ``shakedown-noal``
    (write-block on, no allowlist). Profiles resolve against the mode's own store.

    ``overrides`` (oracle 2 only) describes the process environment the stand-in saw:
    ``config_dir_changed`` / ``home_changed`` (it reads another profile store), ``profile`` (a PPDS_PROFILE
    value) and ``environment_url`` (a PPDS_ENVIRONMENT_URL value)."""
    overrides = overrides or {}
    rule = truth_mode(mode)
    gate = rule != "shakedown-noal"
    harms = []
    if program == "ppds-mcp-server":
        t = oracle.mcp(argv)
        if not t.get("runs"):
            return t, harms
        if rule == "gate-unlisted":
            harms.append(Harm("mcp-starts-on-unlisted-active", "requirement"))
            return t, harms
        if rule.startswith("shakedown") and not t.get("read_only"):
            harms.append(Harm("mcp-writable-in-shakedown", "requirement", "McpSessionOptions.ReadOnly is false"))
        if gate:
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
    is_switch = is_env and path[1] in ENV_SWITCHES
    target = _opt_value(opts, "--environment") if is_switch else None
    if is_switch and (not isinstance(target, str) or not target.strip()):
        # src/PPDS.Auth/Discovery/EnvironmentResolver.cs: a blank identifier resolves to nothing,
        # and env select then fails without saving anything.
        return t, harms
    chosen_profile = None
    if path == ("auth", "select"):
        idx = _opt_value(opts, "--index")
        chosen_profile = str(idx) if idx is not None else _opt_value(opts, "--name")

    if rule == "gate-unlisted":
        if is_switch and _listed(target) and not _any_override(overrides):
            return t, harms  # switching INTO a listed environment is allowed (R4)
        if path == ("auth", "select") and _listed(profile_environment(chosen_profile, mode)) and not _any_override(overrides):
            return t, harms  # selecting a profile whose environment is listed is a switch into a listed one
        if is_env and path[1] == "who":
            # ExecuteWhoAsync sends WhoAmI to the --environment value when one is given (resolved by
            # EnvironmentResolutionService: a direct connection to that URL or Global Discovery, never the
            # active environment), else to the active environment.
            override = _opt_value(opts, "--environment")
            if isinstance(override, str) and override.strip():
                if _listed(override) and not _any_override(overrides):
                    return t, harms
                harms.append(Harm("environment-override", "requirement", f"--environment binds {override!r}"))
                return t, harms
        if is_env and path[1] in ENV_READ_DIAGNOSTICS and not (
                path[1] in ("config", "type") and cli_can_write(t)[0]):
            harms.append(Harm("env-diagnostic-on-unlisted-active", "policy", " ".join(path)))
        else:
            harms.append(Harm("runs-on-unlisted-active", "requirement", " ".join(path) or "<root>"))
        return t, harms

    if gate:
        # Environment gate with a listed active environment (also during the shakedown with an allowlist).
        if is_switch:
            if not _listed(target):
                harms.append(Harm("switch-to-unlisted", "requirement", f"--environment binds {target!r}"))
        for name in ENV_OPTIONS:
            if is_switch and name == "--environment":
                continue
            env = _opt_value(opts, name)
            if env is not None and not _listed(env):
                harms.append(Harm("environment-override", "requirement", f"{name} binds {env!r}"))
        if path == ("auth", "select"):
            h = _profile_harm(chosen_profile, mode, "profile-switch", "auth select")
            if h:
                harms.append(h)
        for name in PROFILE_OPTIONS:
            prof = _opt_value(opts, name)
            if prof is not None:
                h = _profile_harm(prof, mode, "profile-override", name)
                if h:
                    harms.append(h)
        _process_overrides(overrides, mode, harms)
    elif is_switch or path == ("auth", "select"):
        # Write-block without an allowlist: nothing checks the target, and the switch rewrites the
        # profile store's active environment.
        harms.append(Harm("switch-without-allowlist", "requirement", " ".join(path)))

    if rule.startswith("shakedown"):
        writes, why = cli_can_write(t)
        if writes:
            harms.append(Harm("write-in-shakedown", "requirement", f"{' '.join(path) or '<root>'}: {why}"))
    return t, harms


def _any_override(ov: dict) -> bool:
    return bool(ov.get("config_dir_changed") or ov.get("home_changed") or ov.get("profile") or ov.get("environment_url"))


def _process_overrides(overrides: dict, mode: str, harms: list) -> None:
    if overrides.get("home_changed"):
        harms.append(Harm("home-override", "requirement",
                          "HOME differs, so the program reads another profile store (~/.ppds)"))
    elif overrides.get("config_dir_changed"):
        harms.append(Harm("config-folder-override", "requirement",
                          "PPDS_CONFIG_DIR differs from the profile store the hook read"))
    if overrides.get("profile") is not None:
        h = _profile_harm(overrides["profile"], mode, "profile-override", "PPDS_PROFILE")
        if h:
            harms.append(h)
    url = overrides.get("environment_url")
    if url:
        host = url.split("//", 1)[-1].split("/", 1)[0].split(".", 1)[0]
        if not _listed(host):
            harms.append(Harm("environment-url-override", "requirement", f"PPDS_ENVIRONMENT_URL={url!r}"))


# Cause families of the environment gate (listed active environment). For these, R4 asks for a refusal
# of an invocation the hook cannot fully place only when the command text itself shows switch words or
# a target option (coordinator decision on round 3); otherwise a pass is acceptable and is reported as
# policy severity.
ENV_GATE_CAUSES = {"switch-to-unlisted", "environment-override", "profile-override", "profile-switch",
                   "mcp-environment-override", "mcp-profile-override", "mcp-allowed-env-unlisted",
                   "config-folder-override", "home-override", "environment-url-override"}
_SWITCH_GROUP = re.compile(r"(?i)(?<![\w-])(env|org|auth)(?![\w-])")
_SWITCH_VERB = re.compile(r"(?i)(?<![\w-])(select|switch|use)(?![\w-])")
_TARGET_OPT = re.compile(r"(?i)(--environment|(?<![\w-])-env|(?<![\w-])-e(?![\w-])|--profile|(?<![\w-])-p(?![\w-])|--allowed-env|"
                         r"PPDS_CONFIG_DIR|PPDS_PROFILE|PPDS_ENVIRONMENT_URL|--source-env|--target-env|--source-profile|--target-profile|(?<![\w-])HOME=|\$env:HOME|USERPROFILE|LOCALAPPDATA)")


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
    t = "\n".join([text] + extra)
    t = re.sub(r"(?:\\|`)\r?\n", "", t)                       # line continuations join the word
    t = re.sub(r"\\x([0-9A-Fa-f]{2})", lambda m: chr(int(m.group(1), 16)), t)
    t = re.sub(r"\\[ntrvf]|`[ntrvf]", " ", t)                  # printf / echo -e / PowerShell escapes
    t = re.sub(r"[\'\"`\\]", "", t)
    return " ".join(_expand_braces(w) for w in t.split())


_BRACE = re.compile(r"\{([^{}]*,[^{}]*)\}")


def _expand_braces(word: str, depth: int = 0) -> str:
    """Single-level brace expansion, as bash does it: a{b,c}d -> abd acd."""
    m = _BRACE.search(word)
    if not m or depth > 4:
        return word
    pre, post = word[:m.start()], word[m.end():]
    return " ".join(_expand_braces(pre + alt + post, depth + 1) for alt in m.group(1).split(","))


def text_shows_target(text: str) -> bool:
    t = _readable(text)
    return bool((_SWITCH_GROUP.search(t) and _SWITCH_VERB.search(t)) or _TARGET_OPT.search(t))


def apply_text_rule(harms: list, text: str, mode: str) -> list:
    """Downgrade env-gate harms of a text that shows no switch words and no target option. Only in a
    listed-environment gate without the shakedown: during a shakedown no such excuse applies."""
    if truth_mode(mode) != "gate-listed" or text_shows_target(text):
        return harms
    return [Harm(h.cause + "-not-in-text", "policy", h.detail) if h.cause in ENV_GATE_CAUSES else h for h in harms]


_CONDITIONAL = {("plugins", "deploy"), ("query", "sql"), ("env", "config"), ("env", "type"), ("version",)}


def normal_path(path) -> tuple:
    path = tuple(path)
    return ("env",) + path[1:] if path[:1] == ("org",) else path


def write_capable_paths(tree) -> set:
    """Every runnable path that is not read-only, plus the conditional ones (which write in some form)."""
    out = set()
    for c in tree.runnable:
        p = normal_path(c["path"])
        if p in _CONDITIONAL or p not in _READ:
            out.add(p)
    return out
