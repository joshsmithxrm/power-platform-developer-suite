#!/usr/bin/env python3
"""PreToolUse hook on Bash: warn when `claude -p` is invoked from a shell.

Defence-in-depth observability layer for spec specs/dispatch-routing.md ACs
19-20 and Core Requirement #7. The dispatcher
(scripts/claude_dispatch.py) emits the same warning + JSONL row when it
spawns `claude -p` from PPDS scripts; this hook catches *ad-hoc* shell
usage by interactive sessions.

Behaviour:
  * tool_name != "Bash"             -> exit 0 (no-op).
  * command does not start with claude (after stripping env prefixes,
    path, .exe) -> exit 0.
  * argv contains --bg or omits -p (before first |, >, <, ;) -> exit 0.
  * Otherwise: emit stderr warning, append JSONL row to
    .claude/state/sdk-spend.jsonl, exit 0.

This hook NEVER blocks; it is informational only.
"""
from __future__ import annotations

import json
import os
import re
import shlex
import sys
from datetime import datetime, timezone

_ENV_PREFIX_RE = re.compile(r"^[A-Z_][A-Z0-9_]*=.*$")
_EXE_SUFFIX_RE = re.compile(r"\.exe$", re.IGNORECASE)
_PATH_SEP_RE = re.compile(r"^.*[/\\]")
_TERMINATORS = {"|", ";", ">", "<", ">>", "<<", "&&", "||", "&"}


def _normalize_first_token(tok: str) -> str:
    """Strip path prefix and .exe suffix, return lowercase basename."""
    bare = _PATH_SEP_RE.sub("", tok)
    bare = _EXE_SUFFIX_RE.sub("", bare)
    return bare


def _argv_prefix_before_terminator(tokens):
    """Return tokens up to the first shell terminator (|, ;, >, <, etc.)."""
    out = []
    for t in tokens:
        if t in _TERMINATORS:
            break
        out.append(t)
    return out


def _extract_value(tokens, long_flag: str, short_flag: str) -> str:
    """Return value for --long / -short flag; supports `--flag=val` form too."""
    for i, t in enumerate(tokens):
        if t == long_flag or t == short_flag:
            if i + 1 < len(tokens):
                return tokens[i + 1]
        if t.startswith(long_flag + "="):
            return t[len(long_flag) + 1 :]
        if short_flag and t.startswith(short_flag + "=") and len(short_flag) > 1:
            return t[len(short_flag) + 1 :]
    return "none"


def _analyze(command: str):
    """Return (model, agent) if hook should fire; else None."""
    if not command:
        return None
    try:
        tokens = shlex.split(command, posix=True)
    except ValueError:
        # Unbalanced quotes — treat as not-a-claude-invocation.
        return None
    # Strip env-prefix tokens (FOO=bar style).
    idx = 0
    while idx < len(tokens) and _ENV_PREFIX_RE.match(tokens[idx]):
        idx += 1
    if idx >= len(tokens):
        return None
    first = _normalize_first_token(tokens[idx])
    if first != "claude":
        return None
    rest = tokens[idx + 1 :]
    argv_prefix = _argv_prefix_before_terminator(rest)
    if "--bg" in argv_prefix:
        return None
    if "-p" not in argv_prefix:
        return None
    model = _extract_value(argv_prefix, "--model", "-m")
    agent = _extract_value(argv_prefix, "--agent", "-a")
    return model, agent


def _append_jsonl(row: dict) -> None:
    """Append a JSONL row to .claude/state/sdk-spend.jsonl (mkdir parents)."""
    path = os.path.join(".claude", "state", "sdk-spend.jsonl")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(row, ensure_ascii=False) + "\n")


def main() -> None:
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)
    if payload.get("tool_name", "") != "Bash":
        sys.exit(0)
    command = (payload.get("tool_input") or {}).get("command", "")
    session_id = payload.get("session_id", "unknown")
    result = _analyze(command)
    if result is None:
        sys.exit(0)
    model, agent = result
    caller = f"bash:{session_id}"
    # Em-dash literal (U+2014) — counts against monthly Agent SDK credit.
    msg = (
        f"WARN SDK pool: claude -p invoked from {caller} "
        f"(model={model}, agent={agent}) — counts against monthly "
        f"Agent SDK credit, not subscription."
    )
    # Write to stderr with explicit UTF-8 encoding so the em-dash round-trips
    # cleanly on Windows consoles whose default may be cp1252.
    try:
        sys.stderr.buffer.write(msg.encode("utf-8") + b"\n")
        sys.stderr.buffer.flush()
    except (AttributeError, OSError):
        print(msg, file=sys.stderr)
    try:
        _append_jsonl(
            {
                "ts": datetime.now(timezone.utc).isoformat(),
                "caller": caller,
                "model": model,
                "agent": agent,
                "est_input_tokens": len(command) // 4,
            }
        )
    except OSError:
        # Best-effort logging; never block.
        pass
    sys.exit(0)


if __name__ == "__main__":
    main()
