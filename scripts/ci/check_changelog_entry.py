#!/usr/bin/env python3
"""A behaviour change carries its changelog entry and a breaking statement (#1789).

A pull request that changes a package under ``src/`` must, in the same pull
request:

1. add an entry to that package's ``src/<package>/CHANGELOG.md`` under the
   package's ``## [X.Y.Z] - Unreleased`` section (not the empty
   ``## [Unreleased]`` section; see AGENTS.md), and
2. say in its description whether it is breaking, in the "Breaking changes"
   section of the pull request template (or a ``Breaking: yes/no`` line).

The statement must agree with the ``breaking-change`` label, because the label
is what the release plan reads (``scripts/ci/release_plan.py
--breaking-prs-file``) to refuse a non-major version. A breaking pull request's
changelog entry must say "breaking", so the release notes say it too.

Not checked:

- pull requests labelled ``internal`` (no user-visible change);
- Dependabot pull requests (a bot cannot write the entry or the statement;
  major updates are covered by Rule 3 of the Pre-Merge Gate);
- changed files under ``src/`` that the release plan also treats as
  non-product (tests, fixtures, ``*.md`` documentation; see
  ``release_model._is_deterministic_non_product``) and the package's own
  CHANGELOG;
- paths under ``src/`` that belong to no package with a CHANGELOG
  (``src/PPDS.Analyzers`` is a build-time analyzer, never shipped).

Generated code (for example ``src/PPDS.Dataverse/Generated/``) ships in the
package, so it is checked like hand-written code.

The check is not a required status check. It reads only its inputs; it never
calls GitHub.

Usage:
    # In GitHub Actions: labels, body and author from the event payload, the
    # changed files and their contents from git.
    python scripts/ci/check_changelog_entry.py --event "$GITHUB_EVENT_PATH"

    # Without GitHub: everything from one JSON file
    #   {"files": [{"path": ..., "before": ..., "after": ...}],
    #    "labels": [...], "body": "...", "author": "..."}
    python scripts/ci/check_changelog_entry.py --input-json pr.json

Exit codes:
    0 - passed, or not applicable
    1 - a rule failed
    2 - the input could not be read
"""
from __future__ import annotations

import argparse
import difflib
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Optional, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_model import _is_deterministic_non_product  # noqa: E402

INTERNAL_LABEL = "internal"
BREAKING_LABEL = "breaking-change"
DEPENDABOT_AUTHORS = frozenset({"dependabot[bot]", "app/dependabot", "dependabot"})

SRC_PACKAGE_RE = re.compile(r"^src/(?P<package>[^/]+)/")
VERSION_UNRELEASED_RE = re.compile(
    r"^## \[(?P<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\] - Unreleased\s*$"
)
EMPTY_UNRELEASED_RE = re.compile(r"^## \[Unreleased\]\s*$", re.IGNORECASE)
SECTION_HEADING_RE = re.compile(r"^## ")
BREAKING_HEADING_RE = re.compile(r"^#{2,3}\s+Breaking(\s+changes?)?\s*:?\s*$", re.IGNORECASE)
BREAKING_LINE_RE = re.compile(r"^\s*[-*]?\s*\**Breaking\**\s*:\**\s*(?P<rest>.*)$", re.IGNORECASE)
HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)
NOT_BREAKING_RE = re.compile(r"^(none|no|n/a|not breaking)\b", re.IGNORECASE)
BREAKING_RE = re.compile(r"^yes\b", re.IGNORECASE)


@dataclass(frozen=True)
class FileChange:
    path: str
    before: Optional[str] = None
    after: Optional[str] = None


@dataclass
class Result:
    failures: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    @property
    def passed(self) -> bool:
        return not self.failures


def changelog_path(package: str) -> str:
    return f"src/{package}/CHANGELOG.md"


def non_behaviour_reason(path: str, package: str) -> Optional[str]:
    """Return why ``path`` is not a behaviour change, or None if it is one.

    Uses the release plan's own list of deterministic non-product changes, so
    the two checks agree on what needs a release and what needs an entry.
    """
    if path == changelog_path(package):
        return "package changelog"
    return _is_deterministic_non_product(path)


def behaviour_packages(
    paths: Iterable[str], packages_with_changelog: Iterable[str],
) -> tuple[dict[str, list[str]], list[str]]:
    """Map each package to its behaviour-changing paths.

    Returns ``(packages, notes)``. Paths outside ``src/`` are ignored.
    """
    known = set(packages_with_changelog)
    found: dict[str, list[str]] = {}
    notes: list[str] = []
    for path in sorted(set(paths)):
        match = SRC_PACKAGE_RE.match(path)
        if not match:
            continue
        package = match.group("package")
        if package not in known:
            notes.append(f"{path}: no package CHANGELOG for src/{package}; not checked.")
            continue
        reason = non_behaviour_reason(path, package)
        if reason:
            notes.append(f"{path}: {reason}; not a behaviour change.")
            continue
        found.setdefault(package, []).append(path)
    return found, notes


def _section_of(lines: Sequence[str], index: int) -> str:
    """Return the ``## `` heading line above ``lines[index]`` ('' if none)."""
    for i in range(index, -1, -1):
        if SECTION_HEADING_RE.match(lines[i]):
            return lines[i].rstrip()
    return ""


def added_entries(before: Optional[str], after: Optional[str]) -> list[tuple[str, str]]:
    """Return ``(section heading, line)`` for each added non-blank, non-heading line."""
    old = (before or "").splitlines()
    new = (after or "").splitlines()
    added: list[tuple[str, str]] = []
    matcher = difflib.SequenceMatcher(a=old, b=new, autojunk=False)
    for tag, _i1, _i2, j1, j2 in matcher.get_opcodes():
        if tag not in {"insert", "replace"}:
            continue
        for j in range(j1, j2):
            line = new[j]
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            added.append((_section_of(new, j), line))
    return added


def check_changelog(package: str, change: Optional[FileChange]) -> tuple[Optional[str], list[str]]:
    """Return ``(failure or None, entry lines under the version section)``."""
    path = changelog_path(package)
    if change is None:
        return (
            f"{package}: behaviour changed but {path} was not changed. Add an entry "
            "under the package's `## [X.Y.Z] - Unreleased` section, or label the "
            f"pull request `{INTERNAL_LABEL}` if nothing user-visible changed.",
            [],
        )
    entries = added_entries(change.before, change.after)
    in_version = [line for heading, line in entries if VERSION_UNRELEASED_RE.match(heading)]
    if in_version:
        return None, in_version
    if any(EMPTY_UNRELEASED_RE.match(heading) for heading, _ in entries):
        return (
            f"{package}: the entry in {path} is under the empty `## [Unreleased]` "
            "section. Move it under the package's `## [X.Y.Z] - Unreleased` "
            "section (see AGENTS.md).",
            [],
        )
    elsewhere = sorted({heading or "(no section)" for heading, _ in entries})
    if elsewhere:
        return (
            f"{package}: {path} changed, but no entry was added under a "
            f"`## [X.Y.Z] - Unreleased` section (added under: {', '.join(elsewhere)}).",
            [],
        )
    return (
        f"{package}: {path} changed, but no entry line was added. Add an entry "
        "under the package's `## [X.Y.Z] - Unreleased` section.",
        [],
    )


def breaking_statement(body: Optional[str]) -> Optional[str]:
    """Return ``'yes'``, ``'no'`` or None (no statement) from a PR description.

    Reads a ``Breaking:`` line first, then the "Breaking changes" section of
    the pull request template. HTML comments (the template's prompts) do not
    count. Text in the section other than "None"/"No" lists breaking changes,
    so it reads as ``'yes'``.
    """
    text = HTML_COMMENT_RE.sub("", body or "").replace("\r\n", "\n")
    lines = text.split("\n")
    for line in lines:
        match = BREAKING_LINE_RE.match(line)
        if match and not BREAKING_HEADING_RE.match(line):
            verdict = _classify(match.group("rest"))
            if verdict:
                return verdict
    for index, line in enumerate(lines):
        if not BREAKING_HEADING_RE.match(line.strip()):
            continue
        section: list[str] = []
        for following in lines[index + 1:]:
            if re.match(r"^#{1,3}\s", following):
                break
            if following.strip():
                section.append(following.strip())
        if section:
            return _classify(" ".join(section)) or "yes"
    return None


def _classify(text: str) -> Optional[str]:
    cleaned = re.sub(r"^[\s\-*>`_]+", "", text).strip().strip("*_`\"'").strip()
    if not cleaned:
        return None
    if NOT_BREAKING_RE.match(cleaned):
        return "no"
    if BREAKING_RE.match(cleaned):
        return "yes"
    return None


def evaluate(
    changes: Sequence[FileChange],
    *,
    labels: Iterable[str],
    body: Optional[str],
    author: Optional[str],
    packages_with_changelog: Iterable[str],
) -> Result:
    result = Result()
    label_set = {label.strip() for label in labels if label and label.strip()}
    by_path = {change.path: change for change in changes}

    if (author or "").strip().lower() in DEPENDABOT_AUTHORS:
        result.notes.append("Dependabot pull request: not checked.")
        return result

    packages, notes = behaviour_packages(by_path, packages_with_changelog)
    result.notes.extend(notes)

    if INTERNAL_LABEL in label_set:
        if BREAKING_LABEL in label_set:
            result.failures.append(
                f"The pull request is labelled both `{INTERNAL_LABEL}` and "
                f"`{BREAKING_LABEL}`; a breaking change is user-visible. Remove one."
            )
        else:
            result.notes.append(f"Labelled `{INTERNAL_LABEL}`: not checked.")
        return result

    if not packages and BREAKING_LABEL not in label_set:
        result.notes.append("No behaviour change under src/: not checked.")
        return result

    entry_lines: list[str] = []
    for package in sorted(packages):
        failure, lines = check_changelog(package, by_path.get(changelog_path(package)))
        if failure:
            result.failures.append(failure)
        else:
            result.notes.append(f"{package}: changelog entry found.")
        entry_lines.extend(lines)

    statement = breaking_statement(body)
    labelled = BREAKING_LABEL in label_set
    if statement is None:
        result.failures.append(
            "The description has no breaking statement. Fill in the \"Breaking "
            "changes\" section: \"None\", or the breaking change and how to migrate."
        )
    elif statement == "yes" and not labelled:
        result.failures.append(
            f"The description states a breaking change but the pull request is not "
            f"labelled `{BREAKING_LABEL}`. Add the label: the release plan reads it "
            "to refuse a non-major version."
        )
    elif statement == "no" and labelled:
        result.failures.append(
            f"The pull request is labelled `{BREAKING_LABEL}` but the description "
            "says it is not breaking. Correct one of them."
        )
    else:
        result.notes.append(f"Breaking statement: {statement}.")

    if statement == "yes" and packages and entry_lines and not any(
        "breaking" in line.lower() for line in entry_lines
    ):
        result.failures.append(
            "The change is breaking but no changelog entry says so. Start the entry "
            "with \"**Breaking:**\" so the release notes say it."
        )
    return result


# ---------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------

def discover_packages(repo_root: Path) -> list[str]:
    return sorted(path.parent.name for path in (repo_root / "src").glob("*/CHANGELOG.md"))


def _git(repo_root: Path, *args: str, allow_missing: bool = False) -> Optional[str]:
    completed = subprocess.run(
        ["git", *args], cwd=repo_root, capture_output=True, text=True,
        encoding="utf-8", errors="replace",
    )
    if completed.returncode == 0:
        return completed.stdout
    if allow_missing:
        return None
    raise RuntimeError(f"git {' '.join(args)} failed: {completed.stderr.strip()}")


def changes_from_git(repo_root: Path, base: str, head: str) -> list[FileChange]:
    """Changed files of ``head`` since its merge base with ``base``."""
    merge_base = (_git(repo_root, "merge-base", base, head) or "").strip()
    names = _git(repo_root, "diff", "--name-only", "--no-renames", merge_base, head) or ""
    changes = []
    for path in (line.strip() for line in names.splitlines() if line.strip()):
        if path.endswith("CHANGELOG.md"):
            changes.append(FileChange(
                path=path,
                before=_git(repo_root, "show", f"{merge_base}:{path}", allow_missing=True),
                after=_git(repo_root, "show", f"{head}:{path}", allow_missing=True),
            ))
        else:
            changes.append(FileChange(path=path))
    return changes


def read_event(path: Path) -> dict:
    event = json.loads(path.read_text(encoding="utf-8"))
    pr = event.get("pull_request")
    if not isinstance(pr, dict):
        raise ValueError("event payload has no pull_request")
    return {
        "labels": [label.get("name", "") for label in pr.get("labels") or []],
        "body": pr.get("body") or "",
        "author": (pr.get("user") or {}).get("login", ""),
        "base": (pr.get("base") or {}).get("sha", ""),
        "head": (pr.get("head") or {}).get("sha", ""),
    }


def write_summary(text: str) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    try:
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")
    except OSError as error:
        print(f"::warning::could not write step summary: {error}")


def report(result: Result) -> None:
    lines = ["### Changelog entry and breaking statement (#1789)", ""]
    for failure in result.failures:
        print(f"::error title=Changelog check::{failure}")
        lines.append(f"- FAIL: {failure}")
    for note in result.notes:
        print(note)
        lines.append(f"- {note}")
    verdict = "passed" if result.passed else "failed"
    print(f"Changelog check {verdict}.")
    lines.extend(["", f"Result: {verdict}. This check is not required."])
    write_summary("\n".join(lines))


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--event", type=Path, help="GitHub pull_request event payload")
    source.add_argument("--input-json", type=Path, help="files, labels, body and author as JSON")
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args(argv)
    repo_root = args.repo_root.resolve()

    try:
        if args.event:
            event = read_event(args.event)
            changes = changes_from_git(repo_root, event["base"], event["head"])
            labels, body, author = event["labels"], event["body"], event["author"]
        else:
            data = json.loads(args.input_json.read_text(encoding="utf-8"))
            changes = [
                FileChange(path=item["path"], before=item.get("before"), after=item.get("after"))
                for item in data.get("files", [])
            ]
            labels, body, author = data.get("labels", []), data.get("body", ""), data.get("author", "")
        packages = discover_packages(repo_root)
    except (OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
        print(f"::error title=Changelog check::could not read the input: {error}", file=sys.stderr)
        return 2

    result = evaluate(
        changes, labels=labels, body=body, author=author,
        packages_with_changelog=packages,
    )
    report(result)
    return 0 if result.passed else 1


if __name__ == "__main__":
    sys.exit(main())
