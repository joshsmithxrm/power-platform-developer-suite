#!/usr/bin/env python3
"""Reconcile stable CLI releases with co-located Extension releases.

The VS Code Extension bundles the CLI resolved from an exact ``Cli-v*`` tag
on the Extension release commit. Tag creation timestamps cannot prove that
relationship: the two tags are normally pushed one after another and either
event can run first. This helper therefore compares peeled tag commits, plans
deterministic issue actions, and optionally applies those actions.

All version parsing is delegated to the shared strict ``release_model.SemVer``
implementation. The helper never creates tags or invokes a release workflow.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable, Optional, Sequence

from release_model import SemVer


CORELEASE_LABEL = "release:extension-corelease"
MARKER_PREFIX = "ppds-extension-corelease:cli-tag:"
PUBLIC_RELEASE_RUNBOOK = (
    "https://github.com/joshsmithxrm/power-platform-developer-suite/"
    "blob/main/docs/RELEASE.md#cli-extension-co-release-reconciliation"
)
_MARKER_RE = re.compile(
    rf"<!--\s*{re.escape(MARKER_PREFIX)}(Cli-v[^\s<>]+)\s*-->",
)
_LEGACY_TAG_FIELD_RE = re.compile(
    r"^\|\s*Latest stable Cli tag\s*\|\s*`(Cli-v[^`\s<>]+)`\s*\|\s*$",
    re.MULTILINE,
)


@dataclass(frozen=True)
class ReleaseTag:
    """A release tag and the commit to which the tag ultimately points."""

    name: str
    commit: str


@dataclass(frozen=True)
class ExistingIssue:
    """The issue fields used to authenticate and reconcile an alert."""

    number: int
    state: str
    body: str
    labels: frozenset[str]


CommandRunner = Callable[[Sequence[str]], subprocess.CompletedProcess[str]]


def _tag_version(tag: str, prefix: str) -> Optional[SemVer]:
    if not tag.startswith(prefix):
        return None
    try:
        return SemVer.parse(tag[len(prefix):])
    except ValueError:
        return None


def is_stable_cli_tag(tag: str) -> bool:
    version = _tag_version(tag, "Cli-v")
    return version is not None and not version.is_prerelease


def _ranked_tags(
    tags: Iterable[ReleaseTag],
    prefix: str,
    *,
    stable_only: bool,
) -> tuple[list[ReleaseTag], list[str]]:
    ranked: list[tuple[SemVer, ReleaseTag]] = []
    diagnostics: list[str] = []
    for tag in tags:
        if not tag.name.startswith(prefix):
            continue
        try:
            version = SemVer.parse(tag.name[len(prefix):])
        except ValueError as exc:
            diagnostics.append(f"Malformed release tag {tag.name!r}: {exc}")
            continue
        if stable_only and version.is_prerelease:
            continue
        ranked.append((version, tag))
    ranked.sort(key=lambda item: (item[0], item[1].name))
    return [tag for _, tag in ranked], sorted(diagnostics)


def select_latest_stable_cli(tags: Iterable[ReleaseTag]) -> Optional[ReleaseTag]:
    ranked, _ = _ranked_tags(tags, "Cli-v", stable_only=True)
    return ranked[-1] if ranked else None


def select_latest_extension(tags: Iterable[ReleaseTag]) -> Optional[ReleaseTag]:
    # Both odd/even Extension channels bundle a CLI, so every strict Extension
    # SemVer can satisfy the relationship when it points to the CLI commit.
    ranked, _ = _ranked_tags(tags, "Extension-v", stable_only=False)
    return ranked[-1] if ranked else None


def issue_marker(cli_tag: str) -> str:
    if not is_stable_cli_tag(cli_tag):
        raise ValueError(f"Cannot build co-release marker for invalid stable tag {cli_tag!r}")
    return f"<!-- {MARKER_PREFIX}{cli_tag} -->"


def marker_cli_tag(issue: ExistingIssue) -> Optional[str]:
    """Return the owned CLI key from a current marker or exact legacy field.

    The label is required for both formats. Before stable markers existed, the
    workflow emitted an exact ``Latest stable Cli tag`` table row. Recognizing
    that narrowly modeled row migrates open and closed audit records without
    treating arbitrary prose as workflow-owned state.
    """
    if CORELEASE_LABEL not in issue.labels:
        return None
    for pattern in (_MARKER_RE, _LEGACY_TAG_FIELD_RE):
        match = pattern.search(issue.body)
        if match is not None and is_stable_cli_tag(match.group(1)):
            return match.group(1)
    return None


def build_flag_message(cli_tag: str) -> str:
    return (
        f"Extension bundled-CLI refresh missing for {cli_tag} "
        "(no co-located Extension-v* tag)"
    )


def build_issue_body(
    cli_tag: str,
    latest_extension_tag: Optional[str],
) -> str:
    ext_display = f"`{latest_extension_tag}`" if latest_extension_tag else "_none tagged yet_"
    return f"""\
{issue_marker(cli_tag)}
## Extension Bundled-CLI Refresh Missing

`{cli_tag}` does not have an `Extension-v*` tag pointing to the same commit. \
The VS Code Extension publisher resolves and bundles the exact co-located CLI \
tag, so tag timestamps or a newer Extension version on another commit do not \
prove that marketplace users received this CLI.

| Field | Value |
|-------|-------|
| Stable CLI release key | `{cli_tag}` |
| Highest Extension version seen | {ext_display} |
| Required relationship | CLI and Extension tags point to the same commit |

## Options for the Maintainer

- **Cut the refresh** — follow the public release procedure to create an \
  `Extension-v*` tag on the `{cli_tag}` commit
- **Opt out with a reason** — comment the reason on this issue and close it

The workflow will comment and close this alert automatically if a co-located \
Extension tag arrives later. A higher stable CLI release supersedes older open \
alerts so the current release train is the only actionable one.

> **Procedure:** See [CLI/Extension co-release reconciliation]({PUBLIC_RELEASE_RUNBOOK}).
"""


def _co_located_extension(
    cli_tag: ReleaseTag,
    extension_tags: Iterable[ReleaseTag],
) -> Optional[ReleaseTag]:
    matching = [tag for tag in extension_tags if tag.commit == cli_tag.commit]
    return select_latest_extension(matching)


def _close_action(
    issue: ExistingIssue,
    cli_tag: str,
    reason: str,
    comment: str,
) -> dict:
    return {
        "kind": "close",
        "issue_number": issue.number,
        "cli_tag": cli_tag,
        "reason": reason,
        "comment": comment,
    }


def reconcile_corelease(
    *,
    cli_tags: Iterable[ReleaseTag],
    extension_tags: Iterable[ReleaseTag],
    existing_issues: Iterable[ExistingIssue],
) -> dict:
    """Return a deterministic plan that converges for either tag event order."""
    cli_tags = list(cli_tags)
    extension_tags = list(extension_tags)
    existing_issues = list(existing_issues)

    ranked_cli, cli_diagnostics = _ranked_tags(
        cli_tags,
        "Cli-v",
        stable_only=True,
    )
    ranked_extension, extension_diagnostics = _ranked_tags(
        extension_tags,
        "Extension-v",
        stable_only=False,
    )
    diagnostics = cli_diagnostics + extension_diagnostics
    latest_cli = ranked_cli[-1] if ranked_cli else None
    latest_extension = ranked_extension[-1] if ranked_extension else None
    cli_by_name = {tag.name: tag for tag in ranked_cli}

    records: dict[str, list[ExistingIssue]] = {}
    for issue in existing_issues:
        cli_name = marker_cli_tag(issue)
        if cli_name is not None:
            records.setdefault(cli_name, []).append(issue)

    actions: list[dict] = []
    handled_open_issue_numbers: set[int] = set()

    for cli_name, issues in sorted(records.items()):
        open_issues = sorted(
            (issue for issue in issues if issue.state.casefold() == "open"),
            key=lambda issue: issue.number,
        )
        if not open_issues:
            continue

        tagged_cli = cli_by_name.get(cli_name)
        satisfying_extension = (
            _co_located_extension(tagged_cli, ranked_extension)
            if tagged_cli is not None
            else None
        )
        if satisfying_extension is not None:
            for issue in open_issues:
                actions.append(_close_action(
                    issue,
                    cli_name,
                    "satisfied",
                    (
                        f"Automated reconciliation: `{satisfying_extension.name}` now "
                        f"points to the same commit as `{cli_name}`. Closing this "
                        "workflow-owned co-release alert."
                    ),
                ))
                handled_open_issue_numbers.add(issue.number)
            continue

        cli_version = _tag_version(cli_name, "Cli-v")
        latest_version = (
            _tag_version(latest_cli.name, "Cli-v")
            if latest_cli is not None
            else None
        )
        if (
            latest_cli is not None
            and cli_version is not None
            and latest_version is not None
            and cli_version < latest_version
        ):
            for issue in open_issues:
                actions.append(_close_action(
                    issue,
                    cli_name,
                    "superseded",
                    (
                        f"Automated reconciliation: `{latest_cli.name}` is now the "
                        f"highest stable CLI release, so the older `{cli_name}` alert "
                        "is superseded. Closing this workflow-owned alert."
                    ),
                ))
                handled_open_issue_numbers.add(issue.number)

    if latest_cli is None:
        reason = "no stable CLI tag"
    else:
        satisfying_extension = _co_located_extension(latest_cli, ranked_extension)
        current_records = records.get(latest_cli.name, [])
        current_open = sorted(
            (
                issue for issue in current_records
                if issue.state.casefold() == "open"
                and issue.number not in handled_open_issue_numbers
            ),
            key=lambda issue: issue.number,
        )
        if satisfying_extension is not None:
            reason = "current CLI has co-located Extension tag"
        elif current_records:
            reason = "current CLI alert already recorded"
            # Corrupted/manual duplicates preserve the oldest permanent record
            # across both states. If that record is closed, every later open
            # duplicate closes instead of reviving the recorded opt-out.
            canonical = min(current_records, key=lambda issue: issue.number)
            for duplicate in current_open:
                if duplicate.number == canonical.number:
                    continue
                actions.append(_close_action(
                    duplicate,
                    latest_cli.name,
                    "duplicate",
                    (
                        f"Automated reconciliation: issue #{canonical.number} "
                        f"is the canonical workflow-owned alert for `{latest_cli.name}`. "
                        "Closing this duplicate."
                    ),
                ))
        else:
            reason = "current CLI is missing a co-located Extension tag"
            actions.append({
                "kind": "create",
                "cli_tag": latest_cli.name,
                "reason": "missing",
                "title": build_flag_message(latest_cli.name),
                "body": build_issue_body(
                    latest_cli.name,
                    latest_extension.name if latest_extension else None,
                ),
            })

    # A replacement is created before its superseded predecessor is closed.
    # If GitHub rejects the create, the old actionable alert remains visible
    # and the failed tag run can be rerun safely.
    actions.sort(key=lambda action: (
        0 if action["kind"] == "create" else 1,
        int(action.get("issue_number", 0)),
        action["cli_tag"],
    ))
    return {
        "latest_cli_tag": latest_cli.name if latest_cli else None,
        "latest_extension_tag": latest_extension.name if latest_extension else None,
        "reason": reason,
        "actions": actions,
        "diagnostics": diagnostics,
    }


def collect_tags(prefix: str) -> list[ReleaseTag]:
    """Collect lightweight or annotated tags and their peeled commits."""
    result = subprocess.run(
        [
            "git",
            "for-each-ref",
            "--format=%(refname:short)%09%(objectname)%09%(*objectname)",
            f"refs/tags/{prefix}-v*",
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"git for-each-ref for {prefix}-v* failed "
            f"(exit {result.returncode}): {result.stderr.strip()}"
        )
    tags: list[ReleaseTag] = []
    for raw_line in result.stdout.splitlines():
        fields = raw_line.split("\t")
        if len(fields) != 3:
            continue
        name, object_name, peeled_name = (field.strip() for field in fields)
        commit = peeled_name or object_name
        if name and commit:
            tags.append(ReleaseTag(name=name, commit=commit))
    return tags


def load_existing_issues(path: Path) -> list[ExistingIssue]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(payload, list):
        raise ValueError("Existing issue input must be a JSON array")
    issues: list[ExistingIssue] = []
    for item in payload:
        labels = frozenset(
            label.get("name", "") if isinstance(label, dict) else str(label)
            for label in item.get("labels", [])
        )
        issues.append(ExistingIssue(
            number=int(item["number"]),
            state=str(item["state"]),
            body=str(item.get("body") or ""),
            labels=labels,
        ))
    return issues


def _default_runner(args: Sequence[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        list(args),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def _run_checked(runner: CommandRunner, args: Sequence[str]) -> None:
    result = runner(args)
    if result.returncode != 0:
        raise RuntimeError(
            f"Command failed ({result.returncode}): {' '.join(args)}\n"
            f"{result.stderr.strip()}"
        )


def apply_plan(plan: dict, repo: str, *, runner: CommandRunner = _default_runner) -> None:
    """Apply issue-only reconciliation actions; fail loudly on every error."""
    with tempfile.TemporaryDirectory(prefix="ppds-corelease-") as temp_dir:
        temp_root = Path(temp_dir)
        for index, action in enumerate(plan["actions"]):
            if action["kind"] == "close":
                comment_path = temp_root / f"close-{index}.md"
                comment_path.write_text(action["comment"] + "\n", encoding="utf-8")
                _run_checked(runner, [
                    "gh", "issue", "comment", str(action["issue_number"]),
                    "--repo", repo,
                    "--body-file", str(comment_path),
                ])
                _run_checked(runner, [
                    "gh", "issue", "close", str(action["issue_number"]),
                    "--repo", repo,
                    "--reason", "completed",
                ])
            elif action["kind"] == "create":
                body_path = temp_root / f"create-{index}.md"
                body_path.write_text(action["body"], encoding="utf-8")
                _run_checked(runner, [
                    "gh", "issue", "create",
                    "--repo", repo,
                    "--title", action["title"],
                    "--label", CORELEASE_LABEL,
                    "--body-file", str(body_path),
                ])
            else:
                raise ValueError(f"Unknown reconciliation action {action['kind']!r}")


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Plan or apply CLI/Extension co-release issue reconciliation.",
    )
    parser.add_argument("--existing-issues", type=Path, required=True)
    parser.add_argument("--repo", help="GitHub OWNER/REPO; required with --apply")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args(argv)
    if args.apply and not args.repo:
        parser.error("--repo is required with --apply")

    plan = reconcile_corelease(
        cli_tags=collect_tags("Cli"),
        extension_tags=collect_tags("Extension"),
        existing_issues=load_existing_issues(args.existing_issues),
    )
    if args.apply:
        apply_plan(plan, args.repo)
    print(json.dumps(plan, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
