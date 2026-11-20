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
   store (``profiles.json``). Read-only subcommands (``env list``,
   ``env who``, ...) are always allowed; switching INTO a safe env is
   allowed even when currently on an unsafe one.

2. **Write-block during shakedown.** When ``PPDS_SHAKEDOWN=1`` (or whichever
   name is configured under ``safety.readonly_env_var``), a ppds command
   passes only when its command path is one ``CLI_COMMANDS`` classifies as
   read-only (``plugins deploy`` only with ``--dry-run``, ``query sql`` only
   with inline SQL that reads); every other path, one the hook has never
   heard of included, is refused. This provides a mechanical boundary that
   the shakedown / verify skills rely on.

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
command is an invocation. Any exception at the top level refuses a command
that mentions ``ppds``, ``dotnet`` or ``dnx``, unless the hook had already
found that no allowlist and no shakedown is in effect (then every command
passes before it is read). Input is read as UTF-8 bytes.

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

import bisect
import json
import os
import re
import shlex
import sys
import time
from datetime import datetime, timezone
from typing import Iterable, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import normalize_msys_path  # noqa: E402
import _shellparse as sp  # noqa: E402


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
    profile_name = active.get("name")
    if isinstance(profile_name, str) and profile_name.strip():
        candidates.append(profile_name.strip())
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


def _unknown(args) -> list:
    """Readings for a program the hook cannot identify: both PPDS programs."""
    return [[program] + list(args) for program in _PPDS_PROGRAMS]


def _dedupe(argvs: list) -> list:
    seen, out = set(), []
    for argv in argvs:
        program = argv[0].lower()
        program = program[:-4] if program.endswith(".exe") else program
        key = (program,) + tuple(argv[1:])  # `ppds.exe env list` == `ppds env list`
        if key not in seen:
            seen.add(key)
            out.append(argv)
    return out


def _mentions(text: str) -> list:
    """``(start, end, fuzzy)`` for each mention of a guarded program in
    ``text``; ``fuzzy`` when quoting characters sit between the letters."""
    return [(m.start(), m.end(), any(ch in _QUOTING_CHARS for ch in m.group(0)))
            for m in _MENTION_RE.finditer(text)]


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
        mentions = _mentions(command)
        for dialect in ("posix", "ps"):
            out.extend(_read_invocations(command, dialect, cwd, mentions, deadline))
        if _escaped_mention(command) or _DYNAMIC_CODE_RE.search(command):
            out.extend(_unknown([UNREADABLE]))
        return _dedupe(out)
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
    if not result.complete or result.top is None:
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
    dotnet_words = 0
    for stmt in statements:
        _tick(deadline)
        level = stmt.level
        del stack[level + 1:]
        while len(stack) < level + 1:
            stack.append(stack[-1])
        words, texts = stmt.words, stmt.texts
        positions = dict(sp.command_positions(stmt)) if stmt.code else {}
        for i, word in enumerate(words):
            prog = sp.program_name(word)
            if prog in _PPDS_PROGRAMS:
                argv = [prog] + texts[i + 1:]
                if positions.get(i):
                    argv.append(UNREADABLE)  # arguments from elsewhere (xargs, Start-Process)
                out.append(argv)
            elif prog in ("dotnet", "dnx"):
                dotnet_words += 1
                if dotnet_words > _DOTNET_MAX_OCCURRENCES:
                    out.extend(_unknown([UNREADABLE]))
                    continue
                if prog == "dnx":
                    hits = _package_exec(texts[i + 1:])
                else:
                    known = (trust_cwd and stmt.code and not stmt.wrapped and i in positions
                             and not positions[i] and stack[level] != _UNKNOWN_CWD)
                    hits = _dotnet_hits(texts[i + 1:], {stack[level]} if known else {_UNKNOWN_CWD})
                if positions.get(i):
                    hits = [hit + [UNREADABLE] for hit in hits] or _unknown([UNREADABLE])
                out.extend(hits)
        if stmt.code:
            for i, opaque in positions.items():
                word = words[i]
                prog = sp.program_name(word)
                if prog in _GUARDED:
                    continue
                if word.dynamic or prog is None or (word.glob and word.text != "?"):
                    out.extend(_unknown(texts[i + 1:] + ([UNREADABLE] if opaque else [])))
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
        out.append([prog] + args)
    out.extend(_dotnet_invocations(command, deadline))
    return out


# PPDS run through the .NET host. Caps keep the work linear: at most 500
# `dotnet` occurrences per command, and 400 tokens / 4096 characters after
# each, are examined by the original matcher; past a cap the command counts
# as an invocation with an unknown verb.
_DOTNET_MAX_OCCURRENCES = 500
_DOTNET_MAX_TOKENS = 400
_DOTNET_MAX_CHARS = 4096
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


def _run_readings(program: str, unknown: list, after: list) -> list:
    """argv readings for an application started with forwarded ``unknown``
    tokens and ``after`` (the tokens after ``--``): ``after`` alone, and each
    suffix of ``unknown`` followed by ``after``, so an option value the hook
    does not know (``--tl off``) cannot hide the verb. A reading for each;
    any one can block."""
    if program == "ppds-mcp-server":
        # Only the flag's presence matters; the full reading comes first so a
        # block names a --read-only=<value> token when there is one.
        if _read_only_forwarded(unknown):
            return [[program] + unknown + after]
        return _dedupe([[program] + unknown + after, [program] + after])
    readings = [[program] + after]
    for k in range(min(len(unknown), _DOTNET_MAX_TOKENS)):
        readings.append([program] + unknown[k:] + after)
    if len(unknown) > _DOTNET_MAX_TOKENS:
        readings.append([program, UNREADABLE])
    return _dedupe(readings)


def _dotnet_run(tokens: list, project) -> list:
    """argv readings for ``dotnet run <tokens>`` in the original matcher."""
    parsed = _parse_run(tokens)
    if parsed is None:
        return []
    value, unknown, after = parsed
    program = _dotnet_project(value if value is not None else project)
    if program is None:
        return []
    return _run_readings(program, unknown, after)


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
            out.extend(_run_readings(prog, unknown, after))
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
            out.extend(_run_readings(program, before[k + 1:], after))
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
    out, cwd, k = [], None, 0
    for count, match in enumerate(_DOTNET_RE.finditer(command)):
        if count >= _DOTNET_MAX_OCCURRENCES:
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
        out.extend(_dotnet_argvs(args, cwd))
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


# Every command path of the ppds CLI, with how the write-block treats it.
# One line per parent path ("." is the root): `<child>=<class>`.
#   r        reads only; anything after it is arguments and options.
#   g        a group: passes only when nothing (or only a help flag) follows.
#   w        writes, or not known to only read: blocked.
#   dry-run  `plugins deploy`: passes only with --dry-run / -n.
#   sql      `query sql`: passes only with inline SQL that only reads.
#   list     `env config` / `env type`: passes only with --list / --show alone.
# A path that is not listed is blocked. tests/hooks/test_shakedown_cli_commands.py
# derives the command tree from src/PPDS.Cli and fails when a path is missing.
_CLI_COMMANDS = """
.: api=g auth=g connection-references=g connections=g custom-apis=g data=g data-providers=g data-sources=g deployment-settings=g docs=r env=g environment-variables=g flows=g forms=g import-jobs=g interactive=w internal=w logs=g metadata=g model-driven-app=g plugin-traces=g plugins=g publish=w query=g roles=g schema=g serve=w service-endpoints=g solutions=g users=g version=r views=g webresources=g
api: request=w
auth: clear=w create=w delete=w list=r name=w select=w update=w who=r
connection-references: analyze=r connections=r flows=r get=r list=r
connections: get=r list=r
custom-apis: add-parameter=w get=r list=r register=w remove-parameter=w set-plugin=w unregister=w update=w update-parameter=w
data: analyze=r copy=w delete=w export=r import=w load=w schema=r truncate=w update=w users=r
data-providers: get=r list=r register=w unregister=w update=w
data-sources: get=r list=r register=w unregister=w update=w
deployment-settings: generate=r sync=w validate=r
env: config=list list=r select=r type=list who=r
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

_HELP_FLAGS = {"--help", "-h", "-?", "/?", "--version"}
# Words in SQL text that write (or could): any of them makes `query sql` a write.
_SQL_WRITE_RE = re.compile(
    r"\b(insert|update|delete|merge|upsert|truncate|drop|create|alter|exec|execute|into|grant|revoke|"
    r"deny|bulk|openrowset|declare|set)\b", re.IGNORECASE)


def _plugins_deploy_blocked(args: list) -> bool:
    """True unless ``--dry-run`` / ``-n`` is given and not set to false."""
    dry = False
    for k, arg in enumerate(args):
        low = arg.lower()
        if low in ("--dry-run", "-n"):
            nxt = args[k + 1].lower() if k + 1 < len(args) else ""
            if nxt in ("false", "0"):
                return True
            dry = True
        elif low.startswith(("--dry-run=", "--dry-run:", "-n=", "-n:")):
            if low.split("=", 1)[-1].split(":", 1)[-1] != "true":
                return True
            dry = True
    return not dry


def _sql_blocked(args: list) -> str:
    """Why ``query sql <args>`` counts as a write, or "" when it only reads."""
    text = [arg for arg in args if not arg.startswith("-")]
    if any(arg.lower() in ("--file", "-f", "--stdin") or arg.lower().startswith(("--file=", "--file:"))
           for arg in args):
        return "SQL read from a file or stdin"
    if any(sp.DYN in arg for arg in args):
        return "SQL only known when the command runs"
    if not text:
        return "no SQL text"
    match = _SQL_WRITE_RE.search(" ".join(text))
    if match:
        return f"SQL containing {match.group(1).upper()}"
    return ""


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


def is_mutation(argv: list) -> tuple:
    """Return (is_mutation, reason). reason is empty when not a mutation.

    During a shakedown a ppds command passes only when its command path is
    one the hook knows to be read-only (``CLI_COMMANDS``); every other path,
    one the hook has never heard of included, counts as a write. An option
    before the command path, several words in one argument, or run-time text
    there means the command is not known. So does a response file (``@x``)
    anywhere, and a child command name anywhere after its parent's path."""
    if not argv:
        return False, ""

    prog = os.path.basename(argv[0]).lower()
    if prog.endswith(".exe"):
        prog = prog[:-4]
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
        if "--read-only" in argv[1:]:
            return False, ""
        return True, "ppds-mcp-server (missing --read-only flag during shakedown)"

    args = argv[1:]
    if len(args) == 1 and args[0].lower() in _HELP_FLAGS:
        return False, ""
    path, rest = _command_path(args)
    shown = " ".join(path)
    nxt = rest[0] if rest else None
    cls = CLI_COMMANDS[path]

    if cls == "g":
        if nxt is None or (len(rest) == 1 and nxt.lower() in _HELP_FLAGS):
            return False, ""
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
    if cls == "dry-run":
        if _plugins_deploy_blocked(rest):
            return True, f"{shown} (no --dry-run flag present)"
        return False, ""
    if cls == "list":
        if rest and all(arg.lower() in ("--list", "--show", "-s") for arg in rest):
            return False, ""
        return True, f"{shown} (changes settings unless only --list / --show is given)"
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
        f"  Command:    {_shown(argv)}",
        f"  Active env: {_format_active(active)}",
        f"  Allowlist:  {', '.join(allowlist)}",
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
        f"  Command:   {_shown(argv)}",
        f"  Target:    {target}",
        f"  Allowlist: {', '.join(allowlist)}",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_misconfigured(problem, argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: the safe-env allowlist is misconfigured.",
        f"  Command: {_shown(argv)}",
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
            f"  Command: {_shown(argv)}",
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


def _refuse(message: str) -> None:
    """Print ``message`` and exit 2. A failure to print still exits 2."""
    try:
        print(message, file=sys.stderr)
    finally:
        sys.exit(2)


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


def main() -> None:
    try:
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    state = {"raw": "", "gate_off": False}
    try:
        _main(state)
    except SystemExit:
        raise
    except BaseException:  # noqa: BLE001 -- a crash (exit 1) would let the command run
        if state["gate_off"]:
            sys.exit(0)
        if mentions_guarded(state["raw"]):
            _refuse("BLOCKED [shakedown-safety]: the hook could not check this command "
                    "(internal error); it mentions ppds, dotnet or dnx, so it is refused.")
        sys.exit(0)


def _main(state: dict) -> None:
    deadline = time.monotonic() + _budget()
    stream = getattr(sys.stdin, "buffer", None)
    raw = stream.read().decode("utf-8", errors="replace") if stream is not None else sys.stdin.read()
    state["raw"] = raw

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
        state["gate_off"] = True
        sys.exit(0)

    parsed = _payload_command(raw)
    if parsed is None:
        # Not the input shape the hook knows: any mention is an invocation.
        invocations = _unknown([UNREADABLE]) if mentions_guarded(raw) else []
    else:
        command, tool_name, cwd = parsed
        invocations = find_ppds_invocations(command, tool_name, cwd, deadline) if command.strip() else []
    if not invocations:
        sys.exit(0)

    # Concern 2: write-block first -- it's the tighter gate when active.
    if shakedown_active:
        for argv in invocations:
            blocked, reason = is_mutation(argv)
            if blocked:
                _refuse(
                    _block_msg_mutation(
                        argv,
                        reason,
                        env_var_name,
                        env_active=env_active,
                        sentinel_active=sentinel_active,
                    )
                )

    # Concern 1: env allowlist gating -- opt-in. It is active once an
    # allowlist names at least one env; with none configured (missing or
    # empty) the hook does not gate ppds commands, so a fresh clone works
    # without setup. A source that is present but unusable blocks.
    if misconfigured is not None:
        _refuse(_block_msg_misconfigured(misconfigured, invocations[0]))
    if not allowlist:
        sys.exit(0)

    active = get_active_env_names()

    for argv in invocations:
        allow, reason = decide_env(argv, allowlist, active)
        if not allow:
            _refuse(reason)

    sys.exit(0)


if __name__ == "__main__":
    main()
