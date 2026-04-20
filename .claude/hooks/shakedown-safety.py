"""PreToolUse hook: shakedown safety boundary (env allowlist + write-block).

Consolidates the two prior hooks (``dev-env-check.py`` and
``shakedown-readonly.py``) into a single PreToolUse gate. They share ~80% of
their parsing logic (find ``ppds`` invocations in a Bash command, walk argv
against subcommand/verb tables); splitting that across two files made the
safety surface harder to review and invited drift.

Two concerns, one hook:

1. **Env allowlist gating (always on).** Every Bash invocation that mentions
   ``ppds`` / ``ppds-mcp-server`` is gated against a configured allowlist of
   dev/test env names. The active env is read directly from the PPDS profile
   store (``profiles.json``). Read-only subcommands (``env list``,
   ``env who``, ...) are always allowed; switching INTO a safe env is
   allowed even when currently on an unsafe one.

2. **Write-block during shakedown.** When ``PPDS_SHAKEDOWN=1`` (or whichever
   name is configured under ``safety.readonly_env_var``), write/mutation
   verbs (``create``, ``update``, ``delete``, ``plugins deploy`` without
   ``--dry-run``, ``solutions import``, etc.) are refused outright. This
   provides a mechanical boundary that the shakedown / verify skills rely on.

**Allowlist sources (checked in order):**

1. Env var ``$PPDS_SAFE_ENVS`` -- comma-separated list of env names.
2. ``.claude/settings.json`` top-level ``safety.shakedown_safe_envs`` array.
3. ``.claude/state/safe-envs.json`` -- legacy fallback, kept for a single
   release so in-flight branches don't break during migration. Will be
   removed once all trees have re-landed the settings.json form.
4. Neither set -- BLOCK with an instructive message.

**Envelope contract:** Claude Code wraps tool input as
``{"tool_name": "...", "tool_input": {"command": "..."}}``. Always read the
command from the nested ``tool_input`` dict -- reading at the top level was
the v1-prelaunch hook bug fixed by PR #816.

**Fail-safe:** when the active env cannot be determined the hook BLOCKS
rather than allows -- an unknown env is more dangerous than a false
positive.

**Bypass the write-block:** unset the configured env var (default
``PPDS_SHAKEDOWN``). There is intentionally no softer bypass -- if a write
is genuinely needed, that takes deliberate action.
"""

from __future__ import annotations

import json
import os
import re
import shlex
import sys
from typing import Iterable, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import normalize_msys_path  # noqa: E402


# ---------------------------------------------------------------------------
# Settings loader (new home for the allowlist + env var name)
# ---------------------------------------------------------------------------


_DEFAULT_READONLY_ENV_VAR = "PPDS_SHAKEDOWN"


def _project_dir() -> str:
    return normalize_msys_path(
        os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())
    )


def _settings_path() -> str:
    return os.path.join(_project_dir(), ".claude", "settings.json")


def _legacy_safe_envs_path() -> str:
    return os.path.join(_project_dir(), ".claude", "state", "safe-envs.json")


def _read_json(path: str) -> Optional[dict]:
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, json.JSONDecodeError):
        return None


def load_safety_config() -> dict:
    """Return a dict with ``shakedown_safe_envs`` and ``readonly_env_var``.

    Reads from ``.claude/settings.json`` under the ``safety`` top-level key.
    Falls back to sensible defaults when missing. Never raises.
    """
    settings = _read_json(_settings_path()) or {}
    safety = settings.get("safety") if isinstance(settings, dict) else None
    if not isinstance(safety, dict):
        safety = {}

    safe_envs = safety.get("shakedown_safe_envs")
    if not isinstance(safe_envs, list):
        safe_envs = None

    env_var = safety.get("readonly_env_var")
    if not isinstance(env_var, str) or not env_var.strip():
        env_var = _DEFAULT_READONLY_ENV_VAR

    return {
        "shakedown_safe_envs": safe_envs,
        "readonly_env_var": env_var.strip(),
    }


def load_allowlist(config: Optional[dict] = None) -> Optional[list]:
    """Return the list of safe env names, or None when no allowlist configured.

    Resolution order:
    1. ``$PPDS_SAFE_ENVS`` env var (comma-separated).
    2. ``safety.shakedown_safe_envs`` in ``.claude/settings.json``.
    3. ``.claude/state/safe-envs.json`` (legacy fallback -- migration aid).
    4. None -> caller treats as a hard block with a config-help message.
    """
    env_var = os.environ.get("PPDS_SAFE_ENVS", "").strip()
    if env_var:
        return [name.strip() for name in env_var.split(",") if name.strip()]

    if config is None:
        config = load_safety_config()
    safe = config.get("shakedown_safe_envs")
    if isinstance(safe, list):
        return [str(name).strip() for name in safe if str(name).strip()]

    # Legacy fallback -- keep for one release so mid-migration trees still work.
    legacy = _read_json(_legacy_safe_envs_path())
    if isinstance(legacy, dict):
        legacy_safe = legacy.get("safe_envs")
        if isinstance(legacy_safe, list):
            return [str(name).strip() for name in legacy_safe if str(name).strip()]

    return None


# ---------------------------------------------------------------------------
# Active env resolution (read profiles.json directly -- no shelling out)
# ---------------------------------------------------------------------------


def _profile_store_path() -> str:
    """Mirror PPDS.Auth.Profiles.ProfilePaths.ProfilesFile resolution.

    Priority: PPDS_CONFIG_DIR env var > %LOCALAPPDATA%/PPDS on Windows,
    ~/.ppds elsewhere.
    """
    override = os.environ.get("PPDS_CONFIG_DIR", "").strip()
    if override:
        base = override
    elif sys.platform == "win32":
        local_app = os.environ.get("LOCALAPPDATA", "")
        if not local_app:
            local_app = os.path.join(
                os.path.expanduser("~"), "AppData", "Local"
            )
        base = os.path.join(local_app, "PPDS")
    else:
        base = os.path.join(os.path.expanduser("~"), ".ppds")
    return os.path.join(base, "profiles.json")


def get_active_env_names() -> list:
    """Return candidate identifiers for the active env.

    Empty list means no active env (or store missing/malformed) -- caller
    must treat that as a fail-safe BLOCK.
    """
    data = _read_json(_profile_store_path())
    if not isinstance(data, dict):
        return []

    profiles = data.get("profiles") or []
    if not isinstance(profiles, list) or not profiles:
        return []

    # Prefer index-based lookup (matches ProfileCollection.ActiveProfile).
    active_idx = data.get("activeProfileIndex")
    active_name = data.get("activeProfile")
    active = None
    if isinstance(active_idx, int):
        active = next(
            (p for p in profiles if isinstance(p, dict) and p.get("index") == active_idx),
            None,
        )
    if active is None and isinstance(active_name, str) and active_name:
        active = next(
            (
                p
                for p in profiles
                if isinstance(p, dict)
                and isinstance(p.get("name"), str)
                and p.get("name", "").lower() == active_name.lower()
            ),
            None,
        )
    if active is None:
        return []

    env = active.get("environment") or {}
    if not isinstance(env, dict):
        return []

    candidates = []
    for key in ("displayName", "uniqueName"):
        val = env.get(key)
        if isinstance(val, str) and val.strip():
            candidates.append(val.strip())
    url = env.get("url")
    if isinstance(url, str) and url.strip():
        m = re.match(r"https?://([^/]+)", url.strip(), re.IGNORECASE)
        if m:
            host = m.group(1)
            candidates.append(host)
            label = host.split(".", 1)[0]
            if label and label != host:
                candidates.append(label)
    return candidates


def env_matches_allowlist(candidates: Iterable, allowlist: Iterable) -> bool:
    """True when any candidate matches any allowlist entry (case-insensitive)."""
    cand_lower = {c.lower() for c in candidates if c}
    allow_lower = {a.lower() for a in allowlist if a}
    return bool(cand_lower & allow_lower)


# ---------------------------------------------------------------------------
# Bash command parsing (shared by both concerns)
# ---------------------------------------------------------------------------


_PPDS_PROGRAM_RE = re.compile(
    r"""
    (?:^|[\s|;&(`])
    (?:[^\s|;&()`]*[\\/])?
    (ppds(?:\.exe)?|ppds-mcp-server(?:\.exe)?)
    (?=$|[\s|;&)`])
    """,
    re.IGNORECASE | re.VERBOSE,
)

_SHELL_OPERATORS = {"|", ";", "&", "&&", "||", ">", "<", ">>", "<<", chr(96), "(", ")"}


def find_ppds_invocations(command: str) -> list:
    """Return a list of argv-style token lists for each ppds invocation.

    Handles compound bash (``cd foo && ppds env list``) and pipes. The CLI
    argv tokens stop at the first shell operator after the program name.
    """
    if not command:
        return []

    out = []
    for match in _PPDS_PROGRAM_RE.finditer(command):
        prog = match.group(1)
        tail = command[match.end():]
        try:
            tokens = shlex.split(tail, posix=True)
        except ValueError:
            tokens = tail.split()

        args = []
        for tok in tokens:
            if tok in _SHELL_OPERATORS:
                break
            args.append(tok)
        out.append([prog] + args)
    return out


# ---------------------------------------------------------------------------
# Concern 1: env allowlist decision logic
# ---------------------------------------------------------------------------


_ENV_READONLY_SUBCOMMANDS = {"list", "current", "who", "config", "type", "show"}
_ENV_SWITCH_SUBCOMMANDS = {"switch", "select", "use"}


def decide_env(argv, allowlist, active):
    """Return (allow, reason). reason is empty on allow, a message on block."""
    if not argv:
        return True, ""

    prog = os.path.basename(argv[0]).lower()
    if prog.endswith(".exe"):
        prog = prog[:-4]

    if prog == "ppds-mcp-server":
        if env_matches_allowlist(active, allowlist):
            return True, ""
        return False, _block_msg_active_not_allowed(active, allowlist, argv)

    if prog != "ppds":
        return True, ""

    if len(argv) < 2:
        return True, ""

    sub = argv[1].lower()

    if sub == "env" or sub == "org":
        if len(argv) < 3:
            return True, ""
        env_sub = argv[2].lower()
        if env_sub in _ENV_READONLY_SUBCOMMANDS:
            return True, ""
        if env_sub in _ENV_SWITCH_SUBCOMMANDS:
            target = _extract_switch_target(argv[3:])
            if not target:
                return True, ""
            if env_matches_allowlist([target], allowlist):
                return True, ""
            return False, _block_msg_target_not_allowed(target, allowlist, argv)
        if env_matches_allowlist(active, allowlist):
            return True, ""
        return False, _block_msg_active_not_allowed(active, allowlist, argv)

    if env_matches_allowlist(active, allowlist):
        return True, ""
    return False, _block_msg_active_not_allowed(active, allowlist, argv)


def _extract_switch_target(args):
    """Pull the target env name from a ``switch``/``select`` argv tail."""
    for i, tok in enumerate(args):
        if tok in ("--environment", "-env", "--env", "-e"):
            if i + 1 < len(args):
                return args[i + 1]
            return ""
        if tok.startswith("--environment="):
            return tok.split("=", 1)[1]
        if tok.startswith("-env="):
            return tok.split("=", 1)[1]
        if tok.startswith("-"):
            continue
        return tok
    return ""


# ---------------------------------------------------------------------------
# Concern 2: write/mutation decision logic
# ---------------------------------------------------------------------------


_READONLY_SUBCOMMANDS = {
    "env": {"list", "current", "who", "config", "type", "show", "switch", "select", "use", "test"},
    "logs": {"*"},
    "auth": {"list", "show"},
    "version": {"*"},
    "help": {"*"},
}

_MUTATION_VERBS = {
    "create",
    "update",
    "delete",
    "remove",
    "import",
    "apply",
    "register",
    "unregister",
    "publish",
    "truncate",
    "drop",
    "reset",
    "set",
}


def _plugins_deploy_blocked(args: list) -> bool:
    return not any(
        arg in ("--dry-run", "-n") or arg.startswith("--dry-run=")
        for arg in args
    )


_NAMED_MUTATIONS = {
    ("plugins", "deploy"): _plugins_deploy_blocked,
}


def is_mutation(argv: list) -> tuple:
    """Return (is_mutation, reason). reason is empty when not a mutation."""
    if not argv:
        return False, ""

    prog = os.path.basename(argv[0]).lower()
    if prog.endswith(".exe"):
        prog = prog[:-4]
    if prog not in ("ppds", "ppds-mcp-server"):
        return False, ""

    if prog == "ppds-mcp-server":
        if any(
            arg == "--read-only" or arg.startswith("--read-only=")
            for arg in argv[1:]
        ):
            return False, ""
        return True, "ppds-mcp-server (missing --read-only flag during shakedown)"

    if len(argv) < 3:
        return False, ""

    sub = argv[1].lower()
    verb = argv[2].lower()
    rest = argv[3:]

    allowed = _READONLY_SUBCOMMANDS.get(sub)
    if allowed and ("*" in allowed or verb in allowed):
        return False, ""

    named = _NAMED_MUTATIONS.get((sub, verb))
    if named is not None:
        if named(rest):
            return True, f"{sub} {verb} (no --dry-run flag present)"
        return False, ""

    if verb in _MUTATION_VERBS:
        return True, f"{sub} {verb}"

    return False, ""


# ---------------------------------------------------------------------------
# Stderr message helpers
# ---------------------------------------------------------------------------


def _format_active(active):
    if not active:
        return "(unknown -- no active profile / store missing)"
    return active[0]


def _format_allowlist(allowlist):
    if not allowlist:
        return "(empty)"
    return ", ".join(allowlist)


def _config_help_lines():
    return [
        "  Configure the allowlist via either:",
        "    1. Env var: export PPDS_SAFE_ENVS=ppds-dev,ppds-test",
        "    2. File:    .claude/settings.json",
        '       { "safety": { "shakedown_safe_envs": ["ppds-dev", "ppds-test"] } }',
    ]


def _block_msg_no_allowlist(argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: no safe-env allowlist configured.",
        f"  Command: {' '.join(argv)}",
        "  ppds CLI commands are gated against an allowlist of dev/test envs",
        "  to prevent accidental writes against production environments.",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_active_not_allowed(active, allowlist, argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: active env is not in the safe-env allowlist.",
        f"  Command:    {' '.join(argv)}",
        f"  Active env: {_format_active(active)}",
        f"  Allowlist:  {_format_allowlist(allowlist)}",
        "",
        "  To proceed, switch to a safe env:",
        "    ppds env select <name>           # canonical CLI",
        "    ppds env switch <name>           # alias accepted by this hook",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_target_not_allowed(target, allowlist, argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: switch target is not in the safe-env allowlist.",
        f"  Command:   {' '.join(argv)}",
        f"  Target:    {target}",
        f"  Allowlist: {_format_allowlist(allowlist)}",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_mutation(argv: list, reason: str, env_var_name: str) -> str:
    return "\n".join(
        [
            "BLOCKED [shakedown-safety/readonly]: write/mutation command refused.",
            f"  Command: {' '.join(argv)}",
            f"  Pattern: {reason}",
            "",
            f"  {env_var_name}=1 is set, which puts this session in read-only",
            "  mode for ppds CLI operations. Mutating Dataverse during",
            "  shakedown defeats the purpose -- shakedowns are observation,",
            "  not modification.",
            "",
            "  To bypass (deliberate action -- there is no softer marker):",
            f"    unset {env_var_name}",
            "",
            "  For plugin deploys specifically, add --dry-run to validate",
            "  without writing.",
            "  For ppds-mcp-server, add --read-only to the launch args.",
        ]
    )


# ---------------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------------


def main() -> None:
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)

    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command", "")
    if not isinstance(command, str) or not command.strip():
        sys.exit(0)

    invocations = find_ppds_invocations(command)
    if not invocations:
        sys.exit(0)

    config = load_safety_config()
    env_var_name = config["readonly_env_var"]
    shakedown_active = os.environ.get(env_var_name, "").strip() == "1"

    # Concern 2: write-block first -- it's the tighter gate when active.
    if shakedown_active:
        for argv in invocations:
            blocked, reason = is_mutation(argv)
            if blocked:
                print(_block_msg_mutation(argv, reason, env_var_name), file=sys.stderr)
                sys.exit(2)

    # Concern 1: env allowlist gating -- always on.
    allowlist = load_allowlist(config)
    if allowlist is None:
        print(_block_msg_no_allowlist(invocations[0]), file=sys.stderr)
        sys.exit(2)

    active = get_active_env_names()

    for argv in invocations:
        allow, reason = decide_env(argv, allowlist, active)
        if not allow:
            print(reason, file=sys.stderr)
            sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
