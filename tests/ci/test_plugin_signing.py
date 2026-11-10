"""Offline real ZIP/PE-header logic with controlled bytes and mocked tool processes.

No test here signs, accesses keys, runs sn.exe or proves Windows signing compatibility.
"""
from copy import deepcopy
import io
import json
from pathlib import Path
import struct
from types import SimpleNamespace
import zipfile

import pytest
import yaml

from scripts.ci import verify_nuget_handoff as h
from scripts.ci import verify_plugin_signing as p

ROOT = Path(__file__).resolve().parents[2]
SOURCE = "b" * 40
TAG = "Plugins-v3.1.1"


def pe_fixture(key):
    """Header fixture, not an executable or actual managed metadata/signature proof."""
    raw = bytearray(1024)
    raw[:2] = b"MZ"
    struct.pack_into("<I", raw, 60, 128)
    raw[128:132] = b"PE\0\0"
    struct.pack_into("<H", raw, 134, 1)
    struct.pack_into("<H", raw, 148, 224)
    struct.pack_into("<H", raw, 152, 0x10b)
    struct.pack_into("<II", raw, 152 + 96 + 14 * 8, 0x2000, 72)
    struct.pack_into("<III", raw, 376 + 12, 0x2000, 512, 512)
    struct.pack_into("<II", raw, 512 + 32, 0x2080, struct.unpack_from("<I", key, 24)[0] // 8)
    return bytes(raw)


def package(path, dll, *, member=p.DLL):
    with zipfile.ZipFile(path, "x") as archive:
        archive.writestr("PPDS.Plugins.nuspec", f'<package><metadata><id>PPDS.Plugins</id><version>3.1.1</version><repository commit="{SOURCE}"/></metadata></package>')
        archive.writestr(member, dll)
        archive.writestr("README.md", b"unchanged documentation")


def policy_fixture(tmp_path, key, monkeypatch):
    tool = tmp_path / "sn.exe"
    tool.write_bytes(b"CONTROLLED TOOL BYTE FIXTURE; NEVER EXECUTED")
    tool_identity = "C:/controlled-fixture/sn.exe"
    read_bytes = h.regular_bytes
    # Keep the Windows production-path contract on every test host. Only map
    # the pinned tool's bytes to a local fixture; no tool process is executed.
    monkeypatch.setattr(h, "regular_bytes", lambda path, bound:
                        read_bytes(tool if path == Path(tool_identity) else path, bound))
    policy = {"schema": 1, "producer_assurance": "REVIEWED_CONTROLLED_PRODUCER", "activation_approved": True,
              "compatibility_verified": True, "compatibility_receipt_sha256": "c" * 64,
              "public_key_sha256": h.sha(key), "signer": {"path": tool_identity, "sha256": h.sha(tool.read_bytes()), "version": "4.8.0.0"}}
    path = tmp_path / "policy.json"
    p.write_json(path, policy)
    return path, policy


def service_fixture():
    jobs = [
        {"id": 91, "name": h.JOB_NAME, "started_at": "2026-11-10T01:00:00Z", "completed_at": "2026-11-10T01:01:00Z"},
        {"id": 92, "name": "Sign Plugins assembly", "started_at": "2026-11-10T01:02:00Z", "completed_at": "2026-11-10T01:03:00Z"},
        {"id": 93, "name": h.PLUGIN_JOB, "started_at": "2026-11-10T01:04:00Z", "completed_at": "2026-11-10T01:05:00Z"},
    ]
    for job in jobs:
        job.update(head_sha=SOURCE, run_id=17, run_attempt=1, status="completed", conclusion="success")
    artifacts = {}
    for number, prefix, minute in ((71, "nuget-keyless", "00"), (72, "plugins-signed", "02"), (73, "nuget-package", "04")):
        artifacts[number] = {"id": number, "name": f"{prefix}-17-1-{SOURCE}", "digest": "sha256:" + "a" * 64,
                             "expired": False, "size_in_bytes": 100, "created_at": f"2026-11-10T01:{minute}:30Z",
                             "workflow_run": {"id": 17, "head_sha": SOURCE, "repository_id": h.REPO_ID, "head_repository_id": h.REPO_ID}}
    def fetch(url):
        if "/actions/artifacts/" in url:
            return artifacts[int(url.rsplit("/", 1)[1])]
        if "/jobs?" in url:
            return {"total_count": len(jobs), "jobs": jobs}
        if "/git/ref/" in url:
            return {"ref": "refs/tags/" + TAG, "object": {"type": "commit", "sha": SOURCE}}
        assert url.endswith("/ppds.plugins/index.json")
        return {"versions": ["3.1.0"]}
    return fetch, jobs, artifacts


@pytest.fixture
def prepared(tmp_path, monkeypatch):
    key = p.PUBLIC_KEY.read_bytes()  # Public bytes only.
    directory = tmp_path / "build"
    directory.mkdir()
    dll = pe_fixture(key)
    package(directory / "PPDS.Plugins.3.1.1.nupkg", dll)
    package(directory / "PPDS.Plugins.3.1.1.snupkg", b"PDB RECORDING BYTES", member="lib/net462/PPDS.Plugins.pdb")
    manifest = p.prepare(directory, TAG, SOURCE, "17", "1")
    policy_path, policy = policy_fixture(tmp_path, key, monkeypatch)
    fetch, jobs, artifacts = service_fixture()
    calls = []
    def runner(command):
        calls.append(command)
        if command[0] == "pwsh":
            return json.dumps([{"name": "PPDS.Plugins", "version": "3.1.1+" + SOURCE}])
        assert command[0] == str(Path(policy["signer"]["path"])) and command[1] in {"-vf", "-D"}
        return "recorded verification only"
    return SimpleNamespace(directory=directory, dll=dll, manifest=manifest, policy_path=policy_path, policy=policy,
                           fetch=fetch, jobs=jobs, artifacts=artifacts, runner=runner, calls=calls, root=tmp_path)


def signed_fixture(fixture):
    signed = fixture.root / "signed"
    signed.mkdir()
    raw = bytearray(fixture.dll)
    raw[640:768] = bytes([7]) * 128  # Controlled mock signature bytes, never valid crypto.
    (signed / "PPDS.Plugins.dll").write_bytes(raw)
    receipt = {"schema": 1, "source": SOURCE, "tag": TAG, "run_id": "17", "run_attempt": "1",
               "policy_sha256": h.sha(fixture.policy_path.read_bytes()), "input_dll_sha256": h.sha(fixture.dll),
               "public_key_sha256": fixture.policy["public_key_sha256"], "signed_dll_sha256": h.sha(raw),
               "signer_sha256": fixture.policy["signer"]["sha256"]}
    receipt_sha = p.write_json(signed / p.RECEIPT, receipt)
    build = {"artifact_id": "71", "artifact_digest": "a" * 64, "manifest_sha256": fixture.manifest}
    sign = {"artifact_id": "72", "artifact_digest": "a" * 64, "manifest_sha256": receipt_sha}
    return signed, build, sign


def test_policy_defaults_fail_closed(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setenv("PLUGINS_SNK_BASE64", "MUST-NOT-BE-READ")
    with pytest.raises(h.Error, match="not activated"):
        p.admit(tmp_path / "missing", tmp_path / "out", TAG, SOURCE, "17", "1", "71", "a" * 64, "b" * 64,
                fetch=lambda url: calls.append(url))
    assert not calls and not (tmp_path / "out").exists()
    assert p.main(["policy"]) == 1


@pytest.mark.parametrize("field", ["activation_approved", "compatibility_verified", "compatibility_receipt_sha256",
                                   "signer.sha256", "signer.version", "public_key_sha256"])
def test_incomplete_policy_refuses_before_artifact_or_tool(prepared, field):
    policy = deepcopy(prepared.policy)
    owner, key = (policy["signer"], field.split(".")[1]) if field.startswith("signer.") else (policy, field)
    owner[key] = None
    prepared.policy_path.write_text(json.dumps(policy))
    calls = []
    with pytest.raises(h.Error):
        p.admit(prepared.root / "missing", prepared.root / "out", TAG, SOURCE, "17", "1", "71", "a" * 64,
                prepared.manifest, policy_path=prepared.policy_path, fetch=lambda url: calls.append(url), runner=prepared.runner)
    assert not calls and not prepared.calls and not (prepared.root / "out").exists()


def test_workflow_secret_boundary():
    workflow = yaml.safe_load((ROOT / ".github/workflows/publish-nuget.yml").read_text())
    jobs = workflow["jobs"]
    build = str(jobs["build-package"])
    assert "secrets." not in build and "PublicSign=false" in build and "DelaySign=true" in build
    assert "--no-build" in build and "AssemblyOriginatorKeyFile" not in build
    signer = jobs["sign-plugins"]
    steps = signer["steps"]
    secret = [i for i, step in enumerate(steps) if "secrets." in str(step)]
    assert len(secret) == 1
    assert next(i for i, step in enumerate(steps) if step.get("run", "").endswith("verify_plugin_signing.py policy")) < secret[0]
    assert next(i for i, step in enumerate(steps) if "verify_plugin_signing.py admit" in step.get("run", "")) < secret[0]
    assert all("dotnet " not in str(step) for step in steps)
    assert "GITHUB_WORKSPACE" in steps[secret[0]]["run"]
    verify = jobs["verify-signed-plugins"]
    assert verify["needs"] == ["build-package", "sign-plugins"] and "secrets." not in str(verify)
    assert "verify_release_inputs.py nuget" in str(verify) and "-D" not in str(jobs["publish"])
    assert jobs["publish"]["needs"] == ["build-package", "verify-signed-plugins"]
    condition = jobs["publish"]["if"]
    assert "always()" in condition and "needs.verify-signed-plugins.result == 'success'" in condition
    assert "needs.verify-signed-plugins.result == 'skipped'" in condition
    assert "!startsWith" in str(jobs["publish"]["steps"])  # Failed Plugins cannot select keyless output.


def test_admit_checks_actual_bytes_and_source_before_request(prepared):
    out = prepared.root / "admitted"
    p.admit(prepared.directory, out, TAG, SOURCE, "17", "1", "71", "a" * 64, prepared.manifest,
            policy_path=prepared.policy_path, fetch=prepared.fetch, runner=prepared.runner)
    assert (out / "PPDS.Plugins.dll").read_bytes() == prepared.dll
    assert json.loads((out / "signing-request.json").read_bytes())["policy_sha256"] == h.sha(prepared.policy_path.read_bytes())
    assert prepared.calls[0][-1] == "-DelaySignedInput"


def test_repackage_preserves_other_members(prepared, monkeypatch):
    signed, build, sign = signed_fixture(prepared)
    out, chain_path = prepared.root / "final", prepared.root / "chain.json"
    p.verify_signed(prepared.directory, signed, out, chain_path, TAG, SOURCE, "17", "1", build, sign,
                    policy_path=prepared.policy_path, fetch=prepared.fetch, runner=prepared.runner)
    before = dict((info.filename, raw) for info, raw in p.members((prepared.directory / "PPDS.Plugins.3.1.1.nupkg").read_bytes()))
    after = dict((info.filename, raw) for info, raw in p.members((out / "PPDS.Plugins.3.1.1.nupkg").read_bytes()))
    assert before.keys() == after.keys() and {key for key in before if before[key] != after[key]} == {p.DLL}
    assert (out / "PPDS.Plugins.3.1.1.snupkg").read_bytes() == (prepared.directory / "PPDS.Plugins.3.1.1.snupkg").read_bytes()
    assert [command[1] for command in prepared.calls if command[0] != "pwsh"] == ["-vf", "-D"]
    chain = json.loads(chain_path.read_bytes())
    manifest = h.create(out, TAG, SOURCE, "17", "1", chain=chain)
    original_policy = h.signing_policy
    monkeypatch.setattr(h, "signing_policy", lambda ignored: original_policy(prepared.policy_path))
    assert h.verify(out, TAG, SOURCE, "17", "1", "73", "a" * 64, manifest, fetch=prepared.fetch)["producer_job_id"] == 93


@pytest.mark.parametrize("area", ["input", "signed", "receipt", "extra"])
def test_repackage_rejects_tampering(prepared, area):
    signed, build, sign = signed_fixture(prepared)
    if area == "input":
        (prepared.directory / "PPDS.Plugins.3.1.1.nupkg").write_bytes(b"changed")
    elif area == "signed":
        (signed / "PPDS.Plugins.dll").write_bytes(b"changed")
    elif area == "receipt":
        data = json.loads((signed / p.RECEIPT).read_bytes())
        data["source"] = "c" * 40
        (signed / p.RECEIPT).write_text(json.dumps(data))
        sign["manifest_sha256"] = h.sha((signed / p.RECEIPT).read_bytes())
    else:
        (signed / "run-me.ps1").write_text("never execute")
    with pytest.raises((h.Error, zipfile.BadZipFile)):
        p.verify_signed(prepared.directory, signed, prepared.root / "final", prepared.root / "chain.json", TAG, SOURCE,
                        "17", "1", build, sign, policy_path=prepared.policy_path, fetch=prepared.fetch, runner=prepared.runner)
    assert not prepared.calls and not (prepared.root / "final").exists()


@pytest.mark.parametrize("mutation", ["source", "attempt", "producer", "digest"])
def test_chain_rejects_wrong_binding(prepared, mutation):
    signed, build, sign = signed_fixture(prepared)
    if mutation == "source": prepared.artifacts[71]["workflow_run"]["head_sha"] = "c" * 40
    if mutation == "attempt": prepared.jobs[1]["run_attempt"] = 2
    if mutation == "producer": prepared.jobs[1]["name"] = "unexpected producer"
    if mutation == "digest": prepared.artifacts[72]["digest"] = "sha256:" + "c" * 64
    with pytest.raises(h.Error):
        p.verify_signed(prepared.directory, signed, prepared.root / "final", prepared.root / "chain.json", TAG, SOURCE,
                        "17", "1", build, sign, policy_path=prepared.policy_path, fetch=prepared.fetch, runner=prepared.runner)
    assert not prepared.calls


def test_chain_requires_chronology(prepared):
    signed, build, sign = signed_fixture(prepared)
    prepared.jobs[0]["completed_at"] = "2026-11-10T01:02:30Z"
    with pytest.raises(h.Error, match="chronology"):
        p.verify_signed(prepared.directory, signed, prepared.root / "final", prepared.root / "chain.json", TAG, SOURCE,
                        "17", "1", build, sign, policy_path=prepared.policy_path, fetch=prepared.fetch, runner=prepared.runner)


def test_tool_failure_refuses(prepared):
    signed, build, sign = signed_fixture(prepared)
    def runner(command):
        if command[0] == "pwsh": return prepared.runner(command)
        raise h.Error("recorded signature tool failed")
    with pytest.raises(h.Error, match="tool failed"):
        p.verify_signed(prepared.directory, signed, prepared.root / "final", prepared.root / "chain.json", TAG, SOURCE,
                        "17", "1", build, sign, policy_path=prepared.policy_path, fetch=prepared.fetch, runner=runner)
    assert not (prepared.root / "final").exists() and not (prepared.root / "chain.json").exists()


@pytest.mark.parametrize("member", ["../escape.dll", "lib/net462/OTHER.dll", "LIB/net462/PPDS.Plugins.dll"])
def test_unsafe_or_unexpected_payload_rejected(tmp_path, member):
    package(tmp_path / "PPDS.Plugins.3.1.1.nupkg", b"marker", member=member)
    with pytest.raises(h.Error): p.prepare(tmp_path, TAG, SOURCE, "17", "1")


def test_public_signed_flag_is_not_delay_signed(prepared):
    data = bytearray(prepared.dll)
    struct.pack_into("<I", data, 512 + 16, 8)
    with pytest.raises(h.Error, match="delay-signed"):
        p.delay_signed_pe(bytes(data), p.PUBLIC_KEY.read_bytes())


def test_signer_source_has_no_build_or_assembly_load_and_policy_precedes_secret():
    source = (ROOT / "scripts/ci/sign_plugin_assembly.ps1").read_text()
    assert source.index("'activation_approved'") < source.index("GetEnvironmentVariable('PLUGINS_SNK_BASE64'")
    assert "UseShellExecute = $false" in source and "Environment.Clear()" in source
    assert "@('-R'," in source and "@('-vf'," in source and "@('-D'," in source
    assert "dotnet " not in source and "GITHUB_ENV" not in source
    assert "Assembly]::Load" not in source
