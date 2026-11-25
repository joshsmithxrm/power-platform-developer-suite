#!/usr/bin/env python3
"""A behaviour change carries its changelog entry and a breaking statement (#1789).

A pull request that changes a package under ``src/`` must, in the same pull
request:

1. add an entry to that package's ``src/<package>/CHANGELOG.md`` under the
   package's ``## [X.Y.Z] - Unreleased`` section (not the empty
   ``## [Unreleased]`` section; see AGENTS.md), and
2. say in its description whether it is breaking: a ``Breaking: yes`` or
   ``Breaking: no`` line, or the "Breaking changes" section of the pull
   request template.

The statement must agree with the ``breaking-change`` label, because the label
is what the release plan reads (``scripts/ci/release_plan.py
--breaking-prs-file``) to refuse a non-major version. A breaking pull request's
changelog entry must say "breaking", so the release notes say it too.

**Reading the statement.** The statement is the whole "Breaking changes"
section (HTML comments and a trailing "Generated with …" footer removed), or
else the rest of a ``Breaking:`` line. It is "not breaking" only if it is
exactly ``No``, ``None``, ``N/A`` or ``Not breaking``, in any case, with
optional emphasis marks or backticks and an optional final full stop, and
nothing else. Any further text, after any separator, makes it not a "no": it
is a statement of a break if it starts with ``Yes`` or the pull request
carries the ``breaking-change`` label, and otherwise it is rejected as
unclear, with a message that says how to write it. A reason for "no" belongs
in the description's other sections.

Not checked:

- pull requests labelled ``internal`` (no user-visible change). The label
  exists and is the maintainer's to apply;
- Dependabot pull requests (a bot cannot write the entry or the statement;
  major updates are covered by Rule 3 of the Pre-Merge Gate);
- files at the known test locations under ``src/`` (``NON_BEHAVIOUR_PATHS``),
  ``*.md``/``*.mdx`` documentation, and the package's own CHANGELOG;
- paths under ``src/`` that belong to no package with a CHANGELOG
  (``src/PPDS.Analyzers`` is a build-time analyzer, never shipped).

Generated code (for example ``src/PPDS.Dataverse/Generated/``) ships in the
package, so it is checked like hand-written code. A package whose CHANGELOG the
pull request deletes is still checked. A revert that only removes its own
entry fails and needs the ``internal`` exemption; the message says so.

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

INTERNAL_LABEL = "internal"
BREAKING_LABEL = "breaking-change"
DEPENDABOT_AUTHORS = frozenset({"dependabot[bot]", "app/dependabot", "dependabot"})

# Files under src/ that are not a behaviour change. Tied to where tests live
# today, so a product folder named "Test" or "fixtures" is still checked.
NON_BEHAVIOUR_PATHS: tuple[tuple[re.Pattern[str], str], ...] = (
    (re.compile(r"^src/PPDS\.Extension/src/(?:.+/)?__tests__/"), "test file"),
    (re.compile(r"^src/PPDS\.Extension/e2e/"), "end-to-end test file"),
    (re.compile(r"^src/PPDS\.Extension/tools/[^/]+\.test\.mjs$"), "test file"),
    (re.compile(r"^src/PPDS\.Extension/(?:tsconfig\.test\.json|vitest\.config\.ts)$"), "test configuration"),
    (re.compile(r"\.mdx?$", re.IGNORECASE), "documentation"),
)

SRC_PACKAGE_RE = re.compile(r"^src/(?P<package>[^/]+)/")
CHANGELOG_RE = re.compile(r"^src/(?P<package>[^/]+)/CHANGELOG\.md$")
VERSION_UNRELEASED_RE = re.compile(
    r"^## \[(?P<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\] - Unreleased\s*$"
)
EMPTY_UNRELEASED_RE = re.compile(r"^## \[Unreleased\]\s*$", re.IGNORECASE)
SECTION_HEADING_RE = re.compile(r"^## ")
BREAKING_HEADING_RE = re.compile(r"^#{2,3}\s+Breaking(\s+changes?)?\s*:?\s*$", re.IGNORECASE)
BREAKING_LINE_RE = re.compile(r"^\s*[-*]?\s*\**Breaking\**\s*:\**\s*(?P<rest>.*)$", re.IGNORECASE)
HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)
# "Not breaking" is only one of these plain forms and nothing else; any more
# text, after any separator, is not a "no". No pattern judges a reason.
NOT_BREAKING_RE = re.compile(r"^(?:no|none|n/a|not breaking)\.?$", re.IGNORECASE)
BREAKING_RE = re.compile(r"^yes\b", re.IGNORECASE)
# The assisted-pull-request footer that ends a description; it is not part of
# the last section. Only these exact forms are recognised.
FOOTER_RES = (
    re.compile(r"^🤖 Generated with \[Claude Code\]\(https://claude\.com/claude-code\)$"),
    re.compile(r"^Generated with \[Claude Code\]\(https://claude\.com/claude-code\)$"),
    re.compile(r"^🤖 Generated with Claude Code$"),
)
HOW_TO_STATE = (
    'Write "Breaking: no" alone (a reason belongs in the other sections of the '
    'description), or "Breaking: yes" followed by what breaks and how to migrate, '
    f"and label the pull request `{BREAKING_LABEL}`."
)
INTERNAL_HINT = (
    "If nothing user-visible changed, the maintainer can exempt the pull request "
    f"with the `{INTERNAL_LABEL}` label (see docs/MERGE-POLICY.md)."
)


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
    """Return why ``path`` is not a behaviour change, or None if it is one."""
    if path == changelog_path(package):
        return "package changelog"
    for pattern, reason in NON_BEHAVIOUR_PATHS:
        if pattern.search(path):
            return reason
    return None


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


def is_entry_text(line: str) -> bool:
    """A changelog line with content: not blank, a heading, a bare marker or a comment."""
    text = HTML_COMMENT_RE.sub("", line).strip()
    if not text or text.startswith("#"):
        return False
    return bool(re.sub(r"^(?:[-*+]|\d+[.)])\s*", "", text).strip(" -*+"))


def changed_lines(before: Optional[str], after: Optional[str]) -> tuple[list[tuple[str, str]], list[str]]:
    """Return (added entry lines with their section heading, removed entry lines)."""
    old = (before or "").splitlines()
    new = (after or "").splitlines()
    added: list[tuple[str, str]] = []
    removed: list[str] = []
    matcher = difflib.SequenceMatcher(a=old, b=new, autojunk=False)
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag in {"delete", "replace"}:
            removed.extend(line for line in old[i1:i2] if is_entry_text(line))
        if tag in {"insert", "replace"}:
            added.extend((_section_of(new, j), new[j]) for j in range(j1, j2) if is_entry_text(new[j]))
    return added, removed


def added_entries(before: Optional[str], after: Optional[str]) -> list[tuple[str, str]]:
    return changed_lines(before, after)[0]


def check_changelog(package: str, change: Optional[FileChange]) -> tuple[Optional[str], list[str]]:
    """Return ``(failure or None, entry lines under the version section)``."""
    path = changelog_path(package)
    if change is None:
        return (
            f"{package}: behaviour changed but {path} was not changed. Add an entry "
            f"under the package's `## [X.Y.Z] - Unreleased` section. {INTERNAL_HINT}",
            [],
        )
    if change.after is None:
        return (f"{package}: the pull request deletes {path}; the package's changes still need an entry.", [])
    entries, removed = changed_lines(change.before, change.after)
    in_version = [line for heading, line in entries if VERSION_UNRELEASED_RE.match(heading)]
    if in_version:
        return None, in_version
    has_version_section = any(VERSION_UNRELEASED_RE.match(line) for line in change.after.splitlines())
    if not has_version_section:
        return (
            f"{package}: {path} has no `## [X.Y.Z] - Unreleased` section yet, so there is "
            "nowhere correct for the entry. A maintainer step is missing: the maintainer "
            "adds the package's next `## [X.Y.Z] - Unreleased` section (release runbook, "
            "\"Open the next version\"). Ask for it; do not invent a version.",
            [],
        )
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
    if removed:
        return (
            f"{package}: {path} only removes entries. If this reverts a change that was "
            f"never released, no entry is needed: {INTERNAL_HINT} Otherwise add an entry "
            "that says what the revert changes for users.",
            [],
        )
    return (
        f"{package}: {path} changed, but no entry line was added. Add an entry "
        "under the package's `## [X.Y.Z] - Unreleased` section.",
        [],
    )


def strip_footer(text: str) -> str:
    """Drop a recognised "Generated with …" footer from the end of a description."""
    lines = text.rstrip().split("\n")
    while lines and not lines[-1].strip():
        lines.pop()
    if lines and any(pattern.match(lines[-1].strip()) for pattern in FOOTER_RES):
        lines.pop()
    return "\n".join(lines)


def breaking_statement(body: Optional[str]) -> Optional[str]:
    """Return ``'yes'``, ``'no'``, ``'unclear'`` or None (no statement).

    The statement is the whole text of the "Breaking changes" section (HTML
    comments and a trailing "Generated with …" footer removed, a leading
    ``Breaking:`` label allowed). Without that section, it is the rest of a
    ``Breaking:`` line. Only a plain "No", "None", "N/A" or "Not breaking"
    is ``'no'``; text that starts with "Yes" is ``'yes'``; anything else is
    ``'unclear'``.
    """
    text = strip_footer(HTML_COMMENT_RE.sub("", body or "").replace("\r\n", "\n"))
    lines = text.split("\n")
    section_verdict: Optional[str] = None
    in_section: set[int] = set()
    for index, line in enumerate(lines):
        if not BREAKING_HEADING_RE.match(line.strip()):
            continue
        section: list[str] = []
        for offset, following in enumerate(lines[index + 1:], start=index + 1):
            if re.match(r"^#{1,6}\s", following):
                break
            in_section.add(offset)
            if following.strip():
                section.append(following.strip())
        if section:
            first = BREAKING_LINE_RE.match(section[0])
            if first:
                section[0] = first.group("rest")
            section_verdict = classify(" ".join(section).strip())
        break
    line_verdicts = [
        classify(match.group("rest"))
        for index, line in enumerate(lines)
        if index not in in_section
        and (match := BREAKING_LINE_RE.match(line)) and not BREAKING_HEADING_RE.match(line)
    ]
    # A "Breaking: yes" anywhere wins: the safe direction.
    if "yes" in line_verdicts:
        return "yes"
    if section_verdict:
        return section_verdict
    return next((verdict for verdict in line_verdicts if verdict), None)


def classify(text: str) -> Optional[str]:
    """Plain "no" forms, emphasis and backticks allowed; "Yes …"; or unclear."""
    cleaned = re.sub(r"^[\s\-*>]+", "", text).strip()
    plain = re.sub(r"[*_`]", "", cleaned).strip()
    if not plain:
        return None
    if NOT_BREAKING_RE.match(plain):
        return "no"
    if BREAKING_RE.match(plain):
        return "yes"
    return "unclear"


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

    labelled = BREAKING_LABEL in label_set
    if not packages and not labelled:
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
    if statement == "unclear" and labelled:
        # Free text with the label is a description of the break.
        statement = "yes"
    if statement is None:
        result.failures.append(f"The description has no breaking statement. {HOW_TO_STATE}")
    elif statement == "unclear":
        result.failures.append(
            "The breaking statement is unclear: it is neither a plain \"No\"/\"None\" nor "
            f"starts with \"Yes\", and the pull request has no `{BREAKING_LABEL}` label. "
            + HOW_TO_STATE
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
        re.search(r"(?<!non-)(?<!non )\bbreaking\b", line, re.IGNORECASE) for line in entry_lines
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


def packages_at(repo_root: Path, *revisions: str) -> list[str]:
    """Packages with a CHANGELOG at any of ``revisions``."""
    found: set[str] = set()
    for revision in revisions:
        listing = _git(repo_root, "ls-tree", "-r", "--name-only", revision, "--", "src") or ""
        for line in listing.splitlines():
            match = CHANGELOG_RE.match(line.strip())
            if match:
                found.add(match.group("package"))
    return sorted(found)


def changes_from_git(repo_root: Path, base: str, head: str) -> tuple[list[FileChange], list[str]]:
    """Changed files of ``head`` since its merge base with ``base``, and the packages.

    ``--no-renames``: a file moved between packages counts for both.
    """
    merge_base = (_git(repo_root, "merge-base", base, head) or "").strip()
    names = _git(repo_root, "-c", "core.quotepath=off", "diff", "--name-only", "--no-renames",
                 merge_base, head) or ""
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
    return changes, packages_at(repo_root, merge_base, head)


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
            if not event["base"] or not event["head"]:
                raise ValueError("event payload has no base or head sha")
            changes, packages = changes_from_git(repo_root, event["base"], event["head"])
            labels, body, author = event["labels"], event["body"], event["author"]
        else:
            data = json.loads(args.input_json.read_text(encoding="utf-8"))
            changes = [
                FileChange(path=item["path"], before=item.get("before"), after=item.get("after"))
                for item in data.get("files", [])
            ]
            labels, body, author = data.get("labels", []), data.get("body", ""), data.get("author", "")
            packages = sorted(set(discover_packages(repo_root)) | {
                match.group("package") for change in changes
                if (match := CHANGELOG_RE.match(change.path))
            })
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
