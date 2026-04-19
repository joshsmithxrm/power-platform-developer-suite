"""PreToolUse hook: enforce dev-env allowlist for ppds CLI invocations.

Prevents an agent from accidentally connecting to a non-dev/test environment
(e.g. production) when running ppds CLI commands during shakedown / verify
flows. Reads the active environment from the PPDS profile store directly so
the hook stays fast and avoids invoking ppds itself (which would be circular
for a PreToolUse on Bash and would itself trip this same hook).

Allowlist sources (checked in order):
1. Env var ``PPDS_SAFE_ENVS`` -- comma-separated list of env names.
2. File ``.claude/state/safe-envs.json`` -- ``{"safe_envs": ["ppds-dev", ...]}``.
3. Neither set -- BLOCK with an instructive message.

Trigger patterns (block when active env not in allowlist):
- ``ppds env list`` / ``ppds env current`` / ``ppds env who`` -- always allowed
  (read-only diagnostics; ``who`` does connect but is treated as effectively
  read-only because it only issues a WhoAmI request).
- ``ppds env switch <name>`` / ``ppds env select <name>`` -- allowed only when
  the *target* name is in the allowlist.
- Any other ``ppds *`` invocation -- allowed only when the *active* env is in
  the allowlist.
- ``ppds-mcp-server`` -- verify the active env at startup; same rule as
  ``ppds *``.

Envelope contract: Claude Code wraps tool input as
``{"tool_name": "...", "tool_input": {"command": "..."}}``. Always read the
command from the nested ``tool_input`` dict -- reading at the top level was
the v1-prelaunch hook bug fixed by PR #816.

Failure mode: prints a message to stderr explaining the block + how to fix,
then exits 2 (the standard PreToolUse block code). Hook is fail-safe -- when
the active env can't be determined it BLOCKS rather than allows, on the
theory that an unknown env is more dangerous than a false positive.
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
# Allowlist resolution
# ---------------------------------------------------------------------------


def _safe_envs_file() -> str:
    """Resolve the path to safe-envs.json relative to CLAUDE_PROJECT_DIR."""
    project_dir = normalize_msys_path(
        os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())
    )
    return os.path.join(project_dir, ".claude", "state", "safe-envs.json")


def load_allowlist() -> Optional[list]:
    """Return the list of safe env names, or None when no allowlist configured.

    None means "no allowlist source provided" -- caller treats this as a hard
    block with a configuration-help message. An empty list means "allowlist
    is configured but empty" -- same effect (everything blocked) but the
    distinction lets the message be specific.
    """
    env_var = os.environ.get("PPDS_SAFE_ENVS", "").strip()
    if env_var:
        return [name.strip() for name in env_var.split(",") if name.strip()]

    path = _safe_envs_file()
    if not os.path.isfile(path):
        return None

    try:
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, json.JSONDecodeError):
        return None

    safe = data.get("safe_envs")
    if not isinstance(safe, list):
        return None
    return [str(name).strip() for name in safe if str(name).strip()]


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

    Returns a list because callers want to match against any of:
    DisplayName, UniqueName, or the host portion of the URL. Empty list
    means no active env (or store missing/malformed) -- caller must treat
    that as a fail-safe BLOCK.
    """
    path = _profile_store_path()
    if not os.path.isfile(path):
        return []

    try:
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, json.JSONDecodeError):
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
        # Use the host portion (e.g. orgcabef92d.crm.dynamics.com) and the
        # leftmost label (e.g. orgcabef92d) as additional match targets.
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
# Bash command parsing
# ---------------------------------------------------------------------------


_PPDS_PROGRAM_RE = re.compile(
    r"""
    (?:^|[\s|;&(`])         # start of command position
    (?:[^\s|;&()`]*[\\/])?  # optional path prefix
    (ppds(?:\.exe)?|ppds-mcp-server(?:\.exe)?)
    (?=$|[\s|;&)`])
    """,
    re.IGNORECASE | re.VERBOSE,
)


def find_ppds_invocations(command: str) -> list:
    """Return a list of argv-style token lists for each ppds invocation.

    Handles compound bash like ``cd foo && ppds env list`` and pipes like
    ``ppds env list | jq``. The CLI argv tokens stop at the first shell
    operator after the program name; this is a heuristic but is sufficient
    for the patterns we care about (the first few subcommand tokens).
    """
    if not command:
        return []

    out = []
    for match in _PPDS_PROGRAM_RE.finditer(command):
        prog = match.group(1)
        # Slice the rest of the command after the program name.
        tail = command[match.end():]
        try:
            # Use shlex to parse the rest of the command line.
            # This correctly handles quoted operators.
            tokens = shlex.split(tail, posix=True)
        except ValueError:
            tokens = tail.split()

        # Truncate tokens at the first shell operator.
        args = []
        for tok in tokens:
            if tok in ("|", ";", "&", "&&", "||", ">", "<", ">>", "<<", chr(96), "(", ")"):
                break
            args.append(tok)
        out.append([prog] + args)
    return out


# ---------------------------------------------------------------------------
# Decision logic
# ---------------------------------------------------------------------------


# Subcommands of ``ppds env`` that are read-only / always-allowed regardless
# of allowlist state. Includes both the canonical names from the spec
# (current/switch) and the actual CLI names used by EnvCommandGroup
# (who/select). 'config'/'type'/'show' don't talk to Dataverse.
_ENV_READONLY_SUBCOMMANDS = {"list", "current", "who", "config", "type", "show"}

# Subcommands of ``ppds env`` that change the active env. When the *target*
# is in the allowlist we let it through (so users can switch INTO a safe env
# even when currently on an unsafe one).
_ENV_SWITCH_SUBCOMMANDS = {"switch", "select", "use"}


def decide(argv, allowlist, active):
    """Return (allow, reason). reason is empty on allow, a message on block."""
    if not argv:
        return True, ""

    prog = os.path.basename(argv[0]).lower()
    if prog.endswith(".exe"):
        prog = prog[:-4]

    # ppds-mcp-server verifies env at startup -- same rule as ``ppds *``
    if prog == "ppds-mcp-server":
        if env_matches_allowlist(active, allowlist):
            return True, ""
        return False, _block_msg_active_not_allowed(active, allowlist, argv)

    if prog != "ppds":
        return True, ""

    # No further args -> top-level help, allow.
    if len(argv) < 2:
        return True, ""

    sub = argv[1].lower()

    # ``ppds env <subcommand>`` -- special handling for read-only and switch.
    if sub == "env" or sub == "org":
        if len(argv) < 3:
            # ``ppds env`` alone prints help -- allow.
            return True, ""
        env_sub = argv[2].lower()
        if env_sub in _ENV_READONLY_SUBCOMMANDS:
            return True, ""
        if env_sub in _ENV_SWITCH_SUBCOMMANDS:
            target = _extract_switch_target(argv[3:])
            if not target:
                # No target name -- treat as a help/usage invocation; allow.
                return True, ""
            if env_matches_allowlist([target], allowlist):
                return True, ""
            return False, _block_msg_target_not_allowed(target, allowlist, argv)
        # Other env subcommands (delete, update, etc.) -- gate on active env.
        if env_matches_allowlist(active, allowlist):
            return True, ""
        return False, _block_msg_active_not_allowed(active, allowlist, argv)

    # All other ppds subcommands gate on active env.
    if env_matches_allowlist(active, allowlist):
        return True, ""
    return False, _block_msg_active_not_allowed(active, allowlist, argv)


def _extract_switch_target(args):
    """Pull the target env name from a ``switch``/``select`` argv tail.

    Handles bare positional (``ppds env select my-dev``) and the
    ``--environment``/``-env`` flag form used by EnvCommandGroup.
    """
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
        "    2. File:    .claude/state/safe-envs.json",
        '       {"safe_envs": ["ppds-dev", "ppds-test"]}',
    ]


def _block_msg_no_allowlist(argv):
    lines = [
        "BLOCKED [dev-env-check]: no safe-env allowlist configured.",
        f"  Command: {' '.join(argv)}",
        "  ppds CLI commands are gated against an allowlist of dev/test envs",
        "  to prevent accidental writes against production environments.",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_active_not_allowed(active, allowlist, argv):
    lines = [
        "BLOCKED [dev-env-check]: active env is not in the safe-env allowlist.",
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
        "BLOCKED [dev-env-check]: switch target is not in the safe-env allowlist.",
        f"  Command:   {' '.join(argv)}",
        f"  Target:    {target}",
        f"  Allowlist: {_format_allowlist(allowlist)}",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------------


def main():
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        # Empty/garbled stdin -- allow rather than block unrelated tools.
        sys.exit(0)

    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command", "")
    if not isinstance(command, str) or not command.strip():
        sys.exit(0)

    invocations = find_ppds_invocations(command)
    if not invocations:
        sys.exit(0)

    allowlist = load_allowlist()
    if allowlist is None:
        # No allowlist source at all -- block with config help.
        print(_block_msg_no_allowlist(invocations[0]), file=sys.stderr)
        sys.exit(2)

    active = get_active_env_names()

    # Each invocation must independently pass. First failure wins so the
    # user sees a single, focused message.
    for argv in invocations:
        allow, reason = decide(argv, allowlist, active)
        if not allow:
            print(reason, file=sys.stderr)
            sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
