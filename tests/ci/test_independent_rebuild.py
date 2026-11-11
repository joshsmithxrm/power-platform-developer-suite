"""Synthetic offline data tests; no actual compiler, signing key or build is used."""
import copy
from datetime import datetime, timedelta, timezone
import io
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock
import zipfile

from scripts.ci import verify_independent_rebuild as v


def stamp(value):
    return value.strftime("%Y-%m-%dT%H:%M:%SZ")


def synthetic_pe():
    raw = bytearray(1536)
    raw[:2] = b"MZ"
    struct.pack_into("<I", raw, 60, 128)
    raw[128:132] = b"PE\0\0"
    struct.pack_into("<H", raw, 134, 1)
    struct.pack_into("<H", raw, 148, 224)
    struct.pack_into("<H", raw, 152, 0x10B)
    struct.pack_into("<II", raw, 152 + 96 + 14 * 8, 0x2000, 72)
    struct.pack_into("<III", raw, 376 + 12, 0x2000, 1024, 512)
    struct.pack_into("<I", raw, 512 + 16, 1)
    struct.pack_into("<II", raw, 512 + 32, 0x2080, 128)
    return bytes(raw)


def zipped(members, year=2000):
    result = io.BytesIO()
    with zipfile.ZipFile(result, "w") as z:
        for name, raw in members:
            info = zipfile.ZipInfo(name, (year, 1, 1, 0, 0, 0))
            info.external_attr = 0o100644 << 16
            z.writestr(info, raw)
    return result.getvalue()


class RebuildTest(unittest.TestCase):
    def setUp(self):
        temporary_root = Path(__file__).resolve().parents[2] / ".pytest_cache" / "rebuild"
        temporary_root.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=temporary_root)
        def cleanup():
            # Verify the recursive cleanup target stays inside this test workspace.
            self.assertTrue(Path(self.temp.name).resolve().is_relative_to(temporary_root.resolve()))
            self.temp.cleanup()
        self.addCleanup(cleanup)
        self.root = Path(self.temp.name)
        self.now = datetime(2026, 11, 11, 14, 0, 0, tzinfo=timezone.utc)
        self.request = {
            "schema": 1, "mode": "OFFLINE_COMPARISON_ONLY", "repository": v.REPOSITORY,
            "source": {"commit": "a" * 40, "tree": "b" * 40}, "version": "2.0.0",
            "nonce": "c" * 32, "not_before": stamp(self.now - timedelta(minutes=10)),
            "expires": stamp(self.now + timedelta(minutes=10)),
            "candidate": {"controller": "producer.example", "invocation": "candidate-42"},
            "reference": {"controller": "reference.example", "invocation": "reference-42"},
            "inputs": {key: v.sha(key.encode()) for key in v.INPUTS},
            "outputs": v.output_names("2.0.0"), "signature_bytes": 128,
        }
        self.inputs = self.root / "inputs"
        self.inputs.mkdir()
        for name in v.INPUTS:
            (self.inputs / name).write_bytes(name.encode())
        self.dll = synthetic_pe()
        self.pdb = b"BSJB synthetic portable PDB fixture"
        self.xml = b"<doc><assembly><name>PPDS.Plugins</name></assembly></doc>"
        self.left, self.right = self.root / "candidate", self.root / "reference"
        self.left.mkdir()
        self.right.mkdir()
        self.make_artifacts(self.left)
        self.make_artifacts(self.right)
        self.request_path = self.root / "request.json"
        self.admission_path = self.root / "pins.json"
        self.left_receipt, self.right_receipt = self.root / "candidate.json", self.root / "reference.json"
        self.output = self.root / "result.json"
        self.bind()

    def spec(self):
        return ('<package><metadata><id>PPDS.Plugins</id><version>2.0.0</version>'
                '<repository commit="' + self.request["source"]["commit"] + '"/></metadata></package>').encode()

    def make_artifacts(self, directory, dll=None, pdb=None, xml=None, year=2000, extra_package=()):
        dll, pdb, xml = dll or self.dll, pdb or self.pdb, xml or self.xml
        values = {"dll": dll, "pdb": pdb, "xml": xml,
                  "package": zipped([(v.DLL, dll), (v.XML, xml), ("PPDS.Plugins.nuspec", self.spec()), *extra_package], year),
                  "symbols": zipped([(v.PDB, pdb), ("PPDS.Plugins.nuspec", self.spec())], year)}
        for role, raw in values.items():
            (directory / self.request["outputs"][role]).write_bytes(raw)

    def write_json(self, path, value):
        raw = v.canonical(value)
        path.write_bytes(raw)
        return v.sha(raw)

    def raw_inventory(self, directory):
        return {role: {"name": name, "bytes": len((directory / name).read_bytes()), "sha256": v.sha((directory / name).read_bytes())}
                for role, name in self.request["outputs"].items() if (directory / name).exists()}

    def bind(self):
        self.request_sha = self.write_json(self.request_path, self.request)
        self.receipts = {}
        for role, directory, path in (("candidate", self.left, self.left_receipt), ("reference", self.right, self.right_receipt)):
            value = {"schema": 1, "request_sha256": self.request_sha, "nonce": self.request["nonce"],
                     "source": copy.deepcopy(self.request["source"]), "inputs": copy.deepcopy(self.request["inputs"]),
                     **self.request[role], "started": stamp(self.now - timedelta(minutes=2)),
                     "completed": stamp(self.now - timedelta(minutes=1)), "phase": "DELAY_SIGNED_PRE_SIGNING",
                     "outputs": self.raw_inventory(directory)}
            self.receipts[role] = value
            self.write_json(path, value)
        self.repin()

    def repin(self):
        self.admission = {"schema": 1, "mode": "COMPARISON_PINS_ONLY", "request_sha256": self.request_sha,
                          "candidate_receipt_sha256": v.sha(self.left_receipt.read_bytes()),
                          "reference_receipt_sha256": v.sha(self.right_receipt.read_bytes())}
        self.admission_sha = self.write_json(self.admission_path, self.admission)

    def compare(self):
        return v.compare(self.request_path, self.request_sha, self.admission_path, self.admission_sha,
                         self.left, self.left_receipt, self.right, self.right_receipt, self.output, self.now)

    def mutate_receipt(self, change):
        change(self.receipts["reference"])
        self.write_json(self.right_receipt, self.receipts["reference"])
        self.repin()

    def test_prepare_measures_inputs_without_semantic_or_independence_claim(self):
        result = v.prepare(self.request_path, self.request_sha, self.inputs, self.output, self.now)
        self.assertEqual(result["status"], "INPUT_BYTES_BOUND_ONLY")
        self.assertFalse(result["source_or_history_semantics_verified"])
        self.assertFalse(result["independent_rebuild_verified"])
        self.assertFalse(result["signing_admission"])

    def test_prepare_refuses_changed_input(self):
        (self.inputs / "toolchain").write_bytes(b"changed compiler")
        with self.assertRaisesRegex(v.Error, "input bytes differ"):
            v.prepare(self.request_path, self.request_sha, self.inputs, self.output, self.now)

    def test_identical_bytes_never_grant_independence_or_signing(self):
        result = self.compare()
        self.assertEqual(result["status"], "BYTE_IDENTICAL_COMPARISON_ONLY")
        self.assertEqual(result["different_roles"], [])
        for field in ("independent_rebuild_verified", "signing_admission", "release_admission", "controller_labels_authenticated"):
            self.assertIs(result[field], False)

    def test_changed_code_with_consistent_receipt_remains_difference(self):
        raw = bytearray(self.dll)
        raw[1000] = 42
        self.make_artifacts(self.right, dll=bytes(raw))
        self.bind()
        self.assertEqual(self.compare()["different_roles"], ["dll", "package"])

    def test_changed_xml_with_consistent_receipt_is_compared(self):
        self.make_artifacts(self.right, xml=b"<doc>changed</doc>")
        self.bind()
        self.assertEqual(self.compare()["different_roles"], ["xml", "package"])

    def test_same_members_different_zip_timestamps_are_not_normalized(self):
        self.make_artifacts(self.right, year=2001)
        self.bind()
        result = self.compare()
        self.assertEqual(result["different_roles"], ["package", "symbols"])
        self.assertFalse(result["normalization_applied"])

    def test_missing_output_refused(self):
        (self.right / self.request["outputs"]["xml"]).unlink()
        with self.assertRaisesRegex(v.Error, "missing or extra"):
            self.compare()

    def test_extra_output_refused(self):
        (self.right / "extra.dll").write_bytes(self.dll)
        with self.assertRaisesRegex(v.Error, "missing or extra"):
            self.compare()

    def test_package_and_standalone_mismatch_refused(self):
        (self.right / "PPDS.Plugins.xml").write_bytes(b"<doc>not packaged</doc>")
        self.bind()
        with self.assertRaisesRegex(v.Error, "Standalone/package"):
            self.compare()

    def test_additional_executable_refused(self):
        self.make_artifacts(self.right, extra_package=[("tools/other.exe", b"bad")])
        self.bind()
        with self.assertRaisesRegex(v.Error, "Unexpected executable"):
            self.compare()

    def test_duplicate_case_archive_member_refused(self):
        self.make_artifacts(self.right, extra_package=[(v.DLL.upper(), self.dll)])
        self.bind()
        with self.assertRaisesRegex(v.Error, "duplicate archive"):
            self.compare()

    def test_path_traversal_archive_member_refused(self):
        self.make_artifacts(self.right, extra_package=[("../evil", b"bad")])
        self.bind()
        with self.assertRaisesRegex(v.Error, "Unsafe"):
            self.compare()

    def test_public_or_fully_signed_flag_refused(self):
        raw = bytearray(self.dll)
        struct.pack_into("<I", raw, 512 + 16, 9)
        self.make_artifacts(self.right, dll=bytes(raw))
        self.bind()
        with self.assertRaisesRegex(v.Error, "delay-signed"):
            self.compare()

    def test_nonzero_signature_refused(self):
        raw = bytearray(self.dll)
        raw[640] = 1
        self.make_artifacts(self.right, dll=bytes(raw))
        self.bind()
        with self.assertRaisesRegex(v.Error, "reservation is not empty"):
            self.compare()

    def test_receipt_input_substitution_refused(self):
        self.mutate_receipt(lambda r: r["inputs"].update(toolchain="f" * 64))
        with self.assertRaisesRegex(v.Error, "request/source/input"):
            self.compare()

    def test_receipt_source_substitution_refused(self):
        self.mutate_receipt(lambda r: r["source"].update(commit="f" * 40))
        with self.assertRaisesRegex(v.Error, "request/source/input"):
            self.compare()

    def test_receipt_nonce_replay_refused(self):
        self.mutate_receipt(lambda r: r.update(nonce="f" * 32))
        with self.assertRaisesRegex(v.Error, "request/source/input"):
            self.compare()

    def test_receipt_outside_lifetime_refused(self):
        self.mutate_receipt(lambda r: r.update(started=stamp(self.now - timedelta(days=1))))
        with self.assertRaisesRegex(v.Error, "outside request lifetime"):
            self.compare()

    def test_future_receipt_completion_refused(self):
        self.mutate_receipt(lambda r: r.update(completed=stamp(self.now + timedelta(minutes=1))))
        with self.assertRaisesRegex(v.Error, "outside request lifetime"):
            self.compare()

    def test_same_domain_case_insensitive_refused(self):
        self.request["reference"]["controller"] = "PRODUCER.example"
        self.bind()
        with self.assertRaisesRegex(v.Error, "Same declared control domain"):
            self.compare()

    def test_same_invocation_refused(self):
        self.request["reference"]["invocation"] = self.request["candidate"]["invocation"]
        self.bind()
        with self.assertRaisesRegex(v.Error, "Same invocation"):
            self.compare()

    def test_unknown_request_field_cannot_enable_trust(self):
        self.request["trusted"] = True
        self.bind()
        with self.assertRaisesRegex(v.Error, "Unexpected request fields"):
            self.compare()

    def test_unknown_receipt_field_cannot_enable_trust(self):
        self.mutate_receipt(lambda r: r.update(independent=True))
        with self.assertRaisesRegex(v.Error, "Unexpected build-receipt fields"):
            self.compare()

    def test_expired_request_refused(self):
        with self.assertRaisesRegex(v.Error, "not current"):
            v.validate_request(self.request, self.now + timedelta(days=1))

    def test_future_request_refused(self):
        with self.assertRaisesRegex(v.Error, "not current"):
            v.validate_request(self.request, self.now - timedelta(days=1))

    def test_unbounded_request_lifetime_refused(self):
        self.request["expires"] = stamp(self.now + timedelta(days=2))
        with self.assertRaisesRegex(v.Error, "24 hours"):
            v.validate_request(self.request, self.now)

    def test_changed_pin_document_refused(self):
        self.admission_path.write_bytes(self.admission_path.read_bytes() + b" ")
        with self.assertRaisesRegex(v.Error, "Pinned JSON digest"):
            self.compare()

    def test_changed_receipt_without_updated_pin_refused(self):
        self.right_receipt.write_bytes(self.right_receipt.read_bytes() + b" ")
        with self.assertRaisesRegex(v.Error, "Pinned JSON digest"):
            self.compare()

    def test_duplicate_json_key_refused(self):
        with self.assertRaisesRegex(v.Error, "Duplicate JSON key"):
            v.decode(b'{"schema":1,"schema":1}')

    def test_nonfinite_json_refused(self):
        with self.assertRaisesRegex(v.Error, "Non-finite JSON"):
            v.decode(b'{"schema":NaN}')

    def test_hardlinked_input_metadata_refused(self):
        source = self.inputs / "recipe"
        original = Path.lstat
        def metadata(path):
            value = original(path)
            if path == source:
                parts = {name: getattr(value, name) for name in dir(value) if name.startswith("st_")}
                parts["st_nlink"] = 2  # No link creation or extra privilege needed.
                return SimpleNamespace(**parts)
            return value
        with mock.patch.object(Path, "lstat", metadata):
            with self.assertRaisesRegex(v.Error, "non-hardlinked"):
                v.prepare(self.request_path, self.request_sha, self.inputs, self.output, self.now)

    def test_same_artifact_directory_refused(self):
        with self.assertRaisesRegex(v.Error, "Distinct artifact"):
            v.compare(self.request_path, self.request_sha, self.admission_path, self.admission_sha,
                      self.left, self.left_receipt, self.left, self.right_receipt, self.output, self.now)

    def test_same_directory_traversal_alias_refused(self):
        alias = self.left / ".." / self.left.name
        with self.assertRaisesRegex(v.Error, "Parent traversal|Distinct artifact"):
            v.compare(self.request_path, self.request_sha, self.admission_path, self.admission_sha,
                      self.left, self.left_receipt, alias, self.right_receipt, self.output, self.now)

    @unittest.skipUnless(sys.platform == "win32", "Windows UNC path grammar")
    def test_unc_path_refused_before_remote_observation(self):
        with self.assertRaisesRegex(v.Error, "UNC/device paths refused"):
            v.local(r"\\uncontacted.example\share\artifact")

    def test_existing_result_not_overwritten(self):
        self.output.write_bytes(b"preserve existing evidence")
        with self.assertRaises(FileExistsError):
            self.compare()
        self.assertEqual(self.output.read_bytes(), b"preserve existing evidence")

    def test_malformed_pe_offset_refused(self):
        raw = bytearray(self.dll)
        struct.pack_into("<I", raw, 60, 0xFFFFFFFF)
        self.make_artifacts(self.right, dll=bytes(raw))
        self.bind()
        with self.assertRaisesRegex(v.Error, "Invalid PE header"):
            self.compare()

    def test_wrong_pdb_format_refused(self):
        self.make_artifacts(self.right, pdb=b"not portable PDB")
        self.bind()
        with self.assertRaisesRegex(v.Error, "Portable PDB"):
            self.compare()

    def test_package_source_mismatch_refused(self):
        package_path = self.right / self.request["outputs"]["package"]
        members = v.archive(package_path.read_bytes())
        members["PPDS.Plugins.nuspec"] = self.spec().replace(b'a' * 40, b'f' * 40)
        package_path.write_bytes(zipped(list(members.items())))
        self.bind()
        with self.assertRaisesRegex(v.Error, "Package source/version"):
            self.compare()


if __name__ == "__main__":
    unittest.main(verbosity=2)
