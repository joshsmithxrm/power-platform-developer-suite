#!/usr/bin/env python3
"""PreToolUse hook on AskUserQuestion: block in bg/headless sessions.

`AskUserQuestion` does NOT pause a bg/headless Claude session — the daemon
auto-injects an answer within ~15-60s (the "Recommended" option, or the
implicit "Something else" when none is marked). Issue #1137 documents the
smoking gun; PR #1105 was filed unilaterally by a bg worker that thought
it was asking.

Detection signal: CLAUDE_JOB_DIR is exported into every bg/headless
session by the daemon. Interactive sessions do not set it.

Exit codes: 0 = allow, 2 = block (Claude Code re-feeds stderr to the
caller, so the worker sees the message and can switch to /await-operator).
"""
from __future__ import annotations

import json
import os
import sys

_MESSAGE = (
    "BLOCKED — AskUserQuestion does not pause a bg/headless session.\n"
    "The daemon auto-answers within ~15-60s (see #1137 / #1105 smoking gun).\n"
    "Use /await-operator instead:\n"
    "  Skill(skill=\"await-operator\",\n"
    "        artifact_path=\"<draft you want ratified>\",\n"
    "        question=\"<one-sentence decision>\",\n"
    "        options=[\"option1\", \"option2\", ...])\n"
    "Then EXIT THE TURN — do not call further tools.\n"
)


def main() -> int:
    if not os.environ.get("CLAUDE_JOB_DIR", "").strip():
        return 0
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        payload = {}
    if payload.get("tool_name") != "AskUserQuestion":
        return 0
    sys.stderr.write(_MESSAGE)
    return 2


if __name__ == "__main__":
    sys.exit(main())
