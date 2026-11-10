#!/usr/bin/env python3
"""Validate and report a release ledger; never execute or authorize release actions.

Checks declared evidence consistency, not the authenticity/completeness of those
declarations. Keep receipts and exact-head/action authorization checks separate.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import sys


class LedgerError(ValueError):
    """The record cannot support the stated release status."""


def require(condition, message):
    if not condition:
        raise LedgerError(message)


def fields(value, required, optional=(), label="record"):
    require(isinstance(value, dict), f"{label}: expected object")
    missing = set(required) - value.keys()
    extra = value.keys() - set(required) - set(optional)
    require(not missing, f"{label}: missing fields {sorted(missing)}")
    require(not extra, f"{label}: unknown fields {sorted(extra)}")


def text(value, label):
    require(isinstance(value, str) and bool(value.strip()), f"{label}: expected nonempty text")


def texts(value, label, allow_empty=False):
    require(isinstance(value, list), f"{label}: expected list")
    require(allow_empty or bool(value), f"{label}: cannot be empty")
    for item in value:
        text(item, label)
    require(len(value) == len(set(value)), f"{label}: duplicate entries")


def digest(value, length, label):
    require(isinstance(value, str) and re.fullmatch(f"[0-9a-f]{{{length}}}", value),
            f"{label}: expected {length} lowercase hexadecimal characters")


def timestamp(value, label, now=None):
    text(value, label)
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise LedgerError(f"{label}: invalid ISO timestamp") from exc
    require(result.tzinfo is not None and result.utcoffset().total_seconds() == 0,
            f"{label}: use an explicit UTC timestamp")
    if now is not None:
        require(result <= now, f"{label}: future observations are invalid")
    return result


def candidate(value, label):
    fields(value, ("repository", "commit", "tree"), label=label)
    text(value["repository"], f"{label}.repository")
    for key in ("commit", "tree"):
        digest(value[key], 40, f"{label}.{key}")


BINDING_FIELDS = ("candidate", "inputs_sha256", "toolchain", "definition_sha256", "platform", "claims")


def binding(value, label):
    candidate(value["candidate"], f"{label}.candidate")
    for name in ("inputs_sha256", "toolchain"):
        require(isinstance(value[name], dict) and bool(value[name]), f"{label}.{name}: expected nonempty object")
        for key, item in value[name].items():
            text(key, f"{label}.{name} key")
            if name == "inputs_sha256":
                digest(item, 64, f"{label}.{name}.{key}")
            else:
                text(item, f"{label}.{name}.{key}")
    digest(value["definition_sha256"], 64, f"{label}.definition_sha256")
    text(value["platform"], f"{label}.platform")
    texts(value["claims"], f"{label}.claims")


def evidence_record(value, now):
    fields(value, (*BINDING_FIELDS, "id", "source_refs", "outcome", "observed_utc", "invalidation"),
           ("expires_utc",), "evidence")
    text(value["id"], "evidence.id")
    texts(value["source_refs"], "evidence.source_refs")
    binding(value, value["id"])
    require(value["outcome"] in ("passed", "failed", "skipped", "unknown"), f"{value['id']}: invalid outcome")
    observed = timestamp(value["observed_utc"], f"{value['id']}.observed_utc", now)
    texts(value["invalidation"], f"{value['id']}.invalidation")
    if "expires_utc" in value:
        expiry = timestamp(value["expires_utc"], f"{value['id']}.expires_utc")
        require(expiry > observed, f"{value['id']}: expiry must follow observation")


def current_pass(value, now):
    return value["outcome"] == "passed" and (
        "expires_utc" not in value or timestamp(value["expires_utc"], "expires_utc") > now)


def reuse_evidence(old, target, now=None):
    """Compare one evidence record to a new binding; no external reads or writes."""
    now = now or datetime.now(timezone.utc)
    evidence_record(old, now)
    fields(target, BINDING_FIELDS, label="target binding")
    binding(target, "target binding")
    reasons = []
    if not current_pass(old, now):
        reasons.append("prior evidence did not pass or has expired")
    if old["candidate"]["repository"] != target["candidate"]["repository"]:
        reasons.append("repository changed")
    for key in BINDING_FIELDS[1:]:
        before, after = old[key], target[key]
        changed = set(before) != set(after) if key == "claims" else before != after
        if changed:
            reasons.append(f"{key} changed")
    return {"reusable": not reasons, "reasons": reasons, "observed_utc": old["observed_utc"],
            "nonclaims": ["Declared scoped inputs only; source authenticity and input completeness are not verified.",
                          "Recheck ancestry, merge tree and exact-head action authorization separately."]}


def index_records(value, label):
    require(isinstance(value, list), f"{label}: expected list")
    result = {}
    for item in value:
        require(isinstance(item, dict), f"{label}: expected objects")
        identifier = item.get("id")
        text(identifier, f"{label}.id")
        require(identifier not in result, f"{label}: duplicate id {identifier}")
        result[identifier] = item
    return result


def references(value, key, index, label, allow_empty=True):
    texts(value[key], f"{label}.{key}", allow_empty)
    for identifier in value[key]:
        require(identifier in index, f"{label}.{key}: unknown reference {identifier}")


def validate(ledger, now=None):
    """Reject inconsistent status; a valid held ledger is still valid data."""
    now = now or datetime.now(timezone.utc)
    fields(ledger, ("schema_version", "updated_utc", "owner", "candidate", "scope", "sources",
                    "authorizations", "evidence", "reviews", "gates"), label="ledger")
    require(type(ledger["schema_version"]) is int and ledger["schema_version"] == 1, "unsupported schema_version")
    timestamp(ledger["updated_utc"], "updated_utc", now)
    text(ledger["owner"], "owner")
    candidate(ledger["candidate"], "candidate")
    fields(ledger["scope"], ("frozen", "included", "excluded"), label="scope")
    require(type(ledger["scope"]["frozen"]) is bool, "scope.frozen: expected boolean")
    for key in ("included", "excluded"):
        texts(ledger["scope"][key], f"scope.{key}", allow_empty=True)
    require(not set(ledger["scope"]["included"]) & set(ledger["scope"]["excluded"]), "scope: contradictory inclusion/exclusion")
    indexes = {key: index_records(ledger[key], key) for key in ("sources", "authorizations", "evidence", "reviews", "gates")}
    for kind in ("sources", "gates"):
        require(bool(indexes[kind]), f"{kind}: cannot be empty")
    for item in indexes["sources"].values():
        fields(item, ("id", "locator", "observed_utc"), ("sha256",), "source")
        text(item["locator"], "source.locator")
        timestamp(item["observed_utc"], "source.observed_utc", now)
        if "sha256" in item:
            digest(item["sha256"], 64, "source.sha256")
    for kind in ("authorizations", "evidence", "reviews", "gates"):
        for item in indexes[kind].values():
            require("source_refs" in item, f"{item['id']}: missing source_refs")
            references(item, "source_refs", indexes["sources"], item["id"], allow_empty=False)
    for item in indexes["authorizations"].values():
        fields(item, ("id", "source_refs", "status", "actions", "scope"), label="authorization")
        require(item["status"] in ("approved", "held", "denied"), f"{item['id']}: invalid authorization status")
        texts(item["actions"], "authorization.actions")
        text(item["scope"], "authorization.scope")
    for item in indexes["evidence"].values():
        evidence_record(item, now)
    for item in indexes["reviews"].values():
        fields(item, ("id", "source_refs", "candidate", "claims", "assumptions", "findings", "open_prerequisites",
                      "nonclaims", "whole_gate_cleared"), label="review")
        candidate(item["candidate"], f"{item['id']}.candidate")
        for key in ("claims", "assumptions", "findings", "open_prerequisites", "nonclaims"):
            texts(item[key], f"{item['id']}.{key}", allow_empty=key in ("assumptions", "findings", "open_prerequisites"))
        require(type(item["whole_gate_cleared"]) is bool, f"{item['id']}: whole_gate_cleared must be boolean")
        require(not item["whole_gate_cleared"] or not (item["findings"] or item["open_prerequisites"]),
                f"{item['id']}: unresolved review cannot clear whole gate")
    for item in indexes["gates"].values():
        fields(item, ("id", "category", "state", "required_for_release", "claims", "source_refs", "evidence_refs", "review_refs",
                      "authorization_refs", "prerequisites", "owner", "next_action"), ("candidate",), label="gate")
        if "candidate" in item:
            candidate(item["candidate"], f"{item['id']}.candidate")
        require(type(item["required_for_release"]) is bool, f"{item['id']}: required_for_release must be boolean")
        require(item["category"] in ("user_hold", "policy_requirement", "technical_blocker", "coordinator_recommendation"),
                f"{item['id']}: invalid gate category")
        require(item["state"] in ("clear", "blocked", "in_progress", "unstarted", "stale", "unknown"), f"{item['id']}: invalid state")
        for key in ("owner", "next_action"):
            text(item[key], f"{item['id']}.{key}")
        texts(item["claims"], f"{item['id']}.claims")
        for key, kind in (("evidence_refs", "evidence"), ("review_refs", "reviews"),
                          ("authorization_refs", "authorizations"), ("prerequisites", "gates")):
            references(item, key, indexes[kind], item["id"])
    visiting, visited = set(), set()

    def visit(identifier):
        require(identifier not in visiting, f"{identifier}: cyclic prerequisites")
        if identifier in visited:
            return
        visiting.add(identifier)
        for dependency in indexes["gates"][identifier]["prerequisites"]:
            visit(dependency)
        visiting.remove(identifier)
        visited.add(identifier)

    for identifier, gate in indexes["gates"].items():
        visit(identifier)
        if gate["state"] != "clear":
            continue
        require(all(indexes["gates"][key]["state"] == "clear" for key in gate["prerequisites"]),
                f"{identifier}: prerequisite is not clear")
        proof = [indexes["evidence"][key] for key in gate["evidence_refs"]]
        require(proof and all(current_pass(item, now) for item in proof), f"{identifier}: requires passing unexpired evidence")
        claims = set().union(*(set(item["claims"]) for item in proof))
        require(set(gate["claims"]) <= claims, f"{identifier}: evidence does not cover gate claims")
        reviews = [indexes["reviews"][key] for key in gate["review_refs"]]
        gate_candidate = gate.get("candidate", ledger["candidate"])
        for item in proof + reviews:
            require(all(item["candidate"][key] == gate_candidate[key] for key in ("repository", "tree")),
                    f"{identifier}: evidence/review candidate tree differs; record explicit scoped reuse before clearance")
        for item in reviews:
            require(item["whole_gate_cleared"] and set(gate["claims"]) <= set(item["claims"]),
                    f"{identifier}: delta-only or incomplete review cannot clear gate")
        approvals = [indexes["authorizations"][key] for key in gate["authorization_refs"]]
        require(all(item["status"] == "approved" for item in approvals), f"{identifier}: referenced authorization is held or denied")
        if gate["category"] == "user_hold":
            require(any(identifier in item["actions"] for item in approvals), f"{identifier}: needs scoped approval for this gate id")
    return ledger


def cell(value):
    return str(value).replace("\\", "\\\\").replace("|", "\\|").replace("\r", " ").replace("\n", " ")


def render_status(ledger, now=None):
    now = now or datetime.now(timezone.utc)
    validate(ledger, now)
    held = sum(gate["state"] != "clear" and gate["required_for_release"] for gate in ledger["gates"])
    optional = [gate for gate in ledger["gates"] if not gate["required_for_release"]]
    optional_open = sum(gate["state"] != "clear" for gate in optional)
    required_total = len(ledger["gates"]) - len(optional)
    result = [f"Release ledger observed {ledger['updated_utc']}",
              f"Candidate: {ledger['candidate']['repository']} @ {ledger['candidate']['commit']}",
              f"Tree: {ledger['candidate']['tree']}",
              f"Owner: {cell(ledger['owner'])}; scope frozen: {ledger['scope']['frozen']}.",
              f"{held}/{required_total} required gates uncleared; {optional_open}/{len(optional)} optional work records uncleared.",
              "Included: " + cell("; ".join(ledger["scope"]["included"]) or "None declared"),
              "Excluded: " + cell("; ".join(ledger["scope"]["excluded"]) or "None declared"),
              "This validates declared records only. It does not authorize a merge, live test, signing or publication.", "",
              "| Gate | Required | State | Category | Owner | Evidence | Sources | Authorization | Target | Next action |",
              "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    for gate in ledger["gates"]:
        gate_candidate = gate.get("candidate", ledger["candidate"])
        row = {**gate, "target": f"{gate_candidate['repository']}@{gate_candidate['commit']} (tree {gate_candidate['tree']})"}
        result.append("| " + " | ".join(cell(", ".join(row[key])) if key.endswith("_refs") else cell(row[key])
                                       for key in ("id", "required_for_release", "state", "category", "owner", "evidence_refs", "source_refs",
                                                   "authorization_refs", "target", "next_action")) + " |")
    result.extend(["", "| Evidence | Outcome | Observed UTC | Expiry | Sources | Invalidated by |",
                   "| --- | --- | --- | --- | --- | --- |"])
    for item in ledger["evidence"]:
        expiry = item.get("expires_utc", "Not declared")
        if "expires_utc" in item and timestamp(item["expires_utc"], "expires_utc") <= now:
            expiry += " (expired)"
        result.append("| " + " | ".join(cell(value) for value in (item["id"], item["outcome"], item["observed_utc"],
                                                                       expiry, ", ".join(item["source_refs"]),
                                                                       "; ".join(item["invalidation"]))) + " |")
    result.extend(["", "| Authorization | Status | Actions | Exact scope | Sources |", "| --- | --- | --- | --- | --- |"])
    for item in ledger["authorizations"]:
        result.append("| " + " | ".join(cell(value) for value in (item["id"], item["status"], ", ".join(item["actions"]),
                                                                       item["scope"], ", ".join(item["source_refs"]))) + " |")
    result.extend(["", "| Source | Locator | Observed UTC | SHA256 |", "| --- | --- | --- | --- |"])
    for item in ledger["sources"]:
        result.append("| " + " | ".join(cell(value) for value in (item["id"], item["locator"], item["observed_utc"],
                                                                       item.get("sha256", "Not declared"))) + " |")
    return "\n".join(result) + "\n"


def read_json(path):
    def unique_pairs(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, f"duplicate JSON key {key}")
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique_pairs)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for command in ("validate", "status"):
        commands.add_parser(command).add_argument("ledger", type=Path)
    reuse = commands.add_parser("reuse", help="compare an evidence JSON record with a target binding JSON")
    reuse.add_argument("evidence", type=Path)
    reuse.add_argument("binding", type=Path)
    args = parser.parse_args(argv)
    try:
        if args.command == "reuse":
            result = reuse_evidence(read_json(args.evidence), read_json(args.binding))
            print(json.dumps(result, indent=2))
            return 0 if result["reusable"] else 1
        ledger = read_json(args.ledger)
        if args.command == "status":
            print(render_status(ledger), end="")
        else:
            validate(ledger)
            print("Ledger consistency validated. No action authorized.")
        return 0
    except (LedgerError, OSError, ValueError) as exc:
        print(f"release ledger: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
