"""Offline metadata/stream/archive/container controls; never launches an artifact."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import sys
import zipfile

import pytest

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/ci"))
try:
    spec = importlib.util.spec_from_file_location("native_distribution", ROOT / "scripts/ci/verify_native_distribution.py")
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
finally:
    sys.path.pop(0)
SOURCE = "a" * 40


def metadata():
    return (f'[assembly: System.Reflection.AssemblyInformationalVersionAttribute("1.5.0-alpha.0.1+{SOURCE}")]',
            {"runtimeOptions": {"tfm": "net10.0", "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.14"}]}},
            {"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/linux-x64"},
             "libraries": {"runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.14": {}}})


def test_actual_candidate_identity_keeps_prerelease_and_source():
    value = gate.compiled_identity(*metadata(), SOURCE, "linux-x64")
    assert value["candidateOnly"] and value["releaseVersion"] == "1.5.0-alpha.0.1"
    assert value["informationalVersion"].endswith("+" + SOURCE)
    assert gate.bundle_manifest(value)["informationalVersion"] == value["informationalVersion"]


@pytest.mark.parametrize("runtime_version", ["10.0.0", "10.0.13"])
def test_consistent_unpatched_runtime_pack_is_rejected(runtime_version):
    assembly, config, deps = copy.deepcopy(metadata())
    config["runtimeOptions"]["includedFrameworks"][0]["version"] = runtime_version
    deps["libraries"] = {f"runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/{runtime_version}": {}}
    with pytest.raises(ValueError, match="10.0.14 security baseline"):
        gate.compiled_identity(assembly, config, deps, SOURCE, "linux-x64")


@pytest.mark.parametrize("runtime_version", ["10.0.14", "10.0.15", "10.0.100"])
def test_consistent_patched_10_0_runtime_pack_is_accepted(runtime_version):
    assembly, config, deps = copy.deepcopy(metadata())
    config["runtimeOptions"]["includedFrameworks"][0]["version"] = runtime_version
    deps["libraries"] = {f"runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/{runtime_version}": {}}
    value = gate.compiled_identity(assembly, config, deps, SOURCE, "linux-x64")
    assert value["runtimePackVersion"] == runtime_version


@pytest.mark.parametrize("runtime_version", ["10.0.14-preview.1", "10.1.14", "11.0.14"])
def test_runtime_security_floor_does_not_expand_stable_10_0_band(runtime_version):
    assembly, config, deps = copy.deepcopy(metadata())
    config["runtimeOptions"]["includedFrameworks"][0]["version"] = runtime_version
    deps["libraries"] = {f"runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/{runtime_version}": {}}
    with pytest.raises(ValueError, match="Unexpected runtime pack version"):
        gate.compiled_identity(assembly, config, deps, SOURCE, "linux-x64")


@pytest.mark.parametrize("mutation", ["source", "tfm", "pack", "rid", "metadata"])
def test_wrong_runtime_or_source_is_rejected(mutation):
    assembly, config, deps = copy.deepcopy(metadata())
    if mutation == "source": assembly = assembly.replace(SOURCE, "b" * 40)
    if mutation == "tfm": config["runtimeOptions"]["tfm"] = "net8.0"
    if mutation == "pack": deps["libraries"] = {}
    if mutation == "rid": deps["runtimeTarget"]["name"] = ".NETCoreApp,Version=v10.0/osx-arm64"
    if mutation == "metadata": assembly += assembly
    with pytest.raises(ValueError):
        gate.compiled_identity(assembly, config, deps, SOURCE, "linux-x64")


def test_private_environment_drops_credentials_profiles_and_opt_ins(monkeypatch, tmp_path):
    for name in ["GH_TOKEN", "VSCE_PAT", "AZURE_CLIENT_SECRET", "PPDS_CLIENT_SECRET", "PPDS_PROFILE", "PPDS_FORMS_LIVE_CONFIRM"]:
        monkeypatch.setenv(name, "synthetic-not-a-credential")
    env = gate.private_environment(tmp_path / "private")
    assert not any(name in env for name in ["GH_TOKEN", "VSCE_PAT", "AZURE_CLIENT_SECRET", "PPDS_CLIENT_SECRET", "PPDS_PROFILE", "PPDS_FORMS_LIVE_CONFIRM"])
    assert Path(env["PPDS_CONFIG_DIR"]).parent == tmp_path / "private"
    assert not list(Path(env["PPDS_CONFIG_DIR"]).iterdir())


@pytest.mark.parametrize("wire", [b"", b"Content-Length: 999999\r\n\r\n", b"Content-Length: 2\r\n\r\n{", b"Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}"])
def test_invalid_or_truncated_frame_is_rejected(wire):
    with pytest.raises((ValueError, json.JSONDecodeError)):
        gate.read_frame(io.BytesIO(wire))


def heartbeat():
    return {"jsonrpc": "2.0", "id": 1, "result": {"ok": True, "heartbeatCount": 1, "uptimeSeconds": 0,
            "correlationId": "synthetic-correlation", "startedAt": "2026-11-09T00:00:00Z", "daemonVersion": "1.5.0.0"}}


def test_valid_framed_heartbeat_and_negative_response_controls():
    payload = json.dumps(heartbeat()).encode()
    wire = f"Content-Length: {len(payload)}\r\n\r\n".encode() + payload
    assert gate.validate_heartbeat(gate.read_frame(io.BytesIO(wire)))["heartbeatCount"] == 1
    for key, value in [("ok", False), ("heartbeatCount", True), ("uptimeSeconds", -1), ("correlationId", "")]:
        bad = heartbeat()
        bad["result"][key] = value
        with pytest.raises(ValueError): gate.validate_heartbeat(bad)
    bad = heartbeat(); bad["id"] = 2
    with pytest.raises(ValueError): gate.validate_heartbeat(bad)


def test_container_command_is_offline_readonly_and_uses_synthetic_identity(tmp_path):
    binary, fixture, script = tmp_path / "ppds", tmp_path / "fixture", tmp_path / "check.sh"
    binary.write_bytes(b"not-executed"); fixture.mkdir(); script.write_text("not-executed")
    command = gate.docker_command(binary, fixture, script)
    for required in ["--network=none", "--read-only", "--user=10001:10001", "--cap-drop=ALL", "--security-opt=no-new-privileges", "--pull=never"]:
        assert required in command
    assert sum("readonly" in item for item in command) == 3
    assert not any("docker.sock" in item or "--privileged" in item for item in command)


def test_container_output_must_contain_actual_fixture_registration_and_image():
    step = {"entity": "account", "message": "Update", "stage": "PreOperation", "mode": "Synchronous",
            "executionOrder": 7, "filteringAttributes": "name,telephone1",
            "images": [{"name": "Before", "imageType": "PreImage", "attributes": "name,telephone1"}]}
    value = {"success": True, "data": {"assemblies": [{"name": "SingleFilePlugin", "type": "Assembly",
            "allTypeNames": ["SingleFileFixture.AccountPlugin"], "types": [{"steps": [step]}]}]}}
    gate.validate_extraction(value)
    for key in ["entity", "executionOrder", "images"]:
        bad = copy.deepcopy(value)
        del bad["data"]["assemblies"][0]["types"][0]["steps"][0][key]
        with pytest.raises(ValueError): gate.validate_extraction(bad)


@pytest.mark.parametrize("mutation", [None, "binary", "source", "target", "xml"])
def test_actual_vsix_bytes_and_identity_are_verified_without_execution(tmp_path, monkeypatch, mutation):
    package = {"name": "fixture", "publisher": "fixture-publisher", "version": "1.8.0"}
    extension = tmp_path / "src/PPDS.Extension"; extension.mkdir(parents=True)
    (extension / "package.json").write_text(json.dumps(package)); monkeypatch.setattr(gate, "ROOT", tmp_path)
    value = gate.compiled_identity(*metadata(), SOURCE, "linux-x64")
    binary = tmp_path / "ppds"; binary.write_bytes(b"not-executed")
    manifest = gate.bundle_manifest(value)
    if mutation == "source": manifest["informationalVersion"] = "1.5.0-alpha.0.1+" + "b" * 40
    archive = tmp_path / "candidate.vsix"
    with zipfile.ZipFile(archive, "w") as output:
        output.writestr("extension/package.json", json.dumps(package))
        xml_version = "9.9.9" if mutation == "xml" else package["version"]
        output.writestr("extension.vsixmanifest", f'<PackageManifest><Metadata><Identity Id="fixture" Publisher="fixture-publisher" Version="{xml_version}" TargetPlatform="linux-x64"/></Metadata></PackageManifest>')
        output.writestr("extension/bin/ppds.version.json", json.dumps(manifest))
        output.writestr("extension/bin/ppds", b"different" if mutation == "binary" else binary.read_bytes())
    if mutation:
        with pytest.raises(ValueError):
            gate.verify_vsix(archive, "darwin-x64" if mutation == "target" else "linux-x64", value, binary, tmp_path / "extracted")
    else:
        assert gate.verify_vsix(archive, "linux-x64", value, binary, tmp_path / "extracted").read_bytes() == binary.read_bytes()


def compiled_metadata_fixture(tmp_path, monkeypatch):
    assembly, config, deps = metadata()
    value = gate.compiled_identity(assembly, config, deps, SOURCE, "linux-x64")
    root = tmp_path / "source"
    directory = root / "src/PPDS.Cli/bin/Release/net10.0/linux-x64"
    directory.mkdir(parents=True)
    blobs = {"ppds.deps.json": (json.dumps(deps, indent=2) + "\n").encode(),
             "ppds.runtimeconfig.json": (json.dumps(config, indent=4) + "\n").encode()}
    value["buildInputSha256"] = {}
    for name, data in blobs.items():
        source = directory / name
        source.write_bytes(data)
        value["buildInputSha256"][str(source.relative_to(root))] = gate.digest(source)
    monkeypatch.setattr(gate, "ROOT", root)
    evidence = tmp_path / "evidence"
    evidence.mkdir()
    return value, directory, evidence, blobs


def test_compiled_metadata_evidence_retains_exact_bytes_and_bound_hashes(tmp_path, monkeypatch):
    value, directory, evidence, blobs = compiled_metadata_fixture(tmp_path, monkeypatch)
    retained = gate.retain_compiled_metadata(value, evidence)
    assert set(retained) == set(blobs)
    for name, data in blobs.items():
        assert (evidence / name).read_bytes() == data == (directory / name).read_bytes()
        assert retained[name] == {"fileName": name, "sourcePath": str((directory / name).relative_to(gate.ROOT)),
                                  "sha256": value["buildInputSha256"][retained[name]["sourcePath"]]}
        assert gate.digest(evidence / name) == retained[name]["sha256"]


@pytest.mark.parametrize("name", ["ppds.deps.json", "ppds.runtimeconfig.json"])
def test_compiled_metadata_preimage_hash_mismatch_refuses_evidence(tmp_path, monkeypatch, name):
    value, directory, evidence, blobs = compiled_metadata_fixture(tmp_path, monkeypatch)
    (directory / name).write_bytes(blobs[name] + b"\n")
    with pytest.raises(ValueError, match="Compiled metadata changed before retention"):
        gate.retain_compiled_metadata(value, evidence)
    assert not list(evidence.iterdir())
    assert (directory / name).read_bytes() == blobs[name] + b"\n"


@pytest.mark.parametrize("name", ["ppds.deps.json", "ppds.runtimeconfig.json"])
def test_compiled_metadata_existing_evidence_is_refused_without_clobber(tmp_path, monkeypatch, name):
    value, directory, evidence, blobs = compiled_metadata_fixture(tmp_path, monkeypatch)
    sentinel = b"synthetic-existing-evidence-do-not-clobber"
    (evidence / name).write_bytes(sentinel)
    with pytest.raises(ValueError, match="Refuse existing compiled metadata evidence"):
        gate.retain_compiled_metadata(value, evidence)
    assert (evidence / name).read_bytes() == sentinel
    assert [p.name for p in evidence.iterdir()] == [name]
    assert all((directory / key).read_bytes() == data for key, data in blobs.items())
