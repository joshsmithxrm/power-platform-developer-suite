"""PreToolUse hook: shakedown safety boundary (env allowlist + write-block).

Consolidates the two prior hooks (``dev-env-check.py`` and
``shakedown-readonly.py``) into a single PreToolUse gate. They share ~80% of
their parsing logic (find ``ppds`` invocations in a Bash command, walk argv
against subcommand/verb tables); splitting that across two files made the
safety surface harder to review and invited drift.

Two concerns, one hook:

1. **Env allowlist gating (opt-in).** Once an allowlist names at least one
   env, every Bash or PowerShell command that mentions ``ppds`` / ``ppds-mcp-server``
   is gated against that allowlist of dev/test env names. With no allowlist
   configured the gate is off; a misconfigured allowlist blocks (see
   below). The active env is read directly from the PPDS profile
   store (``profiles.json``).

2. **Write-block during shakedown.** When ``PPDS_SHAKEDOWN=1`` (or whichever
   name is configured under ``safety.readonly_env_var``), a ppds command
   passes only when its command path is one ``CLI_COMMANDS`` classifies as
   read-only; every other path, one the hook has never heard of included,
   is refused. This provides a mechanical boundary that the shakedown /
   verify skills rely on.

**The principle: a pass is granted only on a shape the hook positively
recognises, at the statement level and at the argument level.** Everything
else is refused (exit 2):

* *Cannot tell means refuse*, also inside an invocation. An argument list
  the hook could not read in full (``UNREADABLE``: a cap, the deadline, an
  unplaced mention, a program only known at run time with arguments from
  elsewhere), an argument only known at run time, and, in a guess (the
  argv of a program the hook could not identify, or of a word naming
  ``ppds`` where no program runs: ``grep ppds -rE docs``), a response file
  (``@file``), ``-e`` / ``-p``, or a single-dash token that may be a bundle
  naming an environment or profile are refused by the write-block and,
  with an unlisted or unknown active env, by the environment gate. A
  response file or a possible bundle after a word naming ``ppds`` where a
  program runs is placed text, refused in every mode. With a listed active
  env and no shakedown, an invocation the hook cannot place is refused
  **only when** the command text names a switch or a target
  (``text_names_target``): one of ``env`` / ``org`` / ``auth`` together
  with one of ``select`` / ``switch`` / ``use``, or a target option
  (``--environment``, ``-env``, ``--profile``, ``--allowed-env`` and their
  source/target forms anywhere, also inside an assignment or between
  escapes; ``-p`` / ``-e`` do not count). Otherwise it passes, as the
  listed active env would: with a listed active env the hook is a guard
  against mistakes, not containment (it does not see a target in a script
  file, a response file to a guessed program, or run-time words). A
  ``PPDS_`` variable or home-folder variable that moves the target is
  refused separately.
* *A token that grants a pass must be positively placed.* ``--dry-run``
  counts only as the exact bare token, before any ``--``, not directly
  after an option-looking token (it might be that option's value), and not
  followed by a word the flag might take as its value. ``--read-only`` for
  the MCP server counts only as its own parser reads it: the exact token,
  not the value of ``--profile`` / ``--environment`` / ``--allowed-env``. A
  help flag counts only directly after the command path. ``env config`` /
  ``env type`` pass only in the exact forms that read.
* *Both value delimiters.* Wherever an option's value is read, ``--name
  value``, ``--name=value`` and ``--name:value`` (and the aliases) are read.
* *Everything that can change which environment is reached is a target.*
  With an allowlist, an invocation passes only if every environment it
  names (``--environment`` / ``-env`` / ``-e`` (by command path: ``-e`` is
  ``--entity`` on some), ``--source-env``,
  ``--target-env``, the MCP server's ``--environment`` / ``--allowed-env``)
  is listed, literally or as the host of a URL; every profile it names
  (``--profile`` / ``-p``, ``--source-profile``, ``--target-profile``, and
  ``auth select``) resolves in the profile store to a listed environment;
  ``query sql`` / ``query explain`` name no ``environments.json`` label of
  an unlisted environment (``[LABEL].entity``); and the command names no
  ``PPDS_`` variable that can move the target (``PPDS_CONFIG_DIR``,
  ``PPDS_PROFILE``, ``PPDS_ENVIRONMENT_URL`` ...). A single-dash token with
  an ``e`` or ``p`` that is not one of those options may be a bundle of
  short options; it cannot be placed (see above). ``PPDS_PROFILE`` and
  ``PPDS_ENVIRONMENT_URL`` in the hook's own environment are part of the
  active env. The active env is known only by its environment's own names
  (display name, unique name, URL host and its first label), never by the
  name of the profile that holds it.
* *Bounded work.* Every loop over the input runs under the deadline, and
  the work per input is capped (``_MAX_*``, ``_DOTNET_MAX_*`` and the
  reader's caps); past a cap the command is an unreadable invocation
  (refused as above). A command longer than the reader's length cap is not
  scanned whole: its length is checked first and ``_decide_oversized``
  answers with a few byte checks. A watchdog refuses the command if the
  hook has not answered ``_WATCHDOG_GRACE_SECONDS`` after its budget.

What passes the environment gate with an unlisted (or unknown) active env:
``env`` alone, ``env list``, ``env who``, the read forms of ``env config`` /
``env type``, and ``env select`` / ``auth select`` to a listed target.
What passes the write-block: the paths ``CLI_COMMANDS`` marks read-only,
and the conditional ones (``plugins deploy --dry-run``, ``query sql`` with
inline SQL holding no write word as the T-SQL lexer splits words,
``version`` alone or with ``--check``, ``env select`` when an allowlist is
configured, the read forms of ``env config`` / ``env type``,
``ppds-mcp-server --read-only``), each only in its recognised shape.
``ppds`` with no arguments starts the TUI and is refused by the
write-block.

**.NET host forms.** Besides ``ppds`` / ``ppds-mcp-server`` themselves, the
hook recognises the CLI and MCP server started through ``dotnet``:
``dotnet run`` / ``dotnet watch run`` with ``--project`` (or ``-p``) naming
``PPDS.Cli`` / ``PPDS.Mcp`` or after ``cd`` into that directory in the same
command, ``dotnet [exec] <path>/ppds.dll`` / ``ppds-mcp-server.dll``,
``dotnet ppds`` and ``dotnet tool run ppds``. These are normalised to the
same argv and only ever add hits. Matching is broad and best-effort.

**Both shell tools, both readings.** The hook is registered for the Bash
tool and the PowerShell tool. Every command is read with PowerShell's rules
and with POSIX rules (``_shellparse.py``), whatever ``tool_name`` says, and
by the original matcher; a hit from any reading counts. Any word naming
``ppds`` / ``ppds-mcp-server`` (with a path and ``.exe``) or ``dotnet`` /
``dnx`` is an invocation, at any nesting depth: inside substitutions,
script blocks, every quoted string, and the code handed to ``bash -c``,
``pwsh -Command``, ``cmd /c``, ``eval`` and ``pwsh -EncodedCommand``. A
command that only mentions ``ppds`` (a commit message, a search) is also
treated as an invocation; that is the maintainer's decision on #1780.

**Every mention is accounted for.** The reader may clear a command; it is
never the reason one is missed. Each mention of ``ppds``, ``dotnet`` or
``dnx`` in the raw text (any case, also with quoting, escape, brace or glob
characters between the letters) must be placed by each reading in a word
of a text it read to the end: a word naming the program, or a word naming
something else that only contains it (``src/PPDS.Cli``). A mention that is
not placed is an invocation of both programs with an unknown verb.

**Cannot tell -> invocation.** When the hook cannot tell what a command
runs, it treats it as an invocation of both programs: a program only known
at run time (``$x data delete``, ``& $exe ...``, ``timeout 5 $x``, a brace
or glob in the name), wrapper code not in the text (``pwsh -Command $c``,
``$c | iex``, a script file), a ``dotnet run`` project or working directory
it could not resolve, an option before the ``dotnet`` verb it does not
know, an alias that names ``ppds``, text the reader could not finish, and
any cap or the time budget. Such an invocation is gated, and during a
shakedown it is refused when its subcommand or verb is not known; an
option or several words before the verb make the verb unknown.

**Time and failures.** Claude Code runs a command whose PreToolUse hook
exceeds its registered timeout or exits 1. The hook therefore answers
within ``_BUDGET_SECONDS`` (well inside the registered 10 s); past it the
command is refused, and a watchdog refuses it if no answer is given
``_WATCHDOG_GRACE_SECONDS`` later. Any internal error refuses the command,
whatever it is (exit 2). The only commands passed without being read are
those of a session with no allowlist and no shakedown: the hook exits 0
before it reads its input. Input is read as UTF-8 bytes.

**Allowlist sources (checked in order; the first that names an env wins):**

1. Env var ``$PPDS_SAFE_ENVS`` -- comma-separated list of env names.
2. ``.claude/settings.json`` top-level ``safety.shakedown_safe_envs`` array.
3. ``.claude/state/safe-envs.json`` ``safe_envs`` array -- legacy fallback,
   kept for a single release so in-flight branches don't break during
   migration. Will be removed once all trees have re-landed the
   settings.json form.

**Gate off** only when no source is configured at all: ``$PPDS_SAFE_ENVS``
unset or empty, ``safety.shakedown_safe_envs`` absent or ``[]``, and the
legacy file absent or with ``safe_envs`` absent or ``[]``. An empty
settings list falls through to the legacy file. The write-block below
still applies during a shakedown either way.

**Misconfigured -> BLOCK** every ``ppds`` command (exit 2, message naming
the problem): ``$PPDS_SAFE_ENVS`` non-empty but naming no env (e.g.
``","``); ``.claude/settings.json`` present but not parseable JSON, or a
``safety`` / ``shakedown_safe_envs`` value of the wrong type; the legacy
file present but unparseable or with a non-list ``safe_envs``; or any
non-empty list whose entries are not env-name strings.

**Envelope contract:** Claude Code wraps tool input as
``{"tool_name": "...", "tool_input": {"command": "..."}}``. Always read the
command from the nested ``tool_input`` dict -- reading at the top level was
the v1-prelaunch hook bug fixed by PR #816.

**Fail-safe:** with an allowlist configured, when the active env cannot be
determined the hook BLOCKS rather than allows -- an unknown env is more
dangerous than a false positive.

**Activation sources for the write-block (either is sufficient):**

1. Env var ``$PPDS_SHAKEDOWN=1`` (or whichever name is configured under
   ``safety.readonly_env_var``). Best for shell scripts and CI.
2. Sentinel file ``.claude/state/shakedown-active.json`` containing at
   least ``started_at`` (ISO-8601 UTC timestamp string, so the same file
   is consumed by both this hook and the C# ``IShakedownGuard``).
   Required when the activator is
   a Claude Code session: the Bash tool spawns a fresh shell per
   invocation, so inline ``PPDS_SHAKEDOWN=1 ppds ...`` prefixes and
   ``export PPDS_SHAKEDOWN=1`` do not propagate to this hook process.
   Sentinels older than 24h are silently deleted (self-heal) so a
   crashed shakedown session cannot wedge the write-block for future
   sessions.

**Bypass the write-block:** unset the configured env var AND/OR delete
the sentinel file -- whichever is active. There is intentionally no
softer bypass -- if a write is genuinely needed, that takes deliberate
action.
"""

from __future__ import annotations

import base64
import bisect
import json
import os
import re
import shlex
import sys
import threading
import time
from datetime import datetime, timezone
from typing import Iterable, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    from _pathfix import normalize_msys_path  # noqa: E402
    import _shellparse as sp  # noqa: E402
except BaseException:  # noqa: BLE001 -- a missing or broken helper must not exit 1
    if __name__ != "__main__":
        raise
    try:
        sys.stderr.write("BLOCKED [shakedown-safety]: the hook could not load its helper modules "
                         "(_pathfix.py, _shellparse.py next to it), so it is refused.\n")
        sys.stderr.flush()
    finally:
        os._exit(2)


# ---------------------------------------------------------------------------
# Settings loader (new home for the allowlist + env var name)
# ---------------------------------------------------------------------------


_DEFAULT_READONLY_ENV_VAR = "PPDS_SHAKEDOWN"
_SENTINEL_FILENAME = "shakedown-active.json"
_SENTINEL_MAX_AGE_SECONDS = 24 * 60 * 60


def _project_dir() -> str:
    return normalize_msys_path(
        os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())
    )


def _settings_path() -> str:
    return os.path.join(_project_dir(), ".claude", "settings.json")


def _legacy_safe_envs_path() -> str:
    return os.path.join(_project_dir(), ".claude", "state", "safe-envs.json")


def _sentinel_path() -> str:
    return os.path.join(_project_dir(), ".claude", "state", _SENTINEL_FILENAME)


def _read_json(path: str) -> Optional[dict]:
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, json.JSONDecodeError):
        return None


class AllowlistConfigError(Exception):
    """An allowlist source is present but unusable (the hook blocks)."""


_MISSING = object()

_SETTINGS_REL = ".claude/settings.json"
_LEGACY_REL = ".claude/state/safe-envs.json"


def _json_type(value) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, (int, float)):
        return "number"
    if isinstance(value, str):
        return "string"
    if isinstance(value, list):
        return "array"
    if isinstance(value, dict):
        return "object"
    return type(value).__name__


def _read_json_checked(path: str):
    """Return ``(data, error)``. ``data`` is ``_MISSING`` when no file exists.

    ``error`` is a short description when the file exists but cannot be read
    or parsed as JSON; ``data`` is then None.
    """
    if not os.path.isfile(path):
        return _MISSING, None
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh), None
    except (OSError, ValueError) as exc:
        return None, str(exc)


def load_safety_config() -> dict:
    """Return a dict with ``shakedown_safe_envs``, ``readonly_env_var`` and
    ``allowlist_error``.

    Reads from ``.claude/settings.json`` under the ``safety`` top-level key.
    ``shakedown_safe_envs`` is the list as written, or None when the key is
    absent or unusable. ``allowlist_error`` describes why the settings file
    cannot supply an allowlist (unparseable file, or a ``safety`` /
    ``shakedown_safe_envs`` value of the wrong type), else None.
    ``readonly_env_var`` falls back to the default. Never raises.
    """
    settings, read_error = _read_json_checked(_settings_path())
    allowlist_error = None
    safety = {}
    if read_error is not None:
        allowlist_error = f"{_SETTINGS_REL} could not be parsed as JSON ({read_error})."
    elif settings is not _MISSING:
        if not isinstance(settings, dict):
            allowlist_error = (
                f"{_SETTINGS_REL} is a JSON {_json_type(settings)}, not an object."
            )
        else:
            raw_safety = settings.get("safety", _MISSING)
            if isinstance(raw_safety, dict):
                safety = raw_safety
            elif raw_safety is not _MISSING:
                allowlist_error = (
                    f"`safety` in {_SETTINGS_REL} is a {_json_type(raw_safety)}, "
                    "not an object."
                )

    safe_envs = safety.get("shakedown_safe_envs", _MISSING)
    if safe_envs is _MISSING:
        safe_envs = None
    elif not isinstance(safe_envs, list):
        allowlist_error = (
            f"`safety.shakedown_safe_envs` in {_SETTINGS_REL} is a "
            f"{_json_type(safe_envs)} ({safe_envs!r}), not a list."
        )
        safe_envs = None

    env_var = safety.get("readonly_env_var")
    if not isinstance(env_var, str) or not env_var.strip():
        env_var = _DEFAULT_READONLY_ENV_VAR

    return {
        "shakedown_safe_envs": safe_envs,
        "readonly_env_var": env_var.strip(),
        "allowlist_error": allowlist_error,
    }


def _names_from_list(values: list, source: str) -> list:
    """Return the env names in a non-empty JSON list, or raise if it has none."""
    names = []
    for value in values:
        if not isinstance(value, str):
            raise AllowlistConfigError(
                f"{source} contains a {_json_type(value)} entry ({value!r}); "
                "every entry must be an env name string."
            )
        if value.strip():
            names.append(value.strip())
    if not names:
        raise AllowlistConfigError(f"{source} has entries but names no env.")
    return names


def load_allowlist(config: Optional[dict] = None) -> list:
    """Return the safe env names. An empty list means env gating is off.

    Resolution order -- the first source that names an env wins:
    1. ``$PPDS_SAFE_ENVS`` env var (comma-separated), when non-empty.
    2. ``safety.shakedown_safe_envs`` in ``.claude/settings.json``, when a
       non-empty list.
    3. ``safe_envs`` in ``.claude/state/safe-envs.json`` (legacy fallback --
       migration aid), when a non-empty list.

    Returns ``[]`` (gate off) only when nothing is configured: the env var is
    unset or empty, the settings key is absent or ``[]`` (``[]`` falls
    through to the legacy file), and the legacy file is absent or its
    ``safe_envs`` is absent or ``[]``.

    Raises ``AllowlistConfigError`` (the caller blocks) when a source is
    present but unusable: the env var is non-empty but names no env; the
    settings file is unparseable or has a wrong-typed ``safety`` /
    ``shakedown_safe_envs``; the legacy file is unparseable, not an object,
    or has a non-list ``safe_envs``; or a non-empty list names no env or has
    a non-string entry. A source consulted after one that already named envs
    is not read.
    """
    raw = os.environ.get("PPDS_SAFE_ENVS", "")
    if raw.strip():
        names = [name.strip() for name in raw.split(",") if name.strip()]
        if not names:
            raise AllowlistConfigError(
                f"PPDS_SAFE_ENVS is set to {raw!r}, which names no env."
            )
        return names

    if config is None:
        config = load_safety_config()
    if config.get("allowlist_error"):
        raise AllowlistConfigError(config["allowlist_error"])
    safe = config.get("shakedown_safe_envs")
    if safe:
        return _names_from_list(
            safe, f"`safety.shakedown_safe_envs` in {_SETTINGS_REL}"
        )

    # Legacy fallback -- keep for one release so mid-migration trees still work.
    legacy, read_error = _read_json_checked(_legacy_safe_envs_path())
    if legacy is _MISSING:
        return []
    if read_error is not None:
        raise AllowlistConfigError(
            f"{_LEGACY_REL} could not be parsed as JSON ({read_error})."
        )
    if not isinstance(legacy, dict):
        raise AllowlistConfigError(
            f"{_LEGACY_REL} is a JSON {_json_type(legacy)}, not an object."
        )
    legacy_safe = legacy.get("safe_envs", _MISSING)
    if legacy_safe is _MISSING:
        return []
    if not isinstance(legacy_safe, list):
        raise AllowlistConfigError(
            f"`safe_envs` in {_LEGACY_REL} is a {_json_type(legacy_safe)} "
            f"({legacy_safe!r}), not a list."
        )
    if not legacy_safe:
        return []
    return _names_from_list(legacy_safe, f"`safe_envs` in {_LEGACY_REL}")


# ---------------------------------------------------------------------------
# Sentinel file (per-session shakedown activation)
# ---------------------------------------------------------------------------


def _delete_sentinel_quiet() -> None:
    try:
        os.remove(_sentinel_path())
    except OSError:
        pass


def check_sentinel_active() -> bool:
    """True iff a fresh shakedown sentinel file is present.

    ``started_at`` is an ISO-8601 UTC timestamp string -- same format the
    C# ``IShakedownGuard`` parses. Stale sentinels (older than 24h since
    ``started_at``) and malformed sentinels are silently deleted -- a
    crashed session must not wedge the write-block for future sessions.
    """
    path = _sentinel_path()
    if not os.path.isfile(path):
        return False

    data = _read_json(path)
    if not isinstance(data, dict):
        _delete_sentinel_quiet()
        return False

    raw = data.get("started_at")
    if not isinstance(raw, str):
        _delete_sentinel_quiet()
        return False

    try:
        started_at_dt = datetime.fromisoformat(raw)
    except (TypeError, ValueError):
        _delete_sentinel_quiet()
        return False

    # Normalize to aware UTC for age comparison; naive strings assumed UTC.
    if started_at_dt.tzinfo is None:
        started_at_dt = started_at_dt.replace(tzinfo=timezone.utc)

    age_seconds = (datetime.now(timezone.utc) - started_at_dt).total_seconds()
    # Absolute window tolerates clock skew in both directions -- matches the
    # guard spec's "|now - started_at| <= 24h" definition.
    if abs(age_seconds) > _SENTINEL_MAX_AGE_SECONDS:
        _delete_sentinel_quiet()
        return False

    return True


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
    # `PPDS_PROFILE` in the environment the CLI inherits overrides the
    # store's active profile (ProfileResolver); it must name one profile.
    override = os.environ.get("PPDS_PROFILE", "").strip()
    if override:
        named = _profiles_named(data, override)
        if len(named) != 1:
            return []
        active_idx, active_name = None, None
        active = named[0]
    if active is None and isinstance(active_idx, int):
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
    # The profile's own name is not a name of its environment: a profile
    # named like a listed environment may hold an unlisted one.
    return candidates


def env_matches_allowlist(candidates: Iterable, allowlist: Iterable) -> bool:
    """True when any candidate matches any allowlist entry (case-insensitive)."""
    cand_lower = {c.lower() for c in candidates if c}
    allow_lower = {a.lower() for a in allowlist if a}
    return bool(cand_lower & allow_lower)


# ---------------------------------------------------------------------------
# Command parsing (shared by both concerns)
# ---------------------------------------------------------------------------


class _Deadline(Exception):
    """The hook's time budget ran out."""


def _tick(deadline) -> None:
    if deadline is not None and time.monotonic() > deadline:
        raise _Deadline()


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


_PPDS_PROGRAMS = ("ppds", "ppds-mcp-server")
# Words that name a guarded program (lower case, path and .exe removed).
_GUARDED = {"ppds", "ppds-mcp-server", "dotnet", "dnx"}
# Argument standing for text the hook could not read; `is_mutation` treats
# an argv that contains it as a write.
UNREADABLE = "<unreadable>"
# A working directory only known at run time.
_UNKNOWN_CWD = sp.DYN

# A mention of a guarded program in the raw text: `ppds` (also inside
# `ppds.exe`, `ppds-mcp-server`), `dotnet` or `dnx`, in any case, with
# quoting, escape, brace or glob characters allowed between the letters.
_QUOTING = r"""['"`^\\‘’‚‛“”„{},*?\[\]]"""
_MENTION_RE = re.compile(
    "|".join((_QUOTING + "*").join(re.escape(ch) for ch in name) for name in ("ppds", "dotnet", "dnx")),
    re.IGNORECASE,
)
_QUOTING_CHARS = set("'\"`^\\‘’‚‛“”„{},*?[]")
# Escapes the hook decodes on its own, to find a name spelled with them.
_ESCAPE_RE = re.compile(
    r"\\x([0-9A-Fa-f]{1,2})|\\u([0-9A-Fa-f]{1,4})|\\U([0-9A-Fa-f]{1,8})|\\([0-7]{1,3})"
    r"|`u\{([0-9A-Fa-f]{1,6})\}"
)
# PowerShell that runs code only known at run time without a wrapper word.
_DYNAMIC_CODE_RE = re.compile(
    r"\[\s*(?:system\.)?(?:management\.automation\.)?(?:scriptblock|powershell)\s*\]\s*::"
    r"|\[\s*(?:system\.)?(?:diagnostics\.)?process\s*\]\s*::"
    r"|\.\s*invoke(?:returnasis)?\s*\(|\.\s*addscript\s*\(|\$executioncontext\b",
    re.IGNORECASE,
)
# Characters that may touch a mention inside a word naming something else
# (`src/PPDS.Cli`, `PPDS_CONFIG_DIR`, `$env:PPDS_X`, `.dotnet/tools`), and
# `=` inside an option (`--environment=ppds-dev`).
_GLUE_BEFORE = set("_.-/\\:$")
_GLUE_AFTER = set("_.-/\\")

_CD_WORDS = {"cd", "pushd", "chdir", "set-location", "sl", "push-location"}
_CD_POP_WORDS = {"popd", "pop-location"}
_ALIAS_DEFINERS = {"alias", "set-alias", "new-alias", "sal", "nal", "doskey"}
# Anything in the raw text that may change the working directory. When the
# reader did not consume every one as a plain `cd`, a project-less
# `dotnet run` has an unknown working directory.
_CDLIKE_RE = re.compile(
    r"(?i:(?<![\w.$-])(?:cd|chdir|pushd|popd|sl|set-location|push-location|pop-location)(?![\w-]))"
    r"|(?<![\w-])-C(?![\w-])"
    r"|(?i:(?<![\w-])(?:--chdir|--directory|-wo[a-z]*|/d)(?![\w-]))"
)
# Prefixes that take values of their own before the program they run, or
# run a program with arguments from elsewhere: a run-time word after them
# may be the program.
_RUNNER_PREFIXES = {
    "env", "sudo", "doas", "nice", "timeout", "stdbuf", "ionice", "chrt", "taskset", "strace",
    "ltrace", "watch", "xargs", "parallel", "find", "fd", "runas", "gsudo", "start-process", "saps",
    "start", "call", "invoke-item", "ii", "invoke-command", "icm",
}


class _Guess(list):
    """An argv for a program the hook could not identify (read as both PPDS
    programs), or for a word naming one where no program runs (a mention:
    ``grep ppds -rE docs``). ``origin`` is the text as written, for block
    messages; None when there is nothing to show (an unread command, a cap).
    ``note`` says what the reading is, after the text in a block message."""

    __slots__ = ("origin", "note")

    def __init__(self, items, origin=None, note=None):
        super().__init__(items)
        self.origin = origin
        self.note = note or "a program the hook cannot identify; read as ppds"


_MENTION_NOTE = "ppds named where the hook cannot tell that it runs; read as a ppds command"


def _unknown(args, origin=None) -> list:
    """Readings for a program the hook cannot identify: both PPDS programs."""
    return [_Guess([program] + list(args), origin) for program in _PPDS_PROGRAMS]


def _tail(texts: list, start: int) -> list:
    """``texts[start:]`` capped at ``_MAX_ARGS`` words; past the cap the
    reading ends in ``UNREADABLE``."""
    tail = texts[start:start + _MAX_ARGS]
    if len(texts) - start > _MAX_ARGS:
        tail.append(UNREADABLE)
    return tail


def _dedupe(argvs: list, deadline=None) -> list:
    """``argvs`` without repeats, in order; of two equal readings the placed
    one (a plain list) is kept, not the guess."""
    seen, out = {}, []
    for argv in argvs:
        _tick(deadline)
        program = argv[0].lower()
        program = program[:-4] if program.endswith(".exe") else program
        key = (program,) + tuple(argv[1:])  # `ppds.exe env list` == `ppds env list`
        if key not in seen:
            seen[key] = len(out)
            out.append(argv)
        elif isinstance(out[seen[key]], _Guess) and not isinstance(argv, _Guess):
            out[seen[key]] = argv
    return out


def _mentions(text: str) -> list:
    """``(start, end, fuzzy)`` for each mention of a guarded program in
    ``text``; ``fuzzy`` when quoting characters sit between the letters. At
    most ``_MAX_MENTIONS + 1`` are returned (more is past the cap)."""
    out = []
    for m in _MENTION_RE.finditer(text):
        out.append((m.start(), m.end(), any(ch in _QUOTING_CHARS for ch in m.group(0))))
        if len(out) > _MAX_MENTIONS:
            break
    return out


def _unescape(text: str) -> str:
    def repl(m):
        for group, radix in ((1, 16), (2, 16), (3, 16), (4, 8), (5, 16)):
            if m.group(group):
                try:
                    return chr(int(m.group(group), radix))
                except (ValueError, OverflowError):
                    return "�"
        return m.group(0)
    return _ESCAPE_RE.sub(repl, text)


def _escaped_mention(text: str) -> bool:
    """True when decoding escapes spells a guarded name the raw text does not."""
    if not _ESCAPE_RE.search(text):
        return False
    return len(_MENTION_RE.findall(_unescape(text))) > len(_MENTION_RE.findall(text))


def mentions_guarded(text: str) -> bool:
    """True when ``text`` mentions ppds, dotnet or dnx, plainly, with quoting
    characters between the letters, or through an escape."""
    return bool(text) and (bool(_MENTION_RE.search(text)) or _escaped_mention(text))


def find_ppds_invocations(command: str, tool_name: str = "Bash", cwd=None, deadline=None) -> list:
    """Return a list of argv-style token lists for each ppds invocation.

    The text is read in both dialects whatever ``tool_name`` says, and by
    the original matcher; a hit from any reading counts. Every mention of a
    guarded program must be placed by each reading in a word it read to the
    end (``_unplaced``); one that is not is an invocation of both programs
    with an unknown verb. ``cwd`` is the shell's working directory from the
    hook payload; None means a directory that is not a PPDS project,
    ``_UNKNOWN_CWD`` that it is not known. Past ``deadline`` (or any cap)
    the command is an invocation of both programs with an unknown verb.
    """
    if not command:
        return []
    if deadline is None:
        deadline = time.monotonic() + _BUDGET_SECONDS
    try:
        out = []
        out.extend(_original_invocations(command, deadline))
        _tick(deadline)
        mentions = _mentions(command)
        for dialect in ("posix", "ps"):
            out.extend(_read_invocations(command, dialect, cwd, mentions, deadline))
        _tick(deadline)
        if _escaped_mention(command) or _DYNAMIC_CODE_RE.search(command):
            out.extend(_unknown([UNREADABLE]))
        if len(out) > _MAX_INVOCATIONS:
            return _unknown([UNREADABLE])  # past the cap: an invocation, unconditionally
        return _dedupe(out, deadline)
    except _Deadline:
        return _unknown([UNREADABLE])


# -- mention accounting -------------------------------------------------------


def _units(top) -> list:
    out, stack = [], [top]
    while stack:
        unit = stack.pop()
        out.append(unit)
        stack.extend(child for child in unit.children if child.base is not None)
    return out


def _covering(items, starts, mentions, lo, hi, key, out) -> None:
    k = bisect.bisect_left(starts, lo)
    while k < len(mentions) and mentions[k][0] < hi:
        if mentions[k][1] <= hi:
            out.setdefault((key, k), []).append(items)
        k += 1


def _unplaced(result, text: str, mentions: list, deadline) -> bool:
    """True when this reading does not place every mention.

    A mention is placed when every unit (text read) covering it read to the
    end, at least one word covers it, and each covering word that is not
    just the container of a nested text places it: a word naming a guarded
    program (not inside a quoted string glued to other text), or a plain
    mention joined to identifier characters inside a word naming something
    else (``src/PPDS.Cli``)."""
    if not mentions:
        return False
    if len(mentions) > _MAX_MENTIONS or not result.complete or result.top is None:
        return True
    starts = [m[0] for m in mentions]
    words_by, kids_by, spans = {}, {}, {}
    units = _units(result.top)
    for unit in units:
        _tick(deadline)
        unit_spans = set()
        for word in unit.words:
            span = word.span
            if span is None:
                continue
            unit_spans.add(span)
            _covering(word, starts, mentions, span[0], span[1], id(unit), words_by)
        spans[id(unit)] = unit_spans
        for child in unit.children:
            if child.base is None or child.lo is None or child.hi is None:
                continue
            _covering(child, starts, mentions, child.lo, child.hi, id(unit), kids_by)
    for k, mention in enumerate(mentions):
        _tick(deadline)
        if not _judge(result.top, k, mention, text, words_by, kids_by, spans):
            return True
    return False


def _judge(unit, k, mention, text, words_by, kids_by, spans) -> bool:
    if not unit.ok:
        return False
    kids = kids_by.get((id(unit), k), [])
    words = words_by.get((id(unit), k), [])
    results = [_judge(child, k, mention, text, words_by, kids_by, spans) for child in kids]
    for word in words:
        a, b = word.span
        if any(a <= child.lo and child.hi <= b for child in kids):
            continue  # the mention is in a nested text of this word: judged there
        results.append(_judge_word(word, unit, mention, text, spans))
    return bool(results) and all(results)


def _judge_word(word, unit, mention, text, spans) -> bool:
    start, end, fuzzy = mention
    if sp.program_name(word) in _GUARDED:
        # A guarded name read out of a quoted string glued to other text
        # (`--x="ppds ..."`, `['ppds', ...]`) may not carry its arguments.
        x = unit
        while x is not None:
            if x.kind == "string" and x.outer is not None and x.parent is not None \
                    and x.outer not in spans.get(id(x.parent), ()):
                return False
            x = x.parent
        return True
    if fuzzy:
        return False
    if sp.program_name(word) is None and \
            sp.program_name(sp.Word(word.text.replace(sp.DYN, ""), False, False)) in _GUARDED:
        return False  # run-time text next to the name may leave just the name (`${x}ppds`)
    a, b = word.span
    before = text[start - 1] if start > a else None
    after = text[end] if end < b else None
    if before is None and after is None:
        return False
    if before is not None and not (before.isalnum() or before in _GLUE_BEFORE
                                   or (before == "=" and word.text.startswith("-"))):
        return False
    if after is not None and not (after.isalnum() or after in _GLUE_AFTER):
        return False
    return True


# -- invocations from one reading --------------------------------------------


def _read_invocations(command: str, dialect: str, cwd, mentions: list, deadline) -> list:
    """Hits from reading ``command`` with ``_shellparse`` in ``dialect``.

    Every word naming a PPDS program, ``dotnet`` or ``dnx`` counts, in every
    statement the reader finds; so does every mention this reading does not
    place, a program only known at run time, a wrapper whose code is not in
    the text, and any cap or the deadline."""
    result = sp.read(command, dialect, deadline)
    if result.capped:
        return _unknown([UNREADABLE])  # past a cap: an invocation, unconditionally
    _tick(deadline)
    out = []
    if _unplaced(result, command, mentions, deadline):
        out.extend(_unknown([UNREADABLE]))
    statements = result.statements
    cd_statements = sum(
        1 for stmt in statements
        if stmt.code and not stmt.wrapped and stmt.words and stmt.words[0].text.lower() in _CD_WORDS | _CD_POP_WORDS
    )
    trust_cwd = len(_CDLIKE_RE.findall(command)) == cd_statements
    stack = [cwd]  # working directory per subshell level
    program_words = 0
    elements = 0  # argv words produced, capped (the work per input is bounded)
    for stmt in statements:
        _tick(deadline)
        level = stmt.level
        del stack[level + 1:]
        while len(stack) < level + 1:
            stack.append(stack[-1])
        words, texts = stmt.words, stmt.texts
        positions = dict(sp.command_positions(stmt)) if stmt.code else {}
        # A word after a program whose arguments come from elsewhere (`xargs
        # -n 4 ppds`, `parallel -j 4 ppds`) may itself be run with them.
        first_opaque = min((j for j, o in positions.items() if o), default=None)
        for i, word in enumerate(words):
            _tick(deadline)
            prog = sp.program_name(word)
            if prog not in _GUARDED:
                continue
            program_words += 1
            if program_words > _MAX_PROGRAM_WORDS or elements > _MAX_ARG_ELEMENTS:
                out.extend(_unknown([UNREADABLE]))  # past a cap: an invocation, unconditionally
                return out
            fed = positions.get(i) or (i not in positions and first_opaque is not None and i > first_opaque)
            if prog in _PPDS_PROGRAMS:
                argv = [prog] + _tail(texts, i + 1)
                if fed:
                    argv.append(UNREADABLE)  # arguments from elsewhere (xargs, Start-Process)
                elif i not in positions:
                    # Not where a program runs (`grep ppds -rE docs`, a string,
                    # a comment): a mention, read as an invocation whose
                    # options the hook cannot place.
                    argv = _Guess(argv, _shown(texts[max(0, i - 3):i + 9]) + (
                        " ..." if len(texts) > i + 9 else ""), _MENTION_NOTE)
                hits = [argv]
            else:
                if prog == "dnx":
                    hits = _package_exec(_tail(texts, i + 1))
                else:
                    known = (trust_cwd and stmt.code and not stmt.wrapped and i in positions
                             and not positions[i] and stack[level] != _UNKNOWN_CWD)
                    hits = _dotnet_hits(_tail(texts, i + 1), {stack[level]} if known else {_UNKNOWN_CWD})
                if fed:
                    hits = [hit + [UNREADABLE] for hit in hits] or _unknown([UNREADABLE])
            elements += sum(len(hit) for hit in hits)
            out.extend(hits)
        if stmt.code:
            out.extend(_hidden_shell_code(stmt, positions, first_opaque))
            for i, opaque in positions.items():
                word = words[i]
                prog = sp.program_name(word)
                if prog in _GUARDED:
                    continue
                if word.dynamic or prog is None or (word.glob and word.text != "?"):
                    origin = _shown(texts[i:i + 12]) + (" ..." if len(texts) > i + 12 else "")
                    # Fed by a pipe, a here-string or a here-document, a
                    # program known only at run time (`$SH`, `$(which bash)`)
                    # may run that text as code: its arguments are not all
                    # in the words.
                    fed = stmt.piped or stmt.stdin_text
                    out.extend(_unknown(_tail(texts, i + 1) + ([UNREADABLE] if opaque or fed else []),
                                        origin))
                elif opaque and (prog in sp.SHELLS or prog in sp.CODE_WORDS):
                    out.extend(_unknown([UNREADABLE]))
            if stmt.wrapper and (not stmt.code_arg or stmt.dynamic_code or stmt.stdin_text):
                out.extend(_unknown([UNREADABLE]))  # the code it runs is not in the text
            lead = next((w for w in words if not sp.is_assignment(w)), None)
            if lead is not None and not lead.dynamic and lead.text.lower() in _RUNNER_PREFIXES \
                    and any(w.dynamic for w in words[words.index(lead) + 1:]):
                out.extend(_unknown([UNREADABLE]))
            first = words[0].text.lower() if words else ""
            if first in _CD_WORDS:
                stack[level] = _cd(stack[level], words[1:], stmt.dialect)
            elif first in _CD_POP_WORDS:
                stack[level] = _UNKNOWN_CWD
        if words and words[0].text.lower() in _ALIAS_DEFINERS and (
                mentions_guarded(" ".join(texts[1:])) or any(w.dynamic for w in words[1:])):
            out.extend(_unknown([UNREADABLE]))
    return out


# Programs that take a shell's name as data (`grep bash`, `ps | grep pwsh`):
# fed text, a shell named after them is not run.
_NAME_READERS = {
    "grep", "egrep", "fgrep", "rg", "ag", "ack", "pgrep", "pkill", "killall", "pidof", "which",
    "where", "whereis", "type", "man", "help", "select-string", "sls", "get-command", "gcm",
    "get-process", "gps", "ps", "stop-process", "kill", "echo", "printf", "write-output",
    "write-host", "apt", "apt-get", "brew", "choco", "winget", "dnf", "yum", "pacman", "sort",
    "uniq", "wc", "head", "tail", "cut", "tr", "sed", "awk", "cat", "less", "more", "tee",
}


def _hidden_shell_code(stmt, positions: dict, first_opaque) -> list:
    """An invocation the hook could not read when a shell sits where the
    reader did not take it for a wrapper and is given code: behind a prefix
    the reader does not know (`chroot / bash -c ...`, `flock f sh -c ...`),
    after a program whose arguments come from elsewhere, or fed text with no
    script named; and `ssh` with a remote command or fed text."""
    words = stmt.words
    lead = next((w for w in words if not sp.is_assignment(w)), None)
    lead_name = sp.program_name(lead) if lead is not None else None
    for i, word in enumerate(words):
        prog = sp.program_name(word)
        if prog == "ssh" and i in positions:
            operands = [w for w in words[i + 1:] if not w.text.startswith("-")]
            if len(operands) > 1 or stmt.piped or stmt.stdin_text:
                return _unknown([UNREADABLE])
            continue
        if prog not in sp.SHELLS or i == 0 or (i in positions and not positions[i]):
            continue
        if stmt.wrapper is not None and i in positions:
            continue  # the reader read this shell as a wrapper
        later = words[i + 1:]
        if (first_opaque is not None and i >= first_opaque) \
                or any(sp._wrapper_code_flag(prog, w.text) for w in later):
            return _unknown([UNREADABLE])
        if (stmt.piped or stmt.stdin_text) and lead_name not in _NAME_READERS \
                and all(w.text.startswith("-") for w in later):
            return _unknown([UNREADABLE])
    return []


def _is_absolute(path: str) -> bool:
    return path.startswith(("/", "~")) or bool(re.match(r"^[A-Za-z]:", path))


def _cd(base, args, dialect="posix"):
    """The working directory after ``cd`` / ``Set-Location`` with ``args``;
    unknown for any option other than POSIX ``-P`` / ``-L`` / ``-e``, more
    than one path, ``-`` or a path only known at run time."""
    paths = []
    for word in args:
        if word.dynamic:
            return _UNKNOWN_CWD
        text = word.text
        if dialect == "posix" and text in ("-P", "-L", "-e", "-@", "--"):
            continue
        if text == "-" or text.startswith("-") or re.fullmatch(r"/[A-Za-z?]", text):
            return _UNKNOWN_CWD
        paths.append(text)
    if len(paths) > 1:
        return _UNKNOWN_CWD
    if not paths:
        return "~"
    path = paths[0].replace("\\", "/")
    if _is_absolute(path) or base is None:
        return path
    if base == _UNKNOWN_CWD:
        return _UNKNOWN_CWD
    return base.replace("\\", "/") + "/" + path


def _path_program(path):
    """``ppds`` / ``ppds-mcp-server`` when ``path``'s last component names that
    project, ``"?"`` when that component is only known at run time."""
    if path is None:
        return None
    parts = []
    for part in path.replace("\\", "/").split("/"):
        if part == "..":
            if not parts or sp.DYN in parts[-1]:
                return "?" if sp.DYN in path else None
            parts = parts[:-1]
        elif part not in ("", "."):
            parts.append(part)
    if not parts:
        return None
    last = parts[-1].lower()
    if sp.DYN in last or any(ch in last for ch in "*?[{"):
        return "?"  # only known at run time, or a glob / brace pattern
    return _PPDS_PROJECTS.get(last[:-7] if last.endswith(".csproj") else last)


def _project_programs(project, cwds) -> set:
    """Programs a ``dotnet run`` with ``project`` (None: no ``--project``)
    starts, over the candidate working directories."""
    out = set()
    for base in cwds:
        if project is None:
            path = base
        else:
            path = project.replace("\\", "/")
            if not (_is_absolute(path) or base is None):
                path = base.replace("\\", "/") + "/" + path
        out.add(_path_program(path))
    return out


def _original_invocations(command: str, deadline=None) -> list:
    """The original matcher: a ``ppds`` word after whitespace or an operator,
    its tail split with POSIX rules, plus the ``dotnet`` forms. At most
    ``_DOTNET_MAX_OCCURRENCES`` words of each, and ``_DOTNET_MAX_TOKENS``
    tokens / ``_DOTNET_MAX_CHARS`` characters after each, are examined; past
    either cap the reading has an unknown verb."""
    out = []
    for count, match in enumerate(_PPDS_PROGRAM_RE.finditer(command)):
        if count >= _DOTNET_MAX_OCCURRENCES:
            out.extend(_unknown([UNREADABLE]))
            break
        _tick(deadline)
        prog = match.group(1)
        tail = command[match.end():match.end() + _DOTNET_MAX_CHARS]
        cut = len(command) - match.end() > _DOTNET_MAX_CHARS
        try:
            tokens = shlex.split(tail, posix=True)
        except ValueError:
            tokens = tail.split()

        args, stopped = [], False
        for tok in tokens[:_DOTNET_MAX_TOKENS]:
            if tok in _SHELL_OPERATORS:
                stopped = True
                break
            args.append(tok)
        if not stopped and (cut or len(tokens) > _DOTNET_MAX_TOKENS):
            args.append(UNREADABLE)
        # This matcher cannot tell where a program runs: a reading of the
        # reader that places the word replaces it (``_dedupe``).
        out.append(_Guess([prog] + args, _shown([prog] + args[:12]) + (" ..." if len(args) > 12 else ""),
                          _MENTION_NOTE))
    out.extend(_dotnet_invocations(command, deadline))
    return out


# Caps that bound the work per input; past any of them the command is an
# invocation of both programs that the hook could not read (refused by both
# gates). The original matcher examines at most 500 `ppds` and 500 `dotnet`
# words, and 400 tokens / 4096 characters after each. Each reading of the
# reader handles at most 500 words naming a guarded program; an invocation
# keeps at most 400 arguments; all readings together produce at most
# 200,000 argument words and 2,000 invocations; at most 1,000 mentions are
# accounted for.
_DOTNET_MAX_OCCURRENCES = 500
_DOTNET_MAX_TOKENS = 400
_DOTNET_MAX_CHARS = 4096
_MAX_PROGRAM_WORDS = 500
_MAX_ARGS = 400
_MAX_ARG_ELEMENTS = 200_000
_MAX_INVOCATIONS = 2_000
_MAX_MENTIONS = 1_000
_DOTNET_RE = re.compile(
    r"""(?:^|[\s|;&()`'"])(?:[^\s|;&()`'"]*[\\/])?dotnet(?:\.exe)?(?=$|[\s|;&)`'"])""",
    re.IGNORECASE,
)
_CD_RE = re.compile(r"""(?:^|[\s|;&(])(?:cd|pushd)\s+(?:-\S*\s+)*("[^"]*"|'[^']*'|[^\s|;&()]+)""")
# Project (folder or .csproj stem) -> program; AssemblyName in the csproj.
_PPDS_PROJECTS = {"ppds.cli": "ppds", "ppds.mcp": "ppds-mcp-server"}
_PPDS_ASSEMBLIES = {"ppds.dll": "ppds", "ppds-mcp-server.dll": "ppds-mcp-server"}
# `dotnet run` options that take a value / take none (learn.microsoft.com
# dotnet-run); every other token before `--` is forwarded to the app.
_RUN_VALUE_OPTS = set("-a --arch --artifacts-path -c --configuration -e --environment --file "
                      "-f --framework -lp --launch-profile --os --project -r --runtime -v "
                      "--verbosity --property".split())
_RUN_FLAGS = set("--disable-build-servers --force --interactive --no-build --no-cache "
                 "--no-dependencies --no-launch-profile --no-restore --no-self-contained "
                 "--sc --self-contained -? -h --help".split())
# `dotnet [exec]` host options that take a value / take none. Any other
# option before the verb is one the hook does not know.
_HOST_VALUE_OPTS = set("--additionalprobingpath --additional-deps --depsfile --fx-version "
                       "--roll-forward --runtimeconfig --roll-forward-on-no-candidate-fx".split())
_HOST_FLAGS = set("-d --diagnostics -h --help -? /? --info --version --list-sdks --list-runtimes".split())
_OPTION_VALUE_RE = re.compile(r"^(--?[A-Za-z?][\w-]*)[=:](.*)$", re.DOTALL)


def _dotnet_project(path):
    """``ppds`` / ``ppds-mcp-server`` when ``path``'s last component names that project."""
    parts = []
    for part in (path or "").replace("\\", "/").split("/"):
        if part == "..":
            parts = parts[:-1]
        elif part not in ("", "."):
            parts.append(part)
    last = parts[-1].lower() if parts else ""
    return _PPDS_PROJECTS.get(last[:-7] if last.endswith(".csproj") else last)


def _parse_run(tokens: list):
    """Split ``dotnet run <tokens>`` into ``(project, unknown, after)``:
    the ``--project`` value (None when absent), the tokens ``dotnet run``
    does not recognise (forwarded to the application) and the tokens after
    ``--``. None for ``--file`` (a file-based app, not a PPDS project)."""
    project, unknown, after, i = None, [], [], 0
    while i < len(tokens):
        tok = tokens[i]
        if tok == "--":
            after = tokens[i + 1:]
            break
        m = _OPTION_VALUE_RE.match(tok)
        name, inline = (m.group(1), m.group(2)) if m else (tok, None)
        value = inline if inline is not None else (tokens[i + 1] if i + 1 < len(tokens) else "")
        if name in _RUN_VALUE_OPTS or (name == "-p" and "=" not in value):
            if name == "--file":
                return None
            if name in ("--project", "-p"):  # -p:x=y and -p x=y are properties
                project = value
            i += 1 if inline is not None else 2
        else:
            if name != "-p" and name not in _RUN_FLAGS and not name.startswith("--tl"):
                unknown.append(tok)
            i += 1 if name != "-p" or inline is not None else 2
    return project, unknown, after


def _read_only_forwarded(unknown: list) -> bool:
    """True when ``--read-only`` certainly reaches the MCP server: the exact
    token is among the forwarded tokens and no option ``dotnet run`` does not
    recognise comes before it (such an option might take it as its value).
    ``--read-only=<value>`` does not count: the server accepts only the bare
    flag."""
    for tok in unknown:
        if tok == "--read-only":
            return True
        if tok.startswith("-"):
            return False
    return False


class _Reading(list):
    """An argv the hook read out of a ``dotnet`` command: ``origin`` is the
    text as written after ``dotnet``, for block messages."""

    __slots__ = ("origin",)

    def __init__(self, items, origin):
        super().__init__(items)
        self.origin = origin


def _written(tokens: list) -> str:
    return _shown(tokens[:16]) + (" ..." if len(tokens) > 16 else "")


def _run_readings(program: str, unknown: list, after: list, origin=None) -> list:
    """argv readings for an application started with forwarded ``unknown``
    tokens and ``after`` (the tokens after ``--``): ``after`` alone, and each
    suffix of ``unknown`` followed by ``after``, so an option value the hook
    does not know (``--tl off``) cannot hide the verb. A reading for each;
    any one can block."""
    if program == "ppds-mcp-server":
        # The full reading comes first so a block names a --read-only=<value>
        # token when there is one; both orders of the forwarded tokens and the
        # tokens after `--`, so neither can take the flag as its value.
        readings = [[program] + unknown + after, [program] + after + unknown]
        if not _read_only_forwarded(unknown):
            readings.append([program] + after)
        return _dedupe(readings)
    # The likeliest reading first, so a block names it: the tokens after `--`
    # when there are any, else every forwarded token.
    readings = [[program] + after] if after else []
    for k in range(min(len(unknown), _DOTNET_MAX_TOKENS)):
        readings.append([program] + unknown[k:] + after)
    if not after:
        readings.append([program])
    if len(unknown) > _DOTNET_MAX_TOKENS:
        readings.append([program, UNREADABLE])
    readings = _dedupe(readings)
    return [_Reading(r, origin) for r in readings] if origin else readings


def _dotnet_run(tokens: list, project) -> list:
    """argv readings for ``dotnet run <tokens>`` in the original matcher."""
    parsed = _parse_run(tokens)
    if parsed is None:
        return []
    value, unknown, after = parsed
    program = _dotnet_project(value if value is not None else project)
    if program is None:
        return []
    return _run_readings(program, unknown, after, "dotnet run " + _written(tokens))


def _run_hits(tokens: list, cwds) -> list:
    """argv readings for ``dotnet run <tokens>`` over candidate working
    directories; a project only known at run time reads as both programs."""
    parsed = _parse_run(tokens)
    if parsed is None:
        return []
    project, unknown, after = parsed
    out = []
    for program in _project_programs(project, cwds):
        for prog in (_PPDS_PROGRAMS if program == "?" else (program,) if program else ()):
            out.extend(_run_readings(prog, unknown, after, "dotnet run " + _written(tokens)))
    return out


_PACKAGE_PROGRAMS = {"ppds.cli": "ppds", "ppds.mcp": "ppds-mcp-server"}
# `dotnet` verbs that build or test rather than run the project.
_DOTNET_NOT_RUN = {"build", "test", "msbuild", "publish", "pack", "format", "restore", "clean", "vstest"}


def _package_exec(tokens: list) -> list:
    """``dotnet tool exec|execute <package> ...`` / ``dnx <package> ...``: the
    PPDS tool packages (``PPDS.Cli``, ``PPDS.Mcp``, optionally ``@version``)."""
    dd = tokens.index("--") if "--" in tokens else len(tokens)
    before, after = tokens[:dd], tokens[dd + 1:]
    for k, tok in enumerate(before):
        if tok.startswith("-"):
            continue
        name = tok.split("@", 1)[0].lower()
        if sp.DYN in name:
            programs = _PPDS_PROGRAMS
        elif name in _PACKAGE_PROGRAMS:
            programs = (_PACKAGE_PROGRAMS[name],)
        else:
            continue  # an option value, or another package
        out = []
        for program in programs:
            out.extend(_run_readings(program, before[k + 1:], after, _written(tokens)))
        return out
    return []


def _dotnet_hits(tokens: list, cwds) -> list:
    """Normalise the words after a ``dotnet`` word to PPDS argvs (or [])."""
    i = 0
    while i < len(tokens):
        tok = tokens[i]
        low = tok.lower()
        if low == "exec" or low in _HOST_FLAGS:
            i += 1
            continue
        if tok in _HOST_VALUE_OPTS:
            i += 2
            continue
        m = _OPTION_VALUE_RE.match(tok)
        if m and m.group(1) in _HOST_VALUE_OPTS:
            i += 1
            continue
        if tok.startswith("-") or sp.DYN in tok:
            # an option the hook does not know, or run-time text, before the verb
            return _unknown(tokens[i:] + [UNREADABLE])
        break
    if i >= len(tokens):
        return []
    verb, rest = tokens[i].lower(), list(tokens[i + 1:])
    if re.search(r"\s", verb) and not verb.endswith(".dll"):
        return _unknown([UNREADABLE])  # several words in one argument
    if verb == "watch":
        # `dotnet watch [run]` runs the project; `dotnet watch test` does not.
        k = 0
        while k < len(rest) and rest[k] != "--" and rest[k].startswith("-"):
            k += 2 if rest[k] in _RUN_VALUE_OPTS or rest[k] == "-p" else 1
        if k < len(rest) and rest[k] != "--":
            sub = rest[k].lower()
            if sp.DYN in sub:
                return _unknown(rest)
            if sub == "run":
                del rest[k]
            elif sub in _DOTNET_NOT_RUN:
                return []
        verb = "run"
    if verb == "run":
        return _run_hits(rest, cwds)
    if verb == "dnx" or (verb == "tool" and rest[:1] and rest[0].lower() in ("exec", "execute")):
        return _package_exec(rest if verb == "dnx" else rest[1:])
    return _dotnet_other(verb, rest)


def _dotnet_other(verb: str, rest: list) -> list:
    """``dotnet tool run ppds``, ``dotnet [exec] <path>/ppds.dll``, ``dotnet ppds``."""
    if verb == "tool" and rest[:1] and rest[0].lower() == "run":
        j = 1
        while j < len(rest) and rest[j].startswith("-"):
            j += 1
        verb, rest = (rest[j].lower(), rest[j + 1:]) if j < len(rest) else ("", [])
        rest = rest[1:] if rest[:1] == ["--"] else rest
    if sp.DYN in verb:
        return _unknown(rest + [UNREADABLE])
    verb = _PPDS_ASSEMBLIES.get(re.split(r"[\\/]", verb)[-1], verb)
    return [[verb] + rest] if verb in _PPDS_PROGRAMS else []


def _dotnet_argvs(tokens: list, cwd) -> list:
    """Normalise the tokens after a ``dotnet`` word to PPDS argvs (or [])."""
    i = 0
    while i < len(tokens) and (tokens[i].startswith("-") or tokens[i].lower() == "exec"):
        i += 2 if tokens[i] in _HOST_VALUE_OPTS else 1
    verb, rest = (tokens[i].lower(), tokens[i + 1:]) if i < len(tokens) else ("", [])
    if verb == "watch" and "run" in rest[: rest.index("--") if "--" in rest else len(rest)]:
        rest.remove("run")
        verb = "run"
    if verb == "run":
        return _dotnet_run(rest, cwd)
    if verb == "tool" and rest[:1] == ["run"]:
        j = 1
        while j < len(rest) and rest[j].startswith("-"):
            j += 1
        verb, rest = (rest[j].lower(), rest[j + 1:]) if j < len(rest) else ("", [])
        rest = rest[1:] if rest[:1] == ["--"] else rest
    verb = _PPDS_ASSEMBLIES.get(re.split(r"[\\/]", verb)[-1], verb)
    return [[verb] + rest] if verb in ("ppds", "ppds-mcp-server") else []


def _dotnet_invocations(command: str, deadline=None) -> list:
    """Hits for PPDS started through ``dotnet`` in the original matcher."""
    cds, parts = [], []  # (position, directory after each cd / pushd)
    for m in _CD_RE.finditer(command):
        _tick(deadline)
        target = m.group(1).strip("'\"").replace("\\", "/")
        if target.startswith("/") or target[1:2] == ":":
            parts = []
        parts = (parts + target.split("/"))[-32:]
        cds.append((m.start(), "/".join(parts)))
    out, cwd, k, elements = [], None, 0, 0
    for count, match in enumerate(_DOTNET_RE.finditer(command)):
        if count >= _DOTNET_MAX_OCCURRENCES or elements > _MAX_ARG_ELEMENTS:
            out.extend(_unknown([UNREADABLE]))
            break
        _tick(deadline)
        while k < len(cds) and cds[k][0] < match.start():
            cwd, k = cds[k][1], k + 1
        tail = command[match.end():match.end() + _DOTNET_MAX_CHARS]
        cut = len(command) - match.end() > _DOTNET_MAX_CHARS
        tail = tail[1:] if tail[:1] in ("'", '"') else tail
        lexer = shlex.shlex(tail, posix=True)
        # As shlex.split, but a backslash stays literal (Windows paths).
        lexer.whitespace_split, lexer.commenters, lexer.escape = True, "", ""
        args, stopped = [], False
        try:
            tok = lexer.get_token()
            while tok is not None and len(args) < _DOTNET_MAX_TOKENS:
                if tok in _SHELL_OPERATORS:
                    stopped = True
                    break
                args.append(tok.rstrip(";"))
                if tok.endswith(";"):
                    stopped = True
                    break
                tok = lexer.get_token()
            if tok is None:
                stopped = stopped or not cut
        except ValueError:
            args = []
            for tok in tail.split(None, _DOTNET_MAX_TOKENS)[:_DOTNET_MAX_TOKENS]:
                if tok in _SHELL_OPERATORS:
                    stopped = True
                    break
                args.append(tok)
        if not stopped and (cut or len(args) >= _DOTNET_MAX_TOKENS):
            out.extend(_unknown([UNREADABLE]))
        hits = _dotnet_argvs(args, cwd)
        elements += sum(len(hit) for hit in hits)
        out.extend(hits)
    return out


# ---------------------------------------------------------------------------
# Concern 1: env allowlist decision logic
# ---------------------------------------------------------------------------


def _prog(word: str) -> str:
    prog = os.path.basename(word).lower()
    return prog[:-4] if prog.endswith(".exe") else prog


# Options that name the environment or the profile a command reaches. Every
# spelling of each, in any case, with the value after a space, `=` or `:`.
_ENV_OPTIONS = {"--environment", "-env", "-e", "--env", "--source-env", "-se", "--target-env", "-te"}
_PROFILE_OPTIONS = {"--profile", "-p", "--source-profile", "-sp", "--target-profile", "-tp"}
# A single-dash token holding one of these letters may be a bundle of short
# options (`-ve x`, `-eprod`, `-pdev`) that names an environment or profile.
_TARGET_LETTERS = set("ep")
_OPTION_SPLIT_RE = re.compile(r"^(-{1,2}[^=:]*)[=:](.*)$", re.DOTALL)
# The MCP server's own parser (`McpSessionOptions.Parse`): these exact
# tokens take the next argument.
_MCP_VALUE_OPTIONS = ("--profile", "--environment", "--allowed-env")


def _split_option(token: str):
    """``(name, inline value)`` for an option token; the value follows the
    first ``=`` or ``:`` and is None when there is none."""
    m = _OPTION_SPLIT_RE.match(token)
    return (m.group(1), m.group(2)) if m else (token, None)


def _option_values(args: list, names: set, letters: set):
    """``(values, problem)``: ``(token, value)`` for every option in
    ``names`` (any case, value after a space, ``=`` or ``:``), and a problem
    when one has no value or a single-dash token may be a bundle holding one
    of ``letters``."""
    values = []
    for i, tok in enumerate(args):
        if not tok.startswith("-") or tok in ("-", "--"):
            continue
        name, inline = _split_option(tok)
        low = name.lower()
        if low in names:
            value = inline if inline is not None else (args[i + 1] if i + 1 < len(args) else None)
            if value is None:
                return values, f"{tok} has no value"
            values.append((tok, value))
        elif not low.startswith("--") and any(ch in letters for ch in low[1:]):
            return values, f"{tok} may be a bundle of short options that names one"
    return values, ""


def target_listed(value: str, allowlist) -> bool:
    """True when a target typed in a command is on the allowlist: the same
    name (any case), or a URL whose host is an allowlist entry."""
    allow = {a.lower() for a in allowlist if a}
    value = value.strip().lower()
    if not value:
        return False
    if value in allow:
        return True
    m = re.match(r"https?://([^/\s]+)/?$", value)
    return bool(m) and m.group(1) in allow


def _url_candidates(url) -> list:
    out = []
    if isinstance(url, str) and url.strip():
        m = re.match(r"https?://([^/]+)", url.strip(), re.IGNORECASE)
        if m:
            host = m.group(1)
            out.append(host)
            label = host.split(".", 1)[0]
            if label and label != host:
                out.append(label)
    return out


def _env_candidates(env) -> list:
    """Names an environment record of the profile store is known by."""
    if not isinstance(env, dict):
        return []
    out = []
    for key in ("displayName", "uniqueName"):
        val = env.get(key)
        if isinstance(val, str) and val.strip():
            out.append(val.strip())
    return out + _url_candidates(env.get("url"))


def read_profile_store():
    """The profile store as a dict, or None when missing or unreadable."""
    data = _read_json(_profile_store_path())
    return data if isinstance(data, dict) else None


def _profiles_named(store, ref: str) -> list:
    """Profiles ``ref`` may select: by name (any case) or by index."""
    if not isinstance(store, dict) or not isinstance(store.get("profiles"), list):
        return []
    ref = ref.strip()
    out = []
    for p in store["profiles"]:
        if not isinstance(p, dict):
            continue
        name = p.get("name")
        if isinstance(name, str) and name.strip() and name.strip().lower() == ref.lower():
            out.append(p)
        elif ref.isdigit() and p.get("index") == int(ref):
            out.append(p)
    return out


def profile_target_problem(store, ref: str, allowlist) -> str:
    """Why selecting profile ``ref`` may reach an environment not on the
    allowlist, or "" when every profile it may name has a listed one. The
    profile's own name does not count, only its environment's names."""
    profiles = _profiles_named(store, ref)
    if not profiles:
        return f"profile {ref!r} is not in the profile store, so its environment is not known"
    for p in profiles:
        if not env_matches_allowlist(_env_candidates(p.get("environment")), allowlist):
            return f"profile {ref!r} reaches an environment that is not on the allowlist"
    return ""


# Commands on which `-e` is `--entity` / `--entities` (src/PPDS.Cli: their
# environment option is `-env`), and on which `-p` is `--password`. There the
# letter names no target; on every other path it is read as one.
_ENTITY_E_PATHS = frozenset(
    [("data", verb) for verb in ("schema", "delete", "update", "load", "truncate")]
    + [("metadata", "attribute", verb) for verb in
       ("create", "update", "delete", "add-option", "update-option", "remove-option")]
    + [("metadata", "column", verb) for verb in ("create", "update", "delete")]
    + [("metadata", "key", verb) for verb in ("create", "delete", "reactivate")]
    + [("metadata", "table", verb) for verb in ("update", "delete")]
    + [("metadata", "entity", verb) for verb in
       ("update", "delete", "add-statusreason", "list-statusreasons", "update-statusreason",
        "remove-statusreason")])
_PASSWORD_P_PATHS = frozenset({("auth", "create")})


def _target_options(path):
    """``(names, letters)``: the target options and the bundle letters that
    may name a target on command ``path``."""
    names, letters = set(_ENV_OPTIONS | _PROFILE_OPTIONS), set(_TARGET_LETTERS)
    if path in _ENTITY_E_PATHS:
        names.discard("-e")
        letters.discard("e")
    if path in _PASSWORD_P_PATHS:
        names.discard("-p")
        letters.discard("p")
    return names, letters


def _target_problem(args: list, allowlist, store, path=(), guess=False):
    """``(why, unplaced)``: why the environment and profile options in
    ``args`` may reach an environment not on the allowlist, or "";
    ``unplaced`` when the reason is an option the hook cannot place (no
    value, or a possible bundle of short options) rather than a target it
    placed and found not listed. ``path`` is the command path: on some
    commands ``-e`` and ``-p`` name no target (``_target_options``). In a
    ``guess`` the hook does not know the program, so ``-e`` and ``-p`` are
    options it cannot place (``grep -e x``, ``git log -p``), not targets."""
    names, letters = _target_options(path)
    if guess:
        names -= {"-e", "-p"}
    envs, problem = _option_values(args, names, letters)
    if problem:
        return f"{problem} (an environment or profile the hook cannot place)", True
    for tok, value in envs:
        name = _split_option(tok)[0].lower()
        if name in _ENV_OPTIONS:
            if not target_listed(value, allowlist):
                return f"{tok} {value}: that environment is not on the allowlist", False
        else:
            why = profile_target_problem(store, value, allowlist)
            if why:
                return f"{tok} {value}: {why}", False
    return "", False


def _mcp_target_problem(args: list, allowlist, store) -> str:
    """The MCP server's ``--environment``, ``--allowed-env`` and
    ``--profile`` (also with ``=`` / ``:``): each value must be listed."""
    for i, tok in enumerate(args):
        name, inline = _split_option(tok) if tok.startswith("--") else (tok, None)
        low = name.lower()
        if low not in _MCP_VALUE_OPTIONS:
            continue
        value = inline if inline is not None else (args[i + 1] if i + 1 < len(args) else None)
        if value is None:
            continue  # the server ignores a last option with no value
        if low == "--profile":
            why = profile_target_problem(store, value, allowlist)
            if why:
                return f"{tok} {value}: {why}"
        elif not target_listed(value, allowlist):
            return f"{tok} {value}: that environment is not on the allowlist"
    return ""


def _environments_file() -> str:
    return os.path.join(os.path.dirname(_profile_store_path()), "environments.json")


def _url_key(url) -> str:
    return url.strip().rstrip("/").lower() if isinstance(url, str) else ""


def _names_for_url(url, store) -> list:
    """Names of the environment at ``url``: its host and first label, and the
    names profile-store records with the same URL give it."""
    out = _url_candidates(url)
    key = _url_key(url)
    profiles = store.get("profiles") if isinstance(store, dict) else None
    for p in profiles if isinstance(profiles, list) else ():
        env = p.get("environment") if isinstance(p, dict) else None
        if key and isinstance(env, dict) and _url_key(env.get("url")) == key:
            out.extend(_env_candidates(env))
    return out


def unlisted_labels(allowlist, store=None):
    """``(labels, problem)``: the labels of ``environments.json`` (set with
    ``env config --label``) whose environment is not on the allowlist. A SQL
    statement naming one (``[LABEL].entity``) reaches that environment."""
    data, error = _read_json_checked(_environments_file())
    if data is _MISSING:
        return [], ""
    if error is not None or not isinstance(data, dict):
        return [], "the environment settings file could not be read"
    envs = data.get("environments") or []
    if not isinstance(envs, list):
        return [], "the environment settings file could not be read"
    out = []
    for env in envs:
        if not isinstance(env, dict):
            return [], "the environment settings file could not be read"
        label = env.get("label")
        if not isinstance(label, str) or not label.strip():
            continue
        if not env_matches_allowlist(_names_for_url(env.get("url"), store), allowlist):
            out.append(label.strip())
    return out, ""


# Commands whose SQL may name an environment label; `query history execute`
# runs SQL that is not in the command text.
_SQL_PATHS = {("query", "sql"), ("query", "explain"), ("query", "history", "execute")}


def _sql_label_problem(path, rest: list, allowlist, store=None) -> str:
    labels, problem = unlisted_labels(allowlist, store)
    if problem:
        return f"{problem}, so the SQL may name another environment"
    if not labels:
        return ""
    if path == ("query", "history", "execute") or any(
            arg.lower().startswith(("--file", "--stdin")) for arg in rest):
        return "the SQL is not in the command text and may name an environment label not on the allowlist"
    text = " ".join(rest).lower().replace("]]", "]").replace('""', '"')
    hit = next((label for label in labels if label.lower() in text), None)
    if hit is not None:
        return f"the SQL names the environment label {hit!r}, whose environment is not on the allowlist"
    return ""


def _plain(token: str) -> bool:
    """A literal value: not an option, response file or run-time text."""
    return bool(token) and not token.startswith(("-", "/", "@")) and sp.DYN not in token


def _env_settings_read(path, rest: list) -> bool:
    """True for the forms of ``env config`` / ``env type`` that only read
    local settings, each positively recognised: ``env config`` with nothing,
    ``--list``, ``--show`` / ``-s``, a URL, or a URL then ``--show`` /
    ``-s``; ``env type`` with nothing or ``--list``."""
    if path == ("env", "config"):
        if rest in ([], ["--list"], ["--show"], ["-s"]):
            return True
        if len(rest) == 1:
            return _plain(rest[0])
        return len(rest) == 2 and _plain(rest[0]) and rest[1] in ("--show", "-s")
    if path == ("env", "type"):
        return rest in ([], ["--list"])
    return False


def _env_read_form(path, rest: list) -> bool:
    """True for the ``env`` forms that pass with an unlisted active env:
    ``env`` alone or with a help flag, ``env list``, ``env who``, and the
    read forms of ``env config`` / ``env type``."""
    if path == ("env",):
        return not rest or (len(rest) == 1 and rest[0] in _HELP_FLAGS)
    if path in (("env", "list"), ("env", "who")):
        return True
    return _env_settings_read(path, rest)


def _switch_targets(rest: list, names: set, letters: set):
    """``(values, problem)`` for a switch: the option values plus every
    plain word (the CLI takes only the option; a word is checked too)."""
    values, problem = _option_values(rest, names, letters)
    if problem:
        return [], problem
    taken = set()
    for i, tok in enumerate(rest):
        if tok.startswith("-") and _split_option(tok)[1] is None and _split_option(tok)[0].lower() in names:
            taken.add(i + 1)
    words = [tok for i, tok in enumerate(rest) if i not in taken and _plain(tok)]
    return [v for _, v in values] + words, ""


# The words of a switch (`env select`, `org use`, `auth switch` ...) and the
# options that name a target. A command text holding any of them makes an
# invocation the hook cannot place a refusal even with a listed active env.
_SWITCH_NOUNS = frozenset({"env", "org", "auth"})
_SWITCH_VERBS = frozenset({"select", "switch", "use"})
_TEXT_TARGET_OPTIONS = frozenset({
    "--environment", "-env", "--env", "--source-env", "--target-env", "-se", "-te",
    "--profile", "--source-profile", "--target-profile", "-sp", "-tp", "--allowed-env"})
# `-p` and `-e` do not count in the text: most programs have them
# (`mkdir -p`, `git log -p`, `grep -e`), and on some ppds commands they are
# `--password` and `--entity`. In an invocation the hook placed they are
# read by command path (`_target_options`).
# Words for the switch test: split also at `=` (`A=env B=select`).
_TEXT_WORD_RE = re.compile(r"[^\s;|&()<>'\"`,{}\[\]^\\‘’‚‛“”„=]+")
# A target option name anywhere in the text, not only as a word of its own
# (`E=--environment`, `$'\n--profile\n'`): the name not joined to another
# name character on either side (`--env-file` and `x-env` are not it).
_TEXT_OPTION_RE = re.compile(
    r"(?<![A-Za-z0-9_-])(?:" + "|".join(sorted((re.escape(o) for o in _TEXT_TARGET_OPTIONS),
                                               key=len, reverse=True)) + r")(?![A-Za-z0-9_-])",
    re.IGNORECASE)
# Single-letter escapes of C-style strings (`$'...'`, printf, echo -e) and
# of PowerShell (`` `n ``): decoded to the character so the words they
# separate are seen.
_LETTER_ESCAPE_RE = re.compile(r"[\\`]([ntrabfve0])")
_LETTER_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "a": "\a", "b": "\b", "f": "\f", "v": "\v",
                   "e": "\x1b", "0": "\x00"}


def _decode_text(text: str) -> str:
    """``text`` with every escape the hook knows decoded: ``\\x``, ``\\u``,
    octal and `` `u{} `` (``_unescape``), then single-letter escapes."""
    return _LETTER_ESCAPE_RE.sub(lambda m: _LETTER_ESCAPES[m.group(1)], _unescape(text))


_B64_WORD_RE = re.compile(r"[A-Za-z0-9+/]{8,}={0,2}")


def _encoded_payloads(text: str) -> list:
    """The words of ``text`` that decode as base64 of UTF-16LE text."""
    out = []
    for m in _B64_WORD_RE.finditer(text):
        word = m.group(0)
        if len(word) % 4:
            continue
        try:
            out.append(base64.b64decode(word, validate=True).decode("utf-16-le"))
        except (ValueError, UnicodeDecodeError):
            continue
    return out


def text_names_target(command: str) -> bool:
    """True when ``command`` names a switch or a target anywhere: a word of
    ``_SWITCH_NOUNS`` and a word of ``_SWITCH_VERBS`` (any case; words split
    at blanks, quotes, operators and ``=``), or an option of
    ``_TEXT_TARGET_OPTIONS`` anywhere in the text (any case, not joined to
    another name character: ``E=--environment`` counts, ``--env-file`` does
    not); ``-p`` and ``-e`` do not count.
    The text is read as written, with escapes decoded (``\\x``, ``\\u``,
    octal and single-letter ones such as ``\\n`` and `` `n ``), and both
    again with every quoting character removed, so ``e"n"v``,
    ``\\-\\-profile`` and ``env\\nselect`` count; so is every word that
    decodes as an encoded PowerShell command (base64 of UTF-16LE, as ``pwsh
    -EncodedCommand`` takes it), to three levels."""
    if not command:
        return False
    sources, fresh = [command], [command]
    for _ in range(3):
        fresh = [p for text in fresh for p in _encoded_payloads(text)]
        sources.extend(fresh)
    texts = []
    for source in sources:
        decoded = _decode_text(source)
        texts.extend((source, decoded,
                      "".join(ch for ch in source if ch not in _QUOTING_CHARS),
                      "".join(ch for ch in decoded if ch not in _QUOTING_CHARS)))
    if any(_TEXT_OPTION_RE.search(text) for text in texts):
        return True
    nouns = verbs = False
    for text in texts:
        for m in _TEXT_WORD_RE.finditer(text):
            word = m.group(0).lower()
            if word in _SWITCH_NOUNS:
                nouns = True
            elif word in _SWITCH_VERBS:
                verbs = True
            if nouns and verbs:
                return True
    return False


def decide_env(argv, allowlist, active, store=_MISSING, required=None, names_target=True):
    """Return (allow, reason). reason is empty on allow, a message on block.

    With an allowlist configured, an invocation passes only when the hook
    can place everything in it that decides which environment it reaches,
    and all of it is listed: the active env (``active``, and ``required``:
    further candidate lists that must also match), every environment and
    profile option, and for ``env select`` / ``auth select`` the target.

    What it cannot place (text it could not read, run-time text, and in a
    guess (a ``_Guess``: a program it could not identify, or a mention where
    no program runs) a response file, ``-e`` / ``-p``, an option bundle, an
    option with no value, or a switch with no target) is refused when
    ``names_target`` (``text_names_target`` of the whole command text: it
    names a switch or a target option); otherwise only the active env
    decides, so a listed active env passes it. The default is the strict
    reading. A target it placed and found not listed is always refused."""
    if not argv:
        return True, ""
    prog = _prog(argv[0])
    if prog not in _PPDS_PROGRAMS:
        return True, ""
    if store is _MISSING:
        store = read_profile_store()
    args = list(argv[1:])

    def refuse(why):
        return False, _block_msg_target(why, allowlist, argv)

    def active_check():
        if env_matches_allowlist(active, allowlist) and all(
                env_matches_allowlist(c, allowlist) for c in (required or ())):
            return True, ""
        return False, _block_msg_active_not_allowed(active, allowlist, argv)

    def unplaced(why):
        return refuse(why + " (the command text names a switch or a target option)") \
            if names_target else active_check()

    if UNREADABLE in args:
        return unplaced("the hook could not read this command in full, so it cannot tell which "
                        "environment it reaches")
    response = next((arg for arg in args if arg.startswith("@")), None)
    if response is not None:
        # A response file written after a word naming `ppds` is placed text:
        # refused in every mode. In the argv of a program the hook could not
        # identify it is a guess, judged like the rest of the unplaced text.
        why = f"{response} is a response file; its arguments may name another environment"
        return unplaced(why) if isinstance(argv, _Guess) else refuse(why)
    dynamic = any(sp.DYN in arg for arg in args)
    run_time = "an argument only known when the command runs may name another environment"

    if prog == "ppds-mcp-server":
        if dynamic:
            return unplaced(run_time)
        why = _mcp_target_problem(args, allowlist, store)
        return refuse(why) if why else active_check()

    guess = isinstance(argv, _Guess)
    # What the hook cannot place in a guess (a mention, a program it could not
    # identify) is judged like the rest of the unplaced text.
    cannot = unplaced if guess else refuse
    path, rest = _command_path(args)
    if path == ("env",) and rest and rest[0].lower() in ("switch", "use"):
        path, rest = ("env", "select"), rest[1:]  # not CLI commands; checked like `select`
    if path == ("env", "select"):
        if dynamic:
            return unplaced(run_time)
        targets, problem = _switch_targets(rest, {"--environment", "-env"}, _TARGET_LETTERS)
        if problem or not targets:
            return cannot(problem or "env select with no target the hook can place")
        bad = next((t for t in targets if not target_listed(t, allowlist)), None)
        if bad is not None:
            return False, _block_msg_target_not_allowed(bad, allowlist, argv)
    why, cannot_place = _target_problem(args, allowlist, store, path, guess)
    if why:
        # An option the hook cannot place (no value, a possible bundle) is
        # placed text in a ``ppds`` word's argv: refused. In a guess
        # (``$x -ne 0``, ``grep ppds -rE docs``) it is unplaced text.
        return cannot(why) if cannot_place else refuse(why)
    if dynamic:
        # After the target options, so a run-time value of one (`-p $P`) is
        # a target the hook placed and cannot find listed.
        return unplaced(run_time)
    if path == ("env", "select"):
        return True, ""
    if path == ("auth", "select"):
        targets, problem = _switch_targets(rest, {"--index", "-i", "--name", "-n"}, set("in"))
        if problem or not targets:
            return cannot(problem or "auth select with no profile the hook can place")
        for ref in targets:
            why = profile_target_problem(store, ref, allowlist)
            if why:
                return refuse(f"auth select {ref}: {why}")
        return True, ""
    if path in _SQL_PATHS:
        why = _sql_label_problem(path, rest, allowlist, store)
        if why:
            return refuse(why)
    if path[:1] == ("env",) and _env_read_form(path, rest):
        return True, ""
    return active_check()


def _extract_switch_target(args):
    """The first target of a ``switch`` / ``select`` argv tail (the option
    value with a space, ``=`` or ``:``, else a plain word), or ""."""
    targets, problem = _switch_targets(list(args), {"--environment", "-env"}, _TARGET_LETTERS)
    return "" if problem or not targets else targets[0]


# Variables of the CLI that decide which environment it reaches.
_SAFE_PPDS_VARS = {"ppds_shakedown", "ppds_safe_envs", "ppds_hook_budget_seconds", "ppds_internal",
                   "ppds_mcp_log_level", "ppds_force_tui"}
_PPDS_VAR_RE = re.compile(r"ppds_([a-z0-9_]*)", re.IGNORECASE)
_PERSISTENT_SET_RE = re.compile(r"env:\\?ppds_|setenvironmentvariable|\bsetx\b", re.IGNORECASE)


def _ppds_vars(command: str) -> list:
    """Variable names starting ``PPDS_`` in ``command`` (quoting removed,
    escapes decoded) that are not known to leave the target alone; a name
    cut short by run-time text counts too."""
    out = []
    for text in (command, _unescape(command)):
        flat = "".join(ch for ch in text if ch not in _QUOTING_CHARS)
        for m in _PPDS_VAR_RE.finditer(flat):
            if m.group(0).lower() not in _SAFE_PPDS_VARS:
                out.append(m.group(0))
    return out


# Variables that decide the home folder the profile store lives under
# (`~/.ppds`; `%LOCALAPPDATA%\PPDS` on Windows), the way `PPDS_CONFIG_DIR`
# overrides it. Setting one in a command with an invocation is refused.
_HOME_VARS = r"home|userprofile|localappdata|appdata|homedrive|homepath"


def _set_re(names: str, any_drive_use: bool = True):
    """A pattern for a text that sets (or unsets) a variable matching
    ``names``: ``NAME=x`` (also after ``env``, ``set``, ``export``),
    ``$env:NAME = x`` (any other use of the ``env:`` drive too unless
    ``any_drive_use`` is False),
    ``SetEnvironmentVariable(... NAME``, ``export`` / ``declare`` /
    ``unset`` / ``env -u`` and their kin before it. Group 1 to 5 is the name."""
    return re.compile(
        r"(?<![\w$.:-])(" + names + r")\+?=(?!=)"
        r"|env:(" + names + r")" + ("" if any_drive_use else r"\s*[+]?=(?!=)") +
        r"|(?:setenvironmentvariable|set-item|new-item|set-content|remove-item|\bsi\b|\bni\b|\bsc\b|\bri\b)"
        r"[^;|&\n]*?(?<![\w$])(" + names + r")"
        r"|\b(?:export|declare|typeset|readonly|local|unset|setx|set)\s+(?:-\w+\s+)*(" + names + r")"
        r"|(?<![\w-])(?:-u|--unset)[=\s]+(" + names + r")",
        re.IGNORECASE)


_HOME_SET_RE = _set_re(r"(?:" + _HOME_VARS + r")(?![\w$])", any_drive_use=False)
_PPDS_SET_RE = _set_re(r"ppds_[a-z0-9_$]*")


def _set_names(pattern, command: str) -> list:
    """The variable names ``command`` sets by ``pattern`` (quoting removed,
    escapes decoded), as written."""
    out = []
    for text in (command, _unescape(command)):
        flat = "".join(ch for ch in text if ch not in _QUOTING_CHARS)
        for m in pattern.finditer(flat):
            out.append(next(g for g in m.groups() if g))
    return out


def _home_override(command: str) -> str:
    """The home-folder variable ``command`` sets (``HOME=/tmp/x``,
    ``$env:USERPROFILE = ...``, ``export HOME``), upper case, or ""."""
    names = _set_names(_HOME_SET_RE, command)
    return names[0].upper() if names else ""


def target_variable_problem(command: str, invocations: list, strict: bool = True) -> str:
    """Why ``command`` may change the environment the CLI reaches through
    a variable (``PPDS_CONFIG_DIR``, ``PPDS_PROFILE``,
    ``PPDS_ENVIRONMENT_URL`` ...) or the home folder (``HOME=``,
    ``$env:USERPROFILE =``), or "". With an invocation any such ``PPDS_``
    name counts; when ``strict`` is False and every invocation is a guess
    (a mention, a program the hook could not identify), only a name the
    text sets (``_PPDS_SET_RE``). Without an invocation, a PowerShell-style
    assignment that outlives the command (``$env:PPDS_X = ...``,
    ``SetEnvironmentVariable``, ``setx``)."""
    names = _ppds_vars(command)
    if names and invocations and not strict and all(isinstance(argv, _Guess) for argv in invocations):
        names = [n for n in _set_names(_PPDS_SET_RE, command) if n.lower() not in _SAFE_PPDS_VARS]
    if invocations:
        home = _home_override(command)
        if home:
            return (f"the command sets {home}, which decides where the CLI finds its profile "
                    "store and so the environment it reaches")
    if not names:
        return ""
    if invocations:
        return f"the command names {names[0]}, which can change the environment the CLI reaches"
    if _PERSISTENT_SET_RE.search("".join(ch for ch in command if ch not in _QUOTING_CHARS)):
        return f"the command sets {names[0]}, which can change the environment later commands reach"
    return ""


# ---------------------------------------------------------------------------
# Concern 2: write/mutation decision logic
# ---------------------------------------------------------------------------


# Every command path of the ppds CLI, with how the write-block treats it.
# One line per parent path ("." is the root): `<child>=<class>`.
#   r        reads only; anything after it is arguments and options.
#   g        a group: passes only when nothing (or only a help flag) follows.
#   w        writes, or not known to only read: blocked.
#   dry-run  `plugins deploy`: passes only with a positively placed --dry-run.
#   sql      `query sql`: passes only with inline SQL that only reads.
#   list     `env config` / `env type`: passes only in a form that only reads.
#   ver      `version`: passes only alone or with --check (--update replaces the CLI).
#   switch   `env select`: passes only when an allowlist is configured (the
#            environment gate then checks the target).
# The root itself (`ppds` with no arguments) starts the TUI: a write.
# A path that is not listed is blocked. tests/hooks/test_shakedown_cli_commands.py
# derives the command tree from src/PPDS.Cli and fails when a path is missing.
_CLI_COMMANDS = """
.: api=g auth=g connection-references=g connections=g custom-apis=g data=g data-providers=g data-sources=g deployment-settings=g docs=r env=g environment-variables=g flows=g forms=g import-jobs=g interactive=w internal=w logs=g metadata=g model-driven-app=g plugin-traces=g plugins=g publish=w query=g roles=g schema=g serve=w service-endpoints=g solutions=g users=g version=ver views=g webresources=g
api: request=w
auth: clear=w create=w delete=w list=r name=w select=w update=w who=r
connection-references: analyze=r connections=r flows=r get=r list=r
connections: get=r list=r
custom-apis: add-parameter=w get=r list=r register=w remove-parameter=w set-plugin=w unregister=w update=w update-parameter=w
data: analyze=r copy=w delete=w export=r import=w load=w schema=r truncate=w update=w users=r
data-providers: get=r list=r register=w unregister=w update=w
data-sources: get=r list=r register=w unregister=w update=w
deployment-settings: generate=r sync=w validate=r
env: config=list list=r select=switch type=list who=r
environment-variables: export=r get=r list=r set=w url=r
flows: disable=w enable=w get=r list=r url=r
forms: add-field=w add-section=w add-subgrid=w add-tab=w find-section=r find-tab=r get=r list=r move-field=w remove-field=w remove-section=w remove-subgrid=w remove-tab=w reorder-fields=w reorder-fields-strict=w repair-bindings=w set-xml=w update-section=w update-tab=w
import-jobs: data=r get=r list=r url=r wait=r
logs: dump=r tail=r
metadata: attribute=g attributes=r choice=g column=g entities=r entity=r key=g keys=r optionset=r optionsets=r publish=w relationship=g relationships=r table=g
metadata attribute: add-option=w create=w delete=w remove-option=w update=w update-option=w
metadata choice: add-option=w create=w delete=w remove-option=w reorder=w update=w update-option=w
metadata column: create=w delete=w update=w
metadata entity: add-statusreason=w create=w delete=w list-statusreasons=r remove-statusreason=w update=w update-statusreason=w
metadata key: create=w delete=w reactivate=w
metadata optionset: add-option=w create=w delete=w remove-option=w reorder=w update=w update-option=w
metadata relationship: create=w delete=w update=w
metadata table: create=w delete=w update=w
model-driven-app: add-copilot=w add-table=w get=r inspect-app-assistant=r list=r list-copilots=r remove-copilot=w remove-table=w set-charts=w set-forms=w set-sitemap-xml=w set-views=w sitemap=r
plugin-traces: delete=w get=r list=r related=r settings=g timeline=r
plugin-traces settings: get=r set=w
plugins: clean=w deploy=dry-run diff=r disable=w download=g enable=w extract=r get=r list=r register=g unregister=w update=g
plugins download: assembly=r package=r
plugins register: assembly=w image=w package=w step=w type=w
plugins update: assembly=w image=w package=w step=w
query: explain=r fetch=r history=g sql=sql
query history: clear=w delete=w execute=w get=r list=r
roles: assign=w list=r remove=w show=r
schema: compare=r
service-endpoints: get=r list=r register=g unregister=w update=w
service-endpoints register: eventhub=w queue=w topic=w webhook=w
solutions: components=r export=r get=r import=w list=r publish=w url=r
users: list=r roles=r show=r
views: add-column=w clear-filter=w clear-sort=w get=r list=r remove-column=w reorder-columns=w set-fetchxml=w set-filter=w set-sort=w update-column=w
webresources: create=w get=r list=r publish=w pull=r push=w update=w url=r
"""
# Other spellings of a top-level command (deprecated names and `org`).
_CLI_ALIASES = {"org": "env", "connectionreferences": "connection-references",
                "environmentvariables": "environment-variables", "importjobs": "import-jobs",
                "plugintraces": "plugin-traces"}


def _parse_cli_commands(text: str) -> dict:
    """``{path tuple: class}`` from ``_CLI_COMMANDS``."""
    out = {(): "g"}
    for line in text.strip().splitlines():
        parent, _, children = line.partition(":")
        prefix = () if parent.strip() == "." else tuple(parent.split())
        for item in children.split():
            name, _, cls = item.partition("=")
            out[prefix + (name,)] = cls
    return out


CLI_COMMANDS = _parse_cli_commands(_CLI_COMMANDS)
# Child command names per path. The CLI's parser resolves a child named
# after the parent's own options or arguments (`metadata entity account
# add-statusreason`), so such a token anywhere after the path decides.
_CLI_CHILDREN = {}
for _path in CLI_COMMANDS:
    if _path:
        _CLI_CHILDREN.setdefault(_path[:-1], set()).add(_path[-1])

# Help flags, recursive on every command (System.CommandLine's HelpOption),
# compared exactly (the parser is case-sensitive). `--version` is a root
# option only.
_HELP_FLAGS = {"--help", "-h", "-?", "/?", "/h"}
_ROOT_ONLY_FLAGS = _HELP_FLAGS | {"--version"}
# Words in SQL text that write (or could): any of them makes `query sql` a write.
_SQL_WRITE_WORDS = frozenset(
    "insert update delete merge upsert truncate drop create alter exec execute into grant revoke "
    "deny bulk openrowset declare set".split())


def sql_words(text: str):
    """The identifier-like words of ``text`` the way the T-SQL lexer splits
    them, erring towards more words: a number (``1``, ``1.5e3``, ``0x1F``)
    ends where its digits end, so ``1DELETE`` and ``0x12UPDATE`` hold a
    word; an exponent marker with no digits after it (``1eDELETE``,
    ``1.e+DELETE``) ends the number too, and both the word after the marker
    and the word starting at it are read; a word is ASCII letters, digits,
    ``_ @ # $`` after a letter, ``_``,
    ``@`` or ``#``; every other character, a non-ASCII letter included,
    separates. Strings, comments and bracketed names are read like code."""
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if "0" <= c <= "9":
            if c == "0" and text[i + 1:i + 2] in ("x", "X"):
                i += 2
                while i < n and text[i] in "0123456789abcdefABCDEF":
                    i += 1
                continue
            while i < n and "0" <= text[i] <= "9":
                i += 1
            if i < n and text[i] == "." :
                i += 1
                while i < n and "0" <= text[i] <= "9":
                    i += 1
            if i < n and text[i] in "eE":
                j = i + 1
                if j < n and text[j] in "+-":
                    j += 1
                if j < n and "0" <= text[j] <= "9":
                    i = j
                    while i < n and "0" <= text[i] <= "9":
                        i += 1
                    continue
                # An exponent marker with no digits: the T-SQL lexer takes
                # `1e` / `1.e` / `1e+` as the number, so a word starts right
                # after it (`1eDELETE` holds DELETE). The word that starts at
                # the marker itself (`1exec` holds `exec`) is read as well.
                k = j
                while k < n and text[k].isascii() and (text[k].isalnum() or text[k] in "_@#$"):
                    k += 1
                if j < k and (text[j].isalpha() or text[j] in "_@#"):
                    yield text[j:k]
            continue
        if c.isascii() and (c.isalpha() or c in "_@#"):
            j = i + 1
            while j < n and text[j].isascii() and (text[j].isalnum() or text[j] in "_@#$"):
                j += 1
            yield text[i:j]
            i = j
            continue
        i += 1


def sql_write_word(text: str):
    """The first word of ``text`` that writes (or could), lower case, or None."""
    return next((w.lower() for w in sql_words(text) if w.lower() in _SQL_WRITE_WORDS), None)


def _plugins_deploy_blocked(args: list) -> bool:
    """True unless ``--dry-run`` is positively placed: the bare token (no
    ``=`` / ``:`` value, exact spelling), before any ``--``, not directly
    after an option-looking token (whose value it might be), and not
    followed by a word the flag might take as its value. Any other token
    spelled ``--dry-run...`` also blocks."""
    dry = False
    for k, arg in enumerate(args):
        if arg == "--":
            break  # after `--` every token is an operand
        if not arg.lower().startswith("--dry-run"):
            continue
        if arg != "--dry-run":
            return True
        prev = args[k - 1] if k > 0 else None
        nxt = args[k + 1] if k + 1 < len(args) else None
        if prev is not None and prev.startswith(("-", "/")):
            return True
        if nxt is not None and not nxt.startswith("-"):
            return True
        dry = True
    return not dry


# `query sql` options (src/PPDS.Cli/Commands/Query/SqlCommand.cs, the query
# group's options, GlobalOptions): the ones that take a value, and the bool
# ones, which the parser lets take an optional `true` / `false`.
_SQL_VALUE_OPTIONS = frozenset({"--file", "--profile", "-p", "--environment", "-env", "--top", "-t",
                                "--page", "--paging-cookie", "--correlation-id", "--output-format", "-f"})
_SQL_BOOL_OPTIONS = frozenset({"--stdin", "--show-fetchxml", "--explain", "--tds", "--confirm",
                               "--dry-run", "--no-limit", "--count", "-c", "--debug", "--quiet", "-q",
                               "--verbose", "-v"})
_SQL_COMMENT_RE = re.compile(r"\A(?:\s+|--[^\n]*(?:\n|\Z)|/\*.*?\*/)*", re.DOTALL)
_SQL_READ_START_RE = re.compile(r"(?:select|with)(?![A-Za-z0-9_@#$])", re.IGNORECASE)


def _sql_text_args(args: list):
    """``(sql, problem)``: the positional SQL text of ``query sql <args>`` as
    the CLI's parser places it, or a problem when the hook cannot place it:
    an option it does not know, a bool option with an attached value other
    than ``true`` / ``false`` (``--dry-run=no``), or not exactly one SQL
    text. A token with a blank in it is text, not an option."""
    positional, i = [], 0
    while i < len(args):
        tok = args[i]
        if tok == "--":
            positional.extend(args[i + 1:])
            break
        if not tok.startswith("-") or re.search(r"\s", tok):
            positional.append(tok)
            i += 1
            continue
        name, inline = _split_option(tok)
        if name in _SQL_VALUE_OPTIONS:
            i += 1 if inline is not None else 2
            continue
        if name in _SQL_BOOL_OPTIONS:
            if inline is not None and inline.lower() not in ("true", "false"):
                return None, f"{tok} (a value the parser does not take as true or false)"
            if inline is None and i + 1 < len(args) and args[i + 1].lower() in ("true", "false"):
                i += 1
            i += 1
            continue
        return None, f"{tok} (an option the hook does not know on query sql)"
    if len(positional) != 1:
        return None, "no SQL text the hook can place" if not positional else "more than one SQL text"
    return positional[0], ""


def sql_reads(text: str) -> bool:
    """True when SQL ``text`` is positively a read: after leading blanks and
    comments it begins with ``SELECT`` or ``WITH`` (any case)."""
    rest = text[_SQL_COMMENT_RE.match(text).end():]
    return bool(_SQL_READ_START_RE.match(rest))


def _sql_blocked(args: list) -> str:
    """Why ``query sql <args>`` counts as a write, or "" when it only reads.
    It passes only with one SQL text placed as the CLI's parser places it
    that is positively a read (``sql_reads``), and with no word that writes
    in any argument (an option value as well)."""
    if any(arg.lower().startswith(("--file", "--stdin")) for arg in args):
        return "SQL read from a file or stdin"
    if any(sp.DYN in arg for arg in args):
        return "SQL only known when the command runs"
    if not args:
        return "no SQL text"
    word = sql_write_word("\n".join(args))
    if word:
        return f"SQL containing {word.upper()}"
    sql, problem = _sql_text_args(args)
    if problem:
        return problem
    if not sql_reads(sql):
        return "SQL that does not begin with SELECT or WITH, so it is not known to only read"
    return ""


def mcp_read_only(args: list) -> bool:
    """True when the MCP server starts read-only: as its parser
    (``McpSessionOptions.Parse``) reads ``args``, the exact token
    ``--read-only`` appears and is not taken as the value of ``--profile``,
    ``--environment`` or ``--allowed-env``."""
    i, read_only = 0, False
    while i < len(args):
        if args[i] in _MCP_VALUE_OPTIONS and i + 1 < len(args):
            i += 2
            continue
        if args[i] == "--read-only":
            read_only = True
        i += 1
    return read_only


def _hides_verb(token: str) -> bool:
    """True for a token before the verb that may change what runs: an option
    (``--verbose``, ``/x``) or several words in one argument."""
    return token.startswith(("-", "/")) or bool(re.search(r"\s", token))


def _command_path(args: list):
    """``(path, rest)``: the longest known command path at the start of
    ``args`` (aliases resolved) and the arguments after it."""
    path, k = (), 0
    while k < len(args):
        tok = args[k].lower()
        if not path:
            tok = _CLI_ALIASES.get(tok, tok)
        if path + (tok,) not in CLI_COMMANDS:
            break
        path += (tok,)
        k += 1
    return path, args[k:]


def is_mutation(argv: list, allowlist_configured: bool = False) -> tuple:
    """Return (is_mutation, reason). reason is empty when not a mutation.

    During a shakedown a ppds command passes only when its command path is
    one the hook knows to be read-only (``CLI_COMMANDS``); every other path,
    one the hook has never heard of included, counts as a write. An option
    before the command path, several words in one argument, or run-time text
    there means the command is not known. So does a response file (``@x``)
    anywhere, and a child command name anywhere after its parent's path. A
    help flag passes only directly after the command path. ``ppds`` with no
    arguments starts the TUI: a write. ``env select`` passes only when an
    allowlist is configured (``allowlist_configured``), because then the
    environment gate checks its target."""
    if not argv:
        return False, ""

    prog = _prog(argv[0])
    if prog not in ("ppds", "ppds-mcp-server"):
        return False, ""

    if UNREADABLE in argv:
        return True, "a command the hook could not read in full (treated as a write)"

    # The CLI's parser expands `@file` into the arguments the file holds.
    response = next((arg for arg in argv[1:] if arg.startswith("@")), None)
    if response is not None:
        return True, f"{_shown([response])} (a response file, whose arguments are not known; treated as a write)"

    if prog == "ppds-mcp-server":
        # The server honours only the exact token: --read-only=<anything>
        # (false, true, empty) starts it writable.
        valued = [arg for arg in argv[1:] if arg.lower().startswith(("--read-only=", "--read-only:"))]
        if valued:
            return True, (f"ppds-mcp-server {valued[0]} (the server only accepts the bare "
                          "--read-only flag; --read-only=<value> starts it writable)")
        if mcp_read_only(list(argv[1:])):
            return False, ""
        if "--read-only" in argv[1:]:
            return True, ("ppds-mcp-server (--read-only is taken as the value of the option before it; "
                          "the server starts writable)")
        return True, "ppds-mcp-server (missing --read-only flag during shakedown)"

    args = argv[1:]
    if not args:
        return True, "ppds with no arguments (starts the interactive TUI; treated as a write)"
    if len(args) == 1 and args[0] in _ROOT_ONLY_FLAGS:
        return False, ""
    path, rest = _command_path(args)
    shown = " ".join(path)
    nxt = rest[0] if rest else None
    cls = CLI_COMMANDS[path]

    if nxt is not None and nxt in _HELP_FLAGS:
        return False, ""  # help, placed directly after the command path: nothing runs
    if cls == "g":
        if nxt is None:
            return False, ""  # a group alone prints its help
        if sp.DYN in nxt:
            return True, f"{(shown + ' ') if shown else ''}$... (a command only known when it runs; treated as a write)"
        if _hides_verb(nxt):
            return True, (f"{_shown([nxt])} before the command{(' after ' + shown) if shown else ''} "
                          "(the command is not known; treated as a write)")
        return True, (f"{(shown + ' ') if shown else ''}{_shown([nxt])} "
                      "(not a command the hook knows to be read-only; treated as a write)")
    child = next((arg for arg in rest if arg.lower() in _CLI_CHILDREN.get(path, ())), None)
    if (path in _CLI_CHILDREN or cls != "r") and any(sp.DYN in arg for arg in rest):
        # a run-time value could name a child command or turn a condition off
        return True, f"{shown} with a value only known when it runs (treated as a write)"
    if child is not None:
        return True, (f"{shown} ... {_shown([child])} (the CLI runs the {child} command named after "
                      f"other arguments; treated as a write)")
    if cls == "r":
        return False, ""
    if cls == "ver":
        if rest in ([], ["--check"]):
            return False, ""
        return True, f"{shown} (passes only alone or with --check; --update replaces the CLI)"
    if cls == "switch":
        if allowlist_configured:
            return False, ""
        return True, (f"{shown} (with no safe-env allowlist configured nothing checks the target; "
                      "treated as a write)")
    if cls == "dry-run":
        if _plugins_deploy_blocked(rest):
            return True, f"{shown} (no --dry-run flag the hook can place)"
        return False, ""
    if cls == "list":
        if _env_settings_read(path, rest):
            return False, ""
        return True, f"{shown} (changes settings unless it is one of the forms that only read)"
    if cls == "sql":
        why = _sql_blocked(rest)
        return (True, f"{shown} ({why}; treated as a write)") if why else (False, "")
    return True, f"{shown} (writes, or is not known to only read)"


# ---------------------------------------------------------------------------
# Stderr message helpers
# ---------------------------------------------------------------------------


def _shown(argv) -> str:
    """argv as text for a message; run-time-only text shows as ``$...``."""
    return " ".join(argv).replace(sp.DYN, "$...")


def _command_line(argv) -> str:
    """The ``Command:`` line of a block message. A reading the hook made up
    for a program it could not identify shows the words as written, or
    nothing when it has none to show."""
    if isinstance(argv, _Guess):
        if argv.origin:
            return f"{argv.origin}  ({argv.note})"
        return "(not shown: the hook could not read the command in full)"
    if isinstance(argv, _Reading) and argv.origin:
        return f"{argv.origin}  (read as: {_shown(argv)})"
    return _shown(argv)


def _format_active(active):
    if not active:
        return "(unknown -- no active profile / store missing)"
    return active[0]


def _config_help_lines():
    return [
        "  Configure the allowlist via either:",
        "    1. Env var: export PPDS_SAFE_ENVS=ppds-dev,ppds-test",
        "    2. File:    .claude/settings.json",
        '       { "safety": { "shakedown_safe_envs": ["ppds-dev", "ppds-test"] } }',
    ]


def _block_msg_active_not_allowed(active, allowlist, argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: active env is not in the safe-env allowlist.",
        f"  Command:    {_command_line(argv)}",
        f"  Active env: {_format_active(active)}",
        f"  Allowlist:  {', '.join(allowlist)}",
        "",
        "  To proceed, switch to a safe env:",
        "    ppds env select --environment <name>",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_target(why, allowlist, argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: the command may reach an env that is not in the "
        "safe-env allowlist.",
        f"  Command:   {_command_line(argv)}",
        f"  Problem:   {why}",
        f"  Allowlist: {', '.join(allowlist)}",
        "",
        "  Name only listed environments and profiles, literally, in the command.",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_target_not_allowed(target, allowlist, argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: switch target is not in the safe-env allowlist.",
        f"  Command:   {_command_line(argv)}",
        f"  Target:    {target}",
        f"  Allowlist: {', '.join(allowlist)}",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_misconfigured(problem, argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: the safe-env allowlist is misconfigured.",
        f"  Command: {_command_line(argv)}",
        f"  Problem: {problem}",
        "",
        "  Fix it so it names at least one env, or remove it (unset the env",
        "  var, or set the list to []) to leave env gating off.",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_mutation(
    argv: list,
    reason: str,
    env_var_name: str,
    *,
    env_active: bool,
    sentinel_active: bool,
) -> str:
    sentinel_rel = os.path.join(".claude", "state", _SENTINEL_FILENAME)
    sources = []
    if env_active:
        sources.append(f"{env_var_name}=1")
    if sentinel_active:
        sources.append(f"sentinel file {sentinel_rel}")
    activation = " and ".join(sources) if sources else f"{env_var_name}=1"

    bypass_lines = [f"    unset {env_var_name}"]
    if sentinel_active:
        bypass_lines.append(f"    rm {sentinel_rel}")

    return "\n".join(
        [
            "BLOCKED [shakedown-safety/readonly]: write/mutation command refused.",
            f"  Command: {_command_line(argv)}",
            f"  Pattern: {reason}",
            "",
            f"  Active: {activation} -- this session is in read-only mode for",
            "  ppds CLI operations. Mutating Dataverse during shakedown",
            "  defeats the purpose -- shakedowns are observation, not",
            "  modification.",
            "",
            "  To bypass (deliberate action -- there is no softer marker):",
            *bypass_lines,
            "",
            "  For plugin deploys specifically, add --dry-run to validate",
            "  without writing.",
            "  For ppds-mcp-server, add --read-only to the launch args.",
        ]
    )


# ---------------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------------


# Answer well inside the 10 s the hook is registered with: Claude Code runs a
# command whose hook times out or exits 1, in both shell tools, so neither
# may happen while a gate applies. `PPDS_HOOK_BUDGET_SECONDS` can only lower
# the budget (tests use it to reach the deadline).
_BUDGET_SECONDS = 3.5


def _budget() -> float:
    try:
        value = float(os.environ.get("PPDS_HOOK_BUDGET_SECONDS", ""))
    except ValueError:
        return _BUDGET_SECONDS
    if value != value:  # NaN
        return _BUDGET_SECONDS
    return max(0.0, min(value, _BUDGET_SECONDS))


# A watchdog refuses the command when the hook has not answered this long
# after the budget: the last line of defence against a slow input or a slow
# machine (Claude Code runs a command whose hook times out).
_WATCHDOG_GRACE_SECONDS = 1.0


def _refuse(message: str) -> None:
    """Print ``message`` and exit 2. A failure to print still exits 2."""
    try:
        print(message, file=sys.stderr)
    finally:
        sys.exit(2)


def _start_watchdog(seconds: float):
    """A daemon timer that prints a refusal and ends the process with exit
    code 2 after ``seconds``; the caller cancels it when it answers."""
    def fire():
        try:
            sys.stderr.write("BLOCKED [shakedown-safety]: the hook could not check this command "
                             "in time, so it is refused.\n")
            sys.stderr.flush()
        finally:
            os._exit(2)
    timer = threading.Timer(seconds, fire)
    timer.daemon = True
    timer.start()
    return timer


def _payload_command(raw: str):
    """``(command, tool_name, cwd)`` from the hook input, or None when the
    input does not have the shape ``{"tool_input": {"command": "..."}}``."""
    try:
        payload = json.loads(raw[1:] if raw.startswith("﻿") else raw)
    except ValueError:
        return None
    if not isinstance(payload, dict):
        return None
    tool_input = payload.get("tool_input")
    if not isinstance(tool_input, dict):
        return None
    command = tool_input.get("command")
    if not isinstance(command, str):
        return None
    tool_name = payload.get("tool_name")
    tool_name = tool_name if isinstance(tool_name, str) else ""
    # The shell's working directory; when the payload has none, `dotnet run`
    # without a project path cannot be told apart from PPDS, so it counts.
    cwd = payload.get("cwd")
    cwd = cwd if isinstance(cwd, str) and cwd.strip() else _UNKNOWN_CWD
    return command, tool_name, cwd


# Above the reader's length cap (``sp.MAX_TEXT_CHARS``) the hook does not
# read a command; a hook input that is not in the known shape is not
# searched with the mention pattern above this length.
_MAX_RAW_CHARS = 2 * sp.MAX_TEXT_CHARS
# Spelled in a text above the cap, these refuse it: guarded programs, shells
# and code words (lower case, read with quoting characters removed), and
# escapes that can spell a letter.
_OVERSIZED_NAMES = tuple(name.encode() for name in sorted(
    _GUARDED | {s for s in sp.SHELLS if len(s) > 2} | sp.CODE_WORDS))
_OVERSIZED_ESCAPES = (b"\\x", b"\\u", b"\\0", b"\\1", b"`u{")
# Run-time text that may name a program (`$X data delete`): it also refuses
# a text above the cap during a shakedown or with an active environment that
# is not on the allowlist, where what the hook cannot read is refused.
_OVERSIZED_RUNTIME = (b"$", b"`", b"%")
_OVERSIZED_QUOTING = "".join(sorted(_QUOTING_CHARS)).encode("utf-8")


def _decide_oversized(data: bytes, shakedown_active: bool, allowlist, misconfigured) -> None:
    """Answer for a hook input or command longer than the hook reads, with
    checks whose cost is a few passes over the bytes (no parsing): refused
    when the text spells a program name (``_OVERSIZED_NAMES``, quoting
    characters removed) or an escape that can spell one; during a shakedown
    or with an active environment not on the allowlist, also when it holds
    run-time text. Otherwise it passes. Exits."""
    low = data.lower()
    flat = low.translate(None, _OVERSIZED_QUOTING)
    why = ""
    if any(name in flat for name in _OVERSIZED_NAMES):
        why = "it names a program the hook guards, a shell or a code word"
    elif any(esc in data for esc in _OVERSIZED_ESCAPES):
        why = "it holds an escape that can spell a program name"
    elif any(mark in data for mark in _OVERSIZED_RUNTIME):
        strict = shakedown_active or misconfigured is not None or (
            bool(allowlist) and not env_matches_allowlist(get_active_env_names(), allowlist))
        if strict:
            why = "it holds text only known when it runs, which may name a program"
    if why:
        _refuse(f"BLOCKED [shakedown-safety]: the command is longer than the hook reads "
                f"({sp.MAX_TEXT_CHARS:,} characters) and {why}, so it is refused.")
    sys.exit(0)


def main() -> None:
    try:
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    budget = _budget()
    watchdog = _start_watchdog(budget + _WATCHDOG_GRACE_SECONDS)
    try:
        _main(time.monotonic() + budget)
    except SystemExit:
        raise
    except BaseException:  # noqa: BLE001 -- a crash (exit 1) would let the command run
        # Any internal error refuses, whatever the command (exit 2).
        _refuse("BLOCKED [shakedown-safety]: the hook could not check this command "
                "(internal error), so it is refused.")
    finally:
        watchdog.cancel()


def _main(deadline: float) -> None:
    config = load_safety_config()
    env_var_name = config["readonly_env_var"]
    env_active = os.environ.get(env_var_name, "").strip() == "1"
    sentinel_active = check_sentinel_active()
    shakedown_active = env_active or sentinel_active
    misconfigured = None
    try:
        allowlist = load_allowlist(config)
    except AllowlistConfigError as exc:
        allowlist, misconfigured = [], str(exc)
    if not allowlist and misconfigured is None and not shakedown_active:
        # No allowlist and no shakedown: nothing is gated, for any command.
        sys.exit(0)

    stream = getattr(sys.stdin, "buffer", None)
    raw = stream.read().decode("utf-8", errors="replace") if stream is not None else sys.stdin.read()
    parsed = _payload_command(raw)
    # Length first: a text longer than the reader's cap is not scanned whole.
    if parsed is not None and len(parsed[0]) > sp.MAX_TEXT_CHARS:
        _decide_oversized(parsed[0].encode("utf-8", errors="replace"), shakedown_active, allowlist,
                          misconfigured)
    if parsed is None and len(raw) > _MAX_RAW_CHARS:
        # Not the known shape, and too long to search with the mention
        # pattern: any guarded name in it refuses.
        flat = raw.encode("utf-8", errors="replace").lower().translate(None, _OVERSIZED_QUOTING)
        if any(name.encode() in flat for name in _GUARDED):
            _refuse("BLOCKED [shakedown-safety]: the hook input is not in the shape the hook "
                    "knows and names a program it guards, so it is refused.")
        sys.exit(0)
    command = ""
    if parsed is None:
        # Not the input shape the hook knows: any mention is an invocation.
        invocations = _unknown([UNREADABLE]) if mentions_guarded(raw) else []
    else:
        command, tool_name, cwd = parsed
        invocations = find_ppds_invocations(command, tool_name, cwd, deadline) if command.strip() else []

    try:
        # Concern 2: write-block first -- it's the tighter gate when active.
        if shakedown_active:
            for argv in invocations:
                _tick(deadline)
                blocked, reason = is_mutation(argv, allowlist_configured=bool(allowlist))
                if blocked:
                    _refuse(_block_msg_mutation(argv, reason, env_var_name,
                                                env_active=env_active, sentinel_active=sentinel_active))

        # Concern 1: env allowlist gating -- opt-in. It is active once an
        # allowlist names at least one env; with none configured (missing or
        # empty) the hook does not gate ppds commands, so a fresh clone works
        # without setup. A source that is present but unusable blocks.
        if misconfigured is not None and invocations:
            _refuse(_block_msg_misconfigured(misconfigured, invocations[0]))
        if not allowlist:
            sys.exit(0)
        why = target_variable_problem(command, invocations, strict=shakedown_active)
        if why:
            _refuse(_block_msg_target(why, allowlist, invocations[0] if invocations else ["(none)"]))
        if not invocations:
            sys.exit(0)

        active = get_active_env_names()
        required = []
        env_url = os.environ.get("PPDS_ENVIRONMENT_URL", "").strip()
        if env_url:
            # The CLI inherits this variable; it may pick that environment.
            required.append(_url_candidates(env_url) or [env_url])
        store = read_profile_store()
        _tick(deadline)
        # One answer for the whole text: does it name a switch or a target?
        # The payload itself when its shape is not the known one. During a
        # shakedown the strict reading stays: what cannot be placed refuses.
        names_target = shakedown_active or text_names_target(command if parsed is not None else raw)
        for argv in invocations:
            _tick(deadline)
            allow, reason = decide_env(argv, allowlist, active, store, required, names_target)
            if not allow:
                _refuse(reason)
    except _Deadline:
        _refuse("BLOCKED [shakedown-safety]: the hook could not check this command within its "
                "time budget, so it is refused.")
    sys.exit(0)


if __name__ == "__main__":
    main()
