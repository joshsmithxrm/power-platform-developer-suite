#!/usr/bin/env python3
"""Decision text must link the maintainer's decision (#1790).

A sentence in a spec (``specs/``), a release note (``docs/releases/``) or a
package changelog (``src/<package>/CHANGELOG.md``) that states an approval, an
exception, a removal or a date must link the comment in this repository where
the maintainer made that decision, or be marked as proposed. This is the rule
proposed as 9.1 (and 4.3 for removal dates) in the versioning policy of #1831.

Only sentences that a pull request adds or changes are checked, so existing
text does not fail every pull request. A pure rename is not a change.

What it is: a net that catches the classes of decision text listed below. It
is not a proof that no decision text is unlinked; reviewers still read.

Classes, not phrases
--------------------
``DECISION_PHRASES`` is the one list of what counts. Its entries describe
classes of wording:

- **approval**: "approved" used as a verb, with any subject and object (not
  "the approved tool"); "approval" or "sign-off" given or held; "decided",
  "chose", "agreed", "accepted", "confirmed", "authorised" and the like;
  "Decision: …"; "per <name>,".
- **exception**: "exception" in its policy sense (not a thrown exception),
  "exempt", "excepted", "waived", a rule "suspended/relaxed/lifted", "by
  agreement", "this once".
- **removal**: a removal verb in the future or as a schedule ("will be
  removed", "we'll drop", "<version> will remove", "is retired in 2.0", "will
  stop working", "no longer supported", "support … ends", "goes away", "end of
  life", "scheduled for removal", "Removal of …: 3.0").
- **date**: a future date in any ordinary written form, with or without a
  preposition; a version committed to ("until the 3.0 release", "takes effect
  in 2.1", "from 2.1 onwards", "planned for 2.2"); a duration counted in days,
  months or releases ("for two more minors", "at least 90 days", "six months
  after 2.0", "deprecation period").
- **table columns**: a future date under a "When"/"Date"/"Target" column, a
  version under a "Removal"/"Removed in" column (``DECISION_COLUMNS``).

Each entry has an example sentence that only it catches. Alternation inside an
entry is written only as a named fragment (``<<NAME>>`` in ``FRAGMENTS``), and
every fragment alternative has its own example.

Product commitment or technical fact
------------------------------------
``GUARDS`` lists, per class, what makes a matching sentence a technical fact
rather than a product commitment. Each guard has blockers and, where useful,
markers that override them:

- ``TECH`` (removal, date): runtime and data words (rows, records, columns,
  temporary, cache, lock file, certificate, token, log, valid, expiry, created,
  modified, sample, fixture) block, unless the sentence also names a product
  commitment (a version number, a ``--flag``, an alias, "support for", the
  next major or minor, a deprecation).
- ``THIRD_PARTY`` (removal, date): a sentence about Microsoft, Dataverse,
  Azure or GitHub states their plans, not ours.
- ``CODE_EXCEPTION`` (exception): a thrown, raised, caught or unhandled
  exception, an exception handler or message.
- ``TECH_EXEMPT`` (exempt): an exemption from a check, a warning or an
  analyzer is build configuration.
- ``TECH_ACTOR`` (decided, chose, agreed, accepted): a client, server,
  handshake, planner, optimizer, request, connection or runtime decides
  things technically.
- ``REQUIREMENT`` (approval or sign-off nouns): "needs", "requires", "only
  with", "without", "subject to", "pending" name a requirement, not a record.
- ``CONDITIONAL`` ("approved"): "once/when/until/if … approved" is a process.
- ``HABITUAL`` ("approves"): "approves every release" is a process.

Past wording is not matched: removal entries use the future or present ("will
be removed", "is removed in"), never "was removed", and a date counts only if
it is after the day of the change (the head commit's date in git mode).

A test pins the whole expanded set, so an entry, alternative, guard or column
cannot be deleted without a test failing.

What counts as a decision link (``decision_link_re``): a URL to a comment in
this repository (``…/issues/<n>#issuecomment-<id>``, ``…/pull/<n>#issuecomment-<id>``,
``…/pull/<n>#discussion_r<id>``, ``…/pull/<n>#pullrequestreview-<id>``) or the
issue body (``…/issues/<n>#issue-<id>``), anywhere in the sentence.

"Proposed" exempts a decision only where it qualifies it (``PROPOSED_RES``): a
leading "Proposed:", or "(proposed)", "is/are proposed" or "proposed
removal/date/…" in the same clause (clauses are separated by ";").

Limits: headings, fenced code and HTML comments are not checked. A table row
is one unit; abbreviations such as "e.g." do not end a sentence.

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
    """One alternative of a fragment or guard, with a sentence that proves it."""

    pattern: str
    example: str


@dataclass(frozen=True)
class Phrase:
    """One class of decision wording, with a sentence only it catches."""

    category: str
    pattern: str
    example: str
    guards: tuple[str, ...] = ()
    case_sensitive: bool = False


@dataclass(frozen=True)
class Guard:
    """What makes a match a technical fact: blockers, unless a marker is present."""

    blockers: tuple[Alt, ...]
    markers: tuple[Alt, ...] = ()


# ---------------------------------------------------------------------------
# Data: the one list. Patterns match case-insensitively unless an entry says
# otherwise. No pattern may contain "|": every alternative is listed here.
# ---------------------------------------------------------------------------

FRAGMENTS: dict[str, tuple[Alt, ...]] = {
    "HOLDER": (
        Alt(r"maintainer", "The maintainer confirmed the plan."),
        Alt(r"owner", "The owner confirmed the plan."),
    ),
    "BEEN": (
        Alt(r"was", "The proposal was accepted."),
        Alt(r"were", "Both proposals were accepted."),
        Alt(r"has been", "The proposal has been accepted."),
        Alt(r"have been", "Both proposals have been accepted."),
        Alt(r"had been", "The proposal had been accepted earlier."),
    ),
    # Giving or holding an approval or a sign-off.
    "HAVE": (
        Alt(r"gave", "Josh gave approval for it."),
        Alt(r"give", "Josh did give approval for it."),
        Alt(r"gives", "This gives approval for it."),
        Alt(r"has", "The rename has approval."),
        Alt(r"have", "Both renames have approval."),
        Alt(r"had", "The rename had approval."),
        Alt(r"received", "The rename received approval."),
        Alt(r"got", "The rename got approval."),
        Alt(r"granted", "Josh granted approval for it."),
        Alt(r"obtained", "The rename obtained approval."),
        Alt(r"with", "It merged with approval."),
    ),
    "GIVEN": (
        Alt(r"given", "Approval was given."),
        Alt(r"granted", "Approval was granted."),
        Alt(r"received", "Approval was received."),
    ),
    "APPROVAL_VERB": (
        Alt(r"authori[sz]es", "The approval authorizes the release."),
        Alt(r"covers", "The approval covers the release."),
        Alt(r"applies", "The approval applies to the release."),
        Alt(r"allows", "The approval allows the release."),
        Alt(r"extends", "The approval extends to the release."),
    ),
    "ACCEPT_OBJECT": (
        Alt(r"risk", "The maintainer accepted the risk."),
        Alt(r"trade-off", "The maintainer accepted the trade-off."),
        Alt(r"proposal", "The maintainer accepted the proposal."),
        Alt(r"plan", "The maintainer accepted the plan."),
        Alt(r"cost", "The maintainer accepted the cost."),
    ),
    "DECISION_TAIL": (
        Alt(r"is to", "The decision is to keep the port."),
        Alt(r"is that", "The decision is that the port stays."),
        Alt(r"was to", "The decision was to keep the port."),
        Alt(r"was made", "A decision was made to keep the port."),
        Alt(r"was taken", "A decision was taken to keep the port."),
    ),
    # "exception" counts only in a policy frame (an exception to a rule, made,
    # granted, covering something), never on its own: specs and changelogs
    # use the word for thrown exceptions far more often.
    "DET": (
        Alt(r"an", "The server is an exception."),
        Alt(r"the", "The server is the exception."),
        Alt(r"one", "The server is one exception."),
        Alt(r"its", "This is its exception."),
    ),
    "GRANT": (
        Alt(r"make", "We make an exception here."),
        Alt(r"makes", "The policy makes an exception here."),
        Alt(r"made", "Josh made an exception here."),
        Alt(r"grant", "We grant an exception here."),
        Alt(r"grants", "The policy grants an exception here."),
        Alt(r"granted", "Josh granted an exception here."),
        Alt(r"get", "Hotfixes get an exception here."),
        Alt(r"gets", "The hotfix gets an exception here."),
    ),
    "EXC_VERB": (
        Alt(r"covers", "The exception covers 1.8 only."),
        Alt(r"applies", "The exception applies here only."),
        Alt(r"does not cover", "The exception does not cover libraries."),
        Alt(r"does not apply", "The exception does not apply here."),
        Alt(r"is made", "An exception is made here."),
        Alt(r"was made", "An exception was made here."),
        Alt(r"is granted", "An exception is granted here."),
        Alt(r"granted", "Exception granted here."),
        Alt(r"ends", "The exception ends with 2.0."),
    ),
    "EXC_KIND": (
        Alt(r"one-release", "It keeps a one-release exception."),
        Alt(r"one-time", "It keeps a one-time exception."),
        Alt(r"compatibility", "It keeps a compatibility exception."),
        Alt(r"policy", "It keeps a policy exception."),
        Alt(r"scoped", "It keeps a scoped exception."),
    ),
    "SUSPENDED": (
        Alt(r"suspended", "The freeze is suspended for hotfixes."),
        Alt(r"relaxed", "The freeze is relaxed for hotfixes."),
        Alt(r"lifted", "The freeze is lifted for hotfixes."),
    ),
    "RULE_NOUN": (
        Alt(r"rule", "The two-reviewer rule is relaxed for docs."),
        Alt(r"freeze", "The stable freeze is relaxed for docs."),
        Alt(r"gate", "The release gate is relaxed for docs."),
        Alt(r"policy", "The versioning policy is relaxed for docs."),
        Alt(r"requirement", "The soak requirement is relaxed for docs."),
        Alt(r"limit", "The size limit is relaxed for docs."),
    ),
    # Removal verbs, passive: "will be removed".
    "REMOVED": (
        Alt(r"removed", "The setting will be removed."),
        Alt(r"dropped", "The setting will be dropped."),
        Alt(r"deleted", "The setting will be deleted."),
        Alt(r"retired", "The setting will be retired."),
        Alt(r"sunset", "The setting will be sunset."),
        Alt(r"discontinued", "The setting will be discontinued."),
        Alt(r"phased out", "The setting will be phased out."),
        Alt(r"deprecated", "The setting will be deprecated."),
    ),
    # Future, intent or plan before an active removal verb.
    "WILL": (
        Alt(r"\bwill", "We will remove the setting."),
        Alt(r"'ll", "We'll remove the setting."),
        Alt(r"\bshall", "We shall remove the setting."),
        Alt(r"\bplan to", "We plan to remove the setting."),
        Alt(r"\bplans to", "PPDS plans to remove the setting."),
        Alt(r"\bplanned to", "We planned to remove the setting next."),
        Alt(r"\bare going to", "We are going to remove the setting."),
        Alt(r"\bis going to", "PPDS is going to remove the setting."),
        Alt(r"\bintend to", "We intend to remove the setting."),
        Alt(r"\bintends to", "PPDS intends to remove the setting."),
    ),
    "REMOVE": (
        Alt(r"remove", "We will remove the switch."),
        Alt(r"drop", "We will drop the switch."),
        Alt(r"delete", "We will delete the switch."),
        Alt(r"retire", "We will retire the switch."),
        Alt(r"sunset", "We will sunset the switch."),
        Alt(r"discontinue", "We will discontinue the switch."),
        Alt(r"deprecate", "We will deprecate the switch."),
        Alt(r"stop supporting", "We will stop supporting the switch."),
    ),
    "REMOVING": (
        Alt(r"removing", "We are removing the switch."),
        Alt(r"dropping", "We are dropping the switch."),
        Alt(r"retiring", "We are retiring the switch."),
        Alt(r"deprecating", "We are deprecating the switch."),
    ),
    "BEING": (
        Alt(r"are", "We are dropping the theme."),
        Alt(r"is", "PPDS is dropping the theme."),
        Alt(r"'re", "We're dropping the theme."),
    ),
    "ISARE": (
        Alt(r"is", "The verb is retired in 2.0."),
        Alt(r"are", "The verbs are retired in 2.0."),
    ),
    "REMOVES": (
        Alt(r"removes", "Version 3.0 removes the switch."),
        Alt(r"drops", "Version 3.0 drops the switch."),
        Alt(r"retires", "Version 3.0 retires the switch."),
    ),
    "REMOVAL": (
        Alt(r"removal", "The switch is slated for removal."),
        Alt(r"deletion", "The switch is slated for deletion."),
        Alt(r"retirement", "The switch is slated for retirement."),
        Alt(r"deprecation", "The switch is slated for deprecation."),
    ),
    "SCHEDULED": (
        Alt(r"scheduled", "The switch is scheduled for removal."),
        Alt(r"planned", "The switch is planned for removal."),
        Alt(r"slated", "The switch is slated for removal."),
        Alt(r"targeted", "The switch is targeted for removal."),
        Alt(r"marked", "The switch is marked for removal."),
    ),
    # A version a decision points at.
    "VERSION": (
        Alt(r"(?:[a-z][\w.]* )?v?\d+\.\d+(?:\.\d+)?", "The new layout takes effect in CLI 2.1."),
        Alt(r"v\d+", "The new layout takes effect in v4."),
        Alt(r"(?:the )?next major(?: version)?(?: release)?", "The new layout takes effect in the next major."),
        Alt(r"(?:the )?next minor(?: version)?(?: release)?", "The new layout takes effect in the next minor."),
        Alt(r"a future major", "The new layout takes effect in a future major."),
        Alt(r"a future release", "The new layout takes effect in a future release."),
    ),
    # Words before a version that commit to it.
    "VSCHED": (
        Alt(r"until", "The old layout stays until 3.0."),
        Alt(r"takes effect in", "The new layout takes effect in 3.0."),
        Alt(r"takes effect with", "The new layout takes effect with 3.0."),
        Alt(r"lands in", "The new layout lands in 3.0."),
        Alt(r"ships in", "The new layout ships in 3.0."),
        Alt(r"arrives in", "The new layout arrives in 3.0."),
        Alt(r"follows in", "The new layout follows in 3.0."),
        Alt(r"planned for", "The new layout is planned for 3.0."),
        Alt(r"scheduled for", "The new layout is scheduled for 3.0."),
        Alt(r"slated for", "The new layout is slated for 3.0."),
        Alt(r"targeted for", "The new layout is targeted for 3.0."),
        Alt(r"expected in", "The new layout is expected in 3.0."),
        Alt(r"due in", "The new layout is due in 3.0."),
        Alt(r"starting with", "The new layout is the default starting with 3.0."),
        Alt(r"as of", "The new layout is the default as of 3.0."),
        Alt(r"becomes the default in", "The new layout becomes the default in 3.0."),
        Alt(r"becomes an error in", "The old flag becomes an error in 3.0."),
        Alt(r"becomes required in", "The new flag becomes required in 3.0."),
        Alt(r"continues through", "The old layout continues through 3.0."),
        Alt(r"to ship in", "The new layout is planned to ship in 3.0."),
    ),
    # A label before a version: "Ships in: 2.1", "Due: 2.1".
    "LABEL": (
        Alt(r"ships in", "Ships in: 2.1"),
        Alt(r"due", "Due: 2.1"),
        Alt(r"target", "Target: 2.1"),
        Alt(r"planned", "Planned: 2.1"),
        Alt(r"release", "Release: 2.1"),
        Alt(r"eta", "ETA: 2.1"),
    ),
    # A count: a number or a number word, not any word ("for stable releases").
    "COUNT": (
        Alt(r"\d+", "The switch stays for 3 more minors."),
        Alt(r"one", "The switch stays for one more minor release."),
        Alt(r"two", "The switch stays for two more minors."),
        Alt(r"three", "The switch stays for three more minors."),
        Alt(r"four", "The switch stays for four more minors."),
        Alt(r"five", "The switch stays for five more minors."),
        Alt(r"six", "The switch stays for six more minors."),
        Alt(r"twelve", "The switch stays for twelve more minors."),
    ),
    # Units a commitment is counted in.
    "UNIT": (
        Alt(r"days", "Keep the switch for at least 90 days."),
        Alt(r"weeks", "Keep the switch for at least six weeks."),
        Alt(r"months", "Keep the switch for at least six months."),
        Alt(r"years", "Keep the switch for at least two years."),
        Alt(r"releases", "The switch stays at least two releases."),
        Alt(r"major release", "The switch stays at least one major release."),
        Alt(r"minors", "The switch stays at least two minors."),
        Alt(r"minor", "Keep the switch for at least one minor."),
        Alt(r"versions", "The switch stays at least two versions."),
        Alt(r"prereleases", "Keep the switch for at least two prereleases."),
    ),
    "RELUNIT": (
        Alt(r"minors", "The switch stays for two more minors."),
        Alt(r"minor releases", "The switch stays for two more minor releases."),
        Alt(r"minor release", "The switch stays for one more minor release."),
        Alt(r"majors", "The switch stays for two more majors."),
        Alt(r"major releases", "The switch stays for two more major releases."),
        Alt(r"major release", "The switch stays for one more major release."),
        Alt(r"releases", "The switch stays for two more releases."),
        Alt(r"versions", "The switch stays for two more versions."),
        Alt(r"minor versions", "The switch stays for two more minor versions."),
        Alt(r"major versions", "The switch stays for two more major versions."),
    ),
    "UNIT_TIME": (
        Alt(r"days", "The switch stays 90 days after 2.0."),
        Alt(r"weeks", "The switch stays six weeks after 2.0."),
        Alt(r"months", "The switch stays six months after 2.0."),
        Alt(r"years", "The switch stays two years after 2.0."),
    ),
    "AFTER_EVENT": (
        Alt(r"\d", "The shim goes two weeks after 2.0."),
        Alt(r"stable", "The shim goes two weeks after the stable."),
        Alt(r"next", "The shim goes two weeks after the next minor."),
        Alt(r"prerelease", "The shim goes two weeks after the prerelease."),
        Alt(r"release", "The shim goes two weeks after the release."),
        Alt(r"deprecation", "The shim goes two weeks after the deprecation."),
        Alt(r"tag", "The shim goes two weeks after the tag."),
    ),
    "PERIOD": (
        Alt(r"deprecation", "There is a deprecation period."),
        Alt(r"waiting", "There is a waiting period."),
        Alt(r"support", "There is a support period."),
        Alt(r"notice", "There is a notice period."),
        Alt(r"soak", "There is a soak period."),
    ),
    "YEAR_PREP": (
        Alt(r"in", "The CLI moves to the new runtime in 2027."),
        Alt(r"by", "The CLI moves to the new runtime by 2027."),
        Alt(r"until", "The CLI keeps the old runtime until 2027."),
        Alt(r"till", "The CLI keeps the old runtime till 2027."),
        Alt(r"through", "The CLI keeps the old runtime through 2027."),
        Alt(r"after", "The CLI moves to the new runtime after 2027."),
        Alt(r"before", "The CLI moves to the new runtime before 2027."),
        Alt(r"during", "The CLI moves to the new runtime during 2027."),
        Alt(r"from", "The CLI uses the new runtime from 2027."),
    ),
    # Date forms, anywhere in a sentence. Groups are named y, m (number), mn
    # (name), d, q, suffix; names and ordinal suffixes are validated in code.
    "DATE": (
        Alt(r"(?P<y>\d{4})-(?P<m>\d{2})-(?P<d>\d{2})", "Stable ships 2027-01-15."),
        Alt(r"(?P<y>\d{4})-(?P<m>\d{2})(?!-)(?!\d)", "Stable ships 2027-01."),
        Alt(r"(?P<d>\d{1,2})(?P<suffix>[a-z]{2})? (?P<mn>[a-z]{3,9}\.?),? (?P<y>\d{4})",
            "Stable ships 15 January 2027."),
        Alt(r"(?P<mn>[a-z]{3,9}\.?) (?P<d>\d{1,2})(?P<suffix>[a-z]{2})?,? (?P<y>\d{4})",
            "Stable ships January 15th, 2027."),
        Alt(r"(?<!\d )(?P<mn>[a-z]{3,9}) (?P<y>\d{4})", "Stable ships in January 2027."),
        Alt(r"(?P<m>\d{1,2})/(?P<d>\d{1,2})/(?P<y>\d{4})", "Stable ships 1/15/2027."),
        Alt(r"(?P<d>\d{1,2})\.(?P<m>\d{1,2})\.(?P<y>\d{4})", "Stable ships 15.01.2027."),
        Alt(r"q(?P<q>[1-4]) (?P<y>\d{4})", "Stable ships Q1 2027."),
        Alt(r"(?:the )?end of (?:(?P<mn>[a-z]{3,9}) )?(?P<y>\d{4})", "Stable ships at the end of 2027."),
    ),
}

DECISION_PHRASES: tuple[Phrase, ...] = (
    # --- approval -----------------------------------------------------------
    # "approved" as a verb or label; not "the/an/a/its/X's approved <thing>".
    Phrase("approval", r"(?<!the )(?<!an )(?<!a )(?<!'s )(?<!its )(?<!pre-)\bapproved\b",
           "Josh approved a second prerelease.", ("CONDITIONAL", "ADJECTIVE")),
    Phrase("approval", r"\bapproves\b", "The maintainer approves the rename.", ("HABITUAL", "CONDITIONAL")),
    # The noun counts only when an approval is given or held ("gave approval",
    # "has maintainer approval", "the maintainer's approval", "approval was
    # given"), not as a name for a step ("approval scope", "for approval").
    Phrase("approval", r"\b<<HAVE>> (?:[\w'-]+ ){0,2}approval\b", "The maintainer gave approval for a hotfix branch.",
           ("REQUIREMENT",)),
    Phrase("approval", r"\bapproval (?:[\w'-]+ ){0,4}<<BEEN>> <<GIVEN>>\b", "Approval was given in the review."),
    Phrase("approval", r"\b\w+'s approval\b", "It ships on the owner's approval."),
    Phrase("approval", r"\bthe approval <<APPROVAL_VERB>>\b", "The approval authorizes release preparation."),
    Phrase("approval", r"\bsigned[ -]off\b", "The owner signed off on the plan."),
    Phrase("approval", r"\b<<HAVE>> (?:[\w'-]+ ){0,2}sign-?off\b", "The split received sign-off from the owner.",
           ("REQUIREMENT",)),
    Phrase("approval", r"\bsign-?off (?:[\w'-]+ ){0,4}<<BEEN>> <<GIVEN>>\b", "Sign-off was given in the review."),
    Phrase("approval", r"^\W*sign-?off\s*:", "Sign-off: Josh, 2026-11-20"),
    Phrase("approval", r"\bdecided\b", "We decided against a separate package.", ("TECH_ACTOR",)),
    Phrase("approval", r"^\W*decision\s*:", "Decision: the port stays.", ("DESIGN_RECORD",)),
    Phrase("approval", r"\bdecision <<DECISION_TAIL>>\b", "The maintainer's decision is to keep the port."),
    Phrase("approval", r"\bper (?:the )?(?:\w+'s )?decision\b", "Per the owner's decision, notebooks stay."),
    Phrase("approval", r"\bmaintainer decision\b", "This follows a maintainer decision."),
    Phrase("approval", r"\bPer [A-Z][a-z]+,", "Per Josh, the server ships with the CLI.", case_sensitive=True),
    Phrase("approval", r"\bchose\b", "The maintainer chose option C.", ("TECH_ACTOR",)),
    Phrase("approval", r"\bwas chosen\b", "Option B was chosen by the owner.", ("TECH_ACTOR",)),
    Phrase("approval", r"\bagreed\b", "The team agreed on monthly prereleases.", ("TECH_ACTOR",)),
    Phrase("approval", r"\b<<BEEN>> accepted\b", "The roadmap was accepted.", ("TECH_ACTOR",)),
    Phrase("approval", r"\baccepted the <<ACCEPT_OBJECT>>\b", "Josh accepted the risk."),
    Phrase("approval", r"\bgreen[- ]?light\w*", "Josh gave the green light for 1.9."),
    Phrase("approval", r"\bgreen-?lit\b", "The split was greenlit."),
    Phrase("approval", r"\bgo-ahead\b", "Josh gave the go-ahead to publish."),
    Phrase("approval", r"\bokayed\b", "Josh okayed the alias."),
    Phrase("approval", r"\bok'd\b", "Josh OK'd the alias."),
    Phrase("approval", r"\b<<HOLDER>> (?:has )?confirmed\b", "The owner has confirmed the versioning."),
    Phrase("approval", r"\bconfirmed by the <<HOLDER>>\b", "The versioning was confirmed by the maintainer."),
    Phrase("approval", r"\bauthori[sz]ed by the <<HOLDER>>\b", "The route is authorised by the owner."),
    Phrase("approval", r"\bhas authori[sz]ed\b", "The maintainer has authorized a second key."),
    Phrase("approval", r"\bauthori[sz]ed for <<VERSION>>", "The rename is authorized for 2.0."),
    Phrase("approval", r"\bratified\b", "The owner ratified option A."),
    Phrase("approval", r"\bendorsed\b", "The rename is endorsed."),
    # --- exception ----------------------------------------------------------
    Phrase("exception", r"\b<<ISARE>> (?:<<DET>> )(?:\w+ ){0,2}exception\b", "The Plugins package is an exception.",
           ("CODE_EXCEPTION",)),
    Phrase("exception", r"\bexceptions? to\b", "Hotfixes get exceptions to the freeze rule.", ("CODE_EXCEPTION",)),
    Phrase("exception", r"\bexceptions? for\b", "There are exceptions for hotfixes.", ("CODE_EXCEPTION",)),
    Phrase("exception", r"\b<<GRANT>> an exception\b", "The maintainer made an exception.", ("CODE_EXCEPTION",)),
    Phrase("exception", r"\bexception <<EXC_VERB>>\b", "The exception covers only 1.9.", ("CODE_EXCEPTION",)),
    Phrase("exception", r"\b<<EXC_KIND>> exceptions?\b", "The CLI keeps its one-release exception.", ("CODE_EXCEPTION",)),
    Phrase("exception", r"\bas an exception\b", "As an exception, the CLI keeps `-e`.", ("CODE_EXCEPTION",)),
    Phrase("exception", r"\bby exception\b", "Docs skip the soak by exception."),
    Phrase("exception", r"^\W*exception\W*:", "- **Exception:** the CLI keeps `-e`."),
    Phrase("exception", r"\bexempt\w*", "The server is exempt from the two-reviewer rule.", ("TECH_EXEMPT",)),
    Phrase("exception", r"\bexcepted\b", "Test-only packages are excepted from the changelog rule."),
    Phrase("exception", r"\bwaived\b", "The size rule is waived here."),
    Phrase("exception", r"\bwaivers?\b", "A waiver covers the missing build."),
    Phrase("exception", r"\b<<RULE_NOUN>> (?:\w+ ){0,2}<<SUSPENDED>>\b", "The freeze is suspended for security fixes."),
    # Scope rules: "the rule does not apply to X", "excluded from the freeze".
    Phrase("exception", r"\b<<RULE_NOUN>> (?:\w+ ){0,2}does not apply to\b", "The freeze does not apply to the docs site."),
    Phrase("exception", r"\bexcluded from (?:the )?(?:\w+ )?<<RULE_NOUN>>\b", "Docs are excluded from the stable freeze."),
    Phrase("exception", r"\bnot subject to (?:the )?(?:\w+ )?<<RULE_NOUN>>\b", "Hotfixes are not subject to the size limit."),
    Phrase("exception", r"\bby agreement\b", "The server keeps exit 2 by agreement."),
    Phrase("exception", r"\bthis once\b", "The CLI may skip the soak this once."),
    # --- removal ------------------------------------------------------------
    Phrase("removal", r"\bwill be <<REMOVED>>\b", "The setting will be removed.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"<<WILL>> <<REMOVE>>\b", "We will remove the switch.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bto be <<REMOVED>>\b", "The setting is to be retired.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\b<<BEING>> <<REMOVING>>\b", "We are dropping the classic theme.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\b<<ISARE>> <<REMOVED>> (?:\w+ ){0,2}<<VERSION>>", "The verb is retired in 2.0.",
           ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bwill stop working\b", "The v1 protocol will stop working.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bwill no longer\b", "The old key will no longer be read.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bno longer (?:be )?supported\b", "Windows 8 is no longer supported.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bsupport (?:\w+ ){0,4}ends\b", "Support for Server 2016 ends with 2.0.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bgoes away\b", "The switch goes away.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bgoing away\b", "The switch is going away.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bend[ -]of[ -]life\b", "Version 1 reaches end of life with 3.0.", ("THIRD_PARTY",)),
    Phrase("removal", r"\b<<SCHEDULED>> for <<REMOVAL>>\b", "The fallback is scheduled for removal.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\b<<REMOVAL>> (?:is )?<<SCHEDULED>>\b", "Removal is planned.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bremoval\b[^.;:]{0,60}:\s*v?\d+\.\d+", "Removal of the alias: 3.0."),
    Phrase("removal", r"\b<<REMOVAL>> in <<VERSION>>", "Removal in 3.0."),
    Phrase("removal", r"\bremoval date\b", "The removal date is open."),
    Phrase("removal", r"\b<<VERSION>> <<REMOVES>>\b", "Version 3.0 removes the switch.", ("TECH", "THIRD_PARTY")),
    Phrase("removal", r"\bgoes in <<VERSION>>", "It is deprecated and goes in the next minor."),
    # --- date and schedule --------------------------------------------------
    Phrase("date", r"<<DATE>>", "Stable ships 2027-01-15.", ("TECH", "THIRD_PARTY")),
    Phrase("date", r"\b<<YEAR_PREP>> (?P<y>20\d{2})(?!-)(?!/)(?!\d)(?!\.\d)", "The CLI moves to the new runtime in 2027.",
           ("TECH", "THIRD_PARTY")),
    Phrase("date", r"\b<<VSCHED>> (?:the )?<<VERSION>>", "The new layout takes effect in 3.0.", ("TECH", "THIRD_PARTY")),
    Phrase("date", r"^\W*<<LABEL>>\s*:\s*<<VERSION>>", "Ships in: 2.1"),
    Phrase("date", r"\bfrom <<VERSION>> onwards\b", "The fallback is available from 2.1 onwards.", ("TECH", "THIRD_PARTY")),
    Phrase("date", r"\bfrom <<VERSION>> on\b", "The fallback is available from 2.1 on.", ("TECH", "THIRD_PARTY")),
    Phrase("date", r"\bfor (?:at least )?<<COUNT>> (?:more )?<<RELUNIT>>\b", "The switch stays for two more minors.",
           ("TECH", "THIRD_PARTY")),
    Phrase("date", r"\bat least <<COUNT>> <<UNIT>>\b", "Keep the switch for at least 90 days.", ("TECH", "THIRD_PARTY")),
    Phrase("date", r"\ba minimum of <<COUNT>> <<UNIT>>\b", "Keep it for a minimum of two releases.", ("TECH", "THIRD_PARTY")),
    Phrase("date", r"\b<<COUNT>> <<UNIT_TIME>> after (?:the )?<<AFTER_EVENT>>", "The shim goes two weeks after the prerelease.",
           ("TECH", "THIRD_PARTY")),
    Phrase("date", r"\b<<PERIOD>> period\b", "There is a waiting period.", ("TECH",)),
    Phrase("date", r"\bno earlier than\b", "Ship it no earlier than six months out."),
    Phrase("date", r"\bnot earlier than\b", "Ship it not earlier than six months out."),
    Phrase("date", r"\bno sooner than\b", "Ship it no sooner than six months out."),
    Phrase("date", r"\bno later than (?:the )?next\b", "Fixes ship no later than the next patch."),
    Phrase("date", r"\bno later than <<COUNT>> <<UNIT>>\b", "Fixes ship no later than six months out."),
)

GUARDS: dict[str, Guard] = {
    "TECH": Guard(
        blockers=(
            Alt(r"\btemporary\b", "The temporary directory will be deleted when the process exits."),
            Alt(r"\block file\b", "The lock file will be removed after the run."),
            Alt(r"\bcache\b", "Stale entries will be dropped from the cache."),
            Alt(r"\brows?\b", "The command will delete the selected rows after confirmation."),
            Alt(r"\brecords?\b", "The filter selects records due 2027-01-01."),
            Alt(r"\bcolumns?\b", "`column delete` will remove the column from the table."),
            Alt(r"\bcertificates?\b", "The certificate rotates on 2027-04-01."),
            Alt(r"\btokens?\b", "A token stays usable for at least 90 days."),
            Alt(r"\blogs?\b", "The service retains audit logs for at least 30 days."),
            Alt(r"\bvalid\b", "The key is valid for at least 90 days."),
            Alt(r"\bexpir\w*", "The signing key expires on 2027-04-01."),
            Alt(r"\bcreated\b", "Items created on or after 2027-01-01 are exported."),
            Alt(r"\bmodified\b", "Items modified on or after 2027-01-01 are exported."),
            Alt(r"\bsample\b", "The sample order is due 2027-02-01."),
            Alt(r"\bfixture\b", "The fixture uses 2027-12-31 as its date."),
        ),
        markers=(
            Alt(r"\bv?\d+\.\d+\b", "The cache layout will be removed in 3.0."),
            Alt(r"\bv\d+\b", "The old cache will be dropped in v3."),
            Alt(r"`--[a-z]", "The `--cache-dir` switch will be removed."),
            Alt(r"\balias\b", "The rows alias will be removed."),
            Alt(r"\bflag\b", "The cache flag will be removed."),
            Alt(r"\bsupport for\b", "Support for the token cache will be dropped."),
            Alt(r"\bnext major\b", "The token cache goes away in the next major."),
            Alt(r"\bnext minor\b", "The token cache goes away in the next minor."),
            Alt(r"\bnext release\b", "The token cache goes away in the next release."),
            Alt(r"\bdeprecat\w*", "The cache is deprecated and will be removed."),
        ),
    ),
    "THIRD_PARTY": Guard(blockers=(
        Alt(r"^\W*(?:the )?microsoft\b", "Microsoft's legacy endpoint will be removed in 2027."),
        Alt(r"^\W*(?:the )?dataverse\b", "Dataverse will retire the old Web API version in 2027."),
        Alt(r"^\W*(?:the )?azure\b", "Azure will stop issuing classic keys in 2027."),
        Alt(r"^\W*(?:the )?github\b", "GitHub will remove the old runner image in 2027."),
    )),
    # A policy frame that is really about a thrown exception.
    "CODE_EXCEPTION": Guard(blockers=(
        Alt(r"\bthrow\w*", "The command throws an exception for a missing profile."),
        Alt(r"\brais\w*", "The parser raises exceptions for unknown verbs."),
        Alt(r"\bcatch\w*", "The CLI catches exceptions for missing profiles."),
        Alt(r"\bcaught\b", "Exceptions for timeouts are caught by the CLI."),
        Alt(r"\bunhandled\b", "Unhandled exceptions for bad input go to stderr."),
        Alt(r"\bhandler\b", "The handler takes exceptions for bad input."),
        Alt(r"\bwrap\w*", "The service wraps exceptions for bad input."),
        Alt(r"\binner exception", "The cause is the inner exception."),
        Alt(r"\bmaps?\b", "The mapper maps exceptions to exit codes."),
        Alt(r"\bconverts?\b", "The mapper converts exceptions to exit codes."),
        # An exception to a data shape or schema is a technical statement.
        Alt(r"\bshape\b", "The new key is the one exception to the JSON shape."),
        Alt(r"\bschema\b", "The new key is the one exception to the schema."),
    )),
    "TECH_EXEMPT": Guard(blockers=(
        Alt(r"\bcheck\b", "`id` is exempt from the brace-GUID check."),
        Alt(r"\bwarnings?\b", "Generated files are exempt from warnings."),
        Alt(r"\banaly[sz]ers?\b", "Generated files are exempt from the analyzer."),
        Alt(r"\bvalidation\b", "The name field is exempt from validation."),
        # An exemption whose subject is a code path is build configuration.
        Alt(r"`[^`\s]*/[^`\s]*`", "Files under `Generated/` are exempt."),
        # Compiler and analyzer rule ids.
        Alt(r"\b[A-Z]{2,4}\d{3,4}\b", "CS1591 has no `[GeneratedCode]` exemption."),
    )),
    "TECH_ACTOR": Guard(blockers=(
        Alt(r"\bclient\b", "The client agreed to use gzip."),
        Alt(r"\bserver\b", "The server accepted the upload and agreed to compress it."),
        Alt(r"\bhandshake\b", "Both peers agreed on a version during the handshake."),
        Alt(r"\bplanner\b", "The planner decided to use a hash join."),
        Alt(r"\boptimi[sz]er\b", "The optimizer chose a nested loop."),
        Alt(r"\brequest\b", "The request was accepted."),
        Alt(r"\bconnection\b", "The connection was accepted."),
        Alt(r"\bruntime\b", "Each tool is decided at runtime."),
        Alt(r"\boperator\b", "The operator chose shakedown mode."),
        # A sentence about a command or input in a code span: Dataverse or
        # the CLI accepted it, technically.
        Alt(r"^\W*`", "`forms set-xml` with two controls was accepted."),
    )),
    # A requirement word that governs a held approval or sign-off ("must
    # have maintainer approval"): a requirement, not a record that it was given.
    "REQUIREMENT": Guard(blockers=(
        Alt(r"\bonly with (?:[\w'-]+ ){0,3}approval\b", "Pull requests merge only with maintainer approval."),
        Alt(r"\bmust (?:[\w'-]+ ){0,3}approval\b", "A release must have maintainer approval."),
        Alt(r"\bneeds? (?:[\w'-]+ ){0,3}approval\b", "A release needs to have approval."),
        Alt(r"\brequir\w* (?:[\w'-]+ ){0,4}approval\b", "Tagging requires that the release has approval."),
        Alt(r"\bmust (?:[\w'-]+ ){0,3}sign-?off\b", "A stable tag must have sign-off."),
        Alt(r"\bneeds? (?:[\w'-]+ ){0,3}sign-?off\b", "A stable tag needs to have sign-off."),
        Alt(r"\brequir\w* (?:[\w'-]+ ){0,4}sign-?off\b", "Tagging requires that the tag has sign-off."),
    )),
    "CONDITIONAL": Guard(blockers=(
        Alt(r"\bonce\b", "Dates are set once the release is approved."),
        Alt(r"\bwhen\b", "Dates are set when the release is approved."),
        Alt(r"\buntil\b", "Nothing ships until the release is approved."),
        Alt(r"\bif\b", "The tag is pushed if the release is approved."),
        Alt(r"\bunless\b", "Nothing ships unless the release is approved."),
    )),
    # "approved" opening a clause and followed by neither "by" nor ":" is an
    # adjective ("Approved runs are listed", "On 2026-11-01, approved run X").
    "ADJECTIVE": Guard(blockers=(
        Alt(r"^\W*approved (?!by\b)", "Approved runs are listed in the ledger."),
        Alt(r"[,;]\s*approved (?!by\b)", "On Monday, approved run 6d83 passed."),
        Alt(r"\bseparately approved\b", "A separately approved setup was used."),
        Alt(r"\bseparate approved\b", "The separate approved setup passed."),
        Alt(r"\bapproved (?:[\w-]+ ){0,3}fixtures?\b", "Josh used a newly approved live fixture."),
    )),
    # A spec's "**Decision:**" heading line records design rationale.
    "DESIGN_RECORD": Guard(blockers=(
        Alt(r"^\W*\*\*decision:?\*\*", "**Decision:** Keep the rules in one document."),
    )),
    "HABITUAL": Guard(blockers=(
        Alt(r"\bevery\b", "The maintainer approves every release."),
        Alt(r"\beach\b", "The maintainer approves each release."),
        Alt(r"\ball\b", "The maintainer approves all releases."),
    )),
}
# Table columns whose cells are decisions: a future date under a date-like
# header, a version under a removal-like header.
DECISION_COLUMNS: tuple[tuple[str, str, Alt], ...] = (
    ("date", "date", Alt(r"^when$", "| Item | When |\n|---|---|\n| Stable | 2027-01-15 |\n")),
    ("date", "date", Alt(r"^(?:target )?date$", "| Item | Date |\n|---|---|\n| Stable | 2027-01-15 |\n")),
    ("date", "date", Alt(r"^target$", "| Item | Target |\n|---|---|\n| Stable | 2027-01-15 |\n")),
    ("date", "date", Alt(r"^deadline$", "| Item | Deadline |\n|---|---|\n| Stable | 2027-01-15 |\n")),
    ("date", "date", Alt(r"^due$", "| Item | Due |\n|---|---|\n| Stable | 2027-01-15 |\n")),
    ("removal", "version", Alt(r"^removal$", "| Flag | Removal |\n|---|---|\n| `-e` | 3.0 |\n")),
    ("removal", "version", Alt(r"^removed in$", "| Flag | Removed in |\n|---|---|\n| `-e` | 3.0 |\n")),
    ("removal", "version", Alt(r"^removal version$", "| Flag | Removal version |\n|---|---|\n| `-e` | 3.0 |\n")),
)

MONTHS = {
    name.lower(): index
    for index in range(1, 13)
    for name in (calendar.month_name[index], calendar.month_abbr[index])
}
MONTHS["sept"] = 9
ORDINAL_SUFFIXES = {"st", "nd", "rd", "th"}
MACRO_RE = re.compile(r"<<([A-Z_]+)>>")
VERSION_IN_CELL_RE = re.compile(r"\bv?\d+\.\d+(?:\.\d+)?\b")

PROPOSED_PREFIX_RE = re.compile(r"^\W*proposed\W*[:—–-]", re.IGNORECASE)
PROPOSED_RES = (
    re.compile(r"\(proposed\)", re.IGNORECASE),
    re.compile(r"\b(?:is|are) proposed\b", re.IGNORECASE),
    re.compile(r"\bproposed (?:removal|date|deadline|schedule|exception|timeline|approval)\b", re.IGNORECASE),
)

FENCE_RE = re.compile(r"^\s*(```|~~~)")
HEADING_RE = re.compile(r"^\s*#{1,6}\s")
LIST_ITEM_RE = re.compile(r"^\s*(?:[-*+]|\d+[.)])\s+")
TABLE_ROW_RE = re.compile(r"^\s*\|")
TABLE_SEPARATOR_RE = re.compile(r"^\s*\|?\s*:?-{3,}")
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
    guards: tuple[str, ...]


def build_matchers(
    phrases: Sequence[Phrase] = DECISION_PHRASES,
    fragments: Mapping[str, Sequence[Alt]] = FRAGMENTS,
) -> list[Matcher]:
    """Expand every phrase over its fragments' alternatives."""
    matchers: list[Matcher] = []
    for phrase in phrases:
        names = MACRO_RE.findall(phrase.pattern)
        choices = [[alt.pattern for alt in fragments[name]] for name in names]
        flags = 0 if phrase.case_sensitive else re.IGNORECASE
        for combo in itertools.product(*choices):
            pattern = phrase.pattern
            for name, alternative in zip(names, combo):
                pattern = pattern.replace(f"<<{name}>>", f"(?:{alternative})", 1)
            has_date = "DATE" in names or "(?P<y>" in phrase.pattern
            matchers.append(Matcher(phrase.category, re.compile(pattern, flags), has_date, phrase.guards))
    return matchers


def build_guards(guards: Mapping[str, Guard] = GUARDS) -> dict[str, tuple[list[re.Pattern[str]], list[re.Pattern[str]]]]:
    return {
        name: ([re.compile(a.pattern, re.IGNORECASE) for a in guard.blockers],
               [re.compile(a.pattern, re.IGNORECASE) for a in guard.markers])
        for name, guard in guards.items()
    }


_MATCHERS = build_matchers()
_GUARDS = build_guards()


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


# A date or removal inside a link target, a URL, a code span or quoted text
# (a file name such as "review-2026-11.md", a sample value, a UI string such as
# "Will delete: 3 steps") is not one the sentence commits to. They are blanked
# out, keeping positions, before dates and removals are read.
NOT_PROSE_RES = (
    re.compile(r"\]\([^)]*\)"),
    re.compile(r"https?://\S+"),
    re.compile(r"`[^`]*`"),
    re.compile(r'"[^"]*"'),
)


def prose_only(sentence: str) -> str:
    for pattern in NOT_PROSE_RES:
        sentence = pattern.sub(lambda m: " " * len(m.group(0)), sentence)
    return sentence


def _blocked(sentence: str, names: Sequence[str], guards) -> bool:
    for name in names:
        blockers, markers = guards[name]
        if any(b.search(sentence) for b in blockers) and not any(m.search(sentence) for m in markers):
            return True
    return False


def decision_match(
    sentence: str, today: date, matchers: Optional[Sequence[Matcher]] = None, guards=None,
) -> Optional[tuple[str, int]]:
    """Return (category, match position) of the first decision wording, or None."""
    guards = guards if guards is not None else _GUARDS
    prose = prose_only(sentence)
    for matcher in matchers if matchers is not None else _MATCHERS:
        text = prose if matcher.has_date or matcher.category == "removal" else sentence
        for match in matcher.regex.finditer(text):
            if matcher.has_date:
                end = period_end(match.groupdict())
                if end is None or end <= today:
                    continue
            if _blocked(sentence, matcher.guards, guards):
                break
            return matcher.category, match.start()
    return None


def decision_category(sentence: str, today: date, matchers=None, guards=None) -> Optional[str]:
    found = decision_match(sentence, today, matchers, guards)
    return found[0] if found else None


def future_date_in(text: str, today: date) -> bool:
    for alt in FRAGMENTS["DATE"]:
        for match in re.finditer(alt.pattern, text, re.IGNORECASE):
            end = period_end(match.groupdict())
            if end is not None and end > today:
                return True
    return False


def decision_link_re(repository: str) -> re.Pattern[str]:
    owner, name = (re.escape(part) for part in repository.split("/", 1))
    return re.compile(
        r"(?<![\w/?=&%.#:@-])https://github\.com/" + owner + "/" + name + "/"
        r"(?:issues/\d+#(?:issuecomment-\d+|issue-\d+)"
        r"|pull/\d+#(?:issuecomment-\d+|discussion_r\d+|pullrequestreview-\d+))(?![\w-])",
        re.IGNORECASE,
    )


def is_proposed(sentence: str, position: int = 0) -> bool:
    """A leading "Proposed:", or a qualifier in the clause that holds the decision."""
    if PROPOSED_PREFIX_RE.search(sentence):
        return True
    start = sentence.rfind(";", 0, position) + 1
    end = sentence.find(";", position)
    clause = sentence[start:] if end < 0 else sentence[start:end]
    return any(pattern.search(clause) for pattern in PROPOSED_RES)


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


def _cells(line: str) -> list[str]:
    return [cell.strip() for cell in line.strip().strip("|").split("|")]


def _blocks(lines: Sequence[str]) -> list[tuple[Optional[list[str]], list[tuple[int, str]]]]:
    """Group prose lines into blocks: (table header or None, [(line index, text)]).

    A table row is its own block and carries its table's header cells (None
    for prose and for the header row itself).
    """
    blocks: list[tuple[Optional[list[str]], list[tuple[int, str]]]] = []
    current: list[tuple[int, str]] = []
    in_fence = False
    header: Optional[list[str]] = None

    def flush() -> None:
        nonlocal current
        if current:
            blocks.append((None, current))
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
            header = None
            continue
        if TABLE_ROW_RE.match(line):
            flush()
            if TABLE_SEPARATOR_RE.match(line):
                continue
            following = lines[index + 1] if index + 1 < len(lines) else ""
            if TABLE_SEPARATOR_RE.match(following):
                header = [cell.lower() for cell in _cells(line)]
                blocks.append(([], [(index, line)]))
            else:
                blocks.append((header or [], [(index, line)]))
            continue
        header = None
        if LIST_ITEM_RE.match(line):
            flush()
        current.append((index, line))
    flush()
    return blocks


def column_decision(header: Sequence[str], row: str, today: date,
                    columns=DECISION_COLUMNS) -> Optional[str]:
    """A decision stated by a table cell under a decision column, or None."""
    for position, cell in enumerate(_cells(row)):
        if position >= len(header) or not cell:
            continue
        for category, kind, alt in columns:
            if not re.search(alt.pattern, header[position], re.IGNORECASE):
                continue
            if kind == "date" and future_date_in(cell, today):
                return category
            if kind == "version" and VERSION_IN_CELL_RE.search(cell):
                return category
    return None


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
    for header, block in _blocks(lines):
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
        sentences = [text] if header is not None else split_sentences(text)
        position = 0
        for sentence in sentences:
            start = text.index(sentence, position)
            end = start + len(sentence)
            position = end
            sentence_lines = set(owners[start:end]) if sentence else set()
            if changed is not None and not (sentence_lines & changed):
                continue
            found_match = decision_match(sentence, today)
            category = found_match[0] if found_match else None
            where = found_match[1] if found_match else 0
            if category is None and header:
                category = column_decision(header, sentence, today)
            if category and not link_re.search(sentence) and not is_proposed(sentence, where):
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
