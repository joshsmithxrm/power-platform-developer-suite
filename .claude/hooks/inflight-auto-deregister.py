#!/usr/bin/env python3
"""PostToolUse hook: deregister in-flight branch on PR merge / branch delete.

Triggers on Bash matching:
- "gh pr merge:*" - extracts the PR number; uses gh to resolve to branch.
- "git branch -D feat/*" or "-d feat/*" - extracts branch from command.

On exit code 0 only - failed operations leave the registry untouched.
Calls scripts/inflight-deregister.py --branch <name>. Idempotent.

Exit codes:
- 0: always (post-hook is informational; never blocks subsequent tools)
"""
from __future__ import annotations

import json
import os
import re
import shlex
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir  # noqa: E402


def _extract_branch_from_branch_del(command):
    """Parse `git branch -D <name>` (or -d) and return the branch."""
    try:
        toks = shlex.split(command, posix=False)
    except ValueError:
        return None
    if "branch" not in toks:
        return None
    if not any(t in ("-D", "-d", "--delete") for t in toks):
        return None
    # Last non-flag token after `branch` is the branch name
    seen_branch = False
    for tok in toks:
        if tok == "branch":
            seen_branch = True
            continue
        if not seen_branch:
            continue
        if tok.startswith("-"):
            continue
        return tok.strip("'\"")
    return None


def _extract_pr_number(command):
    """Parse `gh pr merge <N> ...` and return the number as int, or None."""
    m = re.search(r"gh\s+pr\s+merge\s+(\d+)", command)
    if m:
        return int(m.group(1))
    return None


def _branch_for_pr(project_dir, pr_number):
    """Resolve PR number to source branch via gh."""
    try:
        result = subprocess.run(
            ["gh", "pr", "view", str(pr_number), "--json", "headRefName",
             "--jq", ".headRefName"],
            cwd=project_dir, capture_output=True, text=True, timeout=15,
        )
        if result.returncode == 0:
            return (result.stdout or "").strip() or None
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass
    return None


def main():
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)
    response = payload.get("tool_response") or payload.get("tool_result") or {}
    rc = response.get("exit_code")
    if rc is None:
        rc = response.get("returncode")
    if rc is None and response.get("success") is False:
        rc = 1
    if rc not in (0, None):
        sys.exit(0)
    tool_input = payload.get("tool_input") or {}
    command = (tool_input.get("command") or "").strip()
    if not command:
        sys.exit(0)
    project_dir = get_project_dir()
    branch = None
    if "gh pr merge" in command:
        n = _extract_pr_number(command)
        if n is not None:
            branch = _branch_for_pr(project_dir, n)
    elif "git branch" in command and any(f in command for f in ("-D", "-d")):
        branch = _extract_branch_from_branch_del(command)
    if not branch:
        sys.exit(0)
    try:
        subprocess.run(
            ["python", "scripts/inflight-deregister.py", "--branch", branch],
            cwd=project_dir, capture_output=True, text=True, timeout=15,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    sys.exit(0)


if __name__ == "__main__":
    main()
