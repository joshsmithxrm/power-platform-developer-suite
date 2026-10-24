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
import re
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime
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
_ACTIONS_JOB_LINK_RE = re.compile(r"/actions/runs/(?P<run_id>\d+)/job/(?P<job_id>\d+)(?:[/?#]|$)")


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


def _enrich_attempt_metadata(check: dict) -> dict:
    """Add GitHub Actions run-attempt metadata for deterministic ordering."""
    enriched = dict(check)
    match = _ACTIONS_JOB_LINK_RE.search(str(check.get("link") or ""))
    if match is None:
        return enriched

    job_id = int(match.group("job_id"))
    linked_run_id = int(match.group("run_id"))
    raw = _run_gh([
        "api", f"repos/{{owner}}/{{repo}}/actions/jobs/{job_id}",
    ])
    metadata = json.loads(raw)
    if metadata.get("id") != job_id or metadata.get("run_id") != linked_run_id:
        # Do not trust ordering metadata that does not describe the linked check.
        # Leaving it absent makes the policy gate fail closed.
        return enriched
    enriched.update({
        "runId": metadata.get("run_id"),
        "runAttempt": metadata.get("run_attempt"),
        "jobId": metadata.get("id"),
        "startedAt": metadata.get("started_at") or check.get("startedAt"),
        "completedAt": metadata.get("completed_at") or check.get("completedAt"),
    })
    return enriched


def fetch_pr_checks(
    pr_number: int,
    evidence: Optional[RequiredEvidence] = None,
) -> list[dict]:
    """Return PR checks, enriching required-job attempts with ordering data."""
    raw = _run_gh([
        "pr", "checks", str(pr_number),
        "--json", "name,state,workflow,startedAt,completedAt,link",
    ])
    checks = json.loads(raw) if raw.strip() else []
    if evidence is None:
        return checks
    return [
        _enrich_attempt_metadata(check)
        if (check.get("name") or "") == evidence.job
        and (check.get("workflow") or "") == evidence.workflow
        else check
        for check in checks
    ]


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
    """Pass iff the newest exact workflow/job attempt ran successfully."""
    matching_checks = [
        check
        for check in checks
        if (check.get("name") or "") == evidence.job
        and (check.get("workflow") or "") == evidence.workflow
    ]
    check_name = f"{evidence.workflow} / {evidence.job}"

    if not matching_checks:
        return False, (
            "Major or unclassifiable dependency update detected, but required "
            f"{evidence.description} ({check_name}) did not run on this PR."
        )

    ordered: list[tuple[tuple[datetime, int, int, int], dict]] = []
    for check in matching_checks:
        try:
            started_at = datetime.fromisoformat(
                str(check["startedAt"]).replace("Z", "+00:00")
            )
            if started_at.tzinfo is None or started_at.utcoffset() is None:
                raise ValueError("startedAt must include a timezone")
            run_id = int(check["runId"])
            run_attempt = int(check["runAttempt"])
            job_id = int(check["jobId"])
            if run_id <= 0 or run_attempt <= 0 or job_id <= 0:
                raise ValueError("ordering identifiers must be positive")
        except (KeyError, TypeError, ValueError):
            return False, (
                f"Required check {check_name} is missing valid attempt/order "
                "metadata; cannot determine the newest validation attempt."
            )
        ordered.append(((started_at, run_id, run_attempt, job_id), check))

    newest_key = max(key for key, _ in ordered)
    newest = [check for key, check in ordered if key == newest_key]
    newest_states = {(check.get("state") or "").upper() for check in newest}
    if len(newest_states) != 1:
        return False, (
            f"Required check {check_name} has conflicting duplicate states for "
            "the newest attempt; cannot accept ambiguous evidence."
        )

    state = newest_states.pop()
    newest_attempt = int(newest[0]["runAttempt"])
    if state in PASSING_STATES:
        return True, (
            "Major or unclassifiable dependency update detected; required "
            f"{evidence.description} ({check_name}) newest attempt "
            f"#{newest_attempt} ran and passed."
        )

    if state in RUNNING_STATES:
        return False, (
            f"Required check {check_name} newest attempt #{newest_attempt} "
            f"is still running ({state or 'UNKNOWN'})."
        )

    if state == "SKIPPED":
        return False, (
            f"Required check {check_name} newest attempt #{newest_attempt} was "
            "SKIPPED; major dependency updates must run the relevant test surface."
        )

    return False, (
        f"Required check {check_name} newest attempt #{newest_attempt} did not "
        f"pass (state={state or 'UNKNOWN'})."
    )


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
        checks = fetch_pr_checks(args.pr, evidence)
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    passed, message = check_required_evidence(checks, evidence)
    print(message)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
