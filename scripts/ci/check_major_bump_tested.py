#!/usr/bin/env python3
"""Rule 3 of the pre-merge gate: major dependency evidence enforcement.

Catches the failure mode from PR #806 (vite 5 -> 8 was merged without the
relevant product tests) without requiring an unrelated .NET job for every
dependency ecosystem.

For dependency PRs (label ``dependencies`` or a Dependabot author):

1. Classify the update with ``scripts/dependabot/classify.py``. Major and
   unclassifiable updates require evaluation; uncertainty fails closed.
2. Select the executable evidence for the classified ecosystem:
   NuGet -> .NET tests, npm -> Extension build/tests, GitHub Actions -> Python
   workflow-policy tests.
3. Require that exact job, from the expected workflow, to have run and passed.

There is no bypass marker. A major update without relevant passing evidence is
unverified and remains blocked.

Usage:
    python -m scripts.ci.check_major_bump_tested --pr 123

Exit codes:
    0 — policy not applicable, or required evidence ran and passed
    1 — major/uncertain update is unclassified or lacks passing evidence
    2 — invocation / GitHub data error
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

# Make scripts/dependabot importable so classification has one implementation.
REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "dependabot"))

try:
    import classify  # noqa: E402  (path manipulation above is intentional)
except ImportError as e:  # pragma: no cover — only hits in broken layouts
    raise SystemExit(
        f"error: cannot import scripts/dependabot/classify.py: {e}\n"
        "Rule 3 depends on the repository dependency classifier."
    )


@dataclass(frozen=True)
class RequiredEvidence:
    """A check run that proves the affected dependency surface was exercised."""

    workflow: str
    job: str
    description: str


# Names are the user-visible values returned by ``gh pr checks --json``.
# Matching both workflow and job avoids accepting an unrelated generic `test`
# check from another workflow or third-party integration.
REQUIRED_EVIDENCE_BY_ECOSYSTEM = {
    "nuget": RequiredEvidence("Test", "test", ".NET unit tests"),
    "npm": RequiredEvidence("Build", "extension", "Extension build and tests"),
    "github-actions": RequiredEvidence(
        "Python Tests",
        "workflow-tests",
        "executable workflow-policy tests",
    ),
}

PASSING_STATES = frozenset({"SUCCESS", "PASS"})
RUNNING_STATES = frozenset({"PENDING", "IN_PROGRESS", "QUEUED", ""})


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
    """Fetch fields needed for dependency classification."""
    raw = _run_gh([
        "pr", "view", str(pr_number),
        "--json", "number,title,body,labels,headRefName,files,author",
    ])
    return json.loads(raw)


def fetch_pr_checks(pr_number: int) -> list[dict]:
    """Return job name, state, and workflow identity for the PR's checks."""
    raw = _run_gh([
        "pr", "checks", str(pr_number),
        "--json", "name,state,workflow",
    ])
    return json.loads(raw) if raw.strip() else []


def required_evidence_for(
    classification: classify.Classification,
) -> tuple[Optional[RequiredEvidence], str]:
    """Resolve required evidence or return a fail-closed explanation."""
    evidence = REQUIRED_EVIDENCE_BY_ECOSYSTEM.get(classification.ecosystem)
    if evidence is not None:
        return evidence, ""
    return None, (
        "Major or unclassifiable dependency update detected, but its ecosystem "
        f"is '{classification.ecosystem}'. Cannot select relevant test evidence; "
        "add an ecosystem label or use a dependency manifest/workflow-only diff."
    )


def check_required_evidence(
    checks: list[dict],
    evidence: RequiredEvidence,
) -> tuple[bool, str]:
    """Pass iff the exact ecosystem-specific workflow job ran successfully."""
    matching_states = [
        (check.get("state") or "").upper()
        for check in checks
        if (check.get("name") or "") == evidence.job
        and (check.get("workflow") or "") == evidence.workflow
    ]
    check_name = f"{evidence.workflow} / {evidence.job}"

    # A successful rerun is sufficient even if an older attempt is also
    # present in the rollup.
    if any(state in PASSING_STATES for state in matching_states):
        return True, (
            "Major or unclassifiable dependency update detected; required "
            f"{evidence.description} ({check_name}) ran and passed."
        )

    if not matching_states:
        return False, (
            "Major or unclassifiable dependency update detected, but required "
            f"{evidence.description} ({check_name}) did not run on this PR."
        )

    if any(state in RUNNING_STATES for state in matching_states):
        state_text = ", ".join(state or "UNKNOWN" for state in matching_states)
        return False, f"Required check {check_name} is still running ({state_text})."

    if all(state == "SKIPPED" for state in matching_states):
        return False, (
            f"Required check {check_name} was SKIPPED; major dependency updates "
            "must run the relevant test surface."
        )

    state_text = ", ".join(state or "UNKNOWN" for state in matching_states)
    return False, f"Required check {check_name} did not pass (state={state_text})."


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Pre-merge Rule 3: major dependency evidence enforcement.",
    )
    parser.add_argument("--pr", type=int, required=True, help="PR number")
    args = parser.parse_args(argv)

    try:
        pr = fetch_pr_payload(args.pr)
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    if not classify.is_dependency_update(pr):
        print("Not a dependency-update PR — rule not applicable.")
        return 0

    classification = classify.classify_pr(pr)
    if not classify.requires_major_evaluation(classification):
        print("Dependency PR is a classified patch/minor update — rule not applicable.")
        return 0

    evidence, error = required_evidence_for(classification)
    if evidence is None:
        print(error)
        return 1

    try:
        checks = fetch_pr_checks(args.pr)
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    passed, message = check_required_evidence(checks, evidence)
    print(message)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
