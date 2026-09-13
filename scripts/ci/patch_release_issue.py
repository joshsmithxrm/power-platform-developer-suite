#!/usr/bin/env python3
"""Prepare one safe, idempotent patch-release issue from a PR event.

The workflow supplies already-generated release-plan JSON/Markdown and a JSON
snapshot of every existing ``release:patch`` issue. This module makes the
event/idempotency decision and writes the title/body to files. It never invokes
a shell, GitHub, a release workflow, or a publishing command.
"""
from __future__ import annotations

import argparse
import html
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Optional, Sequence


PATCH_LABEL = "release:patch"
_REPOSITORY_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


@dataclass(frozen=True)
class PatchPullRequestEvent:
    action: str
    number: int
    merged: bool
    base_ref: str
    labels: frozenset[str]
    event_label: Optional[str]
    title: str
    repository: str

    @property
    def marker(self) -> str:
        return record_marker(self.number)

    @property
    def pull_request_url(self) -> str:
        return f"https://github.com/{self.repository}/pull/{self.number}"


def record_marker(pr_number: int) -> str:
    """Return the stable audit marker shared by every event for one PR."""
    if not isinstance(pr_number, int) or isinstance(pr_number, bool) or pr_number <= 0:
        raise ValueError("pull request number must be a positive integer")
    return f"<!-- ppds-release-record:pull-request:{pr_number} -->"


def parse_event(payload: dict[str, Any]) -> PatchPullRequestEvent:
    """Parse and validate the GitHub pull-request event fields we rely on."""
    try:
        pull_request = payload["pull_request"]
        number = pull_request.get("number", payload.get("number"))
        action = payload["action"]
        merged = pull_request["merged"]
        base_ref = pull_request["base"]["ref"]
        raw_labels = pull_request.get("labels", [])
        title = pull_request.get("title", "")
        repository = payload["repository"]["full_name"]
    except (KeyError, TypeError) as exc:
        raise ValueError(f"invalid pull-request event: missing {exc}") from exc

    if not isinstance(number, int) or isinstance(number, bool) or number <= 0:
        raise ValueError("invalid pull-request event: number must be a positive integer")
    if not isinstance(action, str):
        raise ValueError("invalid pull-request event: action must be a string")
    if not isinstance(merged, bool):
        raise ValueError("invalid pull-request event: merged must be a boolean")
    if not isinstance(base_ref, str):
        raise ValueError("invalid pull-request event: base ref must be a string")
    if not isinstance(title, str):
        raise ValueError("invalid pull-request event: title must be a string")
    if not isinstance(repository, str) or not _REPOSITORY_RE.fullmatch(repository):
        raise ValueError("invalid pull-request event: repository full_name is malformed")
    if not isinstance(raw_labels, list):
        raise ValueError("invalid pull-request event: labels must be a list")

    labels: set[str] = set()
    for label in raw_labels:
        if not isinstance(label, dict) or not isinstance(label.get("name"), str):
            raise ValueError("invalid pull-request event: every label needs a string name")
        labels.add(label["name"])

    event_label_value = payload.get("label")
    event_label: Optional[str] = None
    if event_label_value is not None:
        if not isinstance(event_label_value, dict) or not isinstance(
            event_label_value.get("name"), str
        ):
            raise ValueError("invalid pull-request event: event label needs a string name")
        event_label = event_label_value["name"]

    return PatchPullRequestEvent(
        action=action,
        number=number,
        merged=merged,
        base_ref=base_ref,
        labels=frozenset(labels),
        event_label=event_label,
        title=title,
        repository=repository,
    )


def event_is_eligible(event: PatchPullRequestEvent) -> bool:
    """Accept merge-time labels and a patch label added after merge."""
    if not event.merged or event.base_ref != "main":
        return False
    if event.action == "closed":
        return PATCH_LABEL in event.labels
    if event.action == "labeled":
        return event.event_label == PATCH_LABEL and PATCH_LABEL in event.labels
    return False


def find_existing_record(
    issues: Sequence[dict[str, Any]], marker: str
) -> Optional[dict[str, Any]]:
    """Find the permanent per-PR record regardless of open/closed state."""
    for issue in issues:
        if not isinstance(issue, dict):
            raise ValueError("existing issue data must contain objects")
        body = issue.get("body") or ""
        if not isinstance(body, str):
            raise ValueError("existing issue body must be a string or null")
        if marker in body:
            return issue
    return None


def build_issue_title(pr_number: int, release_targets: Sequence[str]) -> str:
    """Build a bounded title without including the untrusted PR title."""
    targets = [str(target) for target in release_targets]
    suffix = ", ".join(targets) if targets else "release scope review"
    return f"Patch release review for PR #{pr_number}: {suffix}"


def build_issue_body(
    event: PatchPullRequestEvent,
    plan_markdown: str,
) -> str:
    """Render untrusted PR text as escaped HTML and link the public runbook."""
    safe_title = html.escape(event.title, quote=True)
    runbook = (
        f"https://github.com/{event.repository}/blob/main/"
        "docs/RELEASE.md#release-scope-analysis"
    )
    return "\n".join(
        [
            event.marker,
            "",
            (
                "A PR labeled `release:patch` merged to main. Review this "
                "explained scope before taking any release action."
            ),
            "",
            f"**Merged PR:** [#{event.number}]({event.pull_request_url})",
            "**PR title (untrusted event text):**",
            f"<pre>{safe_title}</pre>",
            "",
            plan_markdown.rstrip(),
            "",
            "### Maintainer Checklist",
            "",
            "- [ ] Confirm direct changes, downstream deliverables, delivery tags, and MinVer prerequisites",
            "- [ ] Update CHANGELOGs for release targets",
            f"- [ ] Follow the public [release procedure]({runbook})",
            "- [ ] Verify every publish before closing this issue",
            "",
        ]
    )


def decide_patch_record(
    event: PatchPullRequestEvent,
    plan: dict[str, Any],
    existing_issues: Sequence[dict[str, Any]],
) -> dict[str, Any]:
    """Return a deterministic create/skip decision for this PR."""
    if not event_is_eligible(event):
        return {
            "should_create": False,
            "reason": "event-not-eligible",
            "marker": event.marker,
        }

    existing = find_existing_record(existing_issues, event.marker)
    if existing is not None:
        return {
            "should_create": False,
            "reason": "existing-record",
            "marker": event.marker,
            "existing_issue_number": existing.get("number"),
            "existing_issue_state": existing.get("state"),
        }

    if plan.get("release_needed") is not True:
        return {
            "should_create": False,
            "reason": "no-product-impact",
            "marker": event.marker,
        }

    targets = plan.get("release_targets")
    if not isinstance(targets, list) or not all(isinstance(item, str) for item in targets):
        raise ValueError("release plan must contain a string release_targets list")
    return {
        "should_create": True,
        "reason": "create-record",
        "marker": event.marker,
        "title": build_issue_title(event.number, targets),
    }


def _load_json(path: Path, description: str) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"could not read {description} from {path}: {exc}") from exc


def prepare(
    *,
    event_path: Path,
    plan_json_path: Path,
    plan_markdown_path: Path,
    existing_issues_path: Path,
    title_out: Path,
    body_out: Path,
    decision_out: Path,
) -> dict[str, Any]:
    """Prepare workflow files and return the create/skip decision."""
    payload = _load_json(event_path, "event JSON")
    plan = _load_json(plan_json_path, "release-plan JSON")
    existing_issues = _load_json(existing_issues_path, "existing-issues JSON")
    if not isinstance(payload, dict) or not isinstance(plan, dict):
        raise ValueError("event and release-plan JSON must contain objects")
    if not isinstance(existing_issues, list):
        raise ValueError("existing-issues JSON must contain a list")

    event = parse_event(payload)
    decision = decide_patch_record(event, plan, existing_issues)
    decision_out.write_text(
        json.dumps(decision, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    if decision["should_create"]:
        plan_markdown = plan_markdown_path.read_text(encoding="utf-8")
        title_out.write_text(decision["title"] + "\n", encoding="utf-8")
        body_out.write_text(build_issue_body(event, plan_markdown), encoding="utf-8")
    return decision


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Safely prepare one idempotent patch-release issue.",
    )
    parser.add_argument("--event", type=Path, required=True)
    parser.add_argument("--plan-json", type=Path, required=True)
    parser.add_argument("--plan-markdown", type=Path, required=True)
    parser.add_argument("--existing-issues", type=Path, required=True)
    parser.add_argument("--title-out", type=Path, required=True)
    parser.add_argument("--body-out", type=Path, required=True)
    parser.add_argument("--decision-out", type=Path, required=True)
    args = parser.parse_args(argv)

    try:
        decision = prepare(
            event_path=args.event,
            plan_json_path=args.plan_json,
            plan_markdown_path=args.plan_markdown,
            existing_issues_path=args.existing_issues,
            title_out=args.title_out,
            body_out=args.body_out,
            decision_out=args.decision_out,
        )
    except (OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    print(json.dumps(decision, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
