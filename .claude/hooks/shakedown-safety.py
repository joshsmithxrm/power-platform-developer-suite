"""PreToolUse hook: shakedown safety boundary (env allowlist + write-block).

Consolidates the two prior hooks (``dev-env-check.py`` and
``shakedown-readonly.py``) into a single PreToolUse gate. They share ~80% of
their parsing logic (find ``ppds`` invocations in a Bash command, walk argv
against subcommand/verb tables); splitting that across two files made the
safety surface harder to review and invited drift.

Two concerns, one hook:

1. **Env allowlist gating (opt-in).** Once an allowlist names at least one
   env, every Bash invocation that mentions ``ppds`` / ``ppds-mcp-server``
   is gated against that allowlist of dev/test env names. With no allowlist
   configured the gate is off; a misconfigured allowlist blocks (see
   below). The active env is read directly from the PPDS profile
   store (``profiles.json``). Read-only subcommands (``env list``,
   ``env who``, ...) are always allowed; switching INTO a safe env is
   allowed even when currently on an unsafe one.

2. **Write-block during shakedown.** When ``PPDS_SHAKEDOWN=1`` (or whichever
   name is configured under ``safety.readonly_env_var``), write/mutation
   verbs (``create``, ``update``, ``delete``, ``plugins deploy`` without
   ``--dry-run``, ``solutions import``, etc.) are refused outright. This
   provides a mechanical boundary that the shakedown / verify skills rely on.

**.NET host forms.** Besides ``ppds`` / ``ppds-mcp-server`` themselves, the
hook recognises the CLI and MCP server started through ``dotnet``:
``dotnet run`` / ``dotnet watch run`` with ``--project`` (or ``-p``) naming
``PPDS.Cli`` / ``PPDS.Mcp`` or after ``cd`` into that directory in the same
command, ``dotnet [exec] <path>/ppds.dll`` / ``ppds-mcp-server.dll``,
``dotnet ppds`` and ``dotnet tool run ppds``. These are normalised to the
same argv and only ever add hits. Matching is broad and best-effort.

**Both shell tools.** The hook is registered for the Bash tool and the
PowerShell tool. The payload's ``tool_name`` picks the dialect: PowerShell
text is read with PowerShell's rules (``_shellparse.py``: backtick escapes
and continuations, ``''`` and ``""`` inside quotes, the call operator, script
blocks, sub-expressions, backslash paths); Bash text with POSIX rules, in
addition to the original matcher. Any word naming ``ppds`` /
``ppds-mcp-server`` (with a path and ``.exe``) or ``dotnet`` / ``dnx`` is an
invocation, at any nesting depth: inside substitutions, script blocks and
every quoted string, so ``bash -c``, ``pwsh -Command`` and ``cmd /c`` are
seen. A command that only mentions ``ppds`` (a commit message, a search) is
also treated as an invocation; that is the maintainer's decision on #1780.

**Cannot tell -> invocation.** When the hook cannot tell what a command
runs, it treats it as an invocation of both programs: a program only known
at run time (``$x data delete``, ``& $exe ...``, ``Invoke-Expression $c``),
a ``dotnet run`` project or working directory only known at run time, an
alias that names ``ppds``, more than ``_DOTNET_MAX_OCCURRENCES`` ``dotnet``
words, or text the reader could not finish (an unterminated quote, a cap).
Such an invocation is gated, and during a shakedown it is refused when its
subcommand or verb is not known.

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


_PPDS_PROGRAMS = ("ppds", "ppds-mcp-server")
# Argument standing for text the hook could not read; `is_mutation` treats
# an argv that contains it as a write.
UNREADABLE = "<unreadable>"
# A working directory only known at run time.
_UNKNOWN_CWD = sp.DYN
_MENTION_RE = re.compile(r"ppds|dotnet|dnx", re.IGNORECASE)
_CD_WORDS = {"cd", "pushd", "chdir", "set-location", "sl", "push-location"}
_CD_POP_WORDS = {"popd", "pop-location"}
_ALIAS_DEFINERS = {"alias", "set-alias", "new-alias", "sal", "nal", "doskey"}


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


def find_ppds_invocations(command: str, tool_name: str = "Bash", cwd=None) -> list:
    """Return a list of argv-style token lists for each ppds invocation.

    ``tool_name`` picks the dialect: ``PowerShell`` text is read with
    PowerShell's rules only; ``Bash`` text with the original matcher plus
    POSIX rules; any other value with all of them. ``cwd`` is the shell's
    working directory from the hook payload; None means a directory that is
    not a PPDS project (the original behaviour), ``_UNKNOWN_CWD`` that it is
    not known. Readings only ever add hits; the list is de-duplicated.
    """
    if not command:
        return []
    tool = (tool_name or "").lower()
    out = []
    if tool != "powershell":
        out.extend(_original_invocations(command))
    dialects = {"powershell": ["ps"], "bash": ["posix"]}.get(tool, ["posix", "ps"])
    for dialect in dialects:
        out.extend(_read_invocations(command, dialect, cwd))
    if not out and tool == "powershell" and _PPDS_PROGRAM_RE.search(command):
        out.extend(_unknown([UNREADABLE]))
    return _dedupe(out)


def _strip_quoting(text: str) -> str:
    return re.sub(r"[`^\\'\"‘’‚‛“”„]", "", text)


def _read_invocations(command: str, dialect: str, cwd) -> list:
    """Hits from reading ``command`` with ``_shellparse`` in ``dialect``.

    Every word naming a PPDS program, ``dotnet`` or ``dnx`` counts, in every
    statement the reader finds. A failure here is an invocation (when the
    text mentions PPDS or the .NET host), never a pass.
    """
    try:
        result = sp.read(command, dialect)
        out = []
        if not result.complete and (
            _MENTION_RE.search(_strip_quoting(command))
            or any("encoded" in problem for problem in result.problems)  # hides any mention
        ):
            out.extend(_unknown([UNREADABLE]))
        stack = [cwd]  # working directory per subshell level
        dotnet_words = 0
        for stmt in result.statements:
            level = stmt.level
            del stack[level + 1:]
            while len(stack) < level + 1:
                stack.append(stack[-1])
            words, texts = stmt.words, stmt.texts
            for i, word in enumerate(words):
                prog = sp.program_name(word)
                if prog in _PPDS_PROGRAMS:
                    out.append([prog] + texts[i + 1:])
                elif prog in ("dotnet", "dnx"):
                    dotnet_words += 1
                    if dotnet_words > _DOTNET_MAX_OCCURRENCES:
                        out.extend(_unknown([UNREADABLE]))
                    elif prog == "dnx":
                        out.extend(_package_exec(texts[i + 1:]))
                    else:
                        out.extend(_dotnet_hits(texts[i + 1:], {stack[level], cwd}))
            if stmt.code:
                for i in sp.command_positions(stmt):
                    if words[i].dynamic and sp.program_name(words[i]) is None:
                        out.extend(_unknown(texts[i + 1:]))
                first = words[0].text.lower()
                if first in _CD_WORDS:
                    stack[level] = _cd(stack[level], words[1:])
                elif first in _CD_POP_WORDS:
                    stack[level] = _UNKNOWN_CWD
            if words[0].text.lower() in _ALIAS_DEFINERS and _MENTION_RE.search(" ".join(texts[1:])):
                out.extend(_unknown([UNREADABLE]))
        return out
    except Exception:  # noqa: BLE001 -- a reading failure must not become a pass
        return _unknown([UNREADABLE]) if _MENTION_RE.search(_strip_quoting(command)) else []


def _is_absolute(path: str) -> bool:
    return path.startswith(("/", "~")) or bool(re.match(r"^[A-Za-z]:", path))


def _cd(base, args):
    """The working directory after ``cd`` / ``Set-Location`` with ``args``."""
    for word in args:
        if word.dynamic:
            return _UNKNOWN_CWD
        text = word.text
        if text == "-":
            return _UNKNOWN_CWD
        if text.startswith("-"):
            continue  # -P, -L, -Path, -LiteralPath
        path = text.replace("\\", "/")
        if _is_absolute(path) or base is None:
            return path
        return base.replace("\\", "/") + "/" + path
    return "~"


def _path_program(path):
    """``ppds`` / ``ppds-mcp-server`` when ``path``'s last component names that
    project, ``"?"`` when that component is only known at run time."""
    if path is None:
        return None
    parts = []
    for part in path.replace("\\", "/").split("/"):
        if part == "..":
            if parts and sp.DYN in parts[-1]:
                return "?"
            parts = parts[:-1]
        elif part not in ("", "."):
            parts.append(part)
    if not parts:
        return None
    last = parts[-1].lower()
    if sp.DYN in last:
        return "?"
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


def _original_invocations(command: str) -> list:
    """The original matcher: a ``ppds`` word after whitespace or an operator,
    its tail split with POSIX rules, plus the ``dotnet`` forms."""
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
    out.extend(_dotnet_invocations(command))
    return out


# PPDS run through the .NET host. Additive: these hits are appended to the
# matcher above and nothing is ever cleared. Broad like that matcher: any
# `dotnet` word after the start, whitespace, an operator, a quote or a
# parenthesis. Caps keep the work linear: at most 500 `dotnet` occurrences
# per command, and 400 tokens / 4096 characters after each, are examined by
# this matcher; past 500 the `_shellparse` reading reports an unreadable
# invocation.
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
# `dotnet [exec]` host options that take a value.
_HOST_VALUE_OPTS = set("--additionalprobingpath --additional-deps --depsfile --fx-version "
                       "--roll-forward --runtimeconfig --roll-forward-on-no-candidate-fx".split())
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
    """True when ``--read-only`` certainly reaches the MCP server: it is among
    the forwarded tokens and no option ``dotnet run`` does not recognise
    comes before it (such an option might take it as its value)."""
    for tok in unknown:
        if tok == "--read-only" or tok.startswith("--read-only="):
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
    if program == "ppds-mcp-server" and _read_only_forwarded(unknown):
        return [[program] + unknown + after]
    readings = [[program] + after]
    for k in range(min(len(unknown), _DOTNET_MAX_TOKENS)):
        readings.append([program] + unknown[k:] + after)
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
    """``dotnet tool exec <package> ...`` / ``dnx <package> ...``: the PPDS
    tool packages (``PPDS.Cli``, ``PPDS.Mcp``, optionally ``@version``)."""
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
    while i < len(tokens) and (tokens[i].startswith("-") or tokens[i].lower() == "exec"):
        i += 2 if tokens[i] in _HOST_VALUE_OPTS else 1
    if i >= len(tokens):
        return []
    verb, rest = tokens[i].lower(), list(tokens[i + 1:])
    if sp.DYN in verb:
        return _unknown(rest)
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
    if verb == "dnx" or (verb == "tool" and rest[:1] and rest[0].lower() == "exec"):
        return _package_exec(rest if verb == "dnx" else rest[1:])
    return _dotnet_other(verb, rest)


def _dotnet_other(verb: str, rest: list) -> list:
    """``dotnet tool run ppds``, ``dotnet [exec] <path>/ppds.dll``, ``dotnet ppds``."""
    if verb == "tool" and rest[:1] == ["run"]:
        j = 1
        while j < len(rest) and rest[j].startswith("-"):
            j += 1
        verb, rest = (rest[j].lower(), rest[j + 1:]) if j < len(rest) else ("", [])
        rest = rest[1:] if rest[:1] == ["--"] else rest
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


def _dotnet_invocations(command: str) -> list:
    """Hits for PPDS started through ``dotnet``; [] on any error."""
    try:
        cds, parts = [], []  # (position, directory after each cd / pushd)
        for m in _CD_RE.finditer(command):
            target = m.group(1).strip("'\"").replace("\\", "/")
            if target.startswith("/") or target[1:2] == ":":
                parts = []
            parts = (parts + target.split("/"))[-32:]
            cds.append((m.start(), "/".join(parts)))
        out, cwd, k = [], None, 0
        for count, match in enumerate(_DOTNET_RE.finditer(command)):
            if count >= _DOTNET_MAX_OCCURRENCES:
                break
            while k < len(cds) and cds[k][0] < match.start():
                cwd, k = cds[k][1], k + 1
            tail = command[match.end():match.end() + _DOTNET_MAX_CHARS]
            tail = tail[1:] if tail[:1] in ("'", '"') else tail
            lexer = shlex.shlex(tail, posix=True)
            # As shlex.split, but a backslash stays literal (Windows paths).
            lexer.whitespace_split, lexer.commenters, lexer.escape = True, "", ""
            args = []
            try:
                tok = lexer.get_token()
                while tok is not None and tok not in _SHELL_OPERATORS and len(args) < _DOTNET_MAX_TOKENS:
                    args.append(tok.rstrip(";"))
                    tok = None if tok.endswith(";") else lexer.get_token()
            except ValueError:
                args = []
                for tok in tail.split(None, _DOTNET_MAX_TOKENS)[:_DOTNET_MAX_TOKENS]:
                    if tok in _SHELL_OPERATORS:
                        break
                    args.append(tok)
            out.extend(_dotnet_argvs(args, cwd))
        return out
    except Exception:  # noqa: BLE001 -- new detection must never remove existing hits
        return []


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

    if UNREADABLE in argv:
        return True, "a command the hook could not read in full (treated as a write)"

    if prog == "ppds-mcp-server":
        if any(
            arg == "--read-only" or arg.startswith("--read-only=")
            for arg in argv[1:]
        ):
            return False, ""
        return True, "ppds-mcp-server (missing --read-only flag during shakedown)"

    if len(argv) < 3:
        if len(argv) == 2 and sp.DYN in argv[1]:
            return True, "a subcommand only known when the command runs (treated as a write)"
        return False, ""

    sub = argv[1].lower()
    verb = argv[2].lower()
    rest = argv[3:]

    # 3-level commands: "metadata <noun> <verb>" has the noun at argv[2], verb at argv[3]
    if sub == "metadata" and verb not in _MUTATION_VERBS and len(argv) >= 4:
        verb = argv[3].lower()
        rest = argv[4:]

    if sp.DYN in sub or sp.DYN in verb:
        return True, "a subcommand or verb only known when the command runs (treated as a write)"

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


def main() -> None:
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)

    if not isinstance(payload, dict):
        sys.exit(0)
    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command", "") if isinstance(tool_input, dict) else ""
    if not isinstance(command, str) or not command.strip():
        sys.exit(0)
    tool_name = payload.get("tool_name")
    tool_name = tool_name if isinstance(tool_name, str) else ""
    # The shell's working directory; when the payload has none, `dotnet run`
    # without a project path cannot be told apart from PPDS, so it counts.
    cwd = payload.get("cwd")
    cwd = cwd if isinstance(cwd, str) and cwd.strip() else _UNKNOWN_CWD

    invocations = find_ppds_invocations(command, tool_name, cwd)
    if not invocations:
        sys.exit(0)

    config = load_safety_config()
    env_var_name = config["readonly_env_var"]
    env_active = os.environ.get(env_var_name, "").strip() == "1"
    sentinel_active = check_sentinel_active()
    shakedown_active = env_active or sentinel_active

    # Concern 2: write-block first -- it's the tighter gate when active.
    if shakedown_active:
        for argv in invocations:
            blocked, reason = is_mutation(argv)
            if blocked:
                print(
                    _block_msg_mutation(
                        argv,
                        reason,
                        env_var_name,
                        env_active=env_active,
                        sentinel_active=sentinel_active,
                    ),
                    file=sys.stderr,
                )
                sys.exit(2)

    # Concern 1: env allowlist gating -- opt-in. It is active once an
    # allowlist names at least one env; with none configured (missing or
    # empty) the hook does not gate ppds commands, so a fresh clone works
    # without setup. A source that is present but unusable blocks.
    try:
        allowlist = load_allowlist(config)
    except AllowlistConfigError as exc:
        print(_block_msg_misconfigured(str(exc), invocations[0]), file=sys.stderr)
        sys.exit(2)
    if not allowlist:
        sys.exit(0)

    active = get_active_env_names()

    for argv in invocations:
        allow, reason = decide_env(argv, allowlist, active)
        if not allow:
            print(reason, file=sys.stderr)
            sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
