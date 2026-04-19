#!/usr/bin/env python3
"""Rule 3 of the pre-merge gate: major-version bump test enforcement.

Catches the failure mode from PR #806 (vite 5 -> 8 — TWO major-version jumps,
auto-merged with no real ``test`` job re-run, only ``check-changes`` ran).

For dependabot PRs (label ``dependencies`` OR author ``app/dependabot``):

1. Use ``classify_pr`` from ``scripts/dependabot/classify.py`` to detect
   whether the title indicates a major-version bump.
2. If yes, require the actual ``test`` job (not the path-filter
   ``check-changes`` skip-status) to have run AND passed in the PR's CI rollup.

Block merge if the test job is SKIPPED or didn't run.

Bypass: there is no bypass marker for this rule. A major bump that didn't
trigger the test job is by definition unverified — the fix is to retrigger
CI by pushing an empty commit or hitting "Re-run all jobs", not to wave it
through.

Usage:
    python -m scripts.ci.check_major_bump_tested --pr 123

Exit codes:
    0 — not a dependabot major bump, OR test job ran and passed
    1 — major bump and test job skipped / failed / missing
    2 — invocation / data error
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Optional

# Make scripts/dependabot importable so we can reuse classify_pr without copying.
REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "dependabot"))

try:
    import classify  # noqa: E402  (path manipulation above is intentional)
except ImportError as e:  # pragma: no cover — only hits in broken layouts
    raise SystemExit(
        f"error: cannot import scripts/dependabot/classify.py: {e}\n"
        "Rule 3 depends on the dependabot classifier shipped in PR #814."
    )


# Job names that count as the real test job (must have RUN + SUCCESS).
# Sourced from .github/workflows/test.yml (job id: `test`).
# `test-status` is the gate job that branch protection sees — but it can pass
# trivially when `test` is skipped, which is exactly the failure mode we're
# catching. So we look at `test` directly.
REQUIRED_TEST_JOB_NAMES = ("test",)

# Authors that mark a PR as dependabot-originated.
DEPENDABOT_AUTHORS = frozenset({"dependabot", "app/dependabot", "dependabot[bot]"})


def _run_gh(args: list[str]) -> str:
    try:
        out = subprocess.run(
            ["gh", *args], capture_output=True, text=True, check=True,
        )
    except FileNotFoundError as e:
        raise RuntimeError(f"gh CLI not available: {e}") from e
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"gh {' '.join(args)} failed: {e.stderr.strip()}") from e
    return out.stdout


def fetch_pr_payload(pr_number: int) -> dict:
    """Fetch fields needed for classification."""
    raw = _run_gh([
        "pr", "view", str(pr_number),
        "--json", "number,title,body,labels,headRefName,files,author",
    ])
    return json.loads(raw)


def fetch_pr_checks(pr_number: int) -> list[dict]:
    """Return the list of checks for the PR (gh pr checks --json)."""
    raw = _run_gh(["pr", "checks", str(pr_number), "--json", "name,state"])
    return json.loads(raw) if raw.strip() else []


def is_dependabot_pr(pr: dict) -> bool:
    """True if the PR is dependabot-originated."""
    labels = {lbl.get("name", "").lower() for lbl in pr.get("labels", [])}
    if "dependencies" in labels:
        return True
    author = (pr.get("author") or {}).get("login", "").lower()
    return author in DEPENDABOT_AUTHORS


def is_major_bump(pr: dict) -> bool:
    """True if the dependabot PR title indicates a major-version bump.

    Uses ``classify_pr`` (which uses ``_BUMP_TITLE_RE`` + ``classify_update_type``).
    A grouped bump returns Group B with update_type="unknown" — not major,
    so this returns False. That's the right call: grouped bumps need the
    skill operator to inspect them; we don't auto-flag them here.
    """
    cls = classify.classify_pr(pr)
    return cls.update_type == "major"


def check_test_job_ran(checks: list[dict]) -> tuple[bool, str]:
    """Return (passed, message). Passes iff a required test job both ran and succeeded.

    States we honor (per gh pr checks --json):
      - SUCCESS / pass  -> ran and passed
      - SKIPPED         -> did NOT run, fail the rule
      - PENDING / IN_PROGRESS / QUEUED -> still running, fail (PR isn't ready)
      - FAILURE / CANCELLED / TIMED_OUT / ACTION_REQUIRED / NEUTRAL -> fail
      - missing entirely -> fail
    """
    by_name = {c.get("name", ""): (c.get("state", "") or "").upper() for c in checks}

    found = []
    for required in REQUIRED_TEST_JOB_NAMES:
        if required in by_name:
            found.append((required, by_name[required]))

    if not found:
        return False, (
            "Major-version bump detected, but the required test job "
            f"({', '.join(REQUIRED_TEST_JOB_NAMES)}) did not run on this PR. "
            "Trigger CI by pushing an empty commit or re-running all jobs."
        )

    failures = []
    for name, state in found:
        if state in {"SUCCESS", "PASS"}:
            continue
        if state == "SKIPPED":
            failures.append(
                f"required test job '{name}' was SKIPPED — major bumps must "
                "re-run the test job; trigger CI by pushing an empty commit"
            )
        elif state in {"PENDING", "IN_PROGRESS", "QUEUED", ""}:
            failures.append(f"required test job '{name}' is still running ({state or 'unknown'})")
        else:
            failures.append(f"required test job '{name}' did not pass (state={state})")

    if failures:
        return False, "Major-version bump test enforcement failed:\n  - " + "\n  - ".join(failures)

    return True, (
        "Major-version bump detected; required test job(s) "
        f"({', '.join(n for n, _ in found)}) ran and passed."
    )


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Pre-merge gate Rule 3: major-version bump test enforcement.",
    )
    parser.add_argument("--pr", type=int, required=True, help="PR number")
    args = parser.parse_args(argv)

    try:
        pr = fetch_pr_payload(args.pr)
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    if not is_dependabot_pr(pr):
        print("Not a dependabot PR — rule not applicable.")
        return 0

    if not is_major_bump(pr):
        print("Dependabot PR but not a major-version bump — rule not applicable.")
        return 0

    try:
        checks = fetch_pr_checks(args.pr)
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    passed, message = check_test_job_ran(checks)
    print(message)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
