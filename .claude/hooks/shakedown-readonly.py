"""PreToolUse hook: enforce read-only ppds invocations during shakedown.

Activates only when the env var ``PPDS_SHAKEDOWN=1`` is set (typically by
the shakedown skill). When active, blocks Bash commands that would mutate
Dataverse state -- plugin deploys (without ``--dry-run``), record
create/update/delete on any surface, solution imports, etc. The goal is a
hard safety boundary: an agent running shakedown must not be able to write
to Dataverse no matter how many other safeguards are bypassed.

Bypass: unset ``PPDS_SHAKEDOWN``. There is no softer marker -- if you
genuinely need to run a write command, you take the deliberate action of
clearing the env var. The shakedown skill must NOT auto-clear it.

Envelope contract: same nested ``payload["tool_input"]["command"]`` pattern
as protect-main-branch.py (see PR #816).

Failure mode: prints a message to stderr explaining the block + the
``PPDS_SHAKEDOWN`` mechanism, then exits 2 (standard PreToolUse block).
"""

from __future__ import annotations

import json
import os
import re
import shlex
import sys


# ---------------------------------------------------------------------------
# Bash command parsing (mirrors dev-env-check.py)
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


def find_ppds_invocations(command: str) -> list:
    if not command:
        return []
    out = []
    for match in _PPDS_PROGRAM_RE.finditer(command):
        prog = match.group(1)
        tail = command[match.end():]
        cut_re = re.compile(r"[|;&]|&&|\|\||>|<|`")
        cut_match = cut_re.search(tail)
        if cut_match:
            tail = tail[: cut_match.start()]
        try:
            args = shlex.split(tail, posix=True)
        except ValueError:
            args = tail.split()
        out.append([prog] + args)
    return out


# ---------------------------------------------------------------------------
# Mutation rules
# ---------------------------------------------------------------------------


# Top-level subcommands whose presence is benign (no Dataverse traffic) or
# read-only. Anything outside this set, when paired with a write verb, is
# treated as a mutation candidate.
_READONLY_SUBCOMMANDS = {
    "env": {"list", "current", "who", "config", "type", "show", "switch", "select", "use", "test"},
    "logs": {"*"},  # everything under logs is read-only
    "auth": {"list", "show"},
    "version": {"*"},
    "help": {"*"},
}

# Subcommand-2 verbs that are mutations (apply to any surface). E.g.
# ``ppds plugins create`` or ``ppds data update`` both fall here.
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

# Specific full-path mutations the spec calls out explicitly. Keys are
# ``(subcmd, verb)`` tuples; the value is a callable that returns True when
# the invocation should be blocked. Used for cases where ``--dry-run`` (or
# similar) is an exemption.
def _plugins_deploy_blocked(args: list) -> bool:
    return "--dry-run" not in args and "-n" not in args


_NAMED_MUTATIONS = {
    ("plugins", "deploy"): _plugins_deploy_blocked,
    # ``ppds env delete``, ``env update``, ``solutions import``,
    # ``migrate apply`` are matched via the generic verb table; listing
    # here would be redundant. Kept as the integration point for future
    # special-case bypasses.
}


def is_mutation(argv: list) -> tuple[bool, str]:
    """Return (is_mutation, reason). reason is empty when not a mutation.

    argv is ``[program, sub, verb, ...]`` style. ``sub`` is the surface
    (``plugins``, ``env``, ``data``, etc.) and ``verb`` is the action.
    """
    if len(argv) < 2:
        return False, ""

    prog = os.path.basename(argv[0]).lower()
    if prog.endswith(".exe"):
        prog = prog[:-4]
    if prog not in ("ppds", "ppds-mcp-server"):
        return False, ""

    if prog == "ppds-mcp-server":
        # The MCP server itself is launched, not a one-shot command, so we
        # can't reason about whether it will issue writes. Allow -- the
        # dev-env-check hook still gates which env it talks to.
        return False, ""

    if len(argv) < 3:
        return False, ""

    sub = argv[1].lower()
    verb = argv[2].lower()
    rest = argv[3:]

    # Always-allowed subcommand families.
    allowed = _READONLY_SUBCOMMANDS.get(sub)
    if allowed and ("*" in allowed or verb in allowed):
        return False, ""

    # Special-case named mutations (for --dry-run carve-outs).
    named = _NAMED_MUTATIONS.get((sub, verb))
    if named is not None:
        if named(rest):
            return True, f"{sub} {verb} (no --dry-run flag present)"
        return False, ""

    # Generic verb table.
    if verb in _MUTATION_VERBS:
        return True, f"{sub} {verb}"

    return False, ""


# ---------------------------------------------------------------------------
# Stderr message
# ---------------------------------------------------------------------------


def _block_msg(argv: list, reason: str) -> str:
    return "\n".join(
        [
            "BLOCKED [shakedown-readonly]: write/mutation command refused.",
            f"  Command: {' '.join(argv)}",
            f"  Pattern: {reason}",
            "",
            "  PPDS_SHAKEDOWN=1 is set, which puts this session in read-only",
            "  mode for ppds CLI operations. Mutating Dataverse during",
            "  shakedown defeats the purpose -- shakedowns are observation,",
            "  not modification.",
            "",
            "  To bypass (deliberate action -- there is no softer marker):",
            "    unset PPDS_SHAKEDOWN",
            "",
            "  For plugin deploys specifically, add --dry-run to validate",
            "  without writing.",
        ]
    )


# ---------------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------------


def main() -> None:
    if os.environ.get("PPDS_SHAKEDOWN", "").strip() != "1":
        # Hook is a no-op outside shakedown mode.
        sys.exit(0)

    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        # Empty/garbled stdin -- allow.
        sys.exit(0)

    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command", "")
    if not isinstance(command, str) or not command.strip():
        sys.exit(0)

    invocations = find_ppds_invocations(command)
    if not invocations:
        sys.exit(0)

    for argv in invocations:
        blocked, reason = is_mutation(argv)
        if blocked:
            print(_block_msg(argv, reason), file=sys.stderr)
            sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
