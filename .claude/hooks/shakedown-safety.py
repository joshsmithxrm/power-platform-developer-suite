"""PreToolUse hook: shakedown safety boundary (env allowlist + write-block).

Consolidates the two prior hooks (``dev-env-check.py`` and
``shakedown-readonly.py``) into a single PreToolUse gate. They share ~80% of
their parsing logic (find ``ppds`` invocations in a Bash command, walk argv
against subcommand/verb tables); splitting that across two files made the
safety surface harder to review and invited drift.

Two concerns, one hook:

1. **Env allowlist gating (opt-in).** Once an allowlist names at least one
   env, every Bash command that runs ``ppds`` / ``ppds-mcp-server`` (see
   "Recognised invocations" below) is gated against that allowlist of
   dev/test env names. With no allowlist
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

**Recognised invocations.** Detection is the union of two matchers:

- The broad legacy matcher, unchanged: any ``ppds`` / ``ppds-mcp-server``
  word (optional path and ``.exe``) at the start of the string or after
  whitespace, ``|``, ``;``, ``&``, ``(`` or a backtick.
- A shell-like parse that adds what that misses: a quoted program word,
  the program after assignments and wrappers (``env``, ``sudo``,
  ``timeout`` ...), command strings given to ``bash -c`` (any options),
  ``su -c``, ``script -c``, ``eval``, ``pwsh -Command``, ``cmd /c``,
  here-strings and heredocs fed to a shell, ``echo`` piped into a shell,
  values ``git`` / ``gh`` / ``rg`` would run, and PPDS through the .NET host
  (``dotnet run`` / ``dotnet watch`` for ``PPDS.Cli`` / ``PPDS.Mcp`` by
  ``--project`` or an earlier ``cd``, ``dotnet [exec] .../ppds.dll``,
  ``dotnet tool run|exec``, ``dnx``), normalised to ``[program, *args]``.
  An unknown ``dotnet run`` option before ``--`` yields two readings (args
  after ``--`` only; every unrecognised token plus those) and either blocks.

**Clearing.** All legacy matches in a command string are cleared only when
the parse found nothing and the whole string is data: it parsed cleanly
(no compound command, group, function, keyword, unmodelled quoting or
heredoc delimiter, here-string, process substitution or arithmetic); it is
simple commands joined by ``;``, newline, ``&&``, ``||`` or ``|``; every
command word, including inside ``$(...)``, is a bare unquoted name from
``_DATA_PROGRAMS``; there is no assignment, no output redirection except to
``/dev/null`` or an fd; and ``printf`` / ``rg`` / ``git`` / ``gh`` use only
allowlisted subcommands and exact options, checked on the value bash
delivers (only literal text and plain quotes; a ``"$(...)"`` only as the
whole value of ``-m`` / ``--message`` / ``--title`` / ``--body``).
Otherwise nothing is cleared, so a mixed string such as ``dotnet build &&
git commit ...`` or ``git commit ... && git push`` is handled as before. Matching is best-effort: scripts, aliases, a variable
or glob as the program, interpreters (``python -c``) and a working
directory set outside the string are not seen. The C# ``IShakedownGuard``
blocks writes during a shakedown however the CLI was started.

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


# Design (see the module docstring): detection is the legacy matcher
# (``_legacy_invocations``) plus precise recognition (``_ShellParse`` and
# ``_new_invocations``); the only exoneration is whole-string
# (``_all_data``). A best-effort pre-filter in front of the C#
# ``IShakedownGuard``, not a shell.

_LEGACY_PROGRAM_RE = re.compile(
    r"""
    (?:^|[\s|;&(`])
    (?:[^\s|;&()`]*[\\/])?
    (ppds(?:\.exe)?|ppds-mcp-server(?:\.exe)?)
    (?=$|[\s|;&)`])
    """,
    re.IGNORECASE | re.VERBOSE,
)

_LEGACY_SHELL_OPERATORS = {"|", ";", "&", "&&", "||", ">", "<", ">>", "<<", chr(96), "(", ")"}


def _legacy_invocations(command: str) -> list:
    """The pre-2026-11-19 matcher, unchanged: ``(offset, argv)`` per hit.

    ``offset`` is where the program name starts. The CLI argv tokens stop at
    the first shell operator after the program name.
    """
    out = []
    for match in _LEGACY_PROGRAM_RE.finditer(command):
        prog = match.group(1)
        tail = command[match.end():]
        try:
            tokens = shlex.split(tail, posix=True)
        except ValueError:
            tokens = tail.split()

        args = []
        for tok in tokens:
            if tok in _LEGACY_SHELL_OPERATORS:
                break
            args.append(tok)
        out.append((match.start(1), [prog] + args))
    return out


_PPDS_PROGRAMS = {"ppds", "ppds-mcp-server"}

# Project directory / file stem -> the program it builds (AssemblyName in
# src/PPDS.Cli/PPDS.Cli.csproj and src/PPDS.Mcp/PPDS.Mcp.csproj). The
# PackageId of each project is the same as its project name.
_PPDS_PROJECTS = {"ppds.cli": "ppds", "ppds.mcp": "ppds-mcp-server"}
_PPDS_ASSEMBLIES = {"ppds.dll": "ppds", "ppds-mcp-server.dll": "ppds-mcp-server"}

# Programs whose arguments are data: a ``ppds`` in their arguments is never
# run by them. Deliberately short. Nothing that can run a command it is
# given belongs here (find, xargs, env, sudo, time, timeout, watch, nohup,
# shells, pwsh, cmd, python, node, npx, ssh, docker, wsl, start, eval, ...).
_DATA_PROGRAMS = {"git", "gh", "grep", "rg", "echo", "printf", "cat", "ls",
                  "cd", "head", "tail", "wc", "mkdir"}
# Options of data programs whose value (or the option word itself) is run.
_DATA_EXEC_OPTIONS = {
    "git": {"-c", "--config", "--config-env", "--exec", "-x", "--receive-pack",
            "--upload-pack", "-u", "--extcmd", "--exec-path", "-O",
            "--open-files-in-pager", "--tool", "-t"},
    "gh": {"--exec"},
    "rg": {"--pre"},
}
# git: global options before the subcommand that take a value / that run code.
_GIT_GLOBAL_VALUE_OPTIONS = {"-C", "--git-dir", "--work-tree", "--namespace", "--super-prefix"}
_GIT_GLOBAL_EXEC_OPTIONS = ("-c", "--config-env", "--exec-path")
# Subcommands that never execute an argument they are given (aliases cannot
# shadow built-in subcommands). Not here: config, rebase, bisect, submodule,
# filter-branch, difftool, mergetool, and anything else.
_GIT_DATA_SUBCOMMANDS = {
    "add", "blame", "branch", "checkout", "clone", "commit", "describe", "diff",
    "fetch", "grep", "log", "ls-files", "mv", "pull", "push", "restore",
    "rev-parse", "rm", "shortlog", "show", "stash", "status", "switch", "tag",
    "worktree",
}
# gh: not alias, extension, codespace, or anything else.
_GH_DATA_SUBCOMMANDS = {"pr", "issue", "search", "release", "label", "repo",
                        "browse", "status", "run", "workflow", "api", "gist"}

_LEX_OPERATORS = (
    "&>>", "<<<", "<<-",
    "&&", "||", ";;", "|&", ">>", "<<", "&>", ">&", "<&", ">|", "<>",
    "|", "&", ";", "(", ")", "<", ">",
)
_REDIRECTIONS = {"&>>", "<<<", "<<-", ">>", "<<", "&>", ">&", "<&", ">|", "<>", "<", ">"}
_WORD_BREAK = set(" \t\n|&;()<>")
# Characters a backslash escapes outside quotes. Any other backslash is kept
# literally, so an unquoted Windows path (C:\x\ppds.exe) keeps its separators.
_BACKSLASH_ESCAPES = set(" \t\"'\\$`|&;()<>#*?[]{}~!")

_ASSIGNMENT_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(?:\[[^\]]*\])?\+?=")
# Reserved words after which the next word is still in command position.
_COMMAND_POSITION_KEYWORDS = {"!", "{", "}", "if", "then", "else", "elif", "fi",
                              "do", "done", "while", "until", "esac"}
# Reserved words that start a construct whose following words are not commands.
_NON_COMMAND_KEYWORDS = {"for", "select", "case", "function", "[[", "]]"}

_SHELLS = {"bash", "sh", "zsh", "dash", "ksh", "fish"}
_SHELL_VALUE_OPTIONS = {"-o", "+o", "-O", "+O", "--rcfile", "--init-file"}

# `dotnet run` options (learn.microsoft.com/dotnet/core/tools/dotnet-run).
# Every token `dotnet run` does not recognise is forwarded to the app, as is
# everything after `--`.
_DOTNET_RUN_VALUE_OPTIONS = {
    "-a", "--arch", "--artifacts-path", "-c", "--configuration",
    "-e", "--environment", "--file", "-f", "--framework",
    "-lp", "--launch-profile", "--os", "--project", "-r", "--runtime",
    "-v", "--verbosity", "--property",
}
_DOTNET_RUN_FLAGS = {
    "--disable-build-servers", "--force", "--interactive", "--no-build",
    "--no-cache", "--no-dependencies", "--no-launch-profile", "--no-restore",
    "--no-self-contained", "--sc", "--self-contained", "-?", "-h", "--help",
}
# `dotnet [host-options] <app.dll>` and `dotnet exec [options] <app.dll>`.
_DOTNET_HOST_VALUE_OPTIONS = {
    "--additionalprobingpath", "--additional-deps", "--depsfile",
    "--fx-version", "--roll-forward", "--runtimeconfig",
    "--roll-forward-on-no-candidate-fx",
}
_DOTNET_HOST_FLAGS = {"-d", "--diagnostics"}
_DOTNET_TOOL_EXEC_VALUE_OPTIONS = {"--version", "--source", "--add-source",
                                   "--configfile", "-v", "--verbosity"}
_DOTNET_WATCH_NON_RUN = {"build", "test", "publish", "pack", "msbuild"}
_LONG_OPTION_VALUE_RE = re.compile(r"^(--?[A-Za-z?][\w-]*)[=:](.*)$", re.DOTALL)
_PWSH_VALUE_OPTIONS = {"executionpolicy", "ex", "ep", "configurationname",
                       "workingdirectory", "wd", "outputformat", "of",
                       "inputformat", "if", "windowstyle", "w", "settingsfile",
                       "custompipename", "psconsolefile", "version", "v"}
_MAX_STRING_DEPTH = 8


def _program_name(word: str) -> str:
    """Basename of a program word, lowercased, without a ``.exe`` suffix."""
    base = re.split(r"[\\/]", word)[-1].lower()
    if base.endswith(".exe"):
        base = base[:-4]
    return base


def _command_start(words: list) -> int:
    """Index of the word in command position, or ``len(words)`` if none.

    Skips leading ``NAME=value`` assignments and reserved words such as
    ``if`` / ``then`` / ``do`` / ``!``. A wrapper (``sudo``, ``env``,
    ``xargs`` ...) is the program; ``_new_invocations`` looks past it.
    """
    for i, word in enumerate(words):
        if word in _NON_COMMAND_KEYWORDS:
            break
        if not (_ASSIGNMENT_RE.match(word) or word in _COMMAND_POSITION_KEYWORDS):
            return i
    return len(words)


class _Cmd:
    """One simple command found by ``_ShellParse``."""

    __slots__ = ("words", "spans", "pipe_to", "herestrings", "heredocs", "redirs", "_start")

    def __init__(self):
        self.words = []
        self.spans = []
        self.pipe_to = None
        self.herestrings = []
        self.heredocs = []  # (raw delimiter, quoted, body)
        self.redirs = []    # (operator, raw target)
        self._start = None

    @property
    def heredoc_bodies(self) -> list:
        return [body for _, _, body in self.heredocs]

    @property
    def start(self) -> int:
        if self._start is None:
            self._start = _command_start(self.words)
        return self._start


class _SeqState:
    """Mutable state of one ``_ShellParse._seq`` call (a command list)."""

    def __init__(self, parse):
        self.p = parse
        self.cur = _Cmd()
        self.word = None
        self.wstart = 0
        self.target = None
        self.heredocs = []
        self.prev_pipe = None

    def start_word(self, i):
        if self.word is None:
            self.word = []
            self.wstart = i

    def fd_prefix(self, i) -> str:
        """The word so far when it is an unquoted fd number (``2`` in ``2>``)."""
        if self.word is not None and self.p.s[self.wstart:i].isdigit():
            return self.p.s[self.wstart:i]
        return ""

    def end_word(self, i):
        if self.word is None:
            return
        value = "".join(self.word)
        raw = self.p.s[self.wstart:i]
        self.word = None
        if self.target is not None:
            op = self.target
            self.target = None
            if op in ("<<", "<<-"):
                quoted = any(ch in raw for ch in "'\"\\")
                delim = value.replace("\\", "") if "\\" in raw else value
                self.heredocs.append((self.cur, delim, op == "<<-", quoted, raw))
            elif op == "<<<":
                self.p.unclean = True
                self.cur.herestrings.append(value)
            else:
                self.cur.redirs.append((op, raw))
            return
        self.cur.words.append(value)
        self.cur.spans.append((self.wstart, i))

    def end_cmd(self, op):
        if self.target is not None:
            self.p.unclean = True  # redirection without a target
            self.target = None
        cmd = self.cur
        if not cmd.words and not cmd.herestrings and not cmd.redirs and not any(
            h[0] is cmd for h in self.heredocs
        ):
            return
        self.p.commands.append(cmd)
        if self.prev_pipe is not None:
            self.prev_pipe.pipe_to = cmd
        self.prev_pipe = cmd if op in ("|", "|&") else None
        self.cur = _Cmd()


class _ShellParse:
    """Shell-like parse of a Bash command string.

    Records every simple command (``commands``, including those inside
    ``$(...)``, backticks and unquoted heredoc bodies) and the operators
    that join them (``ops``). Sets ``unclean`` for anything it does not
    model: an unterminated quote, substitution or heredoc, a here-string,
    process substitution, ``${...}`` / ``$((...))`` / ``$[...]``, ``case``,
    a ``( )`` group or ``f()``, a stray ``)``, an escaped backtick inside
    backticks, or nesting beyond ``MAX_DEPTH``.
    """

    MAX_DEPTH = 40

    def __init__(self, s: str):
        self.s = s
        self.unclean = False
        self.commands = []
        self.ops = set()
        self.nested_strings = []
        try:
            self._seq(0, False, 0, len(s))
        except RecursionError:
            self.unclean = True

    def _seq(self, i, in_paren, depth, limit):
        """Parse a command list from ``i``; return the index after it.

        With ``in_paren`` the list is the body of ``$(`` / ``<(`` and ends at
        its unmatched ``)``.
        """
        s = self.s
        if depth > self.MAX_DEPTH:
            self.unclean = True
            return limit
        st = _SeqState(self)
        group = 0
        while i < limit:
            c = s[i]
            if c in " \t":
                st.end_word(i)
                i += 1
            elif c == "\n":
                st.end_word(i)
                st.end_cmd(";")
                i += 1
                if st.heredocs:
                    i = self._heredoc_bodies(i, limit, st.heredocs, depth)
                    st.heredocs = []
            elif c == "#" and st.word is None:
                end = s.find("\n", i, limit)
                i = limit if end < 0 else end
            elif c == "\\":
                nxt = s[i + 1] if i + 1 < limit else ""
                if nxt == "\n":
                    i += 2
                    continue
                st.start_word(i)
                if nxt and nxt in _BACKSLASH_ESCAPES:
                    st.word.append(nxt)
                    i += 2
                else:
                    st.word.append("\\")
                    i += 1
            elif c == "'":
                st.start_word(i)
                end = s.find("'", i + 1, limit)
                if end < 0:
                    self.unclean = True
                    end = limit
                st.word.append(s[i + 1:end])
                i = end + 1
            elif c == "$" and s.startswith("$'", i):
                # ANSI-C quoting: a backslash escapes the next character.
                st.start_word(i)
                j = i + 2
                buf = []
                while j < limit and s[j] != "'":
                    if s[j] == "\\" and j + 1 < limit:
                        buf.append(s[j + 1])
                        j += 2
                    else:
                        buf.append(s[j])
                        j += 1
                if j >= limit:
                    self.unclean = True
                st.word.append("".join(buf))
                i = j + 1
            elif c == '"' or (c == "$" and s.startswith('$"', i)):
                st.start_word(i)
                i = self._expand(i + (2 if c == "$" else 1), limit, '"', depth, st.word)
            elif c == "`":
                st.start_word(i)
                i = self._backtick(i, limit, depth, st.word)
            elif c == "$" and s.startswith("$(", i):
                st.start_word(i)
                i = self._dollar_paren(i, limit, depth, st.word)
            elif c in "<>" and s.startswith("(", i + 1):
                self.unclean = True  # process substitution
                st.start_word(i)
                end = self._seq(i + 2, True, depth + 1, limit)
                st.word.append(s[i:end])
                i = end
            elif c in _WORD_BREAK:
                if c == ")" and group == 0 and in_paren:
                    st.end_word(i)
                    st.end_cmd(")")
                    if st.heredocs:
                        self.unclean = True
                    return i + 1
                if c in "<>" and st.fd_prefix(i):
                    st.word = None
                st.end_word(i)
                op = next(o for o in _LEX_OPERATORS if s.startswith(o, i))
                if op in _REDIRECTIONS:
                    if st.target is not None:
                        self.unclean = True
                    st.target = op
                else:
                    self.ops.add(op)
                    if op == "(":
                        group += 1
                        self.unclean = True  # ( ) group, subshell, f(), array
                    elif op == ")":
                        if group:
                            group -= 1
                        else:
                            self.unclean = True
                    elif op == ";;":
                        self.unclean = True
                    st.end_cmd(op)
                i += len(op)
            else:
                if c == "$" and i + 1 < limit and s[i + 1] in "{[":
                    self.unclean = True
                st.start_word(i)
                st.word.append(c)
                i += 1
        st.end_word(i)
        st.end_cmd(";")
        if st.heredocs or in_paren or group:
            self.unclean = True
        return limit

    def _expand(self, i, limit, quote, depth, out):
        """Scan double-quoted text (``quote='"'``) or an unquoted heredoc body
        (``quote=None``) from ``i``, parsing ``$(...)`` and backticks.
        Returns the index after the closing quote, or ``limit``.
        """
        s = self.s
        escapable = '$`"\\\n' if quote else "$`\\\n"
        while i < limit:
            ch = s[i]
            if quote and ch == quote:
                return i + 1
            if ch == "\\" and i + 1 < limit and s[i + 1] in escapable:
                if s[i + 1] != "\n":
                    out.append(s[i + 1])
                i += 2
            elif ch == "`":
                i = self._backtick(i, limit, depth, out)
            elif ch == "$" and s.startswith("$(", i):
                i = self._dollar_paren(i, limit, depth, out)
            else:
                if ch == "$" and i + 1 < limit and s[i + 1] in "{[":
                    self.unclean = True
                out.append(ch)
                i += 1
        if quote:
            self.unclean = True
        return limit

    def _dollar_paren(self, i, limit, depth, out):
        if self.s.startswith("$((", i):
            self.unclean = True  # arithmetic expansion
        end = self._seq(i + 2, True, depth + 1, limit)
        out.append(self.s[i:end])
        return end

    def _backtick(self, i, limit, depth, out):
        s = self.s
        j = i + 1
        while j < limit and s[j] != "`":
            j += 2 if s[j] == "\\" else 1
        if j >= limit:
            self.unclean = True
            j = limit
        content = s[i + 1:j]
        if "\\" in content:
            # Backslashes inside backticks are unescaped before the shell
            # parses the content, so it is re-parsed as a separate string.
            self.unclean = True
            self.nested_strings.append(
                content.replace("\\\\", "\0").replace("\\`", "`").replace("\\$", "$").replace("\0", "\\")
            )
        else:
            self._seq(i + 1, False, depth + 1, j)
        out.append(s[i:j + 1])
        return j + 1

    def _heredoc_bodies(self, i, limit, pending, depth):
        """Read the bodies of the heredocs started on the line just ended."""
        s = self.s
        for cmd, delim, strip_tabs, quoted, raw in pending:
            body_start = i
            while True:
                if i >= limit:
                    self.unclean = True
                    self._body(cmd, body_start, limit, quoted, raw, depth)
                    return limit
                j = s.find("\n", i, limit)
                line_end = limit if j < 0 else j
                line = s[i:line_end]
                if line.endswith("\r"):
                    line = line[:-1]
                if strip_tabs:
                    line = line.lstrip("\t")
                if line == delim:
                    self._body(cmd, body_start, i, quoted, raw, depth)
                    i = limit if j < 0 else j + 1
                    break
                if j < 0:
                    self.unclean = True
                    self._body(cmd, body_start, limit, quoted, raw, depth)
                    return limit
                i = j + 1
        return i

    def _body(self, cmd, start, end, quoted, raw, depth):
        cmd.heredocs.append((raw, quoted, self.s[start:end]))
        if not quoted:
            self._expand(start, end, None, depth, [])


# -- whole-string exoneration -------------------------------------------------
#
# Every legacy hit is cleared when, and only when, the precise recognisers
# found nothing and the whole string is "all data" (``_all_data``).


def _opts(flags: str, values: str = "", eq_only: str = "") -> dict:
    """Option allowlist: option -> True (takes the next word or ``=value``),
    "=" (value only as ``--name=value``), or False (takes no value)."""
    return {**dict.fromkeys(flags.split(), False), **dict.fromkeys(values.split(), True),
            **dict.fromkeys(eq_only.split(), "=")}


_BARE_NAME_RE = re.compile(r"^[a-z]+$")
_HEREDOC_DELIMITER_RE = re.compile(r"""^(?:\w+|'\w+'|"\w+")$""", re.ASCII)
_DATA_JOINING_OPS = {";", "&&", "||", "|"}
_OUTPUT_REDIRECTIONS = {">", ">>", ">|", "&>", "&>>", ">&"}
_INPUT_REDIRECTIONS = {"<", "<&", "<<", "<<-"}
# Exact options only, checked on the value the shell delivers. Separate
# values only where the program parses them (rg/clap, git parse-options and
# revision options, gh/pflag). Not rg -z (runs decompressors), --pre,
# --pre-glob, --hostname-bin.
_RG_OPTIONS = _opts("-n --line-number -i --ignore-case -S --smart-case -l "
                    "--files-with-matches -w --word-regexp -F --fixed-strings "
                    "-c --count --files --hidden",
                    "-g --glob -t --type -e --regexp -A -B -C")
# git subcommands that run nothing they are given, no option before them.
# Not fetch / pull / clone / push (remote URLs, --upload-pack, --exec),
# config, rebase, bisect, grep (-O runs a pager), or anything else.
# --pretty / --format take their value only after `=`.
_GIT_LOG_OPTIONS = _opts("--oneline --stat --name-only --name-status",
                         "-n --grep --since --until --author", "--format --pretty")
_GIT_SUBCOMMAND_OPTIONS = {
    "commit": _opts("-a --all --amend --no-edit -s --signoff -q --quiet",
                    "-m --message -am -F"),
    "log": _GIT_LOG_OPTIONS,
    "show": _GIT_LOG_OPTIONS,
    "diff": _opts("--stat --name-only --name-status --cached --staged"),
    "status": _opts("-s --short -b --branch --porcelain"),
    "add": _opts("-A --all -u --update"),
}
# gh (command, subcommand) pairs. Not api, alias, extension, repo clone, ...
_GH_WRITE = _opts("--draft -d", "--title -t --body -b --body-file -F --base -B "
                  "--head -H --label -l --repo -R")
_GH_READ = _opts("--comments", "--json --jq -q --limit -L --state -s --repo -R")
_GH_SUBCOMMAND_OPTIONS = {
    **dict.fromkeys([("pr", "create"), ("pr", "edit"), ("pr", "comment"),
                     ("issue", "create"), ("issue", "comment")], _GH_WRITE),
    **dict.fromkeys([("pr", "view"), ("pr", "list"), ("pr", "checks"),
                     ("issue", "view"), ("issue", "list"), ("release", "view"),
                     ("release", "list"), ("run", "view"), ("run", "list"),
                     ("repo", "view"), ("label", "list"), ("search", "prs"),
                     ("search", "issues")], _GH_READ),
}
_CHECKED_PROGRAMS = {"printf", "rg", "git", "gh"}
# The only options whose value may be a `"$(...)"` (the commit-message idiom).
_MESSAGE_OPTIONS = {"-m", "--message", "--title", "--body"}


def _shell_value(raw: str) -> Optional[str]:
    """The value bash delivers for a word, or None when it involves any
    expansion this function does not compute exactly."""
    if raw.startswith("~"):
        return None
    out = []
    i = 0
    while i < len(raw):
        c = raw[i]
        if c in "'\"":
            j = raw.find(c, i + 1)
            if j < 0 or (c == '"' and any(ch in raw[i + 1:j] for ch in "$`\\")):
                return None
            out.append(raw[i + 1:j])
            i = j + 1
        elif c in "\\{}$`*?[":
            return None
        else:
            out.append(c)
            i += 1
    return "".join(out)


def _whole_substitution(raw: str) -> bool:
    """True when ``raw`` is exactly ``"$(...)"`` with a cleanly parsed inside."""
    if not (raw.startswith('"$(') and raw.endswith(')"')) or raw.startswith('"$(('):
        return False
    probe = _ShellParse("")
    probe.s = raw
    return probe._seq(3, True, 1, len(raw) - 1) == len(raw) - 1 and not probe.unclean


def _options_allowed(args: list, allowed: dict) -> bool:
    """True when every ``-`` token in ``args`` is an exact allowed option."""
    i = 0
    while i < len(args):
        arg = args[i]
        name = arg.split("=", 1)[0]
        if arg in allowed:
            i += 2 if allowed[arg] is True else 1
        elif arg.startswith("-") and not ("=" in arg and name.startswith("--") and allowed.get(name)):
            return False
        else:
            i += 1
    return True


def _data_command(cmd, s: str) -> bool:
    """True when ``cmd`` is a bare data program with only allowed arguments."""
    if not cmd.words or cmd.herestrings:
        return False
    a, b = cmd.spans[0]
    prog = cmd.words[0]
    if s[a:b] != prog or not _BARE_NAME_RE.match(prog) or prog not in _DATA_PROGRAMS:
        return False
    for op, target in cmd.redirs:
        if op in _OUTPUT_REDIRECTIONS:
            if target != "/dev/null" and not (op == ">&" and target.isdigit()):
                return False
        elif op not in _INPUT_REDIRECTIONS:
            return False
    for raw, quoted, body in cmd.heredocs:
        if not _HEREDOC_DELIMITER_RE.match(raw) or (not quoted and ("$(" in body or "`" in body)):
            return False
    if prog not in _CHECKED_PROGRAMS:
        return True
    args = []
    for a, b in cmd.spans[1:]:
        value = _shell_value(s[a:b])
        if value is None:
            if not (args and args[-1] in _MESSAGE_OPTIONS and _whole_substitution(s[a:b])):
                return False
            value = "$(...)"
        args.append(value)
    if prog == "printf":
        return not any(arg.startswith("-v") for arg in args)
    if prog == "rg":
        return _options_allowed(args, _RG_OPTIONS)
    if prog == "git":
        if not args or args[0] not in _GIT_SUBCOMMAND_OPTIONS:
            return False
        rest = [w for w in args[1:] if not re.match(r"^-\d+$", w) or args[0] not in ("log", "show")]
        # -F only as `-F -` (the message from stdin).
        if any(w == "-F" and rest[j + 1:j + 2] != ["-"] for j, w in enumerate(rest)):
            return False
        return _options_allowed(rest, _GIT_SUBCOMMAND_OPTIONS[args[0]])
    if len(args) < 2 or (args[0], args[1]) not in _GH_SUBCOMMAND_OPTIONS:
        return False
    return _options_allowed(args[2:], _GH_SUBCOMMAND_OPTIONS[(args[0], args[1])])


def _all_data(parse) -> bool:
    """The whole-string exoneration rule (see the design note above)."""
    if parse.unclean or not parse.ops <= _DATA_JOINING_OPS:
        return False
    return all(_data_command(cmd, parse.s) for cmd in parse.commands)


def _data_argument(words: list, start: int, idx: int) -> bool:
    """True when word ``idx`` of git / gh / rg is not an argument it runs."""
    prog = _program_name(words[start])
    if prog == "git":
        j = start + 1
        while j < len(words) and words[j].startswith("-"):
            w = words[j]
            if w.startswith(_GIT_GLOBAL_EXEC_OPTIONS):
                return False
            j += 2 if w in _GIT_GLOBAL_VALUE_OPTIONS else 1
        if j >= len(words) or words[j] not in _GIT_DATA_SUBCOMMANDS or idx <= j:
            return False
    elif prog == "gh":
        j = start + 1
        if j >= len(words) or words[j] not in _GH_DATA_SUBCOMMANDS or idx <= j:
            return False
    exec_opts = _DATA_EXEC_OPTIONS.get(prog, ())
    for w in (words[idx], words[idx - 1]):
        for opt in exec_opts:
            if w == opt or w.startswith(opt + "="):
                return False
    return True


# -- precise recognition (b) -------------------------------------------------


def _norm_path(path: str) -> str:
    return path.replace("\\", "/")


def _is_absolute(path: str) -> bool:
    return path.startswith("/") or bool(re.match(r"^[A-Za-z]:", path))


def _join_cwd(cwd: Optional[str], path: str) -> str:
    path = _norm_path(path)
    if cwd and not _is_absolute(path):
        path = cwd.rstrip("/") + "/" + path
    return path


def _project_program(path: str) -> Optional[str]:
    """``ppds`` / ``ppds-mcp-server`` when ``path`` names that project."""
    parts = [p for p in _norm_path(path).split("/") if p not in ("", ".")]
    resolved = []
    for part in parts:
        if part == ".." and resolved and resolved[-1] != "..":
            resolved.pop()
        else:
            resolved.append(part)
    if not resolved:
        return None
    last = resolved[-1].lower()
    if last.endswith(".csproj"):
        last = last[: -len(".csproj")]
    return _PPDS_PROJECTS.get(last)


def _split_option(arg: str):
    """``--opt=value`` / ``--opt:value`` -> (``--opt``, value); else (arg, None)."""
    m = _LONG_OPTION_VALUE_RE.match(arg)
    if m:
        return m.group(1), m.group(2)
    return arg, None


def _dotnet_run_argvs(args: list, cwd: Optional[str], project: Optional[str] = None) -> list:
    """Normalise ``dotnet run <args>`` to PPDS argvs (``[]`` when not PPDS).

    Two readings are returned when they differ, and the hook blocks if
    either is blocked: CLI args are only what follows ``--``; and CLI args
    are every token ``dotnet run`` does not recognise plus what follows
    ``--`` (an option this table does not know shifts the second reading).
    """
    unknown = []
    after = []
    i = 0
    n = len(args)
    while i < n:
        arg = args[i]
        if arg == "--":
            after = args[i + 1:]
            break
        name, inline = _split_option(arg)
        if name == "-p":
            # -p is --property when its value contains '=', else --project.
            value = inline if inline is not None else (args[i + 1] if i + 1 < n else "")
            if "=" not in value:
                project = value
            i += 1 if inline is not None else 2
            continue
        if name in _DOTNET_RUN_VALUE_OPTIONS:
            value = inline if inline is not None else (args[i + 1] if i + 1 < n else "")
            if name == "--project":
                project = value
            elif name == "--file":
                return []  # a file-based app, not a project
            i += 1 if inline is not None else 2
            continue
        if name in _DOTNET_RUN_FLAGS or name.startswith("--tl"):
            i += 1
            continue
        unknown.append(arg)
        i += 1

    target = _join_cwd(cwd, project) if project is not None else cwd
    if not target:
        return []
    program = _project_program(target)
    if program is None:
        return []
    only_after = [program] + after
    forwarded = [program] + unknown + after
    return [only_after] if only_after == forwarded else [only_after, forwarded]


def _skip_host_options(args: list, i: int) -> int:
    while i < len(args):
        name, inline = _split_option(args[i])
        if name in _DOTNET_HOST_VALUE_OPTIONS:
            i += 1 if inline is not None else 2
        elif name in _DOTNET_HOST_FLAGS:
            i += 1
        else:
            break
    return i


def _package_program(word: str) -> Optional[str]:
    return _PPDS_PROJECTS.get(word.split("@", 1)[0].lower())


def _tool_exec_argvs(args: list) -> list:
    """``dotnet tool exec`` / ``dnx``: ``[options] <package>[@ver] [--] args``."""
    i = 0
    while i < len(args) and args[i].startswith("-") and args[i] != "--":
        name, inline = _split_option(args[i])
        i += 2 if name in _DOTNET_TOOL_EXEC_VALUE_OPTIONS and inline is None else 1
    if i >= len(args):
        return []
    program = _package_program(args[i])
    if program is None:
        return []
    rest = args[i + 1:]
    if rest[:1] == ["--"]:
        rest = rest[1:]
    return [[program] + rest]


def _dotnet_argvs(args: list, cwd: Optional[str]) -> list:
    """Normalise a ``dotnet ...`` command line to PPDS argvs (``[]`` if none).

    Recognised: ``dotnet run`` / ``dotnet watch [run]`` targeting the
    PPDS.Cli / PPDS.Mcp project (by ``--project`` / ``-p`` or an earlier
    ``cd``); ``dotnet [exec] <path>/ppds.dll`` and ``ppds-mcp-server.dll``;
    ``dotnet ppds``, ``dotnet tool run [options] ppds`` and ``dotnet tool exec
    PPDS.Cli``. Anything else is not PPDS.
    """
    i = _skip_host_options(args, 0)
    if i >= len(args):
        return []
    verb = args[i]
    rest = args[i + 1:]
    lowered = verb.lower()

    if lowered == "run":
        return _dotnet_run_argvs(rest, cwd)
    if lowered == "watch":
        project = None
        j = 0
        while j < len(rest):
            name, inline = _split_option(rest[j])
            if name == "--project":
                project = inline if inline is not None else (rest[j + 1] if j + 1 < len(rest) else "")
                j += 1 if inline is not None else 2
            elif rest[j].lower() == "run":
                j += 1
                break
            elif rest[j].lower() in _DOTNET_WATCH_NON_RUN:
                return []
            elif rest[j].startswith("-") and rest[j] != "--":
                j += 1
            else:
                break
        return _dotnet_run_argvs(rest[j:], cwd, project)
    if lowered == "exec":
        j = 0
        while j < len(rest):
            name, inline = _split_option(rest[j])
            if name in _DOTNET_HOST_VALUE_OPTIONS:
                j += 1 if inline is not None else 2
            elif name.startswith("-"):
                j += 1
            else:
                break
        if j >= len(rest):
            return []
        verb, rest, lowered = rest[j], rest[j + 1:], rest[j].lower()
    if lowered == "tool" and rest:
        sub = rest[0].lower()
        if sub == "run":
            j = 1
            while j < len(rest) and rest[j].startswith("-") and rest[j] != "--":
                j += 1
            if j < len(rest) and rest[j].lower() in _PPDS_PROGRAMS:
                tail = rest[j + 1:]
                if tail[:1] == ["--"]:
                    tail = tail[1:]
                return [[rest[j].lower()] + tail]
            return []
        if sub == "exec":
            return _tool_exec_argvs(rest[1:])
        return []
    if lowered == "dnx":
        return _tool_exec_argvs(rest)

    assembly = _PPDS_ASSEMBLIES.get(re.split(r"[\\/]", lowered)[-1])
    if assembly is not None:
        return [[assembly] + rest]
    if lowered in _PPDS_PROGRAMS:
        return [[lowered] + rest]
    return []


def _shell_c_argument(args: list) -> Optional[str]:
    """The command string of ``bash [options] -c '<cmd>'``, else None."""
    has_c = False
    i = 0
    while i < len(args):
        arg = args[i]
        if arg in _SHELL_VALUE_OPTIONS:
            i += 2
            continue
        if arg.startswith("--"):
            if arg == "--":
                i += 1
                continue
            i += 1
            continue
        if arg.startswith(("-", "+")) and len(arg) > 1:
            letters = arg[1:]
            if "c" in letters:
                has_c = True
            # -o / -O inside a cluster (-euo pipefail) takes the next word.
            i += 2 if ("o" in letters or "O" in letters) else 1
            continue
        return arg if has_c else None
    return None


def _option_value(args: list, names: tuple, short_letter: Optional[str] = None) -> Optional[str]:
    """Value of the first of ``names`` (or a short cluster containing
    ``short_letter``) in ``args``."""
    for i, arg in enumerate(args):
        for name in names:
            if arg == name:
                return args[i + 1] if i + 1 < len(args) else None
            if name.startswith("--") and arg.startswith(name + "="):
                return arg.split("=", 1)[1]
        if (short_letter and arg.startswith("-") and not arg.startswith("--")
                and short_letter in arg[1:]):
            return args[i + 1] if i + 1 < len(args) else None
    return None


def _pwsh_command(prog: str, args: list) -> Optional[str]:
    """The command text of ``pwsh`` / ``powershell`` ``-Command ...``."""
    i = 0
    while i < len(args):
        arg = args[i]
        if arg.startswith(("-", "/")) and len(arg) > 1:
            name = arg.lstrip("-/").lower()
            if name == "c" or (len(name) >= 3 and "command".startswith(name)):
                return " ".join(args[i + 1:])
            if name in ("f", "file", "ec", "e", "encodedcommand"):
                return None
            i += 2 if name in _PWSH_VALUE_OPTIONS else 1
            continue
        # powershell.exe treats a bare word as the command; pwsh as a script.
        return " ".join(args[i:]) if prog == "powershell" else None
    return None


def _cmd_command(args: list) -> Optional[str]:
    """The command text of ``cmd /c ...`` (or ``/k``)."""
    for i, arg in enumerate(args):
        low = arg.lower()
        if low in ("/c", "//c", "/k", "//k"):
            return " ".join(args[i + 1:])
        if not low.startswith("/"):
            return None
    return None


# Programs ``_new_invocations`` handles itself; anything else that is not a
# data program is treated as a possible launcher and searched for one.
_TERMINAL_PROGRAMS = _PPDS_PROGRAMS | _SHELLS | {
    "dotnet", "dnx", "cd", "pushd", "popd", "su", "script", "pwsh", "powershell", "cmd", "eval"}
_LAUNCHED_PROGRAMS = (_TERMINAL_PROGRAMS - {"cd", "pushd", "popd"}) | {"env", "git", "gh", "rg"}


def _launched_index(words: list, start: int) -> Optional[int]:
    """Where a launcher (``sudo``, ``xargs``, ``find -exec`` ...) starts the
    program it runs: the first later word naming a program handled here."""
    if _program_name(words[start]) in _DATA_PROGRAMS | _TERMINAL_PROGRAMS:
        return None
    return next((j for j in range(start + 1, len(words))
                 if _program_name(words[j]) in _LAUNCHED_PROGRAMS), None)


def _effective_program(cmd) -> Optional[str]:
    if cmd.start >= len(cmd.words):
        return None
    j = _launched_index(cmd.words, cmd.start)
    return _program_name(cmd.words[cmd.start if j is None else j])


def _new_invocations(parse, cwd: Optional[str], depth: int, out: list) -> None:
    """Append the invocations the precise parse recognises (part (b))."""

    def scan(text):
        if text and depth < _MAX_STRING_DEPTH:
            try:
                inner = _ShellParse(text)
            except RecursionError:
                return
            _new_invocations(inner, cwd_box[0], depth + 1, out)

    def handle(cmd, words, start):
        prog_word = words[start]
        args = words[start + 1:]
        prog = _program_name(prog_word)

        if prog in _PPDS_PROGRAMS:
            out.append([re.split(r"[\/]", prog_word)[-1]] + args)
        elif prog == "dotnet":
            out.extend(_dotnet_argvs(args, cwd_box[0]))
        elif prog == "dnx":
            out.extend(_tool_exec_argvs(args))
        elif prog in ("cd", "pushd"):
            target = next((a for a in args if not a.startswith("-") or a == "-"), None)
            if target is None or target in ("-", "~") or target.startswith("~/"):
                cwd_box[0] = None
            else:
                cwd_box[0] = _join_cwd(cwd_box[0], target)
        elif prog == "popd":
            cwd_box[0] = None
        elif prog in _SHELLS:
            scan(_shell_c_argument(args))
        elif prog in ("su", "script"):
            scan(_option_value(args, ("-c", "--command"), "c"))
        elif prog in ("pwsh", "powershell"):
            scan(_pwsh_command(prog, args))
        elif prog == "cmd":
            scan(_cmd_command(args))
        elif prog == "eval":
            scan(" ".join(args))
        elif prog == "env":
            # env -S / --split-string runs its value as a command line.
            for j, w in enumerate(args):
                if w in ("-S", "--split-string") and j + 1 < len(args):
                    scan(args[j + 1])
                elif w.startswith("--split-string="):
                    scan(w.split("=", 1)[1])

        if prog in _SHELLS or prog in ("pwsh", "powershell", "cmd"):
            for text in cmd.herestrings + cmd.heredoc_bodies:
                scan(text)
        # git -c core.sshCommand=..., git rebase --exec ..., git config
        # alias.x '!...', gh alias set ..., rg --pre ...: arguments these
        # programs may run are scanned as command strings.
        if prog in _DATA_EXEC_OPTIONS:
            for j in range(start + 1, len(words)):
                if not _data_argument(words, start, j):
                    text = words[j]
                    if "=" in text and (text.startswith("--") or not text.startswith("-")):
                        text = text.split("=", 1)[1]
                    scan(text.lstrip("!"))
        # `echo '...' | bash`, `cat <<EOF | sh`: text piped into a shell.
        if cmd.pipe_to is not None and _effective_program(cmd.pipe_to) in _SHELLS:
            if prog in ("echo", "printf"):
                scan(" ".join(args))
            for text in cmd.herestrings + cmd.heredoc_bodies:
                scan(text)

        launched = _launched_index(words, start)
        if launched is not None:
            handle(cmd, words, launched)

    cwd_box = [cwd]
    for cmd in parse.commands:
        if cmd.start < len(cmd.words):
            handle(cmd, cmd.words, cmd.start)
    for text in parse.nested_strings:
        scan(text)


def find_ppds_invocations(command: str, cwd: Optional[str] = None) -> list:
    """Return a list of argv-style token lists for each PPDS invocation.

    Each entry starts with the program (``ppds`` / ``ppds-mcp-server``,
    possibly with a ``.exe`` suffix) followed by the CLI arguments. The
    result is the legacy matcher's hits plus the forms the parse recognises
    precisely (the .NET host forms normalised to the same shape), or ``[]``
    when the recognisers found nothing and the whole string is all data;
    see the design note above. ``cwd``, when given, is the directory the
    command starts in.
    """
    if not command:
        return []
    try:
        parse = _ShellParse(command)
    except RecursionError:
        parse = None
    found = []
    if parse is not None:
        _new_invocations(parse, cwd, 0, found)
        if not found and _all_data(parse):
            return []
    out = []
    seen = set()
    for _, argv in _legacy_invocations(command):
        out.append(argv)
        seen.add(tuple(argv))
    for argv in found:
        key = tuple(argv)
        if key not in seen:
            seen.add(key)
            out.append(argv)
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

    # 3-level commands: "metadata <noun> <verb>" has the noun at argv[2], verb at argv[3]
    if sub == "metadata" and verb not in _MUTATION_VERBS and len(argv) >= 4:
        verb = argv[3].lower()
        rest = argv[4:]

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
        f"  Command:    {' '.join(argv)}",
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
        f"  Command:   {' '.join(argv)}",
        f"  Target:    {target}",
        f"  Allowlist: {', '.join(allowlist)}",
        "",
    ]
    lines.extend(_config_help_lines())
    return "\n".join(lines)


def _block_msg_misconfigured(problem, argv):
    lines = [
        "BLOCKED [shakedown-safety/env]: the safe-env allowlist is misconfigured.",
        f"  Command: {' '.join(argv)}",
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
            f"  Command: {' '.join(argv)}",
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

    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command", "")
    if not isinstance(command, str) or not command.strip():
        sys.exit(0)

    invocations = find_ppds_invocations(command)
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
