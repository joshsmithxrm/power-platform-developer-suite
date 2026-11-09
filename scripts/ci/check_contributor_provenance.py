#!/usr/bin/env python3
"""Validate supplied contributor provenance snapshots; never contact GitHub or mutate files.

Supported credit footer: the final nonempty paragraph consists entirely of single-line
``Key: value`` trailers, separated from any body by a blank line. Co-authored-by values
must be ``Name <email>``. Continuations and body mentions do not establish credit.
"""

import argparse
import json
import re
from pathlib import Path

STAGES = ("plan", "pre-merge", "post-landing", "post-release")
LIMITATIONS = [
    "Validates only supplied local declarations and snapshots; does not independently verify GitHub truth.",
    "Only snapshots required by the requested stage are checked; other snapshots do not establish credit.",
    "Does not detect semantic overlap, infer identities, grant approval, send replies, close PRs, merge, or publish.",
    "Credit checks support exact author identity or single-line final-block Co-authored-by trailers only.",
    "Release acknowledgement is literal matching, not semantic credit detection.",
]
PR = re.compile(r"https://github\.com/[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_.-]{1,100}/pull/[1-9][0-9]*\Z")
HEAD = re.compile(r"[0-9a-fA-F]{40}\Z")
EMAIL = re.compile(r"[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+\Z")
LOGIN = re.compile(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\Z")
TRAILER = re.compile(r"([A-Za-z0-9-]+): (\S.*)\Z")
COAUTHOR = re.compile(r"([^<>]+) <([^<>]+)>\Z")


class InvalidInput(ValueError):
    """Malformed input, with a fixed message safe to render."""


def object_fields(value, required, optional=()):
    if not isinstance(value, dict) or set(value) - set(required) - set(optional):
        raise InvalidInput("Expected an object with supported fields only.")
    if set(required) - set(value):
        raise InvalidInput("A required input field is missing.")


def text(value, limit=4096, multiline=False):
    if not isinstance(value, str) or len(value) > limit:
        raise InvalidInput("Expected a bounded text field.")
    if any(ord(c) < 32 and not (multiline and c in "\n\r\t") or ord(c) == 127 for c in value):
        raise InvalidInput("A text field contains unsupported control characters.")
    return bool(value.strip())


def named_owner(value):
    return text(value, 200) and value.strip().casefold() not in {
        "unknown", "tbd", "todo", "none", "n/a", "unassigned", "pending", "?"
    }


def version(value):
    if type(value) is not int or value != 1:
        raise InvalidInput("Unsupported schema_version; expected integer 1.")


def pr_url(value):
    if not isinstance(value, str) or not PR.fullmatch(value) or value.split("/")[4] in (".", ".."):
        raise InvalidInput("Expected a canonical https://github.com owner/repository/pull/number URL.")


def sha(value):
    if not isinstance(value, str) or not HEAD.fullmatch(value):
        raise InvalidInput("Expected an exact 40-character hexadecimal commit identifier.")


def identity(value, contributor=False):
    object_fields(value, ("name", "email", "verified", "evidence"), ("login",) if contributor else ())
    if type(value["verified"]) is not bool:
        raise InvalidInput("Identity verified must be a boolean.")
    known = []
    for key in ("name", "email"):
        field = value[key]
        if field is None and not value["verified"]:
            known.append(False)
            continue
        present = text(field, 320)
        if present and (field != field.strip() or "<" in field or ">" in field):
            raise InvalidInput("Identity fields must be single-line canonical names and email addresses.")
        if key == "email" and present and not EMAIL.fullmatch(field):
            raise InvalidInput("Identity email is malformed.")
        known.append(present)
    if "login" in value and (not isinstance(value["login"], str) or not LOGIN.fullmatch(value["login"])):
        raise InvalidInput("Contributor login is malformed.")
    evidence = text(value["evidence"])
    if value["verified"] and not (all(known) and evidence):
        raise InvalidInput("A verified identity requires a name, email and evidence.")
    return value["verified"] and all(known) and evidence


def final_coauthors(message):
    """Return exact identities from the supported final trailer block, never from prose."""
    lines = message.replace("\r\n", "\n").split("\n")
    while lines and not lines[-1].strip():
        lines.pop()
    start = len(lines)
    while start and lines[start - 1].strip():
        start -= 1
    block = lines[start:]
    matches = [TRAILER.fullmatch(line) for line in block]
    if not block or not any(line.strip() for line in lines[:start]) or not all(matches):
        return set()
    credited = set()
    for match in matches:
        if match[1].lower() != "co-authored-by":
            continue
        person = COAUTHOR.fullmatch(match[2])
        if not person or person[1] != person[1].strip() or not EMAIL.fullmatch(person[2]):
            return set()
        credited.add((person[1], person[2]))
    return credited


def validate(record, stage, proposal=None, landing=None, release=None):
    """Return (exit_code, JSON-ready result). Input errors never echo supplied values."""
    result = {"schema_version": 1, "stage": stage if stage in STAGES else None,
              "scope": "supplied-local-snapshots-only", "limitations": LIMITATIONS,
              "valid": False, "closeout_complete": False, "pending_closeout": [], "errors": []}
    errors = result["errors"]
    try:
        if stage not in STAGES:
            raise InvalidInput("Unsupported validation stage.")
        object_fields(record, ("schema_version", "mode", "reason"), ("landing_pr", "landing_head", "sources"))
        version(record["schema_version"])
        if record["mode"] not in ("none", "direct", "incorporated"):
            raise InvalidInput("Unsupported contribution mode.")
        result["mode"] = record["mode"]
        if not text(record["reason"]):
            errors.append("An explicit reason is required.")
        sources = record.get("sources", [])
        if not isinstance(sources, list) or len(sources) > 100:
            raise InvalidInput("Sources must be a list of at most 100 records.")
        if record["mode"] == "none":
            if sources or "landing_pr" in record or "landing_head" in record or any(x is not None for x in (proposal, landing, release)):
                errors.append("A none declaration must not include contribution provenance or snapshots.")
            result["closeout_complete"] = not errors
        else:
            object_fields(record, ("schema_version", "mode", "reason", "landing_pr", "landing_head", "sources"))
            pr_url(record["landing_pr"])
            sha(record["landing_head"])
            if not sources:
                errors.append("Applicable provenance requires at least one source.")
            if record["mode"] == "direct" and len(sources) != 1:
                errors.append("Direct landing requires exactly one source PR.")
            contributors = []
            seen = set()
            for index, source in enumerate(sources, 1):
                object_fields(source, ("pr", "head", "contributors", "disposition", "communication"))
                pr_url(source["pr"])
                sha(source["head"])
                if source["pr"] in seen:
                    errors.append("Duplicate source PR records are not supported.")
                seen.add(source["pr"])
                same_pr = source["pr"] == record["landing_pr"]
                if record["mode"] == "direct" and (not same_pr or source["head"].lower() != record["landing_head"].lower()):
                    errors.append("Direct source PR/head must equal landing PR/head.")
                if record["mode"] == "incorporated" and same_pr:
                    errors.append("Incorporated source PRs must differ from the landing PR.")
                people = source["contributors"]
                if not isinstance(people, list) or not 1 <= len(people) <= 100:
                    raise InvalidInput("Each source requires 1 to 100 contributor identity records.")
                for person in people:
                    verified = identity(person, contributor=True)
                    if not text(person["evidence"]):
                        errors.append("Contributor identity requires evidence or an explanation of uncertainty.")
                    if not verified and stage != "plan":
                        errors.append("Contributor identity is unverified; this stage cannot verify credit.")
                    contributors.append(person)
                disposition = source["disposition"]
                object_fields(disposition, ("decision", "reason", "owner", "state"), ("evidence",))
                if disposition["decision"] not in ("merge-original", "close-as-incorporated", "keep-open") or disposition["state"] not in ("planned", "pending-approval", "done"):
                    raise InvalidInput("Unsupported disposition decision or state.")
                if record["mode"] == "direct" and disposition["decision"] != "merge-original":
                    errors.append("Direct landing disposition must be merge-original.")
                if record["mode"] == "incorporated" and disposition["decision"] == "merge-original":
                    errors.append("Incorporated sources require close-as-incorporated or keep-open disposition.")
                reason_present = text(disposition["reason"])
                owner_present = named_owner(disposition["owner"])
                if not reason_present or not owner_present:
                    errors.append("Disposition requires a reason and named owner.")
                communication = source["communication"]
                object_fields(communication, ("owner", "state", "reply"), ("evidence",))
                if communication["state"] not in ("prepared", "pending-approval", "sent"):
                    raise InvalidInput("Unsupported communication state.")
                owner_present = named_owner(communication["owner"])
                reply_present = text(communication["reply"], 20000, multiline=True)
                if not owner_present or not reply_present:
                    errors.append("Communication requires a named owner and exact nonempty reply.")
                for kind, entry, complete in (("disposition", disposition, "done"), ("communication", communication, "sent")):
                    evidence = text(entry.get("evidence", ""))
                    if entry["state"] == complete and not evidence:
                        errors.append("Completed closeout requires supplied evidence.")
                    if entry["state"] != complete or not evidence:
                        result["pending_closeout"].append({"source": index, "item": kind, "owner": entry["owner"], "state": entry["state"]})
                if stage == "pre-merge" and disposition["state"] == "done" and disposition["decision"] == "merge-original":
                    errors.append("A pre-merge record cannot claim merge-original is already done.")
            result["closeout_complete"] = not result["pending_closeout"]
            if stage == "pre-merge":
                check_commit(proposal, record, contributors, errors, proposed=True)
            elif stage in ("post-landing", "post-release"):
                check_commit(landing, record, contributors, errors, proposed=False)
            if stage == "post-release":
                check_release(release, landing, record, sources, errors)
                if result["pending_closeout"]:
                    errors.append("Required contributor closeout remains unfinished.")
        result["valid"] = not errors
        if errors:
            result["closeout_complete"] = False
        return (0 if not errors else 1), result
    except InvalidInput as exc:
        result["valid"] = False
        result["closeout_complete"] = False
        result["pending_closeout"] = []  # Do not echo partially validated input on malformed records.
        errors.append(str(exc))
        return 2, result


def check_commit(snapshot, record, contributors, errors, proposed):
    if snapshot is None:
        errors.append("The stage requires a proposal snapshot." if proposed else "The stage requires a landing snapshot.")
        return
    required = ("schema_version", "landing_pr", "head", "author", "message", "evidence", "method" if proposed else "commit")
    object_fields(snapshot, required)
    version(snapshot["schema_version"])
    pr_url(snapshot["landing_pr"])
    sha(snapshot["head"])
    if proposed and snapshot["method"] != "squash":
        raise InvalidInput("Only an explicit squash proposal is supported.")
    if not proposed:
        sha(snapshot["commit"])
    if snapshot["landing_pr"] != record["landing_pr"] or snapshot["head"].lower() != record["landing_head"].lower():
        errors.append("Snapshot landing PR/head is stale or mismatched.")
    verified_author = identity(snapshot["author"])
    if not verified_author:
        errors.append("Snapshot author identity is unverified.")
    message_present = text(snapshot["message"], 100000, multiline=True)
    evidence_present = text(snapshot["evidence"])
    if not message_present or not evidence_present:
        errors.append("Commit snapshot requires the actual message and evidence.")
    credits = final_coauthors(snapshot["message"])
    if verified_author:
        credits.add((snapshot["author"]["name"], snapshot["author"]["email"]))
    for person in contributors:
        if (person["name"], person["email"]) not in credits:
            errors.append("A supplied contributor is not credited by actual author identity or a supported final co-author trailer.")


def check_release(snapshot, landing, record, sources, errors):
    if snapshot is None:
        errors.append("Post-release requires a release snapshot.")
        return
    object_fields(snapshot, ("schema_version", "landing_pr", "commit", "notes", "evidence"))
    version(snapshot["schema_version"])
    pr_url(snapshot["landing_pr"])
    sha(snapshot["commit"])
    if snapshot["landing_pr"] != record["landing_pr"] or landing is None or snapshot["commit"].lower() != landing["commit"].lower():
        errors.append("Release snapshot does not bind the supplied actual landing commit and PR.")
    notes_present = text(snapshot["notes"], 200000, multiline=True)
    evidence_present = text(snapshot["evidence"])
    if not notes_present or not evidence_present:
        errors.append("Release snapshot requires actual notes and evidence.")
    for source in sources:
        if not literal_mentioned(source["pr"], snapshot["notes"]):
            errors.append("Release notes omit a literal source PR URL.")
        for person in source["contributors"]:
            name = person["name"]
            login = person.get("login")
            if not (name and literal_mentioned(name, snapshot["notes"]) or login and literal_mentioned("@" + login, snapshot["notes"])):
                errors.append("Release notes omit a literal contributor name or @login.")


def literal_mentioned(token, notes):
    # Avoid crediting PR #1 from #10 or @ann from @anna; this is still only literal matching.
    return re.search(r"(?<![\w/@])" + re.escape(token) + r"(?![\w/-])", notes) is not None


def load_local(path):
    raw = str(path)
    if raw.startswith(("\\\\", "//")) or "://" in raw:
        raise InvalidInput("Only local JSON file paths are supported.")
    try:
        resolved = Path(path).resolve()
        if str(resolved).startswith(("\\\\", "//")) or not resolved.is_file():
            raise InvalidInput("Expected a local JSON file.")
        with resolved.open("rb") as stream:
            data = stream.read(1_048_577)
        if len(data) > 1_048_576:
            raise InvalidInput("Input exceeds the 1 MiB per-file limit.")

        def unique(pairs):
            value = {}
            for key, item in pairs:
                if key in value:
                    raise InvalidInput("Duplicate JSON keys are not supported.")
                value[key] = item
            return value

        def reject_constant(_):
            raise InvalidInput("Non-finite JSON numbers are not supported.")

        return json.loads(data.decode("utf-8"), object_pairs_hook=unique, parse_constant=reject_constant)
    except (OSError, UnicodeError, json.JSONDecodeError, RecursionError, ValueError) as exc:
        if isinstance(exc, InvalidInput):
            raise
        raise InvalidInput("Cannot read a bounded, well-formed UTF-8 JSON input file.") from None


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise InvalidInput("Malformed invocation; use --help for supported arguments.")


def main(argv=None):
    parser = Parser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--record", required=True)
    parser.add_argument("--stage", required=True, choices=STAGES)
    for name in ("proposal", "landing", "release"):
        parser.add_argument("--" + name)
    try:
        args = parser.parse_args(argv)
        record = load_local(args.record)
        snapshots = {name: load_local(getattr(args, name)) if getattr(args, name) else None for name in ("proposal", "landing", "release")}
        code, result = validate(record, args.stage, **snapshots)
    except InvalidInput as exc:
        code, result = 2, {"schema_version": 1, "valid": False, "scope": "supplied-local-snapshots-only",
                           "limitations": LIMITATIONS, "closeout_complete": False, "pending_closeout": [], "errors": [str(exc)]}
    print(json.dumps(result, ensure_ascii=True))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
