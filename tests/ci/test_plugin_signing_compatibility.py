"""Offline contracts only: synthetic PE/ZIP bytes and process/key-operation doubles.

These tests never read a key/public-key file, launch a tool, inspect the ambient
environment, or establish real Windows signing compatibility.
"""
import ast
from copy import deepcopy
import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
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


@pytest.mark.parametrize("error,expected", [
    (d.Error("Pinned tool bytes differ"), "tool-hash"),
    (d.Error("Tool product version mismatch"), "tool-version"),
    (d.Error("Restore used a NuGet fallback folder"), "restore-fallback-folder"),
    (d.Error("PRIVATE_PATH_OR_TOKEN"), "unregistered"),
    (d.Error("Pinned tool bytes differ PRIVATE_VALUE"), "unregistered"),
    (ValueError("Pinned tool bytes differ"), "exception-valueerror"),
    (FileNotFoundError(2, "No such file", "C:/PRIVATE/path.snk"), "exception-filenotfounderror"),
    (KeyError("PRIVATE_KEY_NAME"), "exception-keyerror"),
    (json.JSONDecodeError("PRIVATE_DOCUMENT", "PRIVATE_DOCUMENT", 0), "exception-jsondecodeerror"),
    (type("PrivateCustomError", (Exception,), {})("PRIVATE"), "exception-other"),
])
def test_failure_diagnostic_uses_only_exact_fixed_codes(error, expected):
    code = d.safe_refusal(error)
    assert code == expected and code in d.REFUSAL_CODES and "PRIVATE" not in code.upper()


def literal_refusal_reasons(path):
    """Every constant reason passed to require(...) or raised as Error/ValueError(...) in a source file."""
    tree = ast.parse(path.read_text(encoding="utf-8"))
    wrappers = {node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "require"}
    inside_wrapper = {id(child) for wrapper in wrappers for child in ast.walk(wrapper)}
    reasons, dynamic = set(), []
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call) or id(node) in inside_wrapper:
            continue
        name = getattr(node.func, "id", getattr(node.func, "attr", None))
        if name == "require" and len(node.args) >= 2:
            argument = node.args[1]
        elif name in {"Error", "ValueError"} and node.args:
            argument = node.args[0]
        else:
            continue
        if isinstance(argument, ast.Constant) and isinstance(argument.value, str):
            reasons.add(argument.value)
        else:
            dynamic.append(node.lineno)
    return reasons, dynamic


def test_refusal_registry_covers_every_driver_reason_with_a_distinct_code():
    reasons, dynamic = literal_refusal_reasons(DRIVER_PATH)
    assert len(reasons) > 100
    assert sorted(reasons - set(d.REFUSALS)) == []
    # A reason built at run time can never be printed: it would map to the fixed bucket.
    assert dynamic == []
    codes = list(d.REFUSALS.values())
    assert len(codes) == len(set(codes)), "two reasons share a refusal code"
    assert all(re.fullmatch(r"[a-z0-9]+(-[a-z0-9]+)*", code) and len(code) <= 48 for code in d.REFUSAL_CODES)
    assert d.UNREGISTERED_REFUSAL not in codes

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
    return {"schema": 2, "kind": "ppds-signing-compatibility-plan", "experiment": "offline-fixture",
            "source": SOURCE, "tree": TREE, "version": "3.1.1-compat.1",
            "public_key_sha256": d.digest(public_fixture()), "windows_directory": "C:/Windows",
            "windows_common_data_directory": "C:/ProgramData",
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


NUGET_ORG = "https://api.nuget.org/v3/index.json"


def build_record_fixture(plan, control):
    packages = []
    for name, reference in (("MinVer", "direct-pinned"), ("Microsoft.SourceLink.GitHub", "direct-pinned"),
                            ("Microsoft.NETFramework.ReferenceAssemblies.net462", "transitive")):
        raw = ("synthetic package " + name).encode()
        content = base64.b64encode(hashlib.sha512(b"content " + raw).digest()).decode()
        packages.append({"id": name, "version": "1.0.0", "sha256": d.digest(raw),
                         "sha512": base64.b64encode(hashlib.sha512(raw).digest()).decode(),
                         "content_sha512": content, "source": NUGET_ORG, "reference": reference,
                         "pin": "1.0.0" if reference == "direct-pinned" else None,
                         "signature": {"content_sha512": content, "repository_service_index": NUGET_ORG,
                                       "repository_certificate_sha256": "a" * 64}})
    # The candidate 3.1.1-compat.1 uses MinVer's production rule, not {Major}.{Minor}.{Patch}.0 for both.
    return {"sdk": "10.0.403", "assets_sha256": "e" * 64, "packages": packages,
            "restore": {"fallback_folders": [], "package_folders": 1, "source": NUGET_ORG, "sources": [NUGET_ORG]},
            "evaluated_properties": {"NETCoreSdkVersion": "10.0.403", "MSBuildVersion": "18.0.1",
                "TargetFramework": "net462", "SignAssembly": "true", "PublicSign": str(control).lower(),
                "DelaySign": str(not control).lower(), "Configuration": "Release",
                "PackageVersion": plan["version"], "MinVerVersion": plan["version"],
                "InformationalVersion": plan["version"] + "+" + SOURCE,
                "AssemblyOriginatorKeyFile": "C:/virtual/public-never-opened",
                "AssemblyVersion": "3.0.0.0", "FileVersion": "3.1.1.0"},
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
                       "compiler-product-version": 4, "metadata": 2,
                       # One per recorded package row: three packages in each of the two builds.
                       "package-signature-verification": 6})
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
    metadata = {"name": "PPDS.Plugins", "assembly_version": "3.0.0.0", "file_version": "3.1.1.0", "culture": "",
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
                 build_overlay=["-p:SignAssembly=true", "-p:DebugType=portable",
                                "-p:MinVerVersionOverride=" + plan["version"], "-p:SourceRevisionId=" + SOURCE])
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
    "build_evidence.delay.restore", "build_overlay", "metadata", "metadata.file_version",
    "control_metadata", "control_metadata.pdb",
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


@pytest.mark.parametrize("key", ["sha512", "content_sha512"])
@pytest.mark.parametrize("value", [None, "", "not-base64", "A" * 86 + "=", "A" * 88, ("A" * 86 + "==").encode().hex()])
def test_build_record_refuses_missing_or_malformed_package_hashes(plan, key, value):
    record = build_record_fixture(plan, False)
    d.validate_build_record(deepcopy(record), plan, False)
    if value is None:
        del record["packages"][0][key]
    else:
        record["packages"][0][key] = value
    with pytest.raises(d.Error, match="content hash missing"):
        d.validate_build_record(record, plan, False)


@pytest.mark.parametrize("change,message", [
    (lambda r: r["packages"][0].update(source="https://other.example/v3/index.json"), "source differs"),
    (lambda r: r["packages"][0].update(pin="9.9.9"), "pin observation"),
    (lambda r: r["packages"][2].update(pin="1.0.0"), "pin observation"),
    (lambda r: r["packages"][0].update(reference="direct-unpinned"), "pin observation"),
    (lambda r: r["packages"][0]["signature"].update(content_sha512=r["packages"][1]["content_sha512"]), "signature"),
    (lambda r: r["packages"][0]["signature"].update(repository_service_index="https://other.example/"), "signature"),
    (lambda r: r["packages"][0].pop("signature"), "signature"),
    (lambda r: r["restore"].update(fallback_folders=["C:/Program Files (x86)/Shared/NuGetPackages"]), "Restore folder"),
    (lambda r: r["restore"].update(package_folders=2), "Restore folder"),
    (lambda r: r["evaluated_properties"].update(AssemblyVersion="3.1.1.0"), "evaluated build observations"),
    (lambda r: r["evaluated_properties"].update(FileVersion="3.0.0.0"), "evaluated build observations"),
    (lambda r: r["evaluated_properties"].update(MinVerVersion="0.0.0-alpha.0"), "evaluated build observations"),
])
def test_build_record_refuses_unbound_package_or_version_observations(plan, change, message):
    record = build_record_fixture(plan, False)
    change(record)
    with pytest.raises(d.Error, match=message):
        d.validate_build_record(record, plan, False)


@pytest.mark.parametrize("version,assembly,file", [
    ("3.1.1-compat.1", "3.0.0.0", "3.1.1.0"), ("12.34.56", "12.0.0.0", "12.34.56.0"),
    ("0.0.0-compat.12345", "0.0.0.0", "0.0.0.0"),
])
def test_version_rule_is_minvers_production_derivation(version, assembly, file):
    # The hosted run's 0.0.0-compat.<run> version cannot tell the rules apart; these can.
    assert d.version_rule(version) == {"assembly_version": assembly, "file_version": file}


@pytest.mark.parametrize("version", ["3.1.1+build", "65535.0.0", "1.2"])
def test_version_rule_refuses_build_metadata_and_out_of_range_components(version):
    with pytest.raises(d.REFUSAL_TYPES):
        d.version_rule(version)


@pytest.mark.parametrize("field,value", [("assembly_version", "3.1.1.0"), ("file_version", "3.0.0.0"),
                                         ("file_version", None)])
def test_assembly_metadata_must_carry_the_production_version_rule(monkeypatch, tmp_path, plan, field, value):
    probe = {"name": "PPDS.Plugins", "framework": ".NETFramework,Version=v4.6.2",
             "public_key_sha256": plan["public_key_sha256"], "informational_version": plan["version"] + "+" + SOURCE,
             "assembly_version": "3.0.0.0", "file_version": "3.1.1.0"}
    monkeypatch.setattr(d, "invoke", lambda *args, **kwargs: json.dumps(probe))
    assert d.metadata(None, plan, tmp_path, "probe", "x.dll", "x.pdb")["file_version"] == "3.1.1.0"
    probe[field] = value
    with pytest.raises(d.Error, match="metadata differs"):
        d.metadata(None, plan, tmp_path, "probe", "x.dll", "x.pdb")


@pytest.mark.parametrize("bypass", ["-p:Version=3.1.1-compat.1", "-p:MinVerSkip=true", "-p:assemblyversion=3.1.1.0",
                                    "-p:InformationalVersion=x", "-p:IncludeSourceRevisionInInformationalVersion=false"])
def test_receipt_refuses_an_overlay_that_bypasses_minver(monkeypatch, tmp_path, plan, bypass):
    value, virtual, receipt_path = virtual_receipt(monkeypatch, tmp_path, plan, "prepare")
    value["build_overlay"].append(bypass)
    virtual[receipt_path] = json.dumps(value).encode()
    with pytest.raises(d.Error, match="bypass MinVer"):
        d.receipt(tmp_path, "prepare", plan, PLAN_HASH)


def test_receipt_requires_one_signature_verification_per_package_row(monkeypatch, tmp_path, plan):
    value, virtual, receipt_path = virtual_receipt(monkeypatch, tmp_path, plan, "prepare")
    value["build_evidence"]["public"]["packages"].pop()
    virtual[receipt_path] = json.dumps(value).encode()
    with pytest.raises(d.Error, match="command observations incomplete"):
        d.receipt(tmp_path, "prepare", plan, PLAN_HASH)


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


# The build environment as it was before NuGet folders were added (commit 1524e4192). Git
# source-identity children and every coordinator-launched driver process keep exactly this set.
BUILD_ENVIRONMENT = {"SYSTEMROOT", "WINDIR", "TEMP", "TMP", "ProgramFiles(x86)", "PROGRAMDATA", "ALLUSERSPROFILE",
                     "DOTNET_CLI_HOME", "NUGET_PACKAGES", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO",
                     "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_GENERATE_ASPNET_CERTIFICATE", "PATH"}
NUGET_ENVIRONMENT = BUILD_ENVIRONMENT | {"APPDATA", "LOCALAPPDATA", "DOTNET_CLI_UI_LANGUAGE"}


@pytest.mark.parametrize("operation", ["restore", "build", "pack", "msbuild"])
@pytest.mark.parametrize("nuget", [False, True])
@pytest.mark.parametrize("inherited_certificate_setting", [None, "true"])
def test_build_invocation_suppresses_certificate_generation_without_inheriting_environment(
        monkeypatch, tmp_path, plan, operation, nuget, inherited_certificate_setting):
    calls = []

    def capture(command, **options):
        calls.append((command, options))
        return SimpleNamespace(returncode=0, stdout="fixture-only", stderr="")

    synthetic_environment = {"PATH": "untrusted-parent-path", "PLUGINS_SNK_BASE64": "synthetic-secret-marker",
                             "APPDATA": "C:/Users/PRIVATE/AppData/Roaming"}
    if inherited_certificate_setting is not None:
        synthetic_environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = inherited_certificate_setting
    with monkeypatch.context() as patch:
        patch.setattr(os, "environ", synthetic_environment)
        patch.setattr(d, "pin", lambda *args: None)
        d.invoke(SimpleNamespace(run=capture), plan, "dotnet", [operation], tmp_path,
                 "fixture-" + operation, build=True, nuget=nuget)

    assert len(calls) == 1
    command, options = calls[0]
    assert command == [plan["tools"]["dotnet"]["path"], operation]
    child = options["environment"]
    assert child["DOTNET_GENERATE_ASPNET_CERTIFICATE"] == "false"
    assert child["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] == "1"
    assert "PLUGINS_SNK_BASE64" not in child
    assert "untrusted-parent-path" not in child["PATH"]
    assert "PRIVATE" not in json.dumps(child)
    assert child["PROGRAMDATA"] == child["ALLUSERSPROFILE"] == plan["windows_common_data_directory"]
    assert set(child) == (NUGET_ENVIRONMENT if nuget else BUILD_ENVIRONMENT)
    if nuget:
        # NuGet needs both folders to restore; they must stay inside the owned temporary directory.
        owned = Path(child["TEMP"])
        assert Path(child["APPDATA"]) == owned / "appdata" and Path(child["LOCALAPPDATA"]) == owned / "localappdata"


@pytest.mark.parametrize("tool", ["git", "pwsh", "sn"])
def test_nuget_folders_are_refused_for_non_dotnet_children(monkeypatch, tmp_path, plan, tool):
    monkeypatch.setattr(d, "pin", lambda *args: None)
    rt = SimpleNamespace(run=lambda *a, **kw: pytest.fail("process launched"))
    with pytest.raises(d.Error, match="only for build children"):
        d.invoke(rt, plan, tool, [], tmp_path, "metadata", build=True, nuget=True)
    with pytest.raises(d.Error, match="only for build children"):
        d.environment(plan, tmp_path, nuget=True)


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
        assert set(options["environment"]) == {"SYSTEMROOT", "WINDIR", "TEMP", "TMP", "ProgramFiles(x86)", "PROGRAMDATA", "ALLUSERSPROFILE"}
        assert options["environment"]["PROGRAMDATA"] == options["environment"]["ALLUSERSPROFILE"] == "C:/ProgramData"
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


def test_runtime_refuses_an_unlisted_operation_before_launch(monkeypatch, tmp_path):
    monkeypatch.setattr(d.subprocess, "run", lambda *a, **kw: pytest.fail("process launched"))
    rt = d.Runtime(evidence_class="process-double")
    with pytest.raises(d.Error, match="Unlisted tool operation"):
        rt.run(["C:/fixture/tool.exe"], cwd=tmp_path, environment={}, operation="C:/PRIVATE/argument")
    assert rt.calls == []


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
    assert captured.err.splitlines()[:3] == ["PPDS_COMPAT_REFUSAL=tool-incomplete", "PPDS_COMPAT_OPERATION=sign",
                                             "PPDS_COMPAT_STATUS=timeout"]


def diagnostic_lines(err):
    return [line for line in err.splitlines() if line.startswith("PPDS_COMPAT_")]


@pytest.mark.parametrize("stage,failure,expected", [
    # A tool ran and exited non-zero; its private output never appears.
    ("prepare", "tool-exit", ["PPDS_COMPAT_REFUSAL=tool-result", "PPDS_COMPAT_OPERATION=delay-restore",
                              "PPDS_COMPAT_STATUS=17"]),
    # A tool succeeded, then a validation built from a private value refused.
    ("prepare", "dynamic-reason", ["PPDS_COMPAT_REFUSAL=unregistered", "PPDS_COMPAT_OPERATION=evaluated-properties",
                                   "PPDS_COMPAT_STATUS=0"]),
    # A file read failed with a private path in the exception.
    ("sign", "missing-file", ["PPDS_COMPAT_REFUSAL=exception-filenotfounderror", "PPDS_COMPAT_OPERATION=sign",
                              "PPDS_COMPAT_STATUS=0"]),
    ("verify", "launch", ["PPDS_COMPAT_REFUSAL=tool-incomplete", "PPDS_COMPAT_OPERATION=fresh-forced-verification",
                          "PPDS_COMPAT_STATUS=launch-failure"]),
    # Refused before any tool call: no operation or status line at all.
    ("verify", "before-tools", ["PPDS_COMPAT_REFUSAL=exception-other"]),
])
def test_failure_diagnostics_name_only_fixed_operations_and_status(monkeypatch, tmp_path, plan, capsys,
                                                                    stage, failure, expected):
    monkeypatch.setattr(d.sys, "platform", "win32")
    monkeypatch.setattr(d, "load_plan", lambda *args: plan)
    monkeypatch.setattr(d, "json_write", lambda *args: None)
    private = "C:/Users/PRIVATE_USER/secret-key.snk PRIVATE_TOKEN_VALUE"

    def launch(command, **kwargs):
        if failure == "launch":
            raise OSError(private)
        return SimpleNamespace(returncode=17 if failure == "tool-exit" else 0, stdout=private, stderr=private)

    monkeypatch.setattr(d.subprocess, "run", launch)

    def fail_stage(args, selected_plan, runtime):
        operation = {"tool-exit": "delay-restore", "dynamic-reason": "evaluated-properties",
                     "missing-file": "sign", "launch": "fresh-forced-verification"}.get(failure)
        if operation is not None:
            result = runtime.run(["C:/fixture/tool.exe", private], cwd=tmp_path, environment={}, operation=operation)
            d.require(result.returncode == 0, "Candidate tool result differs from the required outcome")
        if failure == "dynamic-reason":
            raise d.Error("Property differs: " + private)
        if failure == "missing-file":
            raise FileNotFoundError(2, "No such file", private)
        raise type("PrivatePathError", (Exception,), {})(private)

    monkeypatch.setattr(d, stage, fail_stage)
    rt = d.Runtime(evidence_class="process-double")
    arguments = stage_arguments(stage) + (["--key-use-authorization", "fixture-approval-reference"] if stage == "sign" else [])
    assert d.main(arguments, runtime=rt) == 1
    captured = capsys.readouterr()
    assert diagnostic_lines(captured.err) == expected
    assert "PRIVATE" not in captured.out + captured.err


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
    events = []
    monkeypatch.setattr(d, "read", read_virtual)
    monkeypatch.setattr(d, "write", lambda path, raw: events.append(("write", Path(path))) or
                        virtual.__setitem__(Path(path), raw))
    monkeypatch.setattr(d, "json_write", lambda path, value: emitted.append((path, value)))
    monkeypatch.setattr(d, "pin", lambda *args: None)
    monkeypatch.setattr(d, "install_probe", lambda output: output / "virtual-probe.ps1")
    monkeypatch.setattr(d, "tool_versions", lambda *args: {n: v["version"] for n, v in plan["tools"].items()})
    monkeypatch.setattr(d, "source_copy", lambda *args: (args[-1] / "source", helper_fixture_hashes()))
    build_props = []
    monkeypatch.setattr(d, "build_evidence", lambda *args: build_props.append(list(args[6])) or {"fixture_only": True})

    def invoke(*args, **kwargs):
        operations.append(args[5])
        events.append(("invoke", args[5], [str(a) for a in args[3]], kwargs.get("nuget", False)))
        return ""

    monkeypatch.setattr(d, "invoke", invoke)
    monkeypatch.setattr(d, "common", lambda *args: {"evidence_class": "process-double", "cases": {}})
    monkeypatch.setattr(d, "metadata", lambda *args: observed_metadata.append(Path(args[4]).name) or
                        {"fixture_only": True})
    if control == "public":
        result = d.prepare(args, plan, d.Runtime(evidence_class="process-double"))
        assert result["cases"] == {"COMPAT-01": "PASS", "COMPAT-03": "PASS"}
        assert len(emitted) == 1 and observed_metadata == ["delay.dll", "public.dll"]
        # MinVer's own target derives the version: the override reaches restore, build, pack and
        # the evaluated-properties call (build_evidence), and no explicit version property does.
        override = "-p:MinVerVersionOverride=" + plan["version"]
        assert override in result["build_overlay"]
        dotnet = [event for event in events if event[0] == "invoke" and event[1].endswith(("-restore", "-build", "-pack"))]
        assert [event[1] for event in dotnet] == ["delay-restore", "delay-build", "delay-pack",
                                                  "public-restore", "public-build"]
        assert len(build_props) == 2
        for argv in [event[2] for event in dotnet] + build_props:
            assert override in argv and "-p:SourceRevisionId=" + SOURCE in argv
            assert not any(a.lower().startswith(d.EXPLICIT_VERSION_PROPERTIES) for a in argv)
        # Only the dotnet build children get the NuGet folders.
        assert {event[1]: event[3] for event in events if event[0] == "invoke"} == {
            "delay-restore": True, "delay-build": True, "delay-pack": True, "public-restore": True,
            "public-build": True, "delay-forced-rejection": False}
        # The owned user-level NuGet config (clears fallback folders) exists before the first restore.
        config = args.output / "temp/appdata/NuGet/NuGet.Config"
        assert virtual[config] == d.NUGET_USER_CONFIG
        assert events.index(("write", config)) < next(n for n, e in enumerate(events) if e[0] == "invoke")
    else:
        with pytest.raises((d.Error, struct.error)):
            d.prepare(args, plan, d.Runtime(evidence_class="process-double"))
        assert emitted == []
    assert "sign" not in operations


REPOSITORY_NUGET_CONFIG = (b'<?xml version="1.0" encoding="utf-8"?>\n<!-- fixture -->\n<configuration>\n'
                           b'  <packageSources>\n    <clear />\n    <add key="nuget.org" value="' + NUGET_ORG.encode()
                           + b'" protocolVersion="3" />\n  </packageSources>\n</configuration>\n')


def verify_output(package_id, version, content_hash, service_index=NUGET_ORG):
    """The shape `dotnet nuget verify --all` printed locally for an author-signed nuget.org package."""
    return "\n".join([
        "X.509 certificate chain validation will use the default trust store selected by .NET for code signing.",
        "", f"Verifying {package_id}.{version}", f"Content hash: {content_hash}",
        "C:\\virtual\\package.nupkg", "Signature Hash Algorithm: SHA256", "", "Signature type: Author",
        "Verifying the author primary signature with certificate: ", "  Subject Name: CN=Author fixture",
        "  SHA1 hash: " + "C" * 40, "  SHA256 hash: " + "D" * 64, "", "Signature type: Repository",
        "Service index: " + service_index, "Owners: fixture",
        "Verifying the repository countersignature with certificate: ", "  Subject Name: CN=NuGet.org Repository fixture",
        "  SHA1 hash: " + "A" * 40, "  SHA256 hash: " + "B" * 64, "",
        f"Successfully verified package '{package_id}.{version}'."])


class BuildEvidenceFixture:
    """Virtual restore output for build_evidence: assets, package folder, sidecars, config and pins."""

    PACKAGES = (("MinVer", "direct-pinned"), ("Microsoft.SourceLink.GitHub", "direct-pinned"),
                ("Microsoft.NETFramework.ReferenceAssemblies", "direct-sdk-implicit"),
                ("Microsoft.NETFramework.ReferenceAssemblies.net462", "transitive"))

    def __init__(self, monkeypatch, tmp_path, plan):
        self.plan, self.output, self.source, self.work = plan, tmp_path / "out", tmp_path / "source", tmp_path / "work"
        dotnet = tmp_path / "dotnet"
        plan["tools"]["dotnet"]["path"] = str(dotnet / "dotnet.exe")
        sdk = dotnet / "sdk/10.0.403"
        # The SDK reports RoslynTargetsPath with parent segments; this is the real shape.
        (sdk / "Sdks/Microsoft.NET.Sdk/targets").mkdir(parents=True)
        (sdk / "Roslyn/bincore").mkdir(parents=True)
        self.compiler = sdk / "Roslyn/bincore/csc.dll"
        self.msbuild = sdk / "MSBuild.dll"
        self.props = {"NETCoreSdkVersion": "10.0.403", "MSBuildVersion": "18.0.1",
                      "MSBuildBinPath": str(sdk), "RoslynTargetsPath": str(sdk / "Sdks/Microsoft.NET.Sdk/targets")
                      + os.sep + os.path.join("..", "..", "..", "Roslyn"),
                      "CscToolPath": "", "CscToolExe": "", "TargetFramework": "net462", "Configuration": "Release",
                      "SignAssembly": "true", "PublicSign": "false", "DelaySign": "true",
                      "AssemblyOriginatorKeyFile": str(self.output / "test-public.bin"),
                      "AssemblyVersion": "3.0.0.0", "FileVersion": "3.1.1.0", "MinVerVersion": plan["version"],
                      "InformationalVersion": plan["version"] + "+" + SOURCE, "PackageVersion": plan["version"]}
        self.assets = {"libraries": {}, "packageFolders": {str(self.output / "temp/packages"): {}},
                       "project": {"restore": {"sources": {NUGET_ORG: {}}},
                                   "frameworks": {"net462": {"dependencies": {}}}}}
        self.virtual = {self.source / "NuGet.config": REPOSITORY_NUGET_CONFIG,
                        self.source / "Directory.Packages.props": (
                            b'<Project><ItemGroup><PackageVersion Include="MinVer" Version="1.0.0" />'
                            b'<PackageVersion Include="Microsoft.SourceLink.GitHub" Version="1.0.0" />'
                            b'<PackageVersion Include="Unrelated.Package" Version="9.0.0" /></ItemGroup></Project>')}
        self.verified = {}
        dependencies = self.assets["project"]["frameworks"]["net462"]["dependencies"]
        for name, reference in self.PACKAGES:
            raw = ("synthetic resolved package " + name).encode()
            version = "1.0.0"
            relative = name.lower() + "/" + version
            # As with every repository-signed nuget.org package, the content hash the assets file
            # records differs from the hash of the package file. NuGet writes both beside it.
            file_hash = base64.b64encode(hashlib.sha512(raw).digest()).decode()
            content_hash = base64.b64encode(hashlib.sha512(b"content without signature " + raw).digest()).decode()
            assert file_hash != content_hash
            self.assets["libraries"][name + "/" + version] = {"type": "package", "path": relative, "sha512": content_hash}
            if reference == "direct-pinned":
                dependencies[name] = {"target": "Package", "version": "[1.0.0, )", "versionCentrallyManaged": True}
            elif reference == "direct-sdk-implicit":
                dependencies[name] = {"target": "Package", "version": "[1.0.0, )", "autoReferenced": True}
            folder = self.output / "temp/packages" / relative
            archive = name.lower() + "." + version + ".nupkg"
            self.virtual[folder / archive] = raw
            self.virtual[folder / ".nupkg.metadata"] = json.dumps(
                {"version": 2, "contentHash": content_hash, "source": NUGET_ORG}).encode()
            self.virtual[folder / (archive + ".sha512")] = file_hash.encode()
            self.verified[str(folder / archive)] = verify_output(name, version, content_hash)
        self.calls, self.recorded = [], []
        monkeypatch.setattr(d, "read", self.read)
        monkeypatch.setattr(d, "record", lambda path: self.recorded.append(Path(path)) or {"sha256": "f" * 64, "bytes": 123})
        monkeypatch.setattr(d, "invoke", self.invoke)

    def read(self, path, limit=d.h.MAX_PACKAGE):
        if Path(path) == self.work / "obj/project.assets.json":
            return json.dumps(self.assets).encode()
        return self.virtual[Path(path)]

    def invoke(self, *args, **kwargs):
        operation, argv = args[5], [str(a) for a in args[3]]
        self.calls.append((operation, argv, kwargs.get("build", False), kwargs.get("nuget", False)))
        if operation == "evaluated-properties":
            return json.dumps({"Properties": self.props})
        if operation == "selected-sdk":
            return "10.0.403"
        if operation == "package-signature-verification":
            assert argv[:3] == ["nuget", "verify", "--all"]
            return self.verified[argv[3]]
        assert operation == "compiler-product-version"
        return "1.2.3.4"

    def library(self, name):
        return self.assets["libraries"][name + "/1.0.0"]

    def folder(self, name):
        return self.output / "temp/packages" / (name.lower() + "/1.0.0")

    def __call__(self):
        return d.build_evidence(d.Runtime(evidence_class="process-double"), self.plan, self.output, self.source,
                                self.source / "plugins.csproj", self.work, ["-p:MinVerVersionOverride=fixture"],
                                self.output / "probe.ps1", False)


def test_build_evidence_checks_actual_properties_and_package_bindings(monkeypatch, tmp_path, plan):
    f = BuildEvidenceFixture(monkeypatch, tmp_path, plan)
    result = f()
    assert result["sdk"] == "10.0.403" and len(result["packages"]) == 4
    assert result["restore"] == {"fallback_folders": [], "package_folders": 1, "source": NUGET_ORG, "sources": [NUGET_ORG]}
    rows = {row["id"]: row for row in result["packages"]}
    for name, reference in f.PACKAGES:
        row, library = rows[name], f.library(name)
        assert row["content_sha512"] == library["sha512"] and row["sha512"] != library["sha512"]
        assert row["source"] == NUGET_ORG and row["reference"] == reference
        assert row["pin"] == ("1.0.0" if reference == "direct-pinned" else None)
        assert row["signature"] == {"content_sha512": library["sha512"], "repository_service_index": NUGET_ORG,
                                    "repository_certificate_sha256": "b" * 64}
    d.validate_build_record({**build_record_fixture(plan, False), "packages": result["packages"],
                             "restore": result["restore"]}, plan, False)
    # The recorded compiler is the collapsed real path, and the probe reads that same path.
    assert result["binaries"]["roslyn"]["path"] == str(f.compiler)
    assert result["binaries"]["msbuild"]["path"] == str(f.msbuild)
    probed = [argv[argv.index("-Tool") + 1] for operation, argv, *_ in f.calls if operation == "compiler-product-version"]
    assert probed == [str(f.msbuild), str(f.compiler)] and f.recorded == [f.msbuild, f.compiler]
    # The version properties are read after MinVer's target runs, not from evaluation alone.
    evaluated = next(argv for operation, argv, *_ in f.calls if operation == "evaluated-properties")
    assert "-t:GetAssemblyAttributes" in evaluated and "-p:MinVerVersionOverride=fixture" in evaluated
    # Exactly the msbuild and package-verification children get the NuGet folders.
    flags = {operation: (build, nuget) for operation, _, build, nuget in f.calls}
    assert flags == {"evaluated-properties": (True, True), "selected-sdk": (True, False),
                     "compiler-product-version": (False, False), "package-signature-verification": (True, True)}
    assert sum(operation == "package-signature-verification" for operation, *_ in f.calls) == 4


def _set_library_hash(f, value, sidecar=None):
    f.library("MinVer")["sha512"] = value
    metadata = f.folder("MinVer") / ".nupkg.metadata"
    f.virtual[metadata] = json.dumps({"version": 2, "source": NUGET_ORG, **(
        {} if sidecar is None else {"contentHash": sidecar})}).encode()


@pytest.mark.parametrize("change,message", [
    # None == None, or an empty value, must never pass as a content-hash match (S6).
    ("null-hashes", "content hash differs"), ("empty-hashes", "content hash differs"),
    ("assets-hash", "content hash differs"), ("metadata-source", "package source differs"),
    ("metadata-no-source", "package source differs"), ("file-sidecar", "file hash differs"),
    ("verified-content", "Verified package content hash differs"),
    ("verified-index", "repository signature differs"), ("verified-unsigned", "repository signature differs"),
    ("verified-other-package", "verification incomplete"), ("pin", "central pin"), ("range", "central pin"),
    ("unpinned", "Unpinned direct"), ("fallback", "fallback folder"), ("package-folders", "package folder"),
    ("two-sources", "Repository package source"), ("public-sign", "properties differ"),
    ("configuration", "properties differ"), ("assembly-version", "properties differ"),
    ("file-version", "properties differ"), ("minver-skipped", "properties differ"),
    ("compiler-root", "outside the pinned dotnet root"),
])
def test_build_evidence_refuses_unbound_packages_restore_or_properties(monkeypatch, tmp_path, plan, change, message):
    f = BuildEvidenceFixture(monkeypatch, tmp_path, plan)
    f()
    archive = str(f.folder("MinVer") / "minver.1.0.0.nupkg")
    content = f.library("MinVer")["sha512"]
    if change == "null-hashes":
        _set_library_hash(f, None)
    elif change == "empty-hashes":
        _set_library_hash(f, "", "")
    elif change == "assets-hash":
        f.library("MinVer")["sha512"] = base64.b64encode(hashlib.sha512(b"other").digest()).decode()
    elif change in ("metadata-source", "metadata-no-source"):
        row = {"version": 2, "contentHash": content}
        if change == "metadata-source":
            row["source"] = "C:\\Program Files\\dotnet\\library-packs"
        f.virtual[f.folder("MinVer") / ".nupkg.metadata"] = json.dumps(row).encode()
    elif change == "file-sidecar":
        # A package file that is not the one NuGet recorded downloading is refused.
        f.virtual[f.folder("MinVer") / "minver.1.0.0.nupkg.sha512"] = base64.b64encode(hashlib.sha512(b"x").digest())
    elif change == "verified-content":
        # The archive's own signed content hash must equal the resolved graph's hash.
        f.verified[archive] = verify_output("MinVer", "1.0.0", base64.b64encode(hashlib.sha512(b"y").digest()).decode())
    elif change == "verified-index":
        f.verified[archive] = verify_output("MinVer", "1.0.0", content, "https://other.example/v3/index.json")
    elif change == "verified-unsigned":
        f.verified[archive] = "\n".join(line for line in verify_output("MinVer", "1.0.0", content).splitlines()
                                        if "Repository" not in line and "Service index" not in line)
    elif change == "verified-other-package":
        f.verified[archive] = verify_output("MinVer", "1.0.1", content)
    elif change == "pin":
        f.virtual[f.source / "Directory.Packages.props"] = b'<Project><ItemGroup><PackageVersion Include="MinVer" Version="2.0.0" /></ItemGroup></Project>'
    elif change == "range":
        f.assets["project"]["frameworks"]["net462"]["dependencies"]["MinVer"]["version"] = "[0.9.0, )"
    elif change == "unpinned":
        f.assets["project"]["frameworks"]["net462"]["dependencies"]["MinVer"].pop("versionCentrallyManaged")
    elif change == "fallback":
        f.assets["project"]["restore"]["fallbackFolders"] = ["C:\\Program Files (x86)\\Microsoft Visual Studio\\Shared\\NuGetPackages"]
    elif change == "package-folders":
        f.assets["packageFolders"]["C:\\Program Files (x86)\\Microsoft Visual Studio\\Shared\\NuGetPackages"] = {}
    elif change == "two-sources":
        f.virtual[f.source / "NuGet.config"] = REPOSITORY_NUGET_CONFIG.replace(
            b"  </packageSources>", b'    <add key="other" value="https://other.example/v3/index.json" />\n  </packageSources>')
    elif change == "public-sign":
        f.props["PublicSign"] = "true"
    elif change == "configuration":
        f.props["Configuration"] = "Debug"
    elif change == "assembly-version":
        f.props["AssemblyVersion"] = "3.1.1.0"  # The pre-fix overlay rule; production gives 3.0.0.0.
    elif change == "file-version":
        f.props["FileVersion"] = "3.0.0.0"
    elif change == "minver-skipped":
        f.props["MinVerVersion"] = ""
    elif change == "compiler-root":
        f.plan["tools"]["dotnet"]["path"] = str(tmp_path / "other-dotnet/dotnet.exe")
    with pytest.raises(d.Error, match=message):
        f()


def test_canonical_tool_path_collapses_the_parent_segments_the_sdk_reports(tmp_path):
    # MSBuild reports RoslynTargetsPath as <sdk>/Sdks/Microsoft.NET.Sdk/targets/../../../Roslyn.
    targets = tmp_path / "sdk/Sdks/Microsoft.NET.Sdk/targets"
    targets.mkdir(parents=True)
    compiler = tmp_path / "sdk/Roslyn/bincore/csc.dll"
    compiler.parent.mkdir(parents=True)
    compiler.write_bytes(b"synthetic compiler")
    reported = targets / ".." / ".." / ".." / "Roslyn" / "bincore" / "csc.dll"
    assert ".." in reported.parts
    collapsed = d.canonical_tool_path(reported, tmp_path)
    assert collapsed == compiler and ".." not in collapsed.parts
    # A path with no parent segments is returned unchanged.
    assert d.canonical_tool_path(compiler, tmp_path / "sdk") == compiler
    # Both recorded binaries must sit under the pinned dotnet root.
    with pytest.raises(d.Error, match="outside the pinned dotnet root"):
        d.canonical_tool_path(reported, tmp_path / "sdk/Sdks")
    with pytest.raises(d.Error, match="outside the pinned dotnet root"):
        d.canonical_tool_path(tmp_path / "sdk" / ".." / "elsewhere" / "csc.dll", tmp_path / "sdk")


@pytest.mark.skipif(sys.platform == "win32",
                    reason="Windows resolves parent segments lexically before following links, so the collapsed form is exact there")
def test_canonical_tool_path_refuses_a_link_among_the_collapsed_segments(tmp_path):
    # Where the OS follows a link before applying "..", the lexical result can name a different
    # file than the one the reported path reaches. That must be refused, not silently recorded.
    real = tmp_path / "elsewhere/Sdks/inner"
    real.mkdir(parents=True)
    (tmp_path / "elsewhere/Roslyn").mkdir()
    (tmp_path / "elsewhere/Roslyn/csc.dll").write_bytes(b"reached through the link")
    (tmp_path / "sdk").mkdir()
    link = tmp_path / "sdk/targets"
    try:
        os.symlink(real, link, target_is_directory=True)
    except (OSError, NotImplementedError):
        pytest.skip("Directory links are not available to this account")
    # A decoy at the lexically collapsed location: recording it would name a file the OS never opens.
    (tmp_path / "Roslyn").mkdir()
    (tmp_path / "Roslyn/csc.dll").write_bytes(b"decoy at the collapsed location")
    with pytest.raises(d.Error, match="canonical form"):
        d.canonical_tool_path(link / ".." / ".." / "Roslyn" / "csc.dll", tmp_path)


class GitDouble(d.Runtime):
    """Answers the source-inspection git calls from a table; launches nothing."""
    def __init__(self, answers):
        super().__init__(evidence_class="process-double")
        self.answers = answers

    def run(self, command, **kwargs):
        assert kwargs["operation"] == "source-identity"
        # Git children keep the build environment they had before NuGet folders were added.
        assert set(kwargs["environment"]) == BUILD_ENVIRONMENT
        return SimpleNamespace(returncode=0, stdout=self.answers[tuple(command[3:])], stderr="")


def blob_id(raw):
    return hashlib.sha1(b"blob %d\0" % len(raw) + raw).hexdigest()


def tracked_tree(tmp_path, monkeypatch, plan, files, index=None):
    tree = tmp_path / "tree"
    for name, raw in files.items():
        target = tree / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(raw)
    monkeypatch.setattr(d, "ROOT", tree)
    monkeypatch.setattr(d, "HELPERS", ("helper.py",))
    monkeypatch.setattr(d, "pin", lambda *args: None)
    output = tmp_path / "output"
    output.mkdir()
    index = index or {name: blob_id(raw) for name, raw in files.items()}
    listing = "".join(f"100644 {index[name]} 0\t{name}\0" for name in files)
    answers = {("rev-parse", "HEAD"): plan["source"] + "\n", ("rev-parse", "HEAD^{tree}"): plan["tree"] + "\n",
               ("status", "--porcelain"): "", ("ls-files", "-s", "-z"): listing}
    return tree, output, GitDouble(answers)


def test_source_copy_admits_empty_tracked_files(tmp_path, monkeypatch, plan):
    # The real repository tracks zero-byte files (.gitkeep, package __init__.py). A reader
    # that requires a non-empty handoff file refused them, so PREPARE could never pass.
    files = {"helper.py": b"print('helper')\n", "pkg/__init__.py": b"", "keep/.gitkeep": b""}
    assert blob_id(b"") == d.EMPTY_BLOB
    tree, output, rt = tracked_tree(tmp_path, monkeypatch, plan, files)
    copied, inputs = d.source_copy(rt, plan, tree, output)
    assert copied == output / "source"
    for name, raw in files.items():
        assert (copied / name).read_bytes() == raw
        assert inputs[name] == d.digest(raw)
    assert set(inputs) == set(files)


def test_source_copy_refuses_a_tracked_non_empty_file_truncated_to_zero(tmp_path, monkeypatch, plan):
    # The index still records the original non-empty blob (a truncation hidden from status by
    # assume-unchanged/skip-worktree); the worktree file is empty. It must not be copied as empty.
    files = {"helper.py": b"print('helper')\n", "settings.json": b""}
    index = {"helper.py": blob_id(files["helper.py"]), "settings.json": blob_id(b'{"tracked": true}\n')}
    tree, output, rt = tracked_tree(tmp_path, monkeypatch, plan, files, index)
    with pytest.raises(d.Error, match="not an empty tracked blob"):
        d.source_copy(rt, plan, tree, output)


@pytest.mark.parametrize("entry", ["120000 " + "a" * 40 + " 0\tlink", "160000 " + "a" * 40 + " 0\tsubmodule",
                                   "100644 " + "a" * 40 + " 2\tconflicted", "100644 short 0\tname"])
def test_source_copy_refuses_links_submodules_conflicts_and_malformed_index_entries(tmp_path, monkeypatch, plan, entry):
    tree, output, rt = tracked_tree(tmp_path, monkeypatch, plan, {"helper.py": b"x"})
    rt.answers[("ls-files", "-s", "-z")] += entry + "\0"
    with pytest.raises(d.Error, match="Unexpected source index entry"):
        d.source_copy(rt, plan, tree, output)


def hard_link_or_skip(source, target):
    try:
        os.link(source, target)
    except (OSError, NotImplementedError, AttributeError):
        pytest.skip("Hard links are not available on this filesystem")


def test_read_source_keeps_every_other_regular_file_rule_for_empty_files(tmp_path):
    empty = tmp_path / "empty.txt"
    empty.write_bytes(b"")
    assert d.read_source(empty, d.EMPTY_BLOB) == b""
    with pytest.raises(d.Error, match="not an empty tracked blob"):
        d.read_source(empty, blob_id(b"originally non-empty"))
    directory = tmp_path / "directory"
    directory.mkdir()
    with pytest.raises(d.Error):
        d.read_source(directory, d.EMPTY_BLOB)
    hard_link_or_skip(empty, tmp_path / "second-name.txt")
    with pytest.raises(d.Error, match="Regular tracked source file required"):
        d.read_source(empty, d.EMPTY_BLOB)


def test_read_source_still_applies_the_handoff_rule_to_non_empty_files(tmp_path):
    present = tmp_path / "present.txt"
    present.write_bytes(b"content")
    assert d.read_source(present, blob_id(b"content")) == b"content"
    hard_link_or_skip(present, tmp_path / "second-name.txt")
    with pytest.raises(d.Error, match="Bounded regular handoff file required"):
        d.read_source(present, blob_id(b"content"))


@pytest.mark.parametrize("reader", ["read", "record"])
def test_handoff_reads_still_refuse_an_empty_file(tmp_path, reader):
    # Only the tracked-source copy admits empty files; handoff, receipt and package reads do not.
    empty = tmp_path / "empty-handoff.json"
    empty.write_bytes(b"")
    with pytest.raises(d.Error, match="Bounded regular handoff file required"):
        getattr(d, reader)(empty)
