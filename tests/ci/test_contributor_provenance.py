"""Exercise the offline validator's actual functions and executable CLI with synthetic identities."""

import contextlib
import copy
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from scripts.ci import check_contributor_provenance as guard

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/ci/check_contributor_provenance.py"
LANDING = "https://github.com/example/project/pull/20"
SOURCE = "https://github.com/example/project/pull/10"
HEAD = "a" * 40
COMMIT = "c" * 40


def person(name="Ada Example", email="ada@example.test", login="ada"):
    return {"name": name, "email": email, "login": login, "verified": True,
            "evidence": "Captured public source commit identity."}


def record(mode="incorporated", complete=False):
    result = {"schema_version": 1, "mode": mode, "reason": "Combined with a related implementation.",
              "landing_pr": LANDING, "landing_head": HEAD,
              "sources": [{"pr": SOURCE, "head": "b" * 40, "contributors": [person()],
                           "disposition": {"decision": "close-as-incorporated", "reason": "Work lands elsewhere.",
                                           "owner": "Maintainer", "state": "planned"},
                           "communication": {"owner": "Maintainer", "state": "prepared",
                                             "reply": "Thank you. Your work is incorporated in the linked landing PR."}}]}
    if mode == "direct":
        result["sources"][0].update(pr=LANDING, head=HEAD)
        result["sources"][0]["disposition"]["decision"] = "merge-original"
    if complete:
        result["sources"][0]["disposition"].update(state="done", evidence="Captured completed disposition.")
        result["sources"][0]["communication"].update(state="sent", evidence="Captured actual reply.")
    return result


def proposal():
    return {"schema_version": 1, "landing_pr": LANDING, "head": HEAD, "method": "squash",
            "author": {"name": "Maintainer", "email": "maintainer@example.test", "verified": True,
                       "evidence": "Captured proposed squash author."},
            "message": "Implement feature\n\nCo-authored-by: Ada Example <ada@example.test>",
            "evidence": "Captured exact proposal author and full message."}


def landing():
    result = proposal()
    del result["method"]
    result["commit"] = COMMIT
    result["evidence"] = "Captured resulting commit after landing."
    return result


def release():
    return {"schema_version": 1, "landing_pr": LANDING, "commit": COMMIT,
            "notes": f"Implemented feature from {SOURCE}; thanks @ada.",
            "evidence": "Captured actual release notes and included landing commit."}


class ContributorProvenanceTests(unittest.TestCase):
    def check(self, code, value, stage="plan", **snapshots):
        actual, output = guard.validate(value, stage, **snapshots)
        self.assertEqual(code, actual, output)
        self.assertEqual(code == 0, output["valid"])
        self.assertEqual("supplied-local-snapshots-only", output["scope"])
        self.assertIn("does not independently verify GitHub truth", " ".join(output["limitations"]))
        return output

    def test_none_requires_no_identity_snapshot_or_approval(self):
        for stage in guard.STAGES:
            with self.subTest(stage=stage):
                output = self.check(0, {"schema_version": 1, "mode": "none", "reason": "No external code incorporated."}, stage)
                self.assertTrue(output["closeout_complete"])
                self.assertEqual([], output["pending_closeout"])

    def test_none_rejects_contradictory_provenance(self):
        for added in ({"sources": record()["sources"]}, {"landing_pr": LANDING}, {"landing_head": HEAD}):
            with self.subTest(added=tuple(added)):
                self.check(1, {"schema_version": 1, "mode": "none", "reason": "None", **added})
        self.check(1, {"schema_version": 1, "mode": "none", "reason": "None"}, proposal=proposal())

    def test_direct_binds_original_pr_and_head(self):
        self.check(0, record("direct"), "pre-merge", proposal=proposal())
        for field, replacement in (("pr", SOURCE), ("head", "d" * 40)):
            value = record("direct")
            value["sources"][0][field] = replacement
            with self.subTest(field=field):
                self.check(1, value)
        value = record("direct")
        value["sources"][0]["disposition"]["decision"] = "keep-open"
        self.check(1, value)

    def test_incorporation_requires_reason_distinct_sources_and_unique_records(self):
        for mutation in ("reason", "same-pr", "duplicate", "empty", "merge-original"):
            value = record()
            if mutation == "reason": value["reason"] = " "
            if mutation == "same-pr": value["sources"][0]["pr"] = LANDING
            if mutation == "duplicate": value["sources"].append(copy.deepcopy(value["sources"][0]))
            if mutation == "empty": value["sources"] = []
            if mutation == "merge-original": value["sources"][0]["disposition"]["decision"] = "merge-original"
            with self.subTest(mutation=mutation): self.check(1, value)

    def test_named_owners_cannot_be_blank_or_placeholders(self):
        for field in ("disposition", "communication"):
            for owner in ("", " ", "TBD", "unknown", "Unassigned", "n/a", "?"):
                value = record()
                value["sources"][0][field]["owner"] = owner
                with self.subTest(field=field, owner=owner): self.check(1, value)

    def test_explicit_disposition_and_exact_reply_are_required(self):
        for field in ("disposition", "communication"):
            value = record()
            del value["sources"][0][field]
            with self.subTest(missing=field): self.check(2, value)
        value = record()
        value["sources"][0]["communication"]["reply"] = " "
        self.check(1, value)

    def test_actual_proposal_credit_not_body_or_branch_ancestry(self):
        snapshot = proposal()
        snapshot["message"] = "Feature\n\nSource branch contains Ada Example <ada@example.test> commits."
        self.check(1, record(), "pre-merge", proposal=snapshot)
        self.check(1, record(), "pre-merge")
        snapshot = proposal()
        snapshot["author"] = {key: value for key, value in person().items() if key != "login"}
        snapshot["message"] = "Feature with verified actual contributor authorship."
        self.check(0, record(), "pre-merge", proposal=snapshot)

    def test_supported_final_trailers_credit_every_contributor(self):
        value = record()
        value["sources"][0]["contributors"].append(person("Grace Example", "grace@example.test", "grace"))
        snapshot = proposal()
        self.check(1, value, "pre-merge", proposal=snapshot)
        snapshot["message"] += "\nco-authored-by: Grace Example <grace@example.test>\nSigned-off-by: Maintainer <maintainer@example.test>\n\n"
        self.check(0, value, "pre-merge", proposal=snapshot)

    def test_malformed_nonfinal_wrapped_or_missing_separator_trailers_do_not_credit(self):
        trailer = "Co-authored-by: Ada Example <ada@example.test>"
        for message in (trailer, "Title\n" + trailer, "Title\n\n" + trailer + "\n\nLater body",
                        "Title\n\nCo-authored-by: Ada Example ada@example.test",
                        "Title\n\nCo-authored-by: Ada Example\n <ada@example.test>",
                        "Title\n\nCo-authored-by: Ada Example <other@example.test>",
                        "Title\n\n" + trailer + "\nContinuation body"):
            snapshot = proposal()
            snapshot["message"] = message
            with self.subTest(message=message): self.check(1, record(), "pre-merge", proposal=snapshot)

    def test_unknown_contributor_is_plan_only(self):
        value = record()
        value["sources"][0]["contributors"][0].update(name=None, email=None, verified=False, evidence="Identity needs confirmation.")
        self.check(0, value)
        self.check(1, value, "pre-merge", proposal=proposal())
        self.check(1, value, "post-landing", landing=landing())
        value["sources"][0]["contributors"][0]["evidence"] = ""
        self.check(1, value)

    def test_unknown_author_stale_pr_or_head_cannot_verify(self):
        for mutation in ("author", "pr", "head"):
            snapshot = proposal()
            if mutation == "author": snapshot["author"].update(name=None, email=None, verified=False)
            if mutation == "pr": snapshot["landing_pr"] = SOURCE
            if mutation == "head": snapshot["head"] = "d" * 40
            with self.subTest(mutation=mutation): self.check(1, record(), "pre-merge", proposal=snapshot)

    def test_post_landing_checks_actual_credit_independently(self):
        self.check(0, record(), "post-landing", landing=landing())
        actual = landing()
        actual["message"] = "Actual squash dropped contributor credit."
        self.check(1, record(), "post-landing", proposal=proposal(), landing=actual)
        self.check(1, record(), "post-landing", proposal=proposal())
        actual = landing()
        actual["author"]["verified"] = False
        self.check(1, record(), "post-landing", landing=actual)

    def test_pending_approval_stays_visible_and_blocks_release_closeout(self):
        value = record()
        for field in ("disposition", "communication"):
            value["sources"][0][field]["state"] = "pending-approval"
            value["sources"][0][field]["evidence"] = "An existing draft is not completed approval."
        for stage, snapshots in (("plan", {}), ("pre-merge", {"proposal": proposal()}), ("post-landing", {"landing": landing()})):
            with self.subTest(stage=stage):
                output = self.check(0, value, stage, **snapshots)
                self.assertFalse(output["closeout_complete"])
                self.assertEqual(2, len(output["pending_closeout"]))
                self.assertEqual({"Maintainer"}, {item["owner"] for item in output["pending_closeout"]})
        self.check(1, value, "post-release", landing=landing(), release=release())

    def test_completed_closeout_requires_evidence_not_state_labels(self):
        self.check(0, record(complete=True), "post-release", landing=landing(), release=release())
        for field in ("disposition", "communication"):
            value = record(complete=True)
            del value["sources"][0][field]["evidence"]
            with self.subTest(field=field):
                output = self.check(1, value, "post-release", landing=landing(), release=release())
                self.assertFalse(output["closeout_complete"])
                self.assertEqual(field, output["pending_closeout"][0]["item"])

    def test_release_requires_matching_actual_commit_and_literal_credit(self):
        for mutation in ("commit", "pr", "source", "identity", "prefix-pr", "prefix-login"):
            snapshot = release()
            if mutation == "commit": snapshot["commit"] = "d" * 40
            if mutation == "pr": snapshot["landing_pr"] = SOURCE
            if mutation == "source": snapshot["notes"] = "Thanks @ada."
            if mutation == "identity": snapshot["notes"] = SOURCE + " Thanks someone else."
            if mutation == "prefix-pr": snapshot["notes"] = SOURCE + "0 Thanks @ada."
            if mutation == "prefix-login": snapshot["notes"] = SOURCE + " Thanks @adam."
            with self.subTest(mutation=mutation): self.check(1, record(complete=True), "post-release", landing=landing(), release=snapshot)
        snapshot = release()
        snapshot["notes"] = f"Thanks Ada Example: [{SOURCE}]({SOURCE})."
        self.check(0, record(complete=True), "post-release", landing=landing(), release=snapshot)
        self.check(1, record(complete=True), "post-release", release=release())
        self.check(1, record(complete=True), "post-release", landing=landing())

    def test_malformed_versions_types_identities_urls_and_heads_fail_safely(self):
        fixtures = [[], {**record(), "schema_version": True}, {**record(), "schema_version": 1.0},
                    {**record(), "schema_version": 2}, {**record(), "sources": {}},
                    {**record(), "landing_head": "short"}, {**record(), "landing_pr": LANDING + "?x=1"},
                    {**record(), "mode": "invented"}, {**record(), "secret-unknown-key": "PRIVATE"}]
        for field, value in (("name", "Ada\nInjected"), ("email", "not-an-email"), ("verified", "true"),
                             ("login", "@ada"), ("name", ""), ("email", None)):
            item = record()
            item["sources"][0]["contributors"][0][field] = value
            fixtures.append(item)
        for index, item in enumerate(fixtures):
            with self.subTest(index=index):
                output = self.check(2, item)
                self.assertNotIn("PRIVATE", json.dumps(output))

    def test_keep_open_is_explicit_owned_closeout_not_an_automatic_close(self):
        value = record(complete=True)
        value["sources"][0]["disposition"].update(decision="keep-open", reason="Separate remaining work.", evidence="Maintainer recorded decision.")
        self.check(0, value, "post-release", landing=landing(), release=release())

    def test_multiple_sources_each_require_commit_and_release_credit(self):
        value = record(complete=True)
        second = copy.deepcopy(value["sources"][0])
        second.update(pr="https://github.com/example/project/pull/11", head="d" * 40,
                      contributors=[person("Grace Example", "grace@example.test", "grace")])
        value["sources"].append(second)
        actual = landing()
        self.check(1, value, "post-landing", landing=actual)
        actual["message"] += "\nCo-authored-by: Grace Example <grace@example.test>"
        self.check(0, value, "post-landing", landing=actual)
        notes = release()
        self.check(1, value, "post-release", landing=actual, release=notes)
        notes["notes"] += " Also thanks Grace Example for " + second["pr"] + "."
        self.check(0, value, "post-release", landing=actual, release=notes)

    def test_snapshot_versions_and_empty_verified_evidence_are_not_coerced(self):
        for stage, key, fixture in (("pre-merge", "proposal", proposal), ("post-landing", "landing", landing)):
            for version in (True, 1.0, 2):
                snapshot = fixture()
                snapshot["schema_version"] = version
                with self.subTest(stage=stage, version=version): self.check(2, record(), stage, **{key: snapshot})
        snapshot = proposal()
        snapshot["author"]["evidence"] = ""
        self.check(2, record(), "pre-merge", proposal=snapshot)
        snapshot = release()
        snapshot["schema_version"] = True
        self.check(2, record(complete=True), "post-release", landing=landing(), release=snapshot)

    def test_validation_does_not_mutate_input_or_claim_external_truth(self):
        value, snapshot = record(), proposal()
        original = copy.deepcopy((value, snapshot))
        output = self.check(0, value, "pre-merge", proposal=snapshot)
        self.assertEqual(original, (value, snapshot))
        self.assertIn("does not independently verify GitHub truth", " ".join(output["limitations"]))

    def test_late_malformed_input_does_not_echo_partially_validated_owner(self):
        value = record()
        value["sources"][0]["communication"]["owner"] = "PRIVATE-OWNER"
        value["sources"].append({"PRIVATE-KEY": "PRIVATE-VALUE"})
        output = self.check(2, value)
        self.assertNotIn("PRIVATE", json.dumps(output))


class ContributorProvenanceCliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def write(self, name, value):
        path = self.directory / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def run_cli(self, *args):
        # This file-only CLI needs no stdin; pytest's inherited Windows handle may be invalid.
        result = subprocess.run([sys.executable, str(SCRIPT), *map(str, args)],
                                capture_output=True, text=True, stdin=subprocess.DEVNULL, timeout=15)
        self.assertEqual("", result.stderr)
        return result.returncode, json.loads(result.stdout)

    def test_real_cli_valid_plan_and_actual_proposal(self):
        path = self.write("record.json", record())
        snapshot = self.write("proposal.json", proposal())
        before = {file.name: file.read_bytes() for file in self.directory.iterdir()}
        for args in (("--stage", "plan"), ("--stage", "pre-merge", "--proposal", snapshot)):
            code, result = self.run_cli("--record", path, *args)
            self.assertEqual(0, code, result)
        self.assertEqual(before, {file.name: file.read_bytes() for file in self.directory.iterdir()})

    def test_real_cli_post_release_missing_and_complete_closeout(self):
        path = self.write("record.json", record())
        actual = self.write("landing.json", landing())
        notes = self.write("release.json", release())
        args = ("--record", path, "--stage", "post-release", "--landing", actual, "--release", notes)
        self.assertEqual(1, self.run_cli(*args)[0])
        self.write("record.json", record(complete=True))
        code, result = self.run_cli(*args)
        self.assertEqual(0, code, result)
        self.assertTrue(result["closeout_complete"])

    def test_real_cli_invalid_json_duplicate_keys_and_constants_are_safe(self):
        for data in ('{"schema_version":1,"schema_version":1,"PRIVATE":"secret"}',
                     '{"schema_version":1,"mode":"none","reason":"ok","sources":[{"pr":1,"pr":2}]}',
                     '{"PRIVATE":"secret"', '{"schema_version":NaN}', "[[]]", "\xff"):
            path = self.directory / "invalid.json"
            path.write_text(data, encoding="utf-8")
            with self.subTest(data=data):
                code, result = self.run_cli("--record", path, "--stage", "plan")
                self.assertEqual(2, code)
                self.assertNotIn("PRIVATE", json.dumps(result))
                self.assertNotIn("secret", json.dumps(result))

    def test_real_cli_malformed_invocation_and_nonlocal_paths(self):
        path = self.write("record.json", {"schema_version": 1, "mode": "none", "reason": "None"})
        for args in ((), ("--record", path, "--stage", "PRIVATE-INVALID"),
                     ("--record", path, "--stage", "plan", "--arbitrary", "PRIVATE"),
                     ("--record", "https://example.test/PRIVATE", "--stage", "plan"),
                     ("--record", "\\\\server\\share\\PRIVATE", "--stage", "plan"),
                     ("--record", self.directory / "missing-PRIVATE.json", "--stage", "plan")):
            with self.subTest(args=args):
                code, result = self.run_cli(*args)
                self.assertEqual(2, code)
                self.assertNotIn("PRIVATE", json.dumps(result))

    def test_large_and_deep_inputs_are_bounded(self):
        path = self.directory / "large.json"
        for data in (b" " * 1_048_577, b"[" * 2000 + b"]" * 2000, b"\xff\xfe"):
            path.write_bytes(data)
            with self.subTest(length=len(data)):
                code, _ = self.run_cli("--record", path, "--stage", "plan")
                self.assertEqual(2, code)

    def test_main_uses_local_reads_without_network_or_process_launch(self):
        path = self.write("record.json", record())
        with patch("socket.socket", side_effect=AssertionError("Network forbidden")), patch("subprocess.Popen", side_effect=AssertionError("Subprocess forbidden")):
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                code = guard.main(["--record", str(path), "--stage", "plan"])
        self.assertEqual(0, code)
        self.assertTrue(json.loads(output.getvalue())["valid"])


if __name__ == "__main__":
    unittest.main()
