#!/usr/bin/env python3
"""Decision text must link the maintainer's decision (#1790).

A sentence in a spec (``specs/``), a release note (``docs/releases/``) or a
package changelog (``src/<package>/CHANGELOG.md``) that states an approval, an
exception, a removal or a date must link the issue or pull-request comment
where the maintainer made that decision, or say that it is "proposed". This
is rule 9.1 of the versioning policy (``docs/VERSIONING.md``); rule 4.3 is the
removal-date case of it.

The phrases that make a sentence decision text are listed once, in
``DECISION_PHRASES`` below. Only sentences that a pull request adds or
changes are checked, so existing text does not fail every pull request.

What counts as a decision link (``DECISION_LINK_RE``): a GitHub URL to a
comment, i.e. ``.../issues/<n>#issuecomment-<id>``, ``.../pull/<n>#issuecomment-<id>``,
``.../pull/<n>#discussion_r<id>``, ``.../pull/<n>#pullrequestreview-<id>``, or
the issue body itself, ``.../issues/<n>#issue-<id>``. A bare ``#123`` does not
count: it names the issue, not the decision in it.

Skipped: headings (a changelog's ``## [1.4.2] - 2026-10-30`` is a date, not a
decision), fenced code blocks and HTML comments.

The check is not a required status check. It reads only its inputs; it never
calls GitHub.

Usage:
    # In GitHub Actions: changed files and their contents from git.
    python scripts/ci/check_decision_links.py --base <sha> --head <sha>

    # Without GitHub: {"files": [{"path": ..., "before": ..., "after": ...}]}
    python scripts/ci/check_decision_links.py --input-json changes.json

    # Count the sentences in the whole tree that would fail (report only).
    python scripts/ci/check_decision_links.py --whole-tree

Exit codes:
    0 - passed (always 0 with --whole-tree)
    1 - a changed sentence states a decision without a link
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
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Iterable, Optional, Sequence

# ---------------------------------------------------------------------------
# Data: the one list of decision phrases. Each entry is (category, regex).
# Regexes are matched case-insensitively against one sentence.
# ---------------------------------------------------------------------------

DECISION_PHRASES: tuple[tuple[str, str], ...] = (
    # An approval or a sign-off.
    ("approval", r"\bapproved\b"),
    # "approval" alone also names a process ("human approval gates the
    # merge"), so only a determiner or holder makes it a statement.
    ("approval", r"\b(?:the|this|that|with|after|explicit|maintainer'?s?|owner'?s?) approval\b"),
    ("approval", r"\bratified\b"),
    ("approval", r"\bsigned[ -]off\b"),
    ("approval", r"\bsign-off\b"),
    ("approval", r"\bmaintainer (?:decided|decision|agreed|accepted|chose)\b"),
    # An exception to a rule.
    ("exception", r"\b(?:an|the|as an?) exception to\b"),
    ("exception", r"\bexempt(?:ed|ion)? from\b"),
    ("exception", r"\bwaived?\b"),
    ("exception", r"\bwaiver\b"),
    # A removal that has not happened yet.
    ("removal", r"\bwill be removed\b"),
    ("removal", r"\bto be removed\b"),
    ("removal", r"\bscheduled for removal\b"),
    ("removal", r"\bremoval date\b"),
    ("removal", r"\bremoved (?:in|after|by|from) (?:v?\d|the next|a future|version)"),
    # A date or a waiting period stated as a commitment.
    ("date", r"\bno earlier than\b"),
    ("date", r"\bno later than\b"),
    ("date", r"\bnot before\b"),
    ("date", r"\bwaiting period\b"),
    # A future date. "on <date>" records when something happened, and
    # "deadline" is also a timeout, so neither is listed.
    ("date", r"\b(?:by|until|after|before) \d{4}-\d{2}(?:-\d{2})?\b"),
    ("date", r"\b(?:by|until|after|before) (?:January|February|March|April|May|June|July|"
             r"August|September|October|November|December) (?:\d{1,2}, )?\d{4}\b"),
)

_COMPILED = tuple((category, re.compile(pattern, re.IGNORECASE)) for category, pattern in DECISION_PHRASES)

DECISION_LINK_RE = re.compile(
    r"https://github\.com/[\w.-]+/[\w.-]+/"
    r"(?:issues/\d+#(?:issuecomment-\d+|issue-\d+)"
    r"|pull/\d+#(?:issuecomment-\d+|discussion_r\d+|pullrequestreview-\d+))"
)
PROPOSED_RE = re.compile(r"\bproposed\b", re.IGNORECASE)

FENCE_RE = re.compile(r"^\s*(```|~~~)")
HEADING_RE = re.compile(r"^\s*#{1,6}\s")
LIST_ITEM_RE = re.compile(r"^\s*(?:[-*+]|\d+[.)])\s+")
TABLE_ROW_RE = re.compile(r"^\s*\|")
HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)
# A sentence ends at . ! or ? followed by whitespace. URLs hold no whitespace,
# so a link is never split from its sentence.
SENTENCE_SPLIT_RE = re.compile(r"(?<=[.!?])\s+(?=\S)")


@dataclass(frozen=True)
class FileChange:
    path: str
    before: Optional[str] = None
    after: Optional[str] = None


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    category: str
    sentence: str

    def message(self) -> str:
        return (
            f"{self.path}:{self.line}: states {'an' if self.category[0] in 'aeiou' else 'a'} "
            f"{self.category} without a link to the maintainer's decision. Link the "
            "issue comment that records it (…/issues/N#issuecomment-M), or mark the "
            f"sentence \"proposed\" (docs/VERSIONING.md 9.1): {self.sentence}"
        )


def in_scope(path: str) -> bool:
    pure = PurePosixPath(path)
    if pure.suffix.lower() != ".md":
        return False
    parts = pure.parts
    if parts[:1] == ("specs",):
        return True
    if parts[:2] == ("docs", "releases"):
        return True
    return len(parts) == 3 and parts[0] == "src" and parts[2] == "CHANGELOG.md"


def decision_category(sentence: str) -> Optional[str]:
    for category, pattern in _COMPILED:
        if pattern.search(sentence):
            return category
    return None


def has_decision_link(sentence: str) -> bool:
    return bool(DECISION_LINK_RE.search(sentence)) or bool(PROPOSED_RE.search(sentence))


def _blocks(lines: Sequence[str]) -> list[list[tuple[int, str]]]:
    """Group prose lines into blocks (paragraph, list item, table row).

    Each block is a list of ``(line index, text)``. Headings, blank lines and
    fenced code end a block and are not part of any.
    """
    blocks: list[list[tuple[int, str]]] = []
    current: list[tuple[int, str]] = []
    in_fence = False

    def flush() -> None:
        nonlocal current
        if current:
            blocks.append(current)
        current = []

    for index, line in enumerate(lines):
        if FENCE_RE.match(line):
            flush()
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        if not line.strip() or HEADING_RE.match(line):
            flush()
            continue
        if TABLE_ROW_RE.match(line):
            flush()
            blocks.append([(index, line)])
            continue
        if LIST_ITEM_RE.match(line):
            flush()
        current.append((index, line))
    flush()
    return blocks


def _strip_comments(lines: Sequence[str]) -> list[str]:
    """Blank out HTML comments, keeping line numbers."""
    text = "\n".join(lines)
    stripped = HTML_COMMENT_RE.sub(lambda m: "\n" * m.group(0).count("\n"), text)
    return stripped.split("\n")


def changed_line_indexes(before: Optional[str], after: Optional[str]) -> set[int]:
    old = (before or "").splitlines()
    new = (after or "").splitlines()
    changed: set[int] = set()
    matcher = difflib.SequenceMatcher(a=old, b=new, autojunk=False)
    for tag, _i1, _i2, j1, j2 in matcher.get_opcodes():
        if tag in {"insert", "replace"}:
            changed.update(range(j1, j2))
    return changed


def findings_for(path: str, after: Optional[str], changed: Optional[set[int]]) -> list[Finding]:
    """Decision sentences without a link in ``after``.

    ``changed`` limits the check to sentences that touch those line indexes;
    ``None`` checks every sentence.
    """
    if not after:
        return []
    lines = _strip_comments(after.splitlines())
    found: list[Finding] = []
    for block in _blocks(lines):
        # Join the block, remembering which line each character came from.
        text_parts: list[str] = []
        owners: list[int] = []
        for index, line in block:
            piece = line.strip()
            if text_parts:
                text_parts.append(" ")
                owners.append(index)
            text_parts.append(piece)
            owners.extend([index] * len(piece))
        text = "".join(text_parts)
        position = 0
        for sentence in SENTENCE_SPLIT_RE.split(text):
            start = text.index(sentence, position)
            end = start + len(sentence)
            position = end
            sentence_lines = set(owners[start:end]) if sentence else set()
            if changed is not None and not (sentence_lines & changed):
                continue
            category = decision_category(sentence)
            if category and not has_decision_link(sentence):
                found.append(Finding(
                    path=path, line=min(sentence_lines) + 1,
                    category=category, sentence=sentence.strip(),
                ))
    return found


def evaluate(changes: Iterable[FileChange]) -> list[Finding]:
    found: list[Finding] = []
    for change in sorted(changes, key=lambda item: item.path):
        if not in_scope(change.path) or change.after is None:
            continue
        found.extend(findings_for(
            change.path, change.after, changed_line_indexes(change.before, change.after),
        ))
    return found


def whole_tree(repo_root: Path) -> list[Finding]:
    found: list[Finding] = []
    candidates = [
        *sorted((repo_root / "specs").rglob("*.md")),
        *sorted((repo_root / "docs" / "releases").rglob("*.md")),
        *sorted((repo_root / "src").glob("*/CHANGELOG.md")),
    ]
    for path in candidates:
        relative = path.relative_to(repo_root).as_posix()
        if in_scope(relative):
            found.extend(findings_for(relative, path.read_text(encoding="utf-8"), None))
    return found


# ---------------------------------------------------------------------------
# Inputs and output
# ---------------------------------------------------------------------------

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
    merge_base = (_git(repo_root, "merge-base", base, head) or "").strip()
    names = _git(repo_root, "diff", "--name-only", "--no-renames", merge_base, head) or ""
    return [
        FileChange(
            path=path,
            before=_git(repo_root, "show", f"{merge_base}:{path}", allow_missing=True),
            after=_git(repo_root, "show", f"{head}:{path}", allow_missing=True),
        )
        for path in (line.strip() for line in names.splitlines() if line.strip())
        if in_scope(path)
    ]


def write_summary(text: str) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    try:
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")
    except OSError as error:
        print(f"::warning::could not write step summary: {error}")


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--base", help="base revision (with --head)")
    source.add_argument("--input-json", type=Path, help="changed files as JSON")
    source.add_argument("--whole-tree", action="store_true",
                        help="report every sentence in the tree that would fail; exits 0")
    parser.add_argument("--head", help="head revision (with --base)")
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args(argv)
    repo_root = args.repo_root.resolve()

    if args.whole_tree:
        found = whole_tree(repo_root)
        for finding in found:
            print(finding.message())
        by_category: dict[str, int] = {}
        for finding in found:
            by_category[finding.category] = by_category.get(finding.category, 0) + 1
        print(f"{len(found)} sentence(s) would fail: " + json.dumps(by_category, sort_keys=True))
        return 0

    if args.base and not args.head:
        parser.error("--base needs --head")
    try:
        if args.base:
            changes = changes_from_git(repo_root, args.base, args.head)
        else:
            data = json.loads(args.input_json.read_text(encoding="utf-8"))
            changes = [
                FileChange(path=item["path"], before=item.get("before"), after=item.get("after"))
                for item in data.get("files", [])
            ]
    except (OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
        print(f"::error title=Decision links::could not read the input: {error}", file=sys.stderr)
        return 2

    found = evaluate(changes)
    summary = ["### Decision text links its decision (#1790)", ""]
    for finding in found:
        print(f"::error file={finding.path},line={finding.line},title=Decision links::{finding.message()}")
        summary.append(f"- FAIL: {finding.message()}")
    checked = sorted(change.path for change in changes if in_scope(change.path))
    print(f"Checked {len(checked)} file(s): {', '.join(checked) or '(none in scope)'}")
    verdict = "failed" if found else "passed"
    print(f"Decision-link check {verdict}.")
    summary.extend(["", f"Checked {len(checked)} file(s). Result: {verdict}. This check is not required."])
    write_summary("\n".join(summary))
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
