#!/usr/bin/env python3
"""Apply the manual-evaluation label using the shared dependency classifier."""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Optional
from urllib.parse import quote

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "dependabot"))

try:
    import classify  # noqa: E402  (path manipulation above is intentional)
except ImportError as e:  # pragma: no cover — only hits in broken layouts
    raise SystemExit(
        f"error: cannot import scripts/dependabot/classify.py: {e}\n"
        "Major dependency labeling depends on the repository classifier."
    )


EVALUATION_LABEL = "status:needs-evaluation"


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
    """Fetch the same PR shape used by the enforcement gate."""
    raw = _run_gh([
        "pr", "view", str(pr_number),
        "--json", "number,title,body,labels,headRefName,files,author",
    ])
    return json.loads(raw)


def apply_evaluation_label(pr_number: int) -> None:
    """Idempotently apply the workflow-owned evaluation label."""
    _run_gh([
        "api", "--method", "POST",
        f"repos/{{owner}}/{{repo}}/issues/{pr_number}/labels",
        "--field", f"labels[]={EVALUATION_LABEL}",
        "--silent",
    ])


def remove_evaluation_label(pr_number: int) -> None:
    """Remove the workflow-owned evaluation label after reclassification."""
    encoded_label = quote(EVALUATION_LABEL, safe="")
    _run_gh([
        "api", "--method", "DELETE",
        f"repos/{{owner}}/{{repo}}/issues/{pr_number}/labels/{encoded_label}",
        "--silent",
    ])


def evaluate_and_label(pr: dict) -> tuple[bool, str]:
    """Classify one dependency PR and label major/uncertain updates."""
    if not classify.is_dependency_update(pr):
        return False, "Not a dependency-update PR; no label applied."

    classification = classify.classify_pr(pr)
    if not classify.requires_major_evaluation(classification):
        label_names = {
            (label.get("name") or "").lower()
            for label in (pr.get("labels") or [])
        }
        if EVALUATION_LABEL in label_names:
            pr_number = int(pr.get("number", 0))
            if pr_number <= 0:
                raise RuntimeError("PR payload has no valid pull request number")
            remove_evaluation_label(pr_number)
        return False, (
            f"Classified as {classification.update_type}; no evaluation label required."
        )

    pr_number = int(pr.get("number", 0))
    if pr_number <= 0:
        raise RuntimeError("PR payload has no valid pull request number")

    apply_evaluation_label(pr_number)
    return True, (
        f"Applied {EVALUATION_LABEL} to {classification.update_type} "
        f"{classification.ecosystem} dependency update."
    )


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Label major or unclassifiable dependency updates.",
    )
    parser.add_argument("--pr", type=int, required=True, help="PR number")
    args = parser.parse_args(argv)

    try:
        pr = fetch_pr_payload(args.pr)
        _, message = evaluate_and_label(pr)
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    print(message)
    return 0


if __name__ == "__main__":
    sys.exit(main())
