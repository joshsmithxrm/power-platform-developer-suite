"""Offline release evidence must not turn stale or narrow receipts into clearance."""
from copy import deepcopy
from datetime import datetime, timezone
import json
from pathlib import Path
import sys

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "ci"))
import release_ledger as ledger  # noqa: E402

NOW = datetime(2026, 11, 10, 23, 30, tzinfo=timezone.utc)
CANDIDATE = {"repository": "example/ppds", "commit": "a" * 40, "tree": "b" * 40}


def receipt():
    return {"id": "test", "source_refs": ["receipt"], "candidate": deepcopy(CANDIDATE),
            "inputs_sha256": {"source-and-lockfiles": "c" * 64}, "toolchain": {"python": "3.14.3"},
            "definition_sha256": "d" * 64, "platform": "windows-x64", "outcome": "passed",
            "observed_utc": "2026-11-10T23:00:00Z", "invalidation": ["Relevant input or runtime changes"],
            "claims": ["offline acceptance"]}


def record():
    return {"schema_version": 1, "updated_utc": "2026-11-10T23:15:00Z", "owner": "integration owner",
            "candidate": deepcopy(CANDIDATE),
            "scope": {"frozen": True, "included": ["Scoped correction"], "excluded": ["Unrelated maintenance"]},
            "sources": [{"id": "receipt", "locator": "https://example.invalid/receipt",
                         "observed_utc": "2026-11-10T23:00:00Z", "sha256": "e" * 64}],
            "authorizations": [{"id": "hold", "source_refs": ["receipt"], "status": "held",
                                "actions": ["publication"], "scope": "Publishing remains separately held"}],
            "evidence": [receipt()], "reviews": [],
            "gates": [{"id": "offline", "category": "policy_requirement", "state": "clear", "required_for_release": True,
                       "claims": ["offline acceptance"], "source_refs": ["receipt"], "evidence_refs": ["test"],
                       "review_refs": [], "authorization_refs": [], "prerequisites": [],
                       "owner": "integration owner", "next_action": "Retain evidence until invalidated"},
                      {"id": "publication", "category": "user_hold", "state": "blocked", "required_for_release": True,
                       "claims": ["published acceptance"], "source_refs": ["receipt"], "evidence_refs": [],
                       "review_refs": [], "authorization_refs": ["hold"], "prerequisites": ["offline"],
                       "owner": "maintainer", "next_action": "Review concrete publication proposal"}]}


def review():
    return {"id": "peer", "source_refs": ["receipt"], "candidate": deepcopy(CANDIDATE),
            "claims": ["offline acceptance"], "assumptions": ["No live effects"], "findings": [],
            "open_prerequisites": [], "nonclaims": ["No live or publication clearance"], "whole_gate_cleared": True}


def target(old=None):
    old = old or receipt()
    return {key: deepcopy(old[key]) for key in ledger.BINDING_FIELDS}


def test_status_preserves_holds_and_observation_identity():
    data = record()
    assert ledger.validate(data, NOW) is data
    status = ledger.render_status(data, NOW)
    assert "1/2 required gates uncleared" in status
    assert "Included: Scoped correction" in status and "Excluded: Unrelated maintenance" in status
    assert "2026-11-10T23:00:00Z" in status
    assert "user_hold" in status and "No action authorized" not in status
    assert "does not authorize" in status
    assert "https://example.invalid/receipt" in status
    assert "| hold | held | publication | Publishing remains separately held | receipt |" in status
    data["authorizations"][0]["status"] = "denied"
    assert "| hold | denied |" in ledger.render_status(data, NOW)


def test_optional_work_does_not_silently_become_a_release_blocker():
    data = record()
    optional = deepcopy(data["gates"][1])
    optional.update(id="optional-hardening", required_for_release=False, category="coordinator_recommendation")
    data["gates"].append(optional)
    status = ledger.render_status(data, NOW)
    assert "1/2 required gates uncleared; 1/1 optional work records uncleared" in status
    assert "| optional-hardening | False | blocked | coordinator_recommendation |" in status
    optional["required_for_release"] = "false"
    with pytest.raises(ledger.LedgerError, match="must be boolean"):
        ledger.validate(data, NOW)


@pytest.mark.parametrize("kind", ["sources", "gates"])
def test_empty_provenance_or_gate_inventory_is_not_a_release_record(kind):
    data = record()
    data[kind] = []
    with pytest.raises(ledger.LedgerError, match=f"{kind}: cannot be empty"):
        ledger.validate(data, NOW)


@pytest.mark.parametrize("section,key,value,message", [
    (None, "schema_version", True, "schema_version"),
    (None, "updated_utc", "2026-11-11T00:00:00Z", "future"),
    ("scope", "frozen", "yes", "boolean"),
    ("candidate", "commit", "abc123", "hexadecimal"),
    ("candidate", "tree", "g" * 40, "hexadecimal"),
    (None, "owner", "", "nonempty"),
])
def test_rejects_malformed_identity_and_future_observations(section, key, value, message):
    data = record()
    (data if section is None else data[section])[key] = value
    with pytest.raises(ledger.LedgerError, match=message):
        ledger.validate(data, NOW)


@pytest.mark.parametrize("mutation,message", [
    (lambda d: d["gates"][0].update(evidence_refs=["missing"]), "unknown reference"),
    (lambda d: d["evidence"][0].update(source_refs=[]), "cannot be empty"),
    (lambda d: d["evidence"][0].update(source_refs=["missing"]), "unknown reference"),
    (lambda d: d["sources"].append(deepcopy(d["sources"][0])), "duplicate id"),
    (lambda d: d.update(release_ready=True), "unknown fields"),
    (lambda d: d["gates"][0].update(state="green"), "invalid state"),
    (lambda d: d["evidence"][0].pop("invalidation"), "missing fields"),
    (lambda d: d["sources"][0].update(observed_utc="2026-11-11T00:00:00Z"), "future"),
    (lambda d: d["evidence"][0].update(observed_utc="2026-11-11T00:00:00Z"), "future"),
    (lambda d: d["scope"].update(excluded=["Scoped correction"]), "contradictory"),
])
def test_rejects_missing_provenance_and_unknown_status(mutation, message):
    data = record()
    mutation(data)
    with pytest.raises(ledger.LedgerError, match=message):
        ledger.validate(data, NOW)


@pytest.mark.parametrize("outcome", ["failed", "skipped", "unknown"])
def test_nonpassing_receipts_cannot_clear_gate_or_be_reused(outcome):
    data = record()
    data["evidence"][0]["outcome"] = outcome
    with pytest.raises(ledger.LedgerError, match="passing unexpired"):
        ledger.validate(data, NOW)
    assert not ledger.reuse_evidence(data["evidence"][0], target(), NOW)["reusable"]
    data["gates"][0]["state"] = "blocked"
    ledger.validate(data, NOW)  # Retain negative evidence without treating it as clearance.


def test_expired_evidence_is_retained_but_cannot_clear_or_reuse():
    data = record()
    data["evidence"][0]["expires_utc"] = "2026-11-10T23:30:00Z"
    assert not ledger.reuse_evidence(data["evidence"][0], target(), NOW)["reusable"]
    with pytest.raises(ledger.LedgerError, match="passing unexpired"):
        ledger.validate(data, NOW)
    data["gates"][0]["state"] = "stale"
    ledger.validate(data, NOW)
    assert "2026-11-10T23:30:00Z (expired)" in ledger.render_status(data, NOW)


def test_clear_gate_requires_claim_coverage_and_same_tree():
    data = record()
    data["gates"][0]["claims"] = ["real server acceptance"]
    with pytest.raises(ledger.LedgerError, match="cover gate claims"):
        ledger.validate(data, NOW)
    data = record()
    data["evidence"][0]["candidate"]["tree"] = "f" * 40
    with pytest.raises(ledger.LedgerError, match="tree differs"):
        ledger.validate(data, NOW)


def companion_record():
    data = record()
    companion = {"repository": "example/skills", "commit": "c" * 40, "tree": "d" * 40}
    data["gates"][0]["candidate"] = deepcopy(companion)
    data["evidence"][0]["candidate"] = deepcopy(companion)
    data["reviews"] = [review()]
    data["reviews"][0]["candidate"] = deepcopy(companion)
    data["gates"][0]["review_refs"] = ["peer"]
    return data


def test_companion_gate_uses_own_candidate_and_displays_both_targets():
    data = companion_record()
    ledger.validate(data, NOW)
    status = ledger.render_status(data, NOW)
    assert "example/skills@" + "c" * 40 + " (tree " + "d" * 40 + ")" in status
    assert "example/ppds@" + "a" * 40 + " (tree " + "b" * 40 + ")" in status
    assert data["candidate"] == CANDIDATE


@pytest.mark.parametrize("section,key", [("evidence", "repository"), ("evidence", "tree"),
                                        ("reviews", "repository"), ("reviews", "tree")])
def test_companion_gate_rejects_core_or_mismatched_proof(section, key):
    data = companion_record()
    data[section][0]["candidate"][key] = CANDIDATE[key]
    with pytest.raises(ledger.LedgerError, match="tree differs"):
        ledger.validate(data, NOW)


def test_gate_candidate_override_is_validated_even_while_held():
    data = record()
    data["gates"][1]["candidate"] = {"repository": "example/skills"}
    with pytest.raises(ledger.LedgerError, match="missing fields"):
        ledger.validate(data, NOW)


def test_prerequisites_cannot_be_skipped_or_cyclic():
    data = record()
    data["gates"][0]["prerequisites"] = ["publication"]
    with pytest.raises(ledger.LedgerError, match="cyclic"):
        ledger.validate(data, NOW)
    data["gates"][1]["prerequisites"] = []
    with pytest.raises(ledger.LedgerError, match="not clear"):
        ledger.validate(data, NOW)


@pytest.mark.parametrize("key,value,message", [
    ("whole_gate_cleared", False, "delta-only"),
    ("findings", ["Existing blocker remains"], "unresolved review"),
    ("open_prerequisites", ["Target isolation"], "unresolved review"),
    ("claims", ["Unrelated delta"], "incomplete review"),
])
def test_review_contract_cannot_promote_delta_or_unresolved_blocker(key, value, message):
    data = record()
    data["reviews"] = [review()]
    data["gates"][0]["review_refs"] = ["peer"]
    ledger.validate(data, NOW)
    data["reviews"][0][key] = value
    with pytest.raises(ledger.LedgerError, match=message):
        ledger.validate(data, NOW)


def test_user_hold_needs_approved_action_specific_authorization():
    data = record()
    gate = data["gates"][1]
    gate.update(state="clear", claims=["offline acceptance"], evidence_refs=["test"])
    with pytest.raises(ledger.LedgerError, match="held or denied"):
        ledger.validate(data, NOW)
    data["authorizations"][0].update(status="approved", actions=["unrelated action"])
    with pytest.raises(ledger.LedgerError, match="scoped approval"):
        ledger.validate(data, NOW)
    data["authorizations"][0]["actions"] = ["publication"]
    ledger.validate(data, NOW)  # This validates the declaration, never performs the action.


def test_ancestry_only_change_reuses_scoped_evidence_without_authorizing_head():
    new = target()
    new["candidate"]["commit"] = "f" * 40
    result = ledger.reuse_evidence(receipt(), new, NOW)
    assert result["reusable"]
    assert result["observed_utc"] == receipt()["observed_utc"]
    assert "exact-head" in " ".join(result["nonclaims"])


@pytest.mark.parametrize("key,value", [
    ("inputs_sha256", {"source-and-lockfiles": "f" * 64}),
    ("inputs_sha256", {"source-and-lockfiles": "c" * 64, "new-input": "f" * 64}),
    ("toolchain", {"python": "3.15.0"}),
    ("definition_sha256", "f" * 64),
    ("platform", "linux-x64"),
    ("claims", ["broader live acceptance"]),
])
def test_reuse_reports_each_changed_input_dimension(key, value):
    new = target()
    new[key] = value
    result = ledger.reuse_evidence(receipt(), new, NOW)
    assert not result["reusable"]
    assert result["reasons"] == [f"{key} changed"]


def test_reuse_rejects_repository_change_and_malformed_target():
    new = target()
    new["candidate"]["repository"] = "other/repo"
    assert ledger.reuse_evidence(receipt(), new, NOW)["reasons"] == ["repository changed"]
    new["platform"] = ""
    with pytest.raises(ledger.LedgerError):
        ledger.reuse_evidence(receipt(), new, NOW)


def test_cli_and_example_are_read_only_and_fail_closed(tmp_path, capsys):
    example = ROOT / "docs" / "release-ledger.example.json"
    assert ledger.main(["validate", str(example)]) == 0
    assert ledger.main(["status", str(example)]) == 0
    assert "does not authorize" in capsys.readouterr().out
    old = tmp_path / "old.json"
    new = tmp_path / "target.json"
    old.write_text(json.dumps(receipt()), encoding="utf-8")
    new.write_text(json.dumps(target()), encoding="utf-8")
    before = {p.name: p.read_bytes() for p in tmp_path.iterdir()}
    assert ledger.main(["reuse", str(old), str(new)]) == 0
    assert {p.name: p.read_bytes() for p in tmp_path.iterdir()} == before
    new.write_text('{"candidate": null, "candidate": {}}', encoding="utf-8")
    assert ledger.main(["reuse", str(old), str(new)]) == 2
    assert "duplicate JSON key" in capsys.readouterr().err
