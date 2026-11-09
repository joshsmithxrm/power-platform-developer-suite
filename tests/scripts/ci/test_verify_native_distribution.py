"""Offline metadata/stream/archive/container controls; never launches an artifact."""
import copy
import importlib.util
import io
import json
from pathlib import Path, PureWindowsPath
import stat
import sys
from types import SimpleNamespace
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
    monkeypatch.setattr(gate.sys, "platform", "linux")
    for name in ["GH_TOKEN", "VSCE_PAT", "AZURE_CLIENT_SECRET", "PPDS_CLIENT_SECRET", "PPDS_PROFILE", "PPDS_FORMS_LIVE_CONFIRM"]:
        monkeypatch.setenv(name, "synthetic-not-a-credential")
    env = gate.private_environment(tmp_path / "private")
    assert not any(name in env for name in ["GH_TOKEN", "VSCE_PAT", "AZURE_CLIENT_SECRET", "PPDS_CLIENT_SECRET", "PPDS_PROFILE", "PPDS_FORMS_LIVE_CONFIRM"])
    assert Path(env["PPDS_CONFIG_DIR"]).parent == tmp_path / "private"
    assert not list(Path(env["PPDS_CONFIG_DIR"]).iterdir())


class WindowsDirectoryFixture:
    """Metadata-only synthetic Windows filesystem; never reads a real profile."""
    def __init__(self):
        self.nodes = {}
        self.created = []
        self.reader_requests = []
        self.saved = []
        self.forced_security = {}
        self.response_mutation = None
        for value in ["C:/", "C:/Users", "C:/Users/runneradmin", "C:/Users/runneradmin/AppData",
                      "C:/Users/runneradmin/AppData/Local", "D:/", "D:/a", "D:/a/_temp", "D:/a/_temp/evidence"]:
            self.nodes[str(PureWindowsPath(value))] = {"reparse": False, "symlink": False, "redirect": None}

    def path(self, value):
        fixture = self

        class Directory:
            def __init__(self, name): self.value = PureWindowsPath(name)
            def __str__(self): return str(self.value)
            def __eq__(self, other): return str(self) == str(other)
            def __truediv__(self, other): return fixture.path(self.value / other)
            @property
            def parent(self): return fixture.path(self.value.parent)
            @property
            def parents(self): return tuple(fixture.path(p) for p in self.value.parents)
            def exists(self): return str(self) in fixture.nodes
            def is_symlink(self): return fixture.nodes.get(str(self), {}).get("symlink", False)
            def is_relative_to(self, other): return self.value.is_relative_to(PureWindowsPath(str(other)))
            def lstat(self):
                if str(self) not in fixture.nodes: raise FileNotFoundError(str(self))
                return SimpleNamespace(st_mode=stat.S_IFDIR | 0o700,
                                       st_file_attributes=0x400 if fixture.nodes[str(self)]["reparse"] else 0)
            def resolve(self, strict=False):
                if strict and not self.exists(): raise FileNotFoundError(str(self))
                redirect = fixture.nodes.get(str(self), {}).get("redirect")
                return fixture.path(redirect) if redirect else self
            def mkdir(self, mode=0o777):
                if self.exists(): raise FileExistsError(str(self))
                if not self.parent.exists(): raise FileNotFoundError(str(self.parent))
                fixture.nodes[str(self)] = {"reparse": False, "symlink": False, "redirect": None}
                fixture.created.append((str(self), mode))
                fixture.nodes[str(self)]["security"] = synthetic_descriptor(owner_rights=mode == 0o700)

        return Directory(value)


    def read_security(self, paths):
        names = [str(p) for p in paths]
        self.reader_requests.append(names)
        rows = []
        for name in names:
            value = copy.deepcopy(self.forced_security.get(name,
                self.nodes[name].get("security", synthetic_descriptor())))
            rows.append(dict(value, path=name))
        reply = {"complete": True, "currentUserSid": SYNTHETIC_USER, "requestedCount": len(names),
                 "observedCount": len(rows), "rows": rows}
        if self.response_mutation == "partial": reply["complete"] = False
        if self.response_mutation == "count": reply["observedCount"] -= 1
        if self.response_mutation == "boolean-count": reply["requestedCount"] = True
        if self.response_mutation == "duplicate": reply["rows"][-1] = copy.deepcopy(reply["rows"][0])
        if self.response_mutation == "foreign": reply["rows"][0]["path"] = "C:/OtherProfile"
        if self.response_mutation == "missing-sid": reply["currentUserSid"] = None
        if self.response_mutation == "boolean-sid": reply["currentUserSid"] = True
        if self.response_mutation == "empty": reply["rows"] = []
        return reply

    def record(self, path, value):
        self.saved.append((str(path), copy.deepcopy(value)))


SYNTHETIC_USER = "S-1-5-21-1-2-3-1001"


def synthetic_descriptor(*, owner_rights=False):
    sid = "S-1-3-4" if owner_rights else SYNTHETIC_USER
    return {"ownerSid": SYNTHETIC_USER, "daclPresent": True, "daclNull": False,
            "descriptorBytes": 256, "controlFlags": 4,
            "aces": [{"type": 0, "qualifier": 0, "flags": 3, "mask": 0x1f01ff,
                      "sid": sid, "callback": False}]}


def windows_environment_fixture(monkeypatch):
    fixture = WindowsDirectoryFixture()
    monkeypatch.setattr(gate.sys, "platform", "win32")
    monkeypatch.setattr(gate, "Path", fixture.path)
    monkeypatch.setattr(gate, "uuid", SimpleNamespace(uuid4=lambda: SimpleNamespace(hex="a" * 32)), raising=False)
    monkeypatch.setattr(gate, "read_windows_security", fixture.read_security, raising=False)
    monkeypatch.setattr(gate, "save", fixture.record)
    for name, value in {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted", "SYSTEMDRIVE": "C:",
                        "USERPROFILE": "C:/Users/runneradmin", "LOCALAPPDATA": "C:/Users/runneradmin/AppData/Local",
                        "RUNNER_TEMP": "D:/a/_temp"}.items():
        monkeypatch.setenv(name, value)
    return fixture, fixture.path("D:/a/_temp/evidence/runtime")


def test_windows_private_state_uses_fresh_system_profile_child_and_keeps_evidence_on_d(monkeypatch):
    fixture, requested = windows_environment_fixture(monkeypatch)
    for key in ["GH_TOKEN", "AZURE_CLIENT_SECRET", "PPDS_PROFILE", "PPDS_FORMS_LIVE_CONFIRM", "EXISTING_PROFILE_PATH"]:
        monkeypatch.setenv(key, "synthetic-not-a-credential")

    env = gate.private_environment(requested)

    expected = PureWindowsPath("C:/Users/runneradmin/AppData/Local/ppds-native-ci-" + "a" * 32)
    private_keys = ["HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
                    "PPDS_CONFIG_DIR", "TEMP", "TMP", "TMPDIR", "DOTNET_CLI_HOME", "DOCKER_CONFIG", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"]
    assert all(PureWindowsPath(env[k]).is_relative_to(expected) for k in private_keys)
    assert PureWindowsPath(env["HOME"]) == expected / "home"
    assert not requested.exists()
    assert all(PureWindowsPath(path).is_relative_to(expected) and mode == 0o777 for path, mode in fixture.created)
    assert not any(key in env for key in ["GH_TOKEN", "AZURE_CLIENT_SECRET", "PPDS_PROFILE", "PPDS_FORMS_LIVE_CONFIRM", "EXISTING_PROFILE_PATH", "RUNNER_TEMP"])
    assert env["DOTNET_GENERATE_ASPNET_CERTIFICATE"] == "false"


@pytest.mark.parametrize("key,value", [
    ("SYSTEMDRIVE", "D:"), ("SYSTEMDRIVE", ""), ("USERPROFILE", "C:/"),
    ("USERPROFILE", "//server/profile"), ("USERPROFILE", "D:/Users/runneradmin"),
    ("USERPROFILE", "C:/Users/../runneradmin"), ("LOCALAPPDATA", "C:/"),
    ("LOCALAPPDATA", "C:/Users/runneradmin"), ("LOCALAPPDATA", "C:/OtherProfile/Local"),
    ("LOCALAPPDATA", "//server/cache"), ("LOCALAPPDATA", "D:/a/_temp"),
    ("LOCALAPPDATA", "C:/Users/runneradmin/AppData/../Local"),
    ("GITHUB_ACTIONS", "false"), ("RUNNER_ENVIRONMENT", "self-hosted")
])
def test_windows_invalid_or_nonhosted_parents_are_refused_before_mkdir(monkeypatch, key, value):
    fixture, requested = windows_environment_fixture(monkeypatch)
    monkeypatch.setenv(key, value)

    with pytest.raises(ValueError): gate.private_environment(requested)

    assert fixture.created == []


@pytest.mark.parametrize("field", ["reparse", "symlink", "redirect", "missing"])
def test_windows_profile_ancestors_must_be_present_canonical_plain_directories(monkeypatch, field):
    fixture, requested = windows_environment_fixture(monkeypatch)
    parent = str(PureWindowsPath("C:/Users/runneradmin/AppData"))
    if field == "missing": del fixture.nodes[parent]
    else: fixture.nodes[parent][field] = "D:/a/_temp" if field == "redirect" else True

    with pytest.raises((ValueError, FileNotFoundError)): gate.private_environment(requested)

    assert fixture.created == []


def test_windows_existing_private_child_is_refused_without_overwriting(monkeypatch):
    fixture, requested = windows_environment_fixture(monkeypatch)
    target = str(PureWindowsPath("C:/Users/runneradmin/AppData/Local/ppds-native-ci-" + "a" * 32))
    fixture.nodes[target] = {"reparse": False, "symlink": False, "redirect": None}
    before = copy.deepcopy(fixture.nodes)

    with pytest.raises(ValueError): gate.private_environment(requested)

    assert fixture.nodes == before and fixture.created == []


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
    binary, fixture = tmp_path / "ppds", tmp_path / "fixture"
    script = gate.ROOT / "scripts/ci/check_native_glibc.sh"
    binary.write_bytes(b"not-executed"); fixture.mkdir()
    command = gate.docker_command(binary, fixture, script)
    assert f"type=bind,src={script.resolve(strict=True)},dst=/check.sh,readonly" in command
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


# Common before/fixed calls use the unchanged private_environment entrypoint.
ACL_CONTRACT_VECTORS = [
    {'name': 'concrete_current_user_full_control', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'system_full_control', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-18', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'administrators_full_control', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-32-544', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'trustedinstaller_ancestor', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': True, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'trustedinstaller_not_owned_node_trustee', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'foreign_owner', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-9-8-7-1002', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'owner_rights_not_concrete_owner', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-3-4', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'null_dacl', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': True, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'missing_dacl', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': False, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'owner_rights_full_control_grant', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-3-4', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_generic_write', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 1073741824, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_generic_all', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 268435456, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_delete', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 65536, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_delete_children', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 64, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_change_permissions', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 262144, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_take_ownership', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 524288, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_read_execute_sync', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 1179817, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_generic_read', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 2147483648, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_generic_execute', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 536870912, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_create_owned_node', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 6, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'world_create_traversal_ancestor', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': True, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 6, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'inherit_only_ancestor', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': True, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 2032127, 'type': 0, 'flags': 11, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'inherit_only_owned_node', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 2032127, 'type': 0, 'flags': 11, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'unsupported_object_allow_ACE', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 5, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'callback_allow_ACE', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 9, 'flags': 3, 'callback': True, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'missing_mask', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'negative_mask', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': -1, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'boolean_mask', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': True, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'unsupported_flags', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 32, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'invalid_sid', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'not-a-sid', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'too_many_ACEs', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}, {'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'zero_descriptor_size', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 0, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'oversized_descriptor', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 65537, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
    {'name': 'unavailable_ACE_inventory', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': None, 'controlFlags': 4}},
    {'name': 'world_denial_not_grant', 'accepted': True, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': 'S-1-5-21-1-2-3-1001', 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-1-0', 'mask': 2032127, 'type': 1, 'flags': 3, 'callback': False, 'qualifier': 1}], 'controlFlags': 4}},
    {'name': 'missing_owner', 'accepted': False, 'current_sid': 'S-1-5-21-1-2-3-1001', 'ancestor': False, 'descriptor': {'ownerSid': None, 'daclPresent': True, 'daclNull': False, 'descriptorBytes': 256, 'aces': [{'sid': 'S-1-5-21-1-2-3-1001', 'mask': 2032127, 'type': 0, 'flags': 3, 'callback': False, 'qualifier': 0}], 'controlFlags': 4}},
]


@pytest.mark.parametrize("case", ACL_CONTRACT_VECTORS, ids=lambda case: case["name"])
def test_windows_acl_contract(monkeypatch, case):
    fixture, requested = windows_environment_fixture(monkeypatch)
    selected = "C:/Users/runneradmin/AppData/Local" if case["ancestor"] else (
        "C:/Users/runneradmin/AppData/Local/ppds-native-ci-" + "a" * 32)
    fixture.forced_security[str(PureWindowsPath(selected))] = copy.deepcopy(case["descriptor"])
    if case["accepted"]:
        env = gate.private_environment(requested)
        assert PureWindowsPath(env["HOME"]).drive == "C:"
        assert len(fixture.reader_requests) == 2 and len(fixture.saved) == 2
        assert all(mode == 0o777 for _, mode in fixture.created)
    else:
        with pytest.raises(ValueError): gate.private_environment(requested)
        assert fixture.saved, "Rejected metadata must remain inspectable"
        if case["ancestor"]: assert fixture.created == []


@pytest.mark.parametrize("mutation", ["partial", "count", "boolean-count", "duplicate", "foreign", "missing-sid", "boolean-sid", "empty"])
def test_windows_acl_response_contract(monkeypatch, mutation):
    fixture, requested = windows_environment_fixture(monkeypatch)
    fixture.response_mutation = mutation
    with pytest.raises(ValueError): gate.private_environment(requested)
    assert fixture.created == []
    assert fixture.saved, "Malformed metadata must be retained before failure"


@pytest.mark.parametrize("platform", ["linux", "darwin"])
def test_posix_private_directories_keep700(monkeypatch, platform):
    fixture, requested = windows_environment_fixture(monkeypatch)
    monkeypatch.setattr(gate.sys, "platform", platform)
    env = gate.private_environment(requested)
    assert PureWindowsPath(env["HOME"]) == requested.value / "home"
    assert all(mode == 0o700 for _, mode in fixture.created)
    assert fixture.reader_requests == [] and fixture.saved == []


# Additional reader/ordering tests are separate from the immutable common66 block.

def diagnostic_trace(count):
    phases = ["script_entered", "request_read_started", "request_read_completed", "request_validated", "identity_started", "identity_completed"]
    phases += [f"acl_{stage}:{index}" for index in range(count) for stage in ["started", "completed"]]
    phases += ["serialization_started", "serialization_completed", "snapshot_written"]
    return [("PPDS_SECURITY_PHASE_V1:" + phase + "\r\n").encode("ascii") for phase in phases]


@pytest.mark.parametrize("prefix", [0, 1, 2, 6, 7, 8, 11])
def test_windows_reader_diagnostic_timeout_preserves_only_observed_phases(monkeypatch, prefix):
    reader, fixture, _, _ = reader_process_fixture(monkeypatch)
    trace = diagnostic_trace(1); original = gate.subprocess.run
    def timed(command, **kwargs):
        result = original(command, **kwargs)
        raise gate.subprocess.TimeoutExpired(command, 15, output=result.stdout, stderr=b"".join(trace[:prefix]))
    monkeypatch.setattr(gate.subprocess, "run", timed)
    result = reader([fixture.path("C:/Users/runneradmin/AppData/Local")])
    assert result["complete"] is False and result["observedCount"] == 0
    metadata = gate.security_evidence_metadata(result)
    assert "readerDiagnostics" in metadata
    saved = metadata["readerDiagnostics"]
    assert saved["eventCount"] == prefix and saved["lastObservedPhase"] == (trace[prefix - 1].decode().strip().split(":", 1)[1] if prefix else "not_observed")
    assert saved["timeoutSeconds"] == 15 and saved["stdoutBytes"] > 0


@pytest.mark.parametrize("variant", ["complete", "truncated", "unknown", "reordered", "duplicate", "wrong-index", "boolean-index", "oversize", "sensitive"])
def test_windows_reader_diagnostic_protocol_is_bounded_and_never_grants_trust(monkeypatch, variant):
    reader, fixture, _, _ = reader_process_fixture(monkeypatch)
    trace = diagnostic_trace(1); original = gate.subprocess.run
    if variant == "truncated": trace[-1] = trace[-1][:-4]
    if variant == "unknown": trace[1] = b"PPDS_SECURITY_PHASE_V1:unknown\n"
    if variant == "reordered": trace[1], trace[2] = trace[2], trace[1]
    if variant == "duplicate": trace.insert(2, trace[1])
    if variant == "wrong-index": trace[6] = b"PPDS_SECURITY_PHASE_V1:acl_started:1\n"
    if variant == "boolean-index": trace[6] = b"PPDS_SECURITY_PHASE_V1:acl_started:True\n"
    if variant == "oversize": trace = [b"x" * 16385]
    if variant == "sensitive": trace[2] = b"synthetic-private-marker\n"
    def completed(command, **kwargs):
        result = original(command, **kwargs); result.stderr = b"".join(trace); return result
    monkeypatch.setattr(gate.subprocess, "run", completed)
    result = reader([fixture.path("C:/Users/runneradmin/AppData/Local")])
    assert result["complete"] is (variant == "complete")
    saved = gate.security_evidence_metadata(result)
    assert "synthetic-private-marker" not in json.dumps(saved)
    assert "readerDiagnostics" in saved
    assert saved["readerDiagnostics"]["status"] == ("complete" if variant == "complete" else "invalid")


def test_windows_reader_diagnostic_partial_timeout_tail_does_not_advance_phase(monkeypatch):
    reader, fixture, _, _ = reader_process_fixture(monkeypatch)
    trace = diagnostic_trace(1)
    def timed(command, **kwargs):
        raise gate.subprocess.TimeoutExpired(command, 15, output=b"synthetic-private-marker", stderr=b"".join(trace[:2]) + trace[2][:-2])
    monkeypatch.setattr(gate.subprocess, "run", timed)
    result = reader([fixture.path("C:/Users/runneradmin/AppData/Local")])
    saved = gate.security_evidence_metadata(result)
    assert result["complete"] is False and "synthetic-private-marker" not in json.dumps(saved)
    assert "readerDiagnostics" in saved
    assert saved["readerDiagnostics"]["eventCount"] == 2 and saved["readerDiagnostics"]["incompleteTail"] is True
    assert saved["readerDiagnostics"]["lastObservedPhase"] == "request_read_started"
def reader_process_fixture(monkeypatch):
    reader = gate.read_windows_security
    timeout_type = gate.subprocess.TimeoutExpired
    fixture, requested = windows_environment_fixture(monkeypatch)
    ps = str(PureWindowsPath("C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"))
    fixture.nodes[ps] = {"reparse": False, "symlink": False, "redirect": None}
    monkeypatch.setattr(gate, "plain", lambda path: path)
    calls = []
    behavior = {"failure": None}

    def process(command, **kwargs):
        calls.append((command, kwargs))
        paths = json.loads(kwargs["input"])["paths"]
        rows = [dict(synthetic_descriptor(), path=path) for path in paths]
        payload = {"complete": True, "currentUserSid": SYNTHETIC_USER, "requestedCount": len(paths),
                   "observedCount": len(rows), "errorType": None, "rows": rows}
        if behavior["failure"] == "timeout": raise timeout_type(command, 15)
        if behavior["failure"] == "oserror": raise OSError("synthetic-unreported-reader-message")
        output = json.dumps(payload).encode()
        stderr, code = b"".join(diagnostic_trace(len(paths))), 0
        if behavior["failure"] == "malformed": output = b"{"
        if behavior["failure"] == "encoding": output = b"\xff"
        if behavior["failure"] == "shape": output = b"[]"
        if behavior["failure"] == "stdout-bound": output = b" " * 131073
        if behavior["failure"] == "stderr-bound": stderr = b"x" * 16385
        if behavior["failure"] == "nonzero": code = 1
        if behavior["failure"] == "stderr": stderr = b"synthetic-unreported-reader-message"
        return SimpleNamespace(returncode=code, stdout=output, stderr=stderr)

    monkeypatch.setattr(gate, "subprocess", SimpleNamespace(run=process, TimeoutExpired=timeout_type, CREATE_NO_WINDOW=0))
    return reader, fixture, calls, behavior


def test_windows_reader_uses_fixed_noprofile_literal_json_and_scrubbed_environment(monkeypatch):
    reader, fixture, calls, _ = reader_process_fixture(monkeypatch)
    for name in ["GH_TOKEN", "PPDS_CLIENT_SECRET", "PPDS_PROFILE", "PPDS_FORMS_LIVE_CONFIRM", "PSModulePath"]:
        monkeypatch.setenv(name, "synthetic-not-a-credential")
    paths = [fixture.path("C:/Users/runneradmin/AppData/Local")]

    result = reader(paths)

    assert result["complete"] is True and len(calls) == 1
    command, kwargs = calls[0]
    assert PureWindowsPath(command[0]) == PureWindowsPath("C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe")
    assert command[1:5] == ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]
    assert json.loads(kwargs["input"]) == {"paths": [str(path) for path in paths]}
    assert not kwargs.get("shell", False) and kwargs["timeout"] == 15
    assert all(name not in kwargs["env"] for name in ["GH_TOKEN", "PPDS_CLIENT_SECRET", "PPDS_PROFILE", "PPDS_FORMS_LIVE_CONFIRM", "PSModulePath"])
    assert "Get-Acl -LiteralPath" in command[-1]
    assert "[BitConverter]::ToUInt32" in command[-1]
    assert all(token not in command[-1] for token in ["Set-Acl", "SetAccessRule", "SetNamedSecurityInfo", ".Translate(", "Get-AD", "CreateDirectory", "icacls", "takeown"])
    assert "-ExecutionPolicy" not in command


@pytest.mark.parametrize("failure", ["timeout", "oserror", "malformed", "encoding", "shape", "stdout-bound", "stderr-bound", "nonzero", "stderr"])
def test_windows_reader_failures_return_incomplete_without_echoing_output(monkeypatch, failure):
    reader, fixture, calls, behavior = reader_process_fixture(monkeypatch)
    behavior["failure"] = failure

    result = reader([fixture.path("C:/Users/runneradmin/AppData/Local")])

    assert len(calls) == 1 and result["complete"] is False
    assert "synthetic-unreported-reader-message" not in json.dumps(gate.security_evidence_metadata(result))


@pytest.mark.parametrize("path", ["D:/a/_temp", "C:/Users/runneradmin/../Other", "//server/share", "relative"])
def test_windows_reader_refuses_noncanonical_input_before_process(monkeypatch, path):
    reader, fixture, calls, _ = reader_process_fixture(monkeypatch)

    with pytest.raises(ValueError): reader([fixture.path(path)])

    assert calls == []


def test_windows_reader_unsigned_generic_mask_is_retained_and_checked(monkeypatch):
    reader, fixture, calls, _ = reader_process_fixture(monkeypatch)
    result = reader([fixture.path("C:/Users/runneradmin/AppData/Local")])
    row = result["rows"][0]
    row["aces"][0].update({"sid": "S-1-1-0", "mask": 0x80000000})

    assert gate.windows_descriptor_policy(row, SYNTHETIC_USER, False)["accepted"] is True
    row["aces"][0]["mask"] = -2147483648
    assert gate.windows_descriptor_policy(row, SYNTHETIC_USER, False)["accepted"] is False
    assert "[BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$rawAce.AccessMask), 0)" in calls[0][0][-1]


@pytest.mark.parametrize("field,value", [("controlFlags", True), ("controlFlags", None), ("controlFlags", -1),
                                         ("descriptorBytes", True), ("descriptorBytes", None),
                                         ("ace-type", True), ("ace-type", None), ("ace-qualifier", True),
                                         ("ace-callback", None)])
def test_windows_security_invalid_typed_fields_fail_closed(monkeypatch, field, value):
    fixture, requested = windows_environment_fixture(monkeypatch)
    bad = synthetic_descriptor()
    if field.startswith("ace-"): bad["aces"][0][field[4:]] = value
    else: bad[field] = value
    target = str(PureWindowsPath("C:/Users/runneradmin/AppData/Local/ppds-native-ci-" + "a" * 32))
    fixture.forced_security[target] = bad

    with pytest.raises(ValueError): gate.private_environment(requested)

    assert len(fixture.created) == 8 and all(mode == 0o777 for _, mode in fixture.created)
    assert all(path in fixture.nodes for path, _ in fixture.created)
    assert fixture.saved[-1][1]["accepted"] is False


def test_windows_security_checks_parents_before_creation_and_all_private_nodes_after(monkeypatch):
    fixture, requested = windows_environment_fixture(monkeypatch)
    events = []

    def inspect(paths):
        events.append((len(fixture.created), [str(path) for path in paths]))
        return fixture.read_security(paths)

    monkeypatch.setattr(gate, "read_windows_security", inspect)
    gate.private_environment(requested)

    assert len(events) == 2 and events[0][0] == 0 and events[1][0] == 8
    assert len(events[0][1]) <= 8 and len(events[1][1]) == 8
    assert len(fixture.saved) == 2 and all(value["accepted"] for _, value in fixture.saved)


def test_windows_security_rejection_retains_owner_rights_metadata_without_repair(monkeypatch):
    fixture, requested = windows_environment_fixture(monkeypatch)
    target = str(PureWindowsPath("C:/Users/runneradmin/AppData/Local/ppds-native-ci-" + "a" * 32))
    fixture.forced_security[target] = synthetic_descriptor(owner_rights=True)

    with pytest.raises(ValueError): gate.private_environment(requested)

    assert len(fixture.reader_requests) == 2 and len(fixture.created) == 8
    assert all(path in fixture.nodes and mode == 0o777 for path, mode in fixture.created)
    retained = fixture.saved[-1][1]
    assert retained["accepted"] is False
    assert any(item["sid"] == "S-1-3-4" and item["mask"] == 0x1f01ff
               for policy in retained["policies"] for item in policy["allowMutation"])


@pytest.mark.parametrize("phase", ["parent", "runtime"])
def test_windows_security_refusal_in_actual_entrypoint_prevents_sdk_or_artifact_dispatch(monkeypatch, phase):
    fixture, _ = windows_environment_fixture(monkeypatch)
    base_factory = fixture.path

    def full_path(value):
        path = base_factory(value)
        path.absolute = lambda: path
        mkdir = path.mkdir
        path.mkdir = lambda mode=0o777, parents=False: mkdir(mode=mode)
        return path

    fixture.path = full_path
    monkeypatch.setattr(gate, "Path", full_path)
    selected = "C:/Users/runneradmin/AppData/Local" if phase == "parent" else (
        "C:/Users/runneradmin/AppData/Local/ppds-native-ci-" + "a" * 32)
    fixture.forced_security[str(PureWindowsPath(selected))] = synthetic_descriptor(owner_rights=True)
    monkeypatch.setattr(gate, "identity", lambda *_: {"rid": "win-x64"})
    monkeypatch.setattr(gate, "retain_compiled_metadata", lambda *_: {})
    monkeypatch.setattr(gate, "plain", lambda path: path)
    monkeypatch.setattr(gate, "digest", lambda path: "a" * 64)
    dispatched = []
    monkeypatch.setattr(gate, "run", lambda *args, **kwargs: dispatched.append(args))
    monkeypatch.setattr(gate, "heartbeat", lambda *args: dispatched.append(args))
    monkeypatch.setattr(gate.sys, "argv", ["verify", "manifest", "--rid", "win-x64", "--source", "a" * 40,
                                          "--binary", "C:/synthetic/ppds.exe", "--evidence", "D:/a/_temp/entrypoint-evidence"])

    with pytest.raises(ValueError): gate.main()

    assert dispatched == []
    assert any(name.endswith("failure.json") and value["passed"] is False for name, value in fixture.saved)
