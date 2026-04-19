#!/usr/bin/env python3
"""Rule 1 of the pre-merge gate: PR size guardrail.

Blocks merge when a PR is too large to review properly. Per the v1-launch
retro (item #11, root cause: PR #792 — 131 files / 7.5K LoC merged unreviewed),
the limits are:

* > 50 changed files, OR
* > 2000 LoC additions+deletions

Both limits ENFORCING from day one — no soft-warn period (per retro decision).

Bypass marker: ``[size-waived: <reason>]`` in the PR title or body. The reason
must be non-empty (whitespace-only is rejected). Marker is case-sensitive to
avoid casual bypass — a deliberate copy-paste is required.

Usage:
    python -m scripts.ci.check_pr_size --pr 123
    python -m scripts.ci.check_pr_size --json '{"additions": 10, ...}'

Exit codes:
    0 — under limits OR bypass marker present and valid
    1 — over limits and no valid bypass marker
    2 — invocation / data error (gh failure, malformed JSON)
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from typing import Optional

# Match `[size-waived: <reason>]`. Reason is non-greedy up to the closing bracket.
# Case-sensitive; deliberate.
SIZE_WAIVED_RE = re.compile(r"\[size-waived:\s*([^\]]+?)\s*\]")

MAX_FILES = 50
MAX_LOC = 2000


def fetch_pr_json(pr_number: int) -> dict:
    """Fetch PR data via `gh pr view --json`.

    Kept thin so tests can monkeypatch it.
    """
    cmd = [
        "gh", "pr", "view", str(pr_number),
        "--json", "additions,deletions,changedFiles,title,body",
    ]
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, check=True)
    except FileNotFoundError as e:
        raise RuntimeError(f"gh CLI not available: {e}") from e
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"gh pr view failed: {e.stderr.strip()}") from e
    return json.loads(out.stdout)


def find_size_waiver(title: str, body: str) -> Optional[str]:
    """Return the waiver reason if a valid `[size-waived: <reason>]` marker is
    present in title or body, else None.

    Whitespace-only reasons are rejected (returns None).
    """
    for blob in (title or "", body or ""):
        m = SIZE_WAIVED_RE.search(blob)
        if not m:
            continue
        reason = m.group(1).strip()
        if reason:
            return reason
    return None


def check_pr_size(pr: dict) -> tuple[bool, str]:
    """Apply the size rule. Returns (passed, message).

    `pr` must have keys: additions, deletions, changedFiles, title, body.
    """
    try:
        additions = int(pr.get("additions") or 0)
        deletions = int(pr.get("deletions") or 0)
        files = int(pr.get("changedFiles") or 0)
    except (TypeError, ValueError) as e:
        return False, f"malformed PR data (additions/deletions/changedFiles not numeric): {e}"

    title = pr.get("title", "") or ""
    body = pr.get("body", "") or ""
    loc = additions + deletions

    waiver = find_size_waiver(title, body)
    over_files = files > MAX_FILES
    over_loc = loc > MAX_LOC

    if not over_files and not over_loc:
        return True, f"PR size OK ({files} files, {loc} LoC)"

    breaches = []
    if over_files:
        breaches.append(f"{files} files > {MAX_FILES}")
    if over_loc:
        breaches.append(f"{loc} LoC > {MAX_LOC}")
    breach_str = "; ".join(breaches)

    if waiver:
        return True, (
            f"PR size over limit ({breach_str}) — waiver accepted: {waiver}"
        )

    return False, (
        f"PR size exceeds limit ({breach_str}). "
        f"Add `[size-waived: <reason>]` to the PR title or body to bypass."
    )


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Pre-merge gate Rule 1: PR size guardrail.")
    src = parser.add_mutually_exclusive_group(required=True)
    src.add_argument("--pr", type=int, help="PR number to check (uses gh pr view)")
    src.add_argument("--json", type=str, help="PR JSON blob (for testing / piped use)")
    args = parser.parse_args(argv)

    try:
        if args.pr is not None:
            pr = fetch_pr_json(args.pr)
        else:
            pr = json.loads(args.json)
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    passed, message = check_pr_size(pr)
    print(message)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
