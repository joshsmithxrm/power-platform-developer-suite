"""Offline contracts only: synthetic PE/ZIP bytes and process/key-operation doubles.

These tests never read a key/public-key file, launch a tool, inspect the ambient
environment, or establish real Windows signing compatibility.
"""
from copy import deepcopy
import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
from types import SimpleNamespace
import zipfile

import pytest


DRIVER_PATH = Path(__file__).resolve().parents[1] / "compatibility/plugin_signing_compatibility.py"
SPEC = importlib.util.spec_from_file_location("ppds_signing_compatibility_contract", DRIVER_PATH)
d = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = d
SPEC.loader.exec_module(d)
REAL_HOST_OBSERVATION = d.host_observation

SOURCE = "a" * 40
TREE = "b" * 40
PLAN_HASH = "c" * 64
DRIVER_BYTES = b"synthetic reviewed driver identity"
OBSERVED_UTC = "2026-11-11T01:00:00+00:00"


def host_fixture():
    return {"platform": "win32", "process_bits": 64, "native_architecture": "AMD64",
            "windows_major": 10, "windows_minor": 0, "windows_build": 99999}


@pytest.mark.parametrize("native_code,expected", [(9, "AMD64"), (12, "ARM64"), (0, "X86"), (6, None), (None, None)])
def test_native_host_observation_has_no_process_or_environment_fallback(monkeypatch, native_code, expected):
    calls = []
    forbidden_calls = []

    def forbidden(*args, **kwargs):
        forbidden_calls.append(True)
        raise AssertionError("Host metadata attempted a process/environment fallback")

    class ForbiddenEnvironment:
        get = __getitem__ = __iter__ = __len__ = copy = forbidden

    def native_api(pointer):
        calls.append(True)
        if native_code is not None:
            d.ctypes.cast(pointer, d.ctypes.POINTER(d.NativeSystemInfo)).contents.architecture = native_code

    with monkeypatch.context() as patch:
        patch.setattr(d.sys, "platform", "win32")
        patch.setattr(d.sys, "getwindowsversion", lambda: SimpleNamespace(major=10, minor=0, build=99999), raising=False)
        patch.setattr(os, "environ", ForbiddenEnvironment())
        patch.setattr(os, "getenv", forbidden)
        patch.setattr(d.subprocess, "check_output", forbidden)
        patch.setattr(d.ctypes, "WinDLL", forbidden, raising=False)
        if expected is None:
            with pytest.raises(d.Error, match="Unknown native Windows architecture"):
                REAL_HOST_OBSERVATION(kernel32=SimpleNamespace(GetNativeSystemInfo=native_api))
        else:
            result = REAL_HOST_OBSERVATION(kernel32=SimpleNamespace(GetNativeSystemInfo=native_api))
    assert calls == [True] and forbidden_calls == []
    assert native_api.argtypes == [d.ctypes.POINTER(d.NativeSystemInfo)] and native_api.restype is None
    if expected is not None:
        assert result["native_architecture"] == expected and result["windows_build"] == 99999


def helper_fixture_bytes():
    return {name: DRIVER_BYTES if name.endswith("plugin_signing_compatibility.py")
            else ("synthetic helper: " + name).encode() for name in d.HELPERS}


def helper_fixture_hashes():
    return {name: d.digest(raw) for name, raw in helper_fixture_bytes().items()}


@pytest.fixture(autouse=True)
def prohibit_actual_processes_and_key_operations(monkeypatch):
    def forbidden(*args, **kwargs):
        raise AssertionError("Offline test attempted an actual process or key operation")
    monkeypatch.setattr(d.subprocess, "run", forbidden)
    monkeypatch.setattr(d.subprocess, "Popen", forbidden)
    monkeypatch.setattr(d.Runtime, "copy_key", forbidden)
    monkeypatch.setattr(d.Runtime, "remove_key", forbidden)
    monkeypatch.setattr(d, "host_observation", host_fixture)
    monkeypatch.setattr(d, "utc", lambda: OBSERVED_UTC)


def public_fixture():
    """Only the public header shape; no real key material and no file access."""
    raw = bytearray(160)
    struct.pack_into("<II", raw, 0, 0x2400, 0x8004)
    struct.pack_into("<I", raw, 24, 1024)
    return bytes(raw)


def pe_fixture():
    """Synthetic address/header layout, not an executable managed assembly."""
    raw = bytearray(1536)
    raw[:2] = b"MZ"
    struct.pack_into("<I", raw, 60, 128)
    raw[128:132] = b"PE\0\0"
    struct.pack_into("<H", raw, 134, 1)
    struct.pack_into("<H", raw, 148, 224)
    struct.pack_into("<H", raw, 152, 0x10B)
    struct.pack_into("<II", raw, 152 + 96 + 14 * 8, 0x2000, 72)
    struct.pack_into("<III", raw, 376 + 12, 0x2000, 1024, 512)
    struct.pack_into("<II", raw, 512 + 8, 0x2100, 256)
    struct.pack_into("<I", raw, 512 + 16, 1)
    struct.pack_into("<II", raw, 512 + 32, 0x2080, 128)
    raw[768:772] = b"BSJB"
    struct.pack_into("<I", raw, 768 + 12, 4)
    raw[784:788] = b"v4\0\0"
    struct.pack_into("<HH", raw, 788, 0, 1)
    struct.pack_into("<II", raw, 792, 96, 64)
    raw[800:809] = b"#Strings\0"
    raw[864:879] = b"\0AccountPlugin\0"
    return bytes(raw)


@pytest.fixture
def plan():
    tools = {}
    for name in ("git", "dotnet", "pwsh", "sn"):
        tools[name] = {"path": d.FIXED_SIGNER if name == "sn" else f"C:/fixture/{name}.exe",
                       "sha256": "d" * 64, "version": "1.2.3.4"}
    return {"schema": 1, "kind": "ppds-signing-compatibility-plan", "experiment": "offline-fixture",
            "source": SOURCE, "tree": TREE, "version": "3.1.1-compat.1",
            "public_key_sha256": d.digest(public_fixture()), "windows_directory": "C:/Windows",
            "tools": tools}


def stage_arguments(stage):
    args = [stage, "--execute-candidate", "--plan", "C:/fixture/plan.json",
            "--plan-sha256", PLAN_HASH, "--output", "C:/fixture/output"]
    if stage == "prepare":
        return args + ["--source-root", "C:/fixture/source", "--public-key", "C:/virtual/public"]
    if stage == "sign":
        return args + ["--prepared", "C:/fixture/prepared", "--disposable-key", "C:/virtual/private"]
    return args + ["--source-root", "C:/fixture/source", "--prepared", "C:/fixture/prepared",
                   "--signed", "C:/fixture/signed"]


def forbid_effects(monkeypatch):
    calls = []

    def forbidden(*args, **kwargs):
        calls.append((args, kwargs))
        raise AssertionError("Unauthorized path reached an I/O or process boundary")

    for name in ("read", "write", "fresh_output", "load_plan", "pin"):
        monkeypatch.setattr(d, name, forbidden)
    for name in ("run", "copy_key", "remove_key"):
        monkeypatch.setattr(d.Runtime, name, forbidden)
    monkeypatch.setattr(d.subprocess, "run", forbidden)
    for name in ("open", "mkdir", "exists", "read_bytes", "write_bytes", "unlink"):
        monkeypatch.setattr(Path, name, forbidden)
    return calls


@pytest.mark.parametrize("argv", [[], ["prepare"], ["sign"], ["verify"]])
def test_default_refuses_before_io_or_process(monkeypatch, capsys, argv):
    with monkeypatch.context() as patch:
        calls = forbid_effects(patch)
        assert d.main(argv) == 1
        assert calls == []
    assert "no passing compatibility claim" in capsys.readouterr().err


@pytest.mark.parametrize("authorization", [None, "", "x", "approval\nforged"])
def test_sign_requires_key_authorization_before_io(monkeypatch, authorization):
    argv = stage_arguments("sign")
    if authorization is not None:
        argv += ["--key-use-authorization", authorization]
    with monkeypatch.context() as patch:
        calls = forbid_effects(patch)
        assert d.main(argv) == 1
        assert calls == []


@pytest.mark.parametrize("stage", ["prepare", "verify"])
@pytest.mark.parametrize("forbidden", [
    ["--disposable-key", "C:/virtual/private"],
    ["--key-use-authorization", "fixture-approval-reference"],
])
def test_prepare_rejects_key_arguments(monkeypatch, stage, forbidden):
    with monkeypatch.context() as patch:
        calls = forbid_effects(patch)
        assert d.main(stage_arguments(stage) + forbidden) == 1
        assert calls == []


@pytest.mark.parametrize("extra", [
    ["--source-root", "C:/fixture/source"], ["--public-key", "C:/virtual/public"],
    ["--signed", "C:/fixture/other-output"],
])
def test_sign_rejects_build_or_extra_handoffs_before_io(monkeypatch, extra):
    argv = stage_arguments("sign") + ["--key-use-authorization", "fixture-approval-reference"] + extra
    with monkeypatch.context() as patch:
        calls = forbid_effects(patch)
        assert d.main(argv) == 1
        assert calls == []


@pytest.mark.parametrize("stage,argument", [
    ("prepare", "--plan"), ("prepare", "--output"), ("prepare", "--source-root"),
    ("prepare", "--public-key"), ("sign", "--prepared"),
    ("sign", "--disposable-key"), ("verify", "--signed"),
])
@pytest.mark.parametrize("path", [
    "\\\\server\\share\\output", "\\\\?\\C:\\fixture\\output",
    "C:/fixture/output:stream", "C:/fixture/../outside",
])
def test_unsafe_stage_paths_refuse_before_io(monkeypatch, stage, argument, path):
    argv = stage_arguments(stage)
    if stage == "sign":
        argv += ["--key-use-authorization", "fixture-approval-reference"]
    argv[argv.index(argument) + 1] = path
    with monkeypatch.context() as patch:
        calls = forbid_effects(patch)
        assert d.main(argv) == 1
        assert calls == []


def receipt_file_bytes(plan, stage="prepare"):
    if stage == "sign":
        return {"input.dll": b"fixture-dll", "signed.dll": b"synthetic signed output"}
    return {f"PPDS.Plugins.{plan['version']}.nupkg": b"synthetic package inventory fixture",
            f"PPDS.Plugins.{plan['version']}.snupkg": b"synthetic symbol inventory fixture",
            "test-public.bin": public_fixture(), "delay.dll": b"fixture-dll",
            "public.dll": b"synthetic public-sign control", "public.pdb": b"control PDB",
            "PPDS.Plugins.pdb": b"compiled PDB"}


def receipt_fixture(plan, plan_hash, stage="prepare"):
    value = {"schema": 1, "kind": "ppds-signing-compatibility-" + stage,
            **{key: plan[key] for key in ("experiment", "source", "tree", "version")},
            "plan_sha256": plan_hash, "tools": deepcopy(plan["tools"]),
            "driver_sha256": d.digest(DRIVER_BYTES), "probe_sha256": d.digest(d.PROBE.encode()),
            "helper_sha256": helper_fixture_hashes(), "source_inputs": helper_fixture_hashes(),
            "evidence_class": "real-process",  # Parser fixture, never actual execution evidence.
            "files": {name: {"sha256": d.digest(raw), "bytes": len(raw)}
                      for name, raw in receipt_file_bytes(plan, stage).items()}}
    value.update(observation_fixture(plan, stage))
    return value


def build_record_fixture(plan, control):
    packages = []
    for name in ("MinVer", "Microsoft.SourceLink.GitHub", "Microsoft.NETFramework.ReferenceAssemblies.net462"):
        raw = ("synthetic package " + name).encode()
        packages.append({"id": name, "version": "1.0.0", "sha256": d.digest(raw),
                         "sha512": base64.b64encode(hashlib.sha512(raw).digest()).decode()})
    return {"sdk": "10.0.403", "assets_sha256": "e" * 64, "packages": packages,
            "evaluated_properties": {"NETCoreSdkVersion": "10.0.403", "MSBuildVersion": "18.0.1",
                "TargetFramework": "net462", "SignAssembly": "true", "PublicSign": str(control).lower(),
                "DelaySign": str(not control).lower(), "Configuration": "Release",
                "PackageVersion": plan["version"], "InformationalVersion": plan["version"] + "+" + SOURCE,
                "AssemblyOriginatorKeyFile": "C:/virtual/public-never-opened",
                "AssemblyVersion": "3.1.1.0", "FileVersion": "3.1.1.0"},
            "binaries": {name: {"path": f"C:/virtual/{name}.dll", "sha256": "f" * 64,
                                 "bytes": 123, "product_version": "1.2.3.4"}
                         for name in ("msbuild", "roslyn")}}


def observation_fixture(plan, stage):
    """Complete parser fixtures, independent of any measured host/tool execution."""
    if stage == "sign":
        counts = {name: 1 for name in ("sign", "signed-forced-verification", "signature-only-comparison")}
    else:
        counts = {"inventory-" + name: 1 for name in ("git", "dotnet", "pwsh", "sn")}
        counts.update({name: 1 for name in ("delay-restore", "delay-build", "delay-pack", "public-restore",
                                           "public-build", "delay-forced-rejection")})
        counts.update({"source-identity": 5, "evaluated-properties": 2, "selected-sdk": 2,
                       "compiler-product-version": 4, "metadata": 2})
    value = {"recorded_utc": OBSERVED_UTC, "host": host_fixture(),
             "python": {"version": "synthetic interpreter", "executable": "C:/virtual/python.exe",
                        "sha256": "d" * 64},
             "commands": [{"operation": name, "exit_code": 17 if name == "delay-forced-rejection" else 0,
                           "started_utc": OBSERVED_UTC, "completed_utc": OBSERVED_UTC}
                          for name, count in counts.items() for _ in range(count)]}
    if stage == "sign":
        value.update(cases={"COMPAT-02": "SIGNATURE_OPERATIONS_PASSED_METADATA_PENDING"},
                     key_cleanup="completed", prepare_receipt_sha256="e" * 64,
                     authorization_reference="synthetic owner-reference parser fixture")
        return value
    metadata = {"name": "PPDS.Plugins", "assembly_version": "3.1.1.0", "culture": "",
                "public_key_sha256": plan["public_key_sha256"],
                "mvid": "00000000-0000-0000-0000-000000000001", "framework": ".NETFramework,Version=v4.6.2",
                "informational_version": plan["version"] + "+" + SOURCE,
                "codeview": {"guid": "00000000-0000-0000-0000-000000000002", "stamp": 123, "age": 1},
                "pdb": {"guid": "00000000-0000-0000-0000-000000000002", "stamp": 123},
                "mutation": {"name": "AccountPlugin", "heap_offset": 1}}
    value.update(cases={"COMPAT-01": "PASS", "COMPAT-03": "PASS"}, metadata=metadata,
                 control_metadata=deepcopy(metadata),
                 tool_product_versions={name: row["version"] for name, row in plan["tools"].items()},
                 build_evidence={"delay": build_record_fixture(plan, False),
                                 "public": build_record_fixture(plan, True)},
                 build_overlay=["-p:SignAssembly=true", "-p:DebugType=portable", "-p:Version=" + plan["version"],
                                "-p:InformationalVersion=" + plan["version"] + "+" + SOURCE])
    return value


def virtual_receipt(monkeypatch, directory, plan, stage):
    value = receipt_fixture(plan, PLAN_HASH, stage)
    virtual = {d.ROOT / name: raw for name, raw in helper_fixture_bytes().items()}
    virtual.update({directory / name: raw for name, raw in receipt_file_bytes(plan, stage).items()})
    virtual[Path(d.__file__)] = DRIVER_BYTES
    receipt_path = directory / (stage + "-receipt.json")
    virtual[receipt_path] = json.dumps(value).encode()
    monkeypatch.setattr(d, "read", lambda path, limit=d.h.MAX_PACKAGE: virtual[Path(path)])
    return value, virtual, receipt_path


def test_stage_plan_and_receipt_binding(monkeypatch, tmp_path, plan):
    plan_raw = json.dumps(plan).encode()
    plan_hash = d.digest(plan_raw)
    value = receipt_fixture(plan, plan_hash)
    virtual = {tmp_path / "plan.json": plan_raw, Path(d.__file__): DRIVER_BYTES,
               tmp_path / "delay.dll": b"fixture-dll"}
    virtual.update({tmp_path / name: raw for name, raw in receipt_file_bytes(plan).items()})
    virtual.update({d.ROOT / name: raw for name, raw in helper_fixture_bytes().items()})
    monkeypatch.setattr(d, "read", lambda path, limit=d.h.MAX_PACKAGE: virtual[Path(path)])
    assert d.load_plan(tmp_path / "plan.json", plan_hash) == plan
    with pytest.raises(d.Error, match="Plan hash mismatch"):
        d.load_plan(tmp_path / "plan.json", "e" * 64)
    virtual[tmp_path / "prepare-receipt.json"] = json.dumps(value).encode()
    accepted, actual_hash = d.receipt(tmp_path, "prepare", plan, plan_hash)
    assert accepted == value
    assert actual_hash == d.digest(virtual[tmp_path / "prepare-receipt.json"])
    for key, replacement in [("source", "e" * 40), ("tree", "e" * 40),
                             ("plan_sha256", "e" * 64), ("driver_sha256", "e" * 64),
                             ("probe_sha256", "e" * 64), ("evidence_class", "process-double")]:
        bad = deepcopy(value)
        bad[key] = replacement
        virtual[tmp_path / "prepare-receipt.json"] = json.dumps(bad).encode()
        with pytest.raises(d.Error):
            d.receipt(tmp_path, "prepare", plan, plan_hash)
    bad = deepcopy(value)
    bad["tools"]["sn"]["sha256"] = "e" * 64
    virtual[tmp_path / "prepare-receipt.json"] = json.dumps(bad).encode()
    with pytest.raises(d.Error, match="source/tool"):
        d.receipt(tmp_path, "prepare", plan, plan_hash)
    for field in ("helper_sha256", "source_inputs"):
        bad = deepcopy(value)
        bad[field][d.HELPERS[0]] = "e" * 64
        virtual[tmp_path / "prepare-receipt.json"] = json.dumps(bad).encode()
        with pytest.raises(d.Error, match="source"):
            d.receipt(tmp_path, "prepare", plan, plan_hash)
    virtual[tmp_path / "prepare-receipt.json"] = json.dumps(value).encode()
    virtual[tmp_path / "delay.dll"] = b"changed-fixture"
    with pytest.raises(d.Error, match="artifact hash"):
        d.receipt(tmp_path, "prepare", plan, plan_hash)


@pytest.mark.parametrize("stage", ["prepare", "sign"])
@pytest.mark.parametrize("change", ["missing", "extra"])
def test_receipt_rejects_missing_or_extra_handoff_files(monkeypatch, tmp_path, plan, stage, change):
    value = receipt_fixture(plan, PLAN_HASH, stage)
    virtual = {d.ROOT / name: raw for name, raw in helper_fixture_bytes().items()}
    virtual.update({tmp_path / name: raw for name, raw in receipt_file_bytes(plan, stage).items()})
    virtual[Path(d.__file__)] = DRIVER_BYTES
    monkeypatch.setattr(d, "read", lambda path, limit=d.h.MAX_PACKAGE: virtual[Path(path)])
    receipt_path = tmp_path / (stage + "-receipt.json")
    virtual[receipt_path] = json.dumps(value).encode()
    assert d.receipt(tmp_path, stage, plan, PLAN_HASH)[0] == value
    if change == "missing":
        value["files"].pop("public.pdb" if stage == "prepare" else "signed.dll")
    else:
        raw = b"unexpected handoff member"
        virtual[tmp_path / "unexpected.txt"] = raw
        value["files"]["unexpected.txt"] = {"sha256": d.digest(raw), "bytes": len(raw)}
    virtual[receipt_path] = json.dumps(value).encode()
    with pytest.raises(d.Error):
        d.receipt(tmp_path, stage, plan, PLAN_HASH)


@pytest.mark.parametrize("stage,field", [
    (stage, field) for stage in ("prepare", "sign")
    for field in ("commands", "host", "python", "python.sha256", "recorded_utc")
] + [("prepare", field) for field in (
    "build_evidence", "build_evidence.delay.assets_sha256", "build_evidence.public.binaries",
    "build_overlay", "metadata", "control_metadata", "control_metadata.pdb",
    "tool_product_versions", "cases"
)] + [("sign", field) for field in (
    "cases", "key_cleanup", "prepare_receipt_sha256", "authorization_reference"
)])
def test_receipt_rejects_missing_required_observation(monkeypatch, tmp_path, plan, stage, field):
    value, virtual, receipt_path = virtual_receipt(monkeypatch, tmp_path, plan, stage)
    assert d.receipt(tmp_path, stage, plan, PLAN_HASH)[0] == value
    cursor = value
    parts = field.split(".")
    for part in parts[:-1]:
        cursor = cursor[part]
    del cursor[parts[-1]]
    virtual[receipt_path] = json.dumps(value).encode()
    with pytest.raises(d.Error):
        d.receipt(tmp_path, stage, plan, PLAN_HASH)


@pytest.mark.parametrize("stage", ["prepare", "sign"])
@pytest.mark.parametrize("change", ["missing", "failed-success", "wrong-exit", "missing-time", "reversed-time", "extra", "boolean-exit"])
def test_receipt_rejects_failed_or_missing_command(monkeypatch, tmp_path, plan, stage, change):
    value, virtual, receipt_path = virtual_receipt(monkeypatch, tmp_path, plan, stage)
    assert d.receipt(tmp_path, stage, plan, PLAN_HASH)[0] == value
    commands = value["commands"]
    if change == "missing":
        commands.pop(0)
    elif change == "failed-success":
        commands[0]["exit_code"] = 17
    elif change == "wrong-exit":
        row = next(c for c in commands if c["operation"] == ("delay-forced-rejection" if stage == "prepare" else "sign"))
        row["exit_code"] = 0 if stage == "prepare" else 17
    elif change == "missing-time":
        commands[0].pop("started_utc")
    elif change == "reversed-time":
        commands[0]["completed_utc"] = "2026-11-10T01:00:00+00:00"
    elif change == "boolean-exit":
        commands[0]["exit_code"] = False
    else:
        commands.append({"operation": "unapproved-extra-operation", "exit_code": 0,
                         "started_utc": OBSERVED_UTC, "completed_utc": OBSERVED_UTC})
    virtual[receipt_path] = json.dumps(value).encode()
    with pytest.raises(d.Error):
        d.receipt(tmp_path, stage, plan, PLAN_HASH)


@pytest.mark.parametrize("name,version", [
    ("pwsh", "7.6.10 SHA: 16ac95d24d87c61a5d8300dae2d0619db06c555c+16ac95d24d87c61a5d8300dae2d0619db06c555c"),
    ("dotnet", "10.0.15 @Commit: 8e0f8ed2312954727f8919d7d1c2f78b483b4ee7"),
])
def test_product_versions_accept_actual_shape_and_require_exact_match(monkeypatch, tmp_path, plan, name, version):
    plan["tools"][name]["version"] = version
    raw = json.dumps(plan).encode()
    monkeypatch.setattr(d, "read", lambda *args, **kwargs: raw)
    assert d.load_plan(Path("virtual-plan"), d.digest(raw)) == plan

    def product(*args, **kwargs):
        tool_path = args[3][-1]
        return next(row["version"] for row in plan["tools"].values() if row["path"] == tool_path) + "\n"

    monkeypatch.setattr(d, "invoke", product)
    assert d.tool_versions(d.Runtime(evidence_class="process-double"), plan, tmp_path, Path("virtual-probe"))[name] == version
    monkeypatch.setattr(d, "invoke", lambda *args, **kwargs: product(*args, **kwargs).rstrip("\n") + "-changed")
    with pytest.raises(d.Error, match="product version mismatch"):
        d.tool_versions(d.Runtime(evidence_class="process-double"), plan, tmp_path, Path("virtual-probe"))


@pytest.mark.parametrize("version", ["", "version\nforged", "version\rforged", "tab\tvalue", "nul\0value", "del\x7f", "x" * 257, "non-ascii-\u00e9"])
def test_product_version_rejects_controls_newlines_and_overlong_values(monkeypatch, plan, version):
    plan["tools"]["pwsh"]["version"] = version
    raw = json.dumps(plan).encode()
    monkeypatch.setattr(d, "read", lambda *args, **kwargs: raw)
    assert d.product_version(version) is False
    with pytest.raises(d.Error, match="hash/version"):
        d.load_plan(Path("virtual-plan"), d.digest(raw))


@pytest.mark.parametrize("change", ["production-public", "extra-tool", "different-sn", "extra-field"])
def test_plan_refuses_production_identity_or_unexpected_inputs(monkeypatch, plan, change):
    value = deepcopy(plan)
    if change == "production-public":
        value["public_key_sha256"] = d.PRODUCTION_PUBLIC_HASH
    elif change == "extra-tool":
        value["tools"]["shell"] = deepcopy(value["tools"]["pwsh"])
    elif change == "different-sn":
        value["tools"]["sn"]["path"] = "C:/fixture/sn.exe"
    else:
        value["command"] = "not permitted"
    raw = json.dumps(value).encode()
    monkeypatch.setattr(d, "read", lambda *args, **kwargs: raw)
    with pytest.raises(d.Error):
        d.load_plan(Path("virtual-plan"), d.digest(raw))


class SigningDouble(d.Runtime):
    """Private paths are recorded as opaque values; never opened or created."""
    def __init__(self, *, failure=None):
        super().__init__(evidence_class="process-double")
        self.failure = failure
        self.key_events = []
        self.commands = []

    def copy_key(self, supplied, owned):
        self.key_events.append(("copy", supplied, owned))
        if self.failure == "copy":
            raise d.Error("controlled copy failure")

    def remove_key(self, owned):
        self.key_events.append(("remove", owned))
        if self.failure == "cleanup":
            raise OSError("controlled cleanup failure containing a private path")

    def run(self, command, **kwargs):
        operation = kwargs["operation"]
        self.commands.append((command, kwargs))
        code = 17 if self.failure == operation else 0
        self.calls.append({"operation": operation, "exit_code": code})
        return SimpleNamespace(returncode=code, stdout="sensitive tool diagnostics", stderr="sensitive private path")


@pytest.fixture
def signing_fixture(monkeypatch, tmp_path, plan):
    args = SimpleNamespace(prepared=tmp_path / "prepared", output=tmp_path / "signed",
                           disposable_key=Path("C:/virtual/never-open-private"),
                           key_use_authorization="fixture-approval-reference", plan_sha256=PLAN_HASH)
    prepared = {"cases": {"COMPAT-01": "PASS", "COMPAT-03": "PASS"},
                "tool_product_versions": {"sn": plan["tools"]["sn"]["version"]}}
    monkeypatch.setattr(d, "receipt", lambda *args, **kwargs: (prepared, "e" * 64))
    virtual = {args.prepared / "test-public.bin": public_fixture(),
               args.prepared / "delay.dll": pe_fixture(), Path(d.__file__): DRIVER_BYTES}
    virtual.update({d.ROOT / name: raw for name, raw in helper_fixture_bytes().items()})
    virtual[Path(sys.executable)] = b"synthetic interpreter identity, never executed"

    def read_fixture(path, limit=d.h.MAX_PACKAGE):
        path = Path(path)
        if path in virtual:
            return virtual[path]
        assert path.parent == args.output and path.name in {"input.dll", "signed.dll"}
        return path.read_bytes()

    monkeypatch.setattr(d, "read", read_fixture)
    pins = []
    monkeypatch.setattr(d, "pin", lambda value, names: pins.extend(names))
    return SimpleNamespace(args=args, plan=plan, pins=pins)


@pytest.mark.parametrize("operation", ["restore", "build", "pack"])
@pytest.mark.parametrize("inherited_certificate_setting", [None, "true"])
def test_build_invocation_suppresses_certificate_generation_without_inheriting_environment(
        monkeypatch, tmp_path, plan, operation, inherited_certificate_setting):
    calls = []

    def capture(command, **options):
        calls.append((command, options))
        return SimpleNamespace(returncode=0, stdout="fixture-only", stderr="")

    synthetic_environment = {"PATH": "untrusted-parent-path", "PLUGINS_SNK_BASE64": "synthetic-secret-marker"}
    if inherited_certificate_setting is not None:
        synthetic_environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = inherited_certificate_setting
    with monkeypatch.context() as patch:
        patch.setattr(os, "environ", synthetic_environment)
        patch.setattr(d, "pin", lambda *args: None)
        d.invoke(SimpleNamespace(run=capture), plan, "dotnet", [operation], tmp_path,
                 "fixture-" + operation, build=True)

    assert len(calls) == 1
    command, options = calls[0]
    assert command == [plan["tools"]["dotnet"]["path"], operation]
    child = options["environment"]
    assert child["DOTNET_GENERATE_ASPNET_CERTIFICATE"] == "false"
    assert child["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] == "1"
    assert "PLUGINS_SNK_BASE64" not in child
    assert "untrusted-parent-path" not in child["PATH"]
    assert set(child) == {"SYSTEMROOT", "WINDIR", "TEMP", "TMP", "ProgramFiles(x86)",
                          "DOTNET_CLI_HOME", "NUGET_PACKAGES", "DOTNET_CLI_TELEMETRY_OPTOUT",
                          "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
                          "DOTNET_GENERATE_ASPNET_CERTIFICATE", "PATH"}


def test_sign_launches_only_sn_and_does_not_claim_completed_metadata(signing_fixture):
    f = signing_fixture
    rt = SigningDouble()
    value = d.sign(f.args, f.plan, rt)
    assert [command[1] for command, _ in rt.commands] == ["-R", "-vf", "-D"]
    assert {command[0] for command, _ in rt.commands} == {f.plan["tools"]["sn"]["path"]}
    assert set(f.pins) == {"sn"}
    assert [event[0] for event in rt.key_events] == ["copy", "remove"]
    assert value["evidence_class"] == "process-double"
    assert value["cases"] == {"COMPAT-02": "SIGNATURE_OPERATIONS_PASSED_METADATA_PENDING"}
    assert "result" not in value
    for _, options in rt.commands:
        assert set(options["environment"]) == {"SYSTEMROOT", "WINDIR", "TEMP", "TMP", "ProgramFiles(x86)"}
        assert options["environment"]["ProgramFiles(x86)"] == "C:\\Program Files (x86)"


@pytest.mark.parametrize("failure", ["sign", "signed-forced-verification", "signature-only-comparison"])
def test_tool_failure_cannot_emit_pass(signing_fixture, capsys, failure):
    f = signing_fixture
    rt = SigningDouble(failure=failure)
    with pytest.raises(d.Error, match="required outcome"):
        d.sign(f.args, f.plan, rt)
    assert not (f.args.output / "sign-receipt.json").exists()
    assert not (f.args.output / "signed.dll").exists()
    assert any(event[0] == "remove" for event in rt.key_events)
    output = capsys.readouterr()
    assert "sensitive" not in output.out + output.err


@pytest.mark.parametrize("failure", ["copy", "sign", "cleanup"])
def test_sign_cleanup_on_failure(signing_fixture, failure):
    f = signing_fixture
    rt = SigningDouble(failure=failure)
    with pytest.raises(d.Error):
        d.sign(f.args, f.plan, rt)
    assert [event[0] for event in rt.key_events].count("remove") >= 1
    assert not (f.args.output / "sign-receipt.json").exists()
    if failure != "cleanup":
        assert not (f.args.output / "signed.dll").exists()
    assert (f.args.output / "input.dll").read_bytes() == pe_fixture()


def test_runtime_timeout_redacts_diagnostics_and_disables_shell(monkeypatch, tmp_path):
    seen = []

    def timeout(command, **kwargs):
        seen.append((command, kwargs))
        raise subprocess.TimeoutExpired(command, kwargs["timeout"], output="private-key-marker",
                                        stderr="secret-diagnostic-marker")

    monkeypatch.setattr(d.subprocess, "run", timeout)
    rt = d.Runtime(evidence_class="process-double")
    with pytest.raises(d.Error, match="^Candidate tool did not complete$") as error:
        rt.run(["C:/fixture/sn.exe", "-R", "fixture.dll", "private-key-marker"],
               cwd=tmp_path, environment={}, operation="sign", timeout=3)
    assert error.value.__suppress_context__ is True
    assert "private-key-marker" not in str(error.value)
    assert "secret-diagnostic-marker" not in str(error.value)
    assert seen[0][1]["shell"] is False and seen[0][1]["timeout"] == 3
    assert seen[0][1]["env"] == {} and seen[0][1]["stdin"] == subprocess.DEVNULL
    assert rt.calls == [{"operation": "sign", "exit_code": None, "started_utc": OBSERVED_UTC,
                         "completed_utc": OBSERVED_UTC, "status": "timeout"}]


@pytest.mark.parametrize("owns_output", [False, True])
def test_main_retains_only_owned_public_failure_receipt(monkeypatch, tmp_path, plan, capsys, owns_output):
    rt = d.Runtime(evidence_class="process-double")
    emitted = []
    monkeypatch.setattr(d.sys, "platform", "win32")
    monkeypatch.setattr(d, "load_plan", lambda *args: plan)
    monkeypatch.setattr(d, "json_write", lambda path, value: emitted.append((path, value)))

    def timeout(command, **kwargs):
        raise subprocess.TimeoutExpired(command, kwargs["timeout"], output="private-key-marker",
                                        stderr="private-diagnostic-marker")

    def fail_stage(args, selected_plan, runtime):
        runtime.owned_output = tmp_path / "owned" if owns_output else None
        runtime.run(["C:/fixture/sn.exe", "-R", "private-key-marker"], cwd=tmp_path,
                    environment={}, operation="sign", timeout=1)

    monkeypatch.setattr(d.subprocess, "run", timeout)
    monkeypatch.setattr(d, "prepare", fail_stage)
    assert d.main(stage_arguments("prepare"), runtime=rt) == 1
    captured = capsys.readouterr()
    assert "private-key-marker" not in captured.out + captured.err + json.dumps(emitted, default=str)
    assert "private-diagnostic-marker" not in captured.out + captured.err + json.dumps(emitted, default=str)
    assert len(emitted) == int(owns_output)
    if owns_output:
        path, failure = emitted[0]
        assert path == tmp_path / "owned/failure-receipt.json"
        assert failure["result"] == "FAIL" and failure["commands"][0]["status"] == "timeout"


@pytest.mark.parametrize("operation,cleanup", [("sign", "in_progress"), ("signed-forced-verification", "completed")])
def test_private_phase_diagnostics_are_not_retained(monkeypatch, tmp_path, operation, cleanup):
    monkeypatch.setattr(d.subprocess, "run", lambda *args, **kwargs: SimpleNamespace(
        returncode=0, stdout="private-key-marker", stderr="private-diagnostic-marker"))
    rt = d.Runtime(evidence_class="process-double", key_cleanup=cleanup)
    rt.run(["C:/fixture/sn.exe", "fixture-argument"], cwd=tmp_path,
           environment={}, operation=operation)
    assert rt.calls[0]["diagnostics_omitted"] == "private-key phase"
    assert "private-key-marker" not in json.dumps(rt.calls)
    assert "private-diagnostic-marker" not in json.dumps(rt.calls)


@pytest.mark.parametrize("variant", ["missing", "skipped", "double", "extra", "pass"])
def test_six_case_receipt_requires_real_results(variant):
    cases = {name: "PASS" for name in d.CASES}
    evidence = "real-process"
    if variant == "missing":
        cases.pop("COMPAT-06")
    elif variant == "skipped":
        cases["COMPAT-02"] = "SKIP"
    elif variant == "double":
        evidence = "process-double"
    elif variant == "extra":
        cases["COMPAT-07"] = "PASS"
    if variant == "pass":
        assert d.complete_cases(cases, evidence) == "PASS"  # Schema-only fixture assertion.
    else:
        with pytest.raises(d.Error, match="All six real cases"):
            d.complete_cases(cases, evidence)


def zip_package(version, dll):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr("PPDS.Plugins.nuspec", '<package><metadata><id>PPDS.Plugins</id>'
                         f'<version>{version}</version><repository commit="{SOURCE}"/></metadata></package>')
        archive.writestr(d.p.DLL, dll)
        archive.writestr("README.md", b"unchanged synthetic package member")
    return output.getvalue()


def test_package_adapter_checks_real_bytes(monkeypatch, tmp_path, plan):
    """Real ZIP/extraction/package rules; the signature/metadata processes are doubles."""
    monkeypatch.setattr(d.release.tempfile, "tempdir", str(tmp_path))
    monkeypatch.setattr(d, "pin", lambda *args: None)
    project = tmp_path / "plugins.csproj"
    project.write_text('<Project><PropertyGroup><TargetFramework>net462</TargetFramework>'
                       '<AssemblyName>PPDS.Plugins</AssemblyName></PropertyGroup></Project>', encoding="utf-8")
    graph = SimpleNamespace(surfaces={"PPDS.Plugins": SimpleNamespace(
        project_path=project.name, project_dependencies=set(), is_tool=False)})
    public_path = Path("C:/virtual/public-never-opened")
    dll_bytes = b"synthetic DLL bytes: never loaded or executed"
    observed = []

    class PackageDouble(d.Runtime):
        def run(self, command, **options):
            operation = options["operation"]
            assert options["environment"]["ProgramFiles(x86)"] == "C:\\Program Files (x86)"
            assert "PATH" not in options["environment"]
            if operation == "packaged-forced-verification":
                actual = Path(command[2]).read_bytes()
                observed.append((operation, actual))
                return SimpleNamespace(returncode=0 if actual == dll_bytes else 17, stdout="", stderr="")
            assert operation == "packaged-metadata"
            directory = Path(command[command.index("-Directory") + 1])
            assert Path(command[command.index("-ExpectedPublicKey") + 1]) == public_path
            observed.append((operation, (directory / d.DLL).read_bytes()))
            return SimpleNamespace(returncode=0, stderr="", stdout=json.dumps([
                {"name": "PPDS.Plugins", "version": plan["version"] + "+" + SOURCE}]))

    rt = PackageDouble(evidence_class="process-double")
    adapter = d.package_adapter(rt, plan, tmp_path, public_path)
    package = tmp_path / "candidate.nupkg"
    package.write_bytes(zip_package(plan["version"], dll_bytes))
    d.release.verify_nuget(package, "Plugins-v" + plan["version"], tmp_path, SOURCE,
                           {"PPDS.Plugins": plan["version"]}, graph, inspect=adapter)
    assert observed == [("packaged-forced-verification", dll_bytes), ("packaged-metadata", dll_bytes)]
    package.write_bytes(zip_package(plan["version"], b"tampered synthetic DLL"))
    with pytest.raises(d.Error, match="required outcome"):
        d.release.verify_nuget(package, "Plugins-v" + plan["version"], tmp_path, SOURCE,
                               {"PPDS.Plugins": plan["version"]}, graph, inspect=adapter)
    assert observed[-1] == ("packaged-forced-verification", b"tampered synthetic DLL")


def test_pe_aware_mutations_change_only_classified_bytes():
    original = pe_fixture()
    meta = {"mutation": {"name": "AccountPlugin", "heap_offset": 1}}
    signature, metadata, offsets = d.mutations(original, meta)
    assert offsets == {"signature_offset": 640, "metadata_offset": 865}
    assert [i for i, (a, b) in enumerate(zip(original, signature)) if a != b] == [640]
    assert [i for i, (a, b) in enumerate(zip(original, metadata)) if a != b] == [865]
    assert metadata[865:879] == b"accountPlugin\0"
    assert d.pe_layout(signature) == d.pe_layout(metadata) == d.pe_layout(original)
    with pytest.raises(d.Error, match="Valid metadata name mutation"):
        d.mutations(original, {"mutation": {"name": "DifferentName", "heap_offset": 1}})


@pytest.mark.parametrize("control", ["public", "malformed", "dirty-reservation", "delay"])
def test_prepare_requires_the_specific_public_sign_control(monkeypatch, tmp_path, plan, control):
    """Prepare orchestration with virtual files and synthetic compiler/process output."""
    args = SimpleNamespace(output=tmp_path / "prepare", source_root=d.ROOT,
                           public_key=Path("C:/virtual/public-never-opened"), plan_sha256=PLAN_HASH)
    public_dll = bytearray(pe_fixture())
    struct.pack_into("<I", public_dll, 512 + 16, 9)
    if control == "malformed":
        public_dll = bytearray(b"not a PE")
    elif control == "dirty-reservation":
        public_dll[640] = 1
    elif control == "delay":
        struct.pack_into("<I", public_dll, 512 + 16, 1)
    virtual = {args.public_key: public_fixture()}
    expected = [f"PPDS.Plugins.{plan['version']}.{suffix}" for suffix in ("nupkg", "snupkg")]
    for name in expected:
        virtual[args.output / "packages" / name] = zip_package(plan["version"], pe_fixture())
    emitted, observed_metadata, operations = [], [], []

    def read_virtual(path, limit=d.h.MAX_PACKAGE):
        path = Path(path)
        if path in virtual:
            return virtual[path]
        if path.name == d.DLL and "bin" in path.parts:
            return bytes(public_dll) if "public" in path.parts else pe_fixture()
        if path.name == "PPDS.Plugins.pdb" and "bin" in path.parts:
            return b"synthetic PDB"
        raise AssertionError(f"Unmodelled fixture read: {path}")

    real_iterdir = Path.iterdir
    monkeypatch.setattr(Path, "iterdir", lambda path: iter(args.output / "packages" / n for n in expected)
                        if path == args.output / "packages" else real_iterdir(path))
    monkeypatch.setattr(d, "read", read_virtual)
    monkeypatch.setattr(d, "write", lambda path, raw: virtual.__setitem__(Path(path), raw))
    monkeypatch.setattr(d, "json_write", lambda path, value: emitted.append((path, value)))
    monkeypatch.setattr(d, "pin", lambda *args: None)
    monkeypatch.setattr(d, "install_probe", lambda output: output / "virtual-probe.ps1")
    monkeypatch.setattr(d, "tool_versions", lambda *args: {n: v["version"] for n, v in plan["tools"].items()})
    monkeypatch.setattr(d, "source_copy", lambda *args: (args[-1] / "source", helper_fixture_hashes()))
    monkeypatch.setattr(d, "build_evidence", lambda *args: {"fixture_only": True})
    monkeypatch.setattr(d, "invoke", lambda *args, **kwargs: operations.append(args[5]) or "")
    monkeypatch.setattr(d, "common", lambda *args: {"evidence_class": "process-double", "cases": {}})
    monkeypatch.setattr(d, "metadata", lambda *args: observed_metadata.append(Path(args[4]).name) or
                        {"fixture_only": True})
    if control == "public":
        result = d.prepare(args, plan, d.Runtime(evidence_class="process-double"))
        assert result["cases"] == {"COMPAT-01": "PASS", "COMPAT-03": "PASS"}
        assert len(emitted) == 1 and observed_metadata == ["delay.dll", "public.dll"]
    else:
        with pytest.raises((d.Error, struct.error)):
            d.prepare(args, plan, d.Runtime(evidence_class="process-double"))
        assert emitted == []
    assert "sign" not in operations


def test_build_evidence_checks_actual_properties_and_package_hashes(monkeypatch, tmp_path, plan):
    output, source, work = tmp_path / "out", tmp_path / "source", tmp_path / "work"
    props = {"NETCoreSdkVersion": "10.0.403", "MSBuildVersion": "18.0.1",
             "MSBuildBinPath": str(tmp_path / "sdk"), "RoslynTargetsPath": str(tmp_path / "roslyn"),
             "CscToolPath": "", "CscToolExe": "", "TargetFramework": "net462", "Configuration": "Release",
             "SignAssembly": "true", "PublicSign": "false", "DelaySign": "true",
             "AssemblyOriginatorKeyFile": str(output / "test-public.bin"),
             "AssemblyVersion": "3.1.1.0", "FileVersion": "3.1.1.0",
             "InformationalVersion": plan["version"] + "+" + SOURCE, "PackageVersion": plan["version"]}
    assets = {"libraries": {}}
    virtual = {}
    for name in ("MinVer", "Microsoft.SourceLink.GitHub", "Microsoft.NETFramework.ReferenceAssemblies.net462"):
        raw = ("synthetic resolved package " + name).encode()
        version = "1.0.0"
        relative = name.lower() + "/" + version
        assets["libraries"][name + "/" + version] = {
            "type": "package", "path": relative, "sha512": base64.b64encode(hashlib.sha512(raw).digest()).decode()}
        virtual[output / "temp/packages" / relative / (name.lower() + "." + version + ".nupkg")] = raw

    def read_virtual(path, limit=d.h.MAX_PACKAGE):
        if Path(path) == work / "obj/project.assets.json":
            return json.dumps(assets).encode()
        return virtual[Path(path)]

    def invoke(*args, **kwargs):
        operation = args[5]
        if operation == "evaluated-properties":
            return json.dumps({"Properties": props})
        if operation == "selected-sdk":
            return "10.0.403"
        assert operation == "compiler-product-version"
        return "1.2.3.4"

    monkeypatch.setattr(d, "read", read_virtual)
    monkeypatch.setattr(d, "record", lambda path: {"sha256": "f" * 64, "bytes": 123})
    monkeypatch.setattr(d, "invoke", invoke)
    call = lambda: d.build_evidence(d.Runtime(evidence_class="process-double"), plan, output, source,
                                   source / "plugins.csproj", work, [], output / "probe.ps1", False)
    result = call()
    assert result["sdk"] == "10.0.403" and len(result["packages"]) == 3
    assert set(result["binaries"]) == {"msbuild", "roslyn"}
    first = next(iter(assets["libraries"].values()))
    first["sha512"] = "incorrect-content-hash"
    with pytest.raises(d.Error, match="content hash"):
        call()
    props["PublicSign"] = "true"
    with pytest.raises(d.Error, match="properties differ"):
        call()
    props["PublicSign"] = "false"
    props["Configuration"] = "Debug"
    with pytest.raises(d.Error, match="properties differ"):
        call()
