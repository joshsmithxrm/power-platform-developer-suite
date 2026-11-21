#!/usr/bin/env python3
"""Decision text must link the maintainer's decision (#1790).

A sentence in a spec (``specs/``), a release note (``docs/releases/``) or a
package changelog (``src/<package>/CHANGELOG.md``) that states an approval, an
exception, a removal or a date must link the comment in this repository where
the maintainer made that decision, or be marked as proposed. This is the rule
proposed as 9.1 (and 4.3 for removal dates) in the versioning policy of #1831.

Only sentences that a pull request adds or changes are checked, so existing
text does not fail every pull request. A pure rename is not a change.

The phrases
-----------
``DECISION_PHRASES`` is the one list. Each entry has a category, a regex and an
example sentence that only that entry catches. Alternation is written only as
a named fragment (``<<NAME>>``, listed in ``FRAGMENTS``), and every fragment
alternative has its own example sentence. A test generated from these lists
removes each entry and each alternative in turn and checks that its example
is no longer caught, so an entry or alternative cannot be added without a
sentence that proves it.

A **date** counts only if it is after the day of the change (the head
commit's date in git mode). A past date, or the day itself, records what
happened; a later date commits to something. Month names are validated in code, so a capitalised
word that is not a month is not a date.

What counts as a decision link (``decision_link_re``): a URL to a comment in
this repository, i.e. ``…/issues/<n>#issuecomment-<id>``,
``…/pull/<n>#issuecomment-<id>``, ``…/pull/<n>#discussion_r<id>``,
``…/pull/<n>#pullrequestreview-<id>``, or the issue body itself,
``…/issues/<n>#issue-<id>``. A bare ``#123``, an issue URL without a comment
anchor, another repository's URL, or a URL embedded in another URL does not
count.

"Proposed" exempts a sentence only where it qualifies the decision
(``PROPOSED_RES``): a leading "Proposed:", "(proposed)", "is/are proposed", or
"proposed removal/date/exception/…". "The proposed flag was approved" is not
exempt.

Skipped: headings, fenced code blocks and HTML comments. A table row is one
unit; abbreviations such as "e.g." and "i.e." do not end a sentence.

The check is not a required status check. It reads only its inputs; it never
calls GitHub.

Usage:
    python scripts/ci/check_decision_links.py --base <sha> --head <sha>
    python scripts/ci/check_decision_links.py --input-json changes.json
    python scripts/ci/check_decision_links.py --whole-tree

Exit codes:
    0 - passed (always 0 with --whole-tree)
    1 - a changed sentence states a decision without a link
    2 - the input could not be read (fails closed)
"""
from __future__ import annotations

import argparse
import calendar
import difflib
import itertools
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from datetime import date, datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Iterable, Mapping, Optional, Sequence

DEFAULT_REPOSITORY = "joshsmithxrm/power-platform-developer-suite"


@dataclass(frozen=True)
class Alt:
    """One alternative of a fragment, with a sentence only it catches."""

    pattern: str
    example: str


@dataclass(frozen=True)
class Phrase:
    """One decision phrase, with a sentence only it catches."""

    category: str
    pattern: str
    example: str


# ---------------------------------------------------------------------------
# Data: the one list of decision phrases and their fragments.
# Patterns are matched case-insensitively against one sentence. A pattern may
# not contain "|": every alternative is listed here, with its example.
# ---------------------------------------------------------------------------

FRAGMENTS: dict[str, tuple[Alt, ...]] = {
    # Who decides.
    "HOLDER": (
        Alt(r"maintainer", "The maintainer has confirmed the plan."),
        Alt(r"owner", "The owner has confirmed the plan."),
    ),
    # Passive "was approved" and its tenses. "is approved" is left out: it
    # describes a process ("dates are set when the release is approved").
    "BEEN": (
        Alt(r"was", "The flag change was approved last week."),
        Alt(r"were", "Both changes were approved last week."),
        Alt(r"has been", "The flag change has been approved."),
        Alt(r"have been", "Both changes have been approved."),
        Alt(r"had been", "The flag change had been approved earlier."),
    ),
    # What follows an active "approved". "approved list", "approved tool"
    # and "approved run" are left out: they name a thing, not a decision.
    "APPROVED_OBJECT": (
        Alt(r"the", "Josh approved the plan."),
        Alt(r"this", "Josh approved this."),
        Alt(r"shipping", "Josh approved shipping it."),
        Alt(r"it", "Josh approved it."),
    ),
    "AGREED_LINK": (
        Alt(r"to", "The maintainer agreed to keep the old flag."),
        Alt(r"that", "Everyone agreed that the old flag stays."),
        Alt(r"with", "This is the order agreed with the owner."),
    ),
    # "we decided", "the owner has decided", "it was decided".
    "DECIDER": (
        Alt(r"we", "We decided not to rename it."),
        Alt(r"it was", "It was decided not to rename it."),
        Alt(r"has been", "It has been decided not to rename it."),
        Alt(r"maintainer", "The maintainer decided not to rename it."),
        Alt(r"owner", "The owner decided not to rename it."),
        Alt(r"maintainer has", "The maintainer has decided not to rename it."),
        Alt(r"owner has", "The owner has decided not to rename it."),
    ),
    # Removal verbs that always mean a decision ("will be retired").
    "RETIRED": (
        Alt(r"retired", "The flag will be retired."),
        Alt(r"sunset", "The flag will be sunset."),
        Alt(r"discontinued", "The flag will be discontinued."),
        Alt(r"phased out", "The flag will be phased out."),
    ),
    # Removal verbs that also describe cleanup ("the temporary file will be
    # deleted"); they count only with a version or release after them.
    "REMOVED": (
        Alt(r"removed", "The flag will be removed by 3.0."),
        Alt(r"dropped", "The flag will be dropped in 3.0."),
        Alt(r"deleted", "The flag will be deleted in 3.0."),
    ),
    "WHEN": (
        Alt(r"in v?\d", "The alias will be dropped in v3."),
        Alt(r"from v?\d", "The alias will be dropped from 3.0 on."),
        Alt(r"with v?\d", "The alias will be dropped with 3.0."),
        Alt(r"after v?\d", "The alias will be dropped after 3.0."),
        Alt(r"by v?\d", "The alias will be dropped by 3.0."),
        Alt(r"in (?:the )?next", "The alias will be dropped in the next major."),
        Alt(r"in a future", "The alias will be dropped in a future release."),
        Alt(r"next release", "The alias will be deleted next release."),
        Alt(r"next major", "The alias will be deleted next major."),
    ),
    # Removal verbs, active ("we will remove", "plans to drop").
    "REMOVE": (
        Alt(r"remove", "We will remove the flag."),
        Alt(r"drop", "We will drop the flag."),
        Alt(r"delete", "We will delete the flag."),
        Alt(r"retire", "We will retire the flag."),
        Alt(r"sunset", "We will sunset the flag."),
        Alt(r"discontinue", "We will discontinue the flag."),
    ),
    "REMOVES": (
        Alt(r"removes", "Version 3.0 removes the flag."),
        Alt(r"drops", "Version 3.0 drops the flag."),
        Alt(r"deletes", "Version 3.0 deletes the flag."),
        Alt(r"retires", "Version 3.0 retires the flag."),
    ),
    "REMOVAL": (
        Alt(r"removal", "The flag is slated for removal."),
        Alt(r"deletion", "The flag is slated for deletion."),
        Alt(r"retirement", "The flag is slated for retirement."),
        Alt(r"deprecation", "The flag is slated for deprecation."),
    ),
    "SCHEDULED": (
        Alt(r"scheduled", "Profile migration is scheduled for 3.1."),
        Alt(r"planned", "Profile migration is planned for 3.1."),
        Alt(r"slated", "Profile migration is slated for 3.1."),
        Alt(r"targeted", "Profile migration is targeted for 3.1."),
    ),
    # A version a decision points at.
    "VERSION": (
        Alt(r"(?:[a-z][\w.]* )?v?\d+\.\d+(?:\.\d+)?", "Profile migration is planned for CLI 2.1."),
        Alt(r"v\d+", "Profile migration is planned for v4."),
        Alt(r"(?:the )?next major(?: version)?(?: release)?", "Profile migration is planned for the next major."),
        Alt(r"(?:the )?next minor(?: version)?(?: release)?", "Profile migration is planned for the next minor."),
        Alt(r"a future major", "Profile migration is planned for a future major."),
        Alt(r"a future release", "Profile migration is planned for a future release."),
    ),
    # Units a waiting period is counted in.
    "UNIT": (
        Alt(r"days", "Keep the alias for at least 90 days."),
        Alt(r"weeks", "Keep the alias for at least six weeks."),
        Alt(r"months", "Keep the alias for at least six months."),
        Alt(r"years", "Keep the alias for at least two years."),
        Alt(r"releases", "Keep the alias for at least two releases."),
        Alt(r"minor releases", "Keep the alias for at least two minor releases."),
        Alt(r"minor release", "Keep the alias for at least one minor release."),
        Alt(r"major release", "Keep the alias for at least one major release."),
        Alt(r"versions", "Keep the alias for at least two versions."),
    ),
    "AFTER_EVENT": (
        Alt(r"stable", "The alias goes two weeks after the stable."),
        Alt(r"prerelease", "The alias goes two weeks after the prerelease."),
        Alt(r"release", "The alias goes two weeks after the release."),
        Alt(r"deprecation", "The alias goes two weeks after the deprecation."),
        Alt(r"tag", "The alias goes two weeks after the tag."),
    ),
    # Prepositions before a (future) date.
    "DATE_PREP": (
        Alt(r"on", "Version 3 ships on 2027-01-15."),
        Alt(r"in", "Version 3 ships in 2027-01."),
        Alt(r"from", "Version 3 is the default from 2027-01-15."),
        Alt(r"through", "Version 2 is supported through 2027-06-30."),
        Alt(r"as of", "As of 2027-03-01 version 3 is the default."),
        Alt(r"by", "Version 3 lands by 2027-03-01."),
        Alt(r"until", "Version 2 stays until 2027-03-01."),
        Alt(r"till", "Version 2 stays till 2027-03-01."),
        Alt(r"after", "Version 3 lands after 2027-03-01."),
        Alt(r"before", "Version 3 lands before 2027-03-01."),
        Alt(r"starting", "Version 3 is the default starting 2027-03-01."),
        Alt(r"at", "Version 2 support stops at the end of March 2027."),
        Alt(r"for", "Version 3 is pencilled in for 2027-03-01."),
    ),
    # Date forms. Groups are named y, m (number), mn (name), d, q, suffix;
    # month names and ordinal suffixes are validated in code.
    "DATE": (
        Alt(r"(?P<y>\d{4})-(?P<m>\d{2})-(?P<d>\d{2})", "Version 2 stays until 2027-03-01."),
        Alt(r"(?P<y>\d{4})-(?P<m>\d{2})(?!-)(?!\d)", "Version 2 stays until 2027-03."),
        Alt(r"(?P<d>\d{1,2})(?P<suffix>[a-z]{2})? (?P<mn>[a-z]{3,9}\.?),? (?P<y>\d{4})",
            "Version 2 stays until 1 March 2027."),
        Alt(r"(?P<mn>[a-z]{3,9}\.?) (?P<d>\d{1,2})(?P<suffix>[a-z]{2})?,? (?P<y>\d{4})",
            "Version 2 stays until March 1st, 2027."),
        Alt(r"(?P<mn>[a-z]{3,9}) (?P<y>\d{4})", "Version 2 stays until March 2027."),
        Alt(r"(?P<m>\d{1,2})/(?P<d>\d{1,2})/(?P<y>\d{4})", "Version 2 stays until 3/1/2027."),
        Alt(r"(?P<d>\d{1,2})\.(?P<m>\d{1,2})\.(?P<y>\d{4})", "Version 2 stays until 01.03.2027."),
        Alt(r"q(?P<q>[1-4]) (?P<y>\d{4})", "Version 2 stays until Q1 2027."),
        Alt(r"(?:the )?end of (?:(?P<mn>[a-z]{3,9}) )?(?P<y>\d{4})", "Version 2 stays until the end of March 2027."),
        Alt(r"(?P<y>20\d{2})(?!-)(?!/)(?!\d)(?!\.\d)", "Version 2 stays until 2027."),
    ),
}

DECISION_PHRASES: tuple[Phrase, ...] = (
    # --- approval -----------------------------------------------------------
    Phrase("approval", r"\b<<BEEN>> approved\b", "The rename had been approved before the freeze."),
    Phrase("approval", r"\bapproved by\b", "Merged once approved by Josh."),
    Phrase("approval", r"\bapproved <<APPROVED_OBJECT>>\b", "Josh approved the rename."),
    Phrase("approval", r"\bstatus:?\**:?\s*approved\b", "**Status:** Approved"),
    # The noun alone names a requirement ("needs maintainer approval"); only
    # a record that it was given counts.
    Phrase("approval", r"\bapproval was given\b", "Approval was given in the review."),
    Phrase("approval", r"\bapproval was granted\b", "Approval was granted in the review."),
    Phrase("approval", r"\bwith the <<HOLDER>>'s approval\b", "Merged with the maintainer's approval."),
    Phrase("approval", r"\bsigned[ -]off\b", "Josh signed off on the rename."),
    Phrase("approval", r"\bgave (?:\w+ )?sign-?off\b", "Josh gave his signoff for the rename."),
    Phrase("approval", r"\bsign-?off was given\b", "Maintainer sign-off was given."),
    Phrase("approval", r"\bgo-ahead\b", "Josh gave the go-ahead for the rename."),
    Phrase("approval", r"\bgreen-?lit\b", "The rename was greenlit."),
    Phrase("approval", r"\bgreen-?lighted\b", "The rename was greenlighted."),
    Phrase("approval", r"\bokayed\b", "Josh okayed the rename."),
    Phrase("approval", r"\bagreed <<AGREED_LINK>>\b", "The owner agreed to the rename."),
    Phrase("approval", r"\bas agreed\b", "As agreed, the rename goes ahead."),
    Phrase("approval", r"\b<<DECIDER>> decided\b", "We decided to keep the old name."),
    Phrase("approval", r"\bdecision was made\b", "The decision was made to keep the alias."),
    Phrase("approval", r"\bdecision was taken\b", "The decision was taken to keep the alias."),
    Phrase("approval", r"\bper (?:the )?(?:maintainer'?s )?decision\b", "Per the maintainer's decision the alias stays."),
    Phrase("approval", r"\bmaintainer decision\b", "This follows a maintainer decision."),
    # "authorized by the token" and "authorized for the scope" are auth terms.
    Phrase("approval", r"\bauthori[sz]ed by the <<HOLDER>>\b", "The rename is authorised by the owner."),
    Phrase("approval", r"\bauthori[sz]ed for <<VERSION>>", "The rename is authorized for 2.0."),
    Phrase("approval", r"\bhas authori[sz]ed\b", "Josh has authorised the rename."),
    Phrase("approval", r"\b<<HOLDER>> (?:has )?confirmed\b", "The maintainer confirmed the rename."),
    Phrase("approval", r"\bconfirmed by the <<HOLDER>>\b", "The rename was confirmed by the owner."),
    Phrase("approval", r"\baccepted by the <<HOLDER>>\b", "The rename was accepted by the maintainer."),
    Phrase("approval", r"\b<<HOLDER>> (?:has )?accepted\b", "The owner has accepted the rename."),
    Phrase("approval", r"\bratified\b", "The owner ratified option A."),
    Phrase("approval", r"\bendorsed\b", "The rename is endorsed."),
    # --- exception ----------------------------------------------------------
    Phrase("exception", r"\bexception is made\b", "An exception is made for the MCP server."),
    Phrase("exception", r"\bexception was made\b", "An exception was made for the MCP server."),
    Phrase("exception", r"\bexception granted\b", "Exception granted for the MCP server."),
    Phrase("exception", r"\bexception to (?:\w+ )?rule\b", "This is an exception to the rule."),
    Phrase("exception", r"\bexception to (?:\w+ )?policy\b", "This is an exception to this policy."),
    Phrase("exception", r"\bas an exception\b", "As an exception, the MCP server keeps the old code."),
    Phrase("exception", r"\bone-release exception\b", "It keeps its one-release exception."),
    Phrase("exception", r"\bone-time exception\b", "It keeps its one-time exception."),
    Phrase("exception", r"\bcompatibility exception\b", "This is a compatibility exception for 1.8."),
    # "Generated/ is exempt" describes build configuration; an exemption
    # from a rule says "exempt from".
    Phrase("exception", r"\bexempt(?:ed)? from\b", "The MCP package is exempted from the freeze."),
    Phrase("exception", r"\bwaived\b", "The size rule is waived here."),
    Phrase("exception", r"\bwaiver\b", "This change has a waiver."),
    Phrase("exception", r"\bby agreement\b", "Rule 3 does not apply to Plugins by agreement."),
    # --- removal ------------------------------------------------------------
    Phrase("removal", r"\bwill be <<RETIRED>>\b", "The setting will be sunset."),
    Phrase("removal", r"\bwill be <<REMOVED>> <<WHEN>>", "The setting will be deleted next release."),
    Phrase("removal", r"\bto be <<RETIRED>>\b", "The setting is to be retired."),
    Phrase("removal", r"\bwe will <<REMOVE>>\b", "We will remove the flag."),
    Phrase("removal", r"\bplan\w{0,3} to <<REMOVE>>\b", "PPDS plans to retire the setting."),
    Phrase("removal", r"\b<<VERSION>> <<REMOVES>>\b", "Version 3.0 removes the flag."),
    Phrase("removal", r"\b<<SCHEDULED>> for <<REMOVAL>>\b", "The flag is slated for removal."),
    Phrase("removal", r"\b<<REMOVAL>> (?:is )?<<SCHEDULED>> for\b", "Removal is planned for later."),
    Phrase("removal", r"\b<<REMOVAL>> in <<VERSION>>\b", "Removal in 3.0."),
    Phrase("removal", r"\bremoved in <<VERSION>>\b", "It is removed in v3."),
    Phrase("removal", r"\bgoing away\b", "The setting is going away."),
    Phrase("removal", r"\bgoes away\b", "The setting goes away."),
    Phrase("removal", r"\bgoes in <<VERSION>>\b", "It is deprecated and goes in the next minor."),
    Phrase("removal", r"\bno longer (?:be )?supported\b", "Windows 8 is no longer supported."),
    Phrase("removal", r"\bwill no longer accept\b", "The CLI will no longer accept the short form."),
    Phrase("removal", r"\bwill no longer support\b", "The CLI will no longer support the short form."),
    Phrase("removal", r"\bend[ -]of[ -]life\b", "Version 1 reaches end of life with 3.0."),
    Phrase("removal", r"\bremoval date\b", "The removal date is not set yet."),
    # --- date and schedule --------------------------------------------------
    Phrase("date", r"\bno earlier than\b", "Ship it no earlier than six months out."),
    Phrase("date", r"\bnot earlier than\b", "Ship it not earlier than six months out."),
    Phrase("date", r"\bno sooner than\b", "Ship it no sooner than six months out."),
    # "no later than the timeout" is a bound, not a date: only a date, a
    # duration or "the next" release counts.
    Phrase("date", r"\bno later than (?:the )?next\b", "Fixes ship no later than the next patch."),
    Phrase("date", r"\bno later than <<DATE>>", "Fixes ship no later than 2027-03-01."),
    Phrase("date", r"\bno later than \w+ <<UNIT>>\b", "Fixes ship no later than six months out."),
    Phrase("date", r"\bat least \w+ <<UNIT>>\b", "Keep the alias for at least 90 days."),
    Phrase("date", r"\ba minimum of \w+ <<UNIT>>\b", "Keep it for a minimum of two releases."),
    Phrase("date", r"\bwaiting period\b", "There is a waiting period."),
    Phrase("date", r"\b<<SCHEDULED>> for <<VERSION>>", "Profile migration is scheduled for 3.1."),
    Phrase("date", r"\b\w+ <<UNIT>> after (?:the )?<<AFTER_EVENT>>\b", "The alias goes two weeks after the prerelease."),
    Phrase("date", r"\b<<DATE_PREP>> <<DATE>>", "The cut-over happens after 2027-02-01."),
    Phrase("date", r"\btarget(?: date)?: <<DATE>>", "Target: 2027-01-15"),
    Phrase("date", r"\bdate is <<DATE>>", "The release date is 2027-01-15."),
    Phrase("date", r"\bend of (?P<y>20\d{2})\b", "Support stops end of 2027."),
)

MONTHS = {
    name.lower(): index
    for index in range(1, 13)
    for name in (calendar.month_name[index], calendar.month_abbr[index])
}
MONTHS["sept"] = 9
ORDINAL_SUFFIXES = {"st", "nd", "rd", "th"}
MACRO_RE = re.compile(r"<<([A-Z_]+)>>")

PROPOSED_RES = (
    re.compile(r"^\W*proposed\W*[:—–-]", re.IGNORECASE),
    re.compile(r"\(proposed\)", re.IGNORECASE),
    re.compile(r"\b(?:is|are) proposed\b", re.IGNORECASE),
    re.compile(r"\bproposed (?:removal|date|deadline|schedule|exception|timeline|approval)\b", re.IGNORECASE),
)

FENCE_RE = re.compile(r"^\s*(```|~~~)")
HEADING_RE = re.compile(r"^\s*#{1,6}\s")
LIST_ITEM_RE = re.compile(r"^\s*(?:[-*+]|\d+[.)])\s+")
TABLE_ROW_RE = re.compile(r"^\s*\|")
HTML_COMMENT_RE = re.compile(r"<!--.*?-->", re.DOTALL)
SENTENCE_SPLIT_RE = re.compile(r"(?<=[.!?])\s+(?=\S)")
ABBREVIATIONS = ("e.g.", "i.e.", "etc.", "vs.", "cf.", "approx.", "incl.", "no.", "resp.", "fig.", "al.")


# ---------------------------------------------------------------------------
# Matching
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Matcher:
    category: str
    regex: re.Pattern[str]
    has_date: bool


def build_matchers(
    phrases: Sequence[Phrase] = DECISION_PHRASES,
    fragments: Mapping[str, Sequence[Alt]] = FRAGMENTS,
) -> list[Matcher]:
    """Expand every phrase over its fragments' alternatives."""
    matchers: list[Matcher] = []
    for phrase in phrases:
        names = MACRO_RE.findall(phrase.pattern)
        choices = [[alt.pattern for alt in fragments[name]] for name in names]
        for combo in itertools.product(*choices):
            pattern = phrase.pattern
            for name, alternative in zip(names, combo):
                pattern = pattern.replace(f"<<{name}>>", f"(?:{alternative})", 1)
            has_date = "DATE" in names or "(?P<y>" in phrase.pattern
            matchers.append(Matcher(phrase.category, re.compile(pattern, re.IGNORECASE), has_date))
    return matchers


_MATCHERS = build_matchers()


def period_end(groups: Mapping[str, Optional[str]]) -> Optional[date]:
    """The last day a matched date can mean, or None if it is not a date."""
    try:
        year = int(groups["y"])
    except (KeyError, TypeError, ValueError):
        return None
    month: Optional[int] = None
    if groups.get("m"):
        month = int(groups["m"])
    elif groups.get("mn"):
        month = MONTHS.get(groups["mn"].rstrip(".").lower())
        if month is None:
            return None
    suffix = groups.get("suffix")
    if suffix and suffix.lower() not in ORDINAL_SUFFIXES:
        return None
    if groups.get("q"):
        month = int(groups["q"]) * 3
    if month is None:
        return date(year, 12, 31)
    if not 1 <= month <= 12:
        return None
    if groups.get("d"):
        try:
            return date(year, month, int(groups["d"]))
        except ValueError:
            return None
    return date(year, month, calendar.monthrange(year, month)[1])


def decision_category(sentence: str, today: date, matchers: Optional[Sequence[Matcher]] = None) -> Optional[str]:
    for matcher in matchers if matchers is not None else _MATCHERS:
        for match in matcher.regex.finditer(sentence):
            if not matcher.has_date:
                return matcher.category
            end = period_end(match.groupdict())
            if end is not None and end > today:
                return matcher.category
    return None


def decision_link_re(repository: str) -> re.Pattern[str]:
    owner, name = (re.escape(part) for part in repository.split("/", 1))
    return re.compile(
        r"(?<![\w/?=&%.#:@-])https://github\.com/" + owner + "/" + name + "/"
        r"(?:issues/\d+#(?:issuecomment-\d+|issue-\d+)"
        r"|pull/\d+#(?:issuecomment-\d+|discussion_r\d+|pullrequestreview-\d+))(?![\w-])",
        re.IGNORECASE,
    )


def is_proposed(sentence: str) -> bool:
    return any(pattern.search(sentence) for pattern in PROPOSED_RES)


# ---------------------------------------------------------------------------
# Documents
# ---------------------------------------------------------------------------

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
        article = "an" if self.category[0] in "aeiou" else "a"
        return (
            f"{self.path}:{self.line}: states {article} {self.category} without a link to the "
            "maintainer's decision. Link the comment that records it "
            "(…/issues/N#issuecomment-M in this repository), or mark it \"Proposed:\" "
            f"(docs/MERGE-POLICY.md, Decision Link Check): {self.sentence}"
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


def _blocks(lines: Sequence[str]) -> list[tuple[bool, list[tuple[int, str]]]]:
    """Group prose lines into blocks: (is a table row, [(line index, text)])."""
    blocks: list[tuple[bool, list[tuple[int, str]]]] = []
    current: list[tuple[int, str]] = []
    in_fence = False

    def flush() -> None:
        nonlocal current
        if current:
            blocks.append((False, current))
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
            blocks.append((True, [(index, line)]))
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


def split_sentences(text: str) -> list[str]:
    pieces = SENTENCE_SPLIT_RE.split(text)
    sentences: list[str] = []
    for piece in pieces:
        if sentences and sentences[-1].lower().endswith(ABBREVIATIONS):
            sentences[-1] = sentences[-1] + " " + piece
        else:
            sentences.append(piece)
    return sentences


def changed_line_indexes(before: Optional[str], after: Optional[str]) -> set[int]:
    old = (before or "").splitlines()
    new = (after or "").splitlines()
    changed: set[int] = set()
    matcher = difflib.SequenceMatcher(a=old, b=new, autojunk=False)
    for tag, _i1, _i2, j1, j2 in matcher.get_opcodes():
        if tag in {"insert", "replace"}:
            changed.update(range(j1, j2))
    return changed


def findings_for(
    path: str, after: Optional[str], changed: Optional[set[int]], *,
    today: date, repository: str = DEFAULT_REPOSITORY,
) -> list[Finding]:
    """Decision sentences without a link in ``after``.

    ``changed`` limits the check to sentences that touch those line indexes;
    ``None`` checks every sentence.
    """
    if not after:
        return []
    link_re = decision_link_re(repository)
    lines = _strip_comments(after.splitlines())
    found: list[Finding] = []
    for is_table_row, block in _blocks(lines):
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
        sentences = [text] if is_table_row else split_sentences(text)
        position = 0
        for sentence in sentences:
            start = text.index(sentence, position)
            end = start + len(sentence)
            position = end
            sentence_lines = set(owners[start:end]) if sentence else set()
            if changed is not None and not (sentence_lines & changed):
                continue
            category = decision_category(sentence, today)
            if category and not link_re.search(sentence) and not is_proposed(sentence):
                found.append(Finding(
                    path=path, line=min(sentence_lines) + 1,
                    category=category, sentence=sentence.strip(),
                ))
    return found


def evaluate(
    changes: Iterable[FileChange], *, today: date, repository: str = DEFAULT_REPOSITORY,
) -> list[Finding]:
    found: list[Finding] = []
    for change in sorted(changes, key=lambda item: item.path):
        if not in_scope(change.path) or change.after is None:
            continue
        found.extend(findings_for(
            change.path, change.after, changed_line_indexes(change.before, change.after),
            today=today, repository=repository,
        ))
    return found


def whole_tree(repo_root: Path, *, today: date, repository: str = DEFAULT_REPOSITORY) -> list[Finding]:
    found: list[Finding] = []
    candidates = [
        *sorted((repo_root / "specs").rglob("*.md")),
        *sorted((repo_root / "docs" / "releases").rglob("*.md")),
        *sorted((repo_root / "src").glob("*/CHANGELOG.md")),
    ]
    for path in candidates:
        relative = path.relative_to(repo_root).as_posix()
        if in_scope(relative):
            found.extend(findings_for(relative, path.read_text(encoding="utf-8"), None,
                                      today=today, repository=repository))
    return found


# ---------------------------------------------------------------------------
# Git (fails closed: every git error stops the check with exit 2)
# ---------------------------------------------------------------------------

def _git(repo_root: Path, *args: str) -> str:
    completed = subprocess.run(
        ["git", *args], cwd=repo_root, capture_output=True, text=True,
        encoding="utf-8", errors="replace",
    )
    if completed.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {completed.stderr.strip()}")
    return completed.stdout


def changes_from_git(repo_root: Path, base: str, head: str) -> list[FileChange]:
    """In-scope changes of ``head`` since its merge base with ``base``.

    Uses NUL-separated output, so a non-ASCII path is read as it is, and rename
    detection, so a pure rename compares the old and the new file.
    """
    # git exits non-zero when there is no merge base, which raises here.
    merge_base = _git(repo_root, "merge-base", base, head).strip()
    fields = _git(repo_root, "diff", "-z", "--name-status", "-M", merge_base, head).split("\0")
    changes: list[FileChange] = []
    index = 0
    while index < len(fields) and fields[index]:
        status = fields[index]
        kind = status[:1]
        if kind in {"R", "C"}:
            old_path, new_path = fields[index + 1], fields[index + 2]
            index += 3
        else:
            old_path = new_path = fields[index + 1]
            index += 2
        if kind == "D" or not in_scope(new_path):
            continue
        # A new file, or one moved in from outside the checked paths, is
        # checked whole; otherwise only the lines that differ from before.
        if kind == "A" or not in_scope(old_path):
            before = None
        else:
            before = _git(repo_root, "show", f"{merge_base}:{old_path}")
        after = _git(repo_root, "show", f"{head}:{new_path}")
        changes.append(FileChange(path=new_path, before=before, after=after))
    return changes


def commit_date(repo_root: Path, revision: str) -> date:
    return date.fromisoformat(_git(repo_root, "show", "-s", "--format=%cs", revision).strip())


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
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY") or DEFAULT_REPOSITORY,
                        help="owner/name whose comment links count")
    parser.add_argument("--today", type=date.fromisoformat,
                        help="the day a date must be after to count (default: head commit date, or today)")
    args = parser.parse_args(argv)
    repo_root = args.repo_root.resolve()

    if "/" not in args.repository:
        print(f"::error title=Decision links::--repository must be owner/name, got {args.repository!r}",
              file=sys.stderr)
        return 2
    if args.whole_tree:
        today = args.today or datetime.now(timezone.utc).date()
        found = whole_tree(repo_root, today=today, repository=args.repository)
        for finding in found:
            print(finding.message())
        by_category: dict[str, int] = {}
        for finding in found:
            by_category[finding.category] = by_category.get(finding.category, 0) + 1
        print(f"{len(found)} sentence(s) would fail: " + json.dumps(by_category, sort_keys=True))
        return 0

    try:
        if args.base is not None:
            if not args.base or not args.head:
                raise ValueError("--base and --head must both be given and non-empty")
            changes = changes_from_git(repo_root, args.base, args.head)
            today = args.today or commit_date(repo_root, args.head)
        else:
            data = json.loads(args.input_json.read_text(encoding="utf-8"))
            changes = [
                FileChange(path=item["path"], before=item.get("before"), after=item.get("after"))
                for item in data.get("files", [])
            ]
            today = args.today or datetime.now(timezone.utc).date()
    except (OSError, ValueError, KeyError, TypeError, AttributeError, RuntimeError) as error:
        print(f"::error title=Decision links::could not read the input: {error}", file=sys.stderr)
        return 2

    found = evaluate(changes, today=today, repository=args.repository)
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
