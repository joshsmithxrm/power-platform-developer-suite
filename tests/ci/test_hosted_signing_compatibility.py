"""Offline admission/lifecycle contracts. Fake bytes only; no signer or network."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
from types import SimpleNamespace

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("hosted_signing_contract", ROOT / "scripts/ci/hosted_signing_compatibility.py")
h = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(h)


def args(stage="sign"):
    return SimpleNamespace(stage=stage, source="a" * 40, experiment="offline-test", authorization="offline-test-only",
                           admit_network_connected_disposable_signing=True)


def environment():
    return {"GITHUB_REPOSITORY": h.REPOSITORY, "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_RUN_ATTEMPT": "1",
            "GITHUB_SHA": "a" * 40, "GITHUB_RUN_ID": "12345", "RUNNER_ENVIRONMENT": "github-hosted",
            "RUNNER_OS": "Windows", "RUNNER_ARCH": "X64", "ACTIONS_RUNTIME_TOKEN": "fake-runtime-token"}


def test_default_refuses_before_environment_files_or_tools(monkeypatch):
    class ForbiddenEnvironment:
        def get(self, *args):
            raise AssertionError("environment touched before admission")
        __getitem__ = __iter__ = get
    monkeypatch.setattr(h, "os", SimpleNamespace(environ=ForbiddenEnvironment()))
    monkeypatch.setattr(h, "load_driver", lambda: pytest.fail("driver loaded before admission"))
    monkeypatch.setattr(h, "Hosted", lambda *a: pytest.fail("host touched before admission"))
    assert h.main([]) == 1


@pytest.mark.parametrize("field,value", [("stage", None), ("source", "main"), ("experiment", "../elsewhere"),
                                        ("authorization", ""), ("admit_network_connected_disposable_signing", False)])
def test_explicit_admission_is_required(field, value):
    value_args = args()
    setattr(value_args, field, value)
    with pytest.raises(ValueError):
        h.authorize(value_args)


@pytest.mark.parametrize("field,value", [("GITHUB_EVENT_NAME", "push"), ("GITHUB_RUN_ATTEMPT", "2"),
                                        ("GITHUB_REPOSITORY", "another/repo"), ("GITHUB_SHA", "b" * 40),
                                        ("RUNNER_ENVIRONMENT", "self-hosted"), ("RUNNER_OS", "Linux"),
                                        ("RUNNER_ARCH", "ARM64"), ("GITHUB_RUN_ID", "../1"),
                                        ("ACTIONS_RUNTIME_TOKEN", "")])
def test_wrong_or_replayed_host_refused(field, value):
    env = environment()
    env[field] = value
    with pytest.raises(ValueError):
        h.admit_host(args(), env)


def signing_context(tmp_path, monkeypatch, fail=None):
    """Exercise real coordinator sequencing with process/driver/transport doubles."""
    obj = h.Hosted.__new__(h.Hosted)
    obj.args, obj.root, obj.env = args(), tmp_path, {"SYSTEMROOT": "C:/Windows"}
    obj.version, obj.tree = "0.0.0-compat.12345", "b" * 40
    obj.binding, obj.context = h.admit_host(args(), environment()), {"test_double": True}
    obj.tools = {"sn": {"path": "fake-never-invoked-sn"}}
    events = []

    def execute(command, **kwargs):
        kind = "generate" if "-k" in command else "export"
        events.append(kind)
        if kind == "generate":
            Path(command[-1]).write_bytes(b"synthetic-not-a-key")
        if kind == fail:
            raise ValueError("injected process failure")
        if kind == "export":
            Path(command[-1]).write_bytes(b"synthetic-public-only")

    monkeypatch.setattr(h, "execute", execute)
    obj.d = SimpleNamespace(pin=lambda *a: None, environment=lambda *a, **kw: {},
                            read=lambda p, *a: Path(p).read_bytes(), public_shape=lambda _: None,
                            digest=lambda raw: "c" * 64)

    def plan(directory):
        events.append("plan")
        obj.plan_hash = "c" * 64
        obj.plan_directory = directory
        if fail == "plan":
            raise ValueError("injected plan refusal")
    obj.plan = plan

    def transfer(operation, stage, directory, **kwargs):
        kind = stage + "-" + operation
        events.append(kind)
        if kind == fail:
            raise ValueError("injected transport failure")
        if operation == "download":
            directory.mkdir()
        if stage == "sign":
            assert not (tmp_path / "original-disposable-key.snk").exists()
            assert json.loads((directory / "custody.json").read_text())["original_key_cleanup"] == "completed"
    obj.transfer = transfer

    def driver(stage, output, *extras):
        events.append("driver-" + stage)
        assert (tmp_path / "original-disposable-key.snk").read_bytes() == b"synthetic-not-a-key"
        if fail == "driver-sign":
            raise ValueError("injected signer failure")
        output.mkdir()
        h.json_write(output / "sign-receipt.json", {"key_cleanup": "failed" if fail == "driver-cleanup" else "completed"})
    obj.driver = driver
    return obj, events


@pytest.mark.parametrize("point", ["generate", "export", "plan", "plan-upload", "prepare-download", "driver-sign", "driver-cleanup"])
def test_each_failure_cleans_original_and_never_publishes_signed_output(tmp_path, monkeypatch, point, capsys):
    obj, events = signing_context(tmp_path, monkeypatch, point)
    with pytest.raises(ValueError):
        obj.sign()
    assert not (tmp_path / "original-disposable-key.snk").exists()
    assert "sign-upload" not in events
    assert events.count("generate") == 1
    assert events.count("driver-sign") <= 1
    phases = [json.loads(line)["hosted_compatibility_phase"] for line in capsys.readouterr().out.splitlines()]
    assert "original-key-cleanup-completed" in phases


def test_cleanup_failure_withholds_qualified_artifact(tmp_path, monkeypatch, capsys):
    obj, events = signing_context(tmp_path, monkeypatch)
    monkeypatch.setattr(h, "cleanup_original", lambda _: (_ for _ in ()).throw(ValueError("injected cleanup failure")))
    with pytest.raises(ValueError, match="cleanup failure"):
        obj.sign()
    assert "sign-upload" not in events
    assert "original-key-cleanup-completed" not in capsys.readouterr().out
    # This is a test-double byte file, not signing material.
    (tmp_path / "original-disposable-key.snk").unlink()


def test_success_removes_original_before_upload_and_signs_once(tmp_path, monkeypatch):
    obj, events = signing_context(tmp_path, monkeypatch)
    obj.sign()
    assert events == ["generate", "export", "plan", "plan-upload", "prepare-download", "driver-sign", "sign-upload"]
    assert not (tmp_path / "original-disposable-key.snk").exists()


def test_no_private_file_or_directory_is_a_stage_upload():
    for stage in ("plan", "prepare", "sign", "evidence"):
        inventory = h.stage_files(stage, "0.0.0-compat.12345")
        assert len(inventory) <= 16
        assert all("/" not in name and "\\" not in name and not name.endswith((".snk", ".pfx", ".pem")) for name in inventory)
        assert "original-disposable-key.snk" not in inventory
    assert "custody.json" in h.stage_files("sign", "0.0.0-compat.12345")


def test_verifier_requires_completed_job_bindings_before_download(tmp_path):
    obj = h.Hosted.__new__(h.Hosted)
    obj.env, obj.root = {}, tmp_path
    obj.transfer = lambda *a, **kw: pytest.fail("unbound artifact download")
    with pytest.raises(ValueError, match="Completed job"):
        obj.verify()


def test_upload_stages_only_closed_public_inventory(tmp_path, monkeypatch):
    obj = h.Hosted.__new__(h.Hosted)
    obj.args, obj.root, obj.version = args(), tmp_path, "0.0.0-compat.12345"
    obj.binding = h.admit_host(args(), environment())
    obj.node, obj.adapter, obj.pins = Path("unused-node"), Path("unused-adapter"), {}
    obj.env = {"GITHUB_OUTPUT": str(tmp_path / "outputs")}
    source = tmp_path / "driver-output"
    source.mkdir()
    (source / "temp").mkdir()
    (source / "temp/private.snk").write_bytes(b"synthetic-never-read")
    names = h.stage_files("sign", obj.version)
    for name in names:
        (source / name).write_bytes(b"public-test-data")
    def read(path):
        assert path.suffix != ".snk"
        return path.read_bytes()
    obj.d = SimpleNamespace(read=read, write=lambda path, raw: path.write_bytes(raw))
    def execute(command, **kwargs):
        upload = Path(command[command.index("--root") + 1])
        assert {p.name for p in upload.iterdir()} == names
        assert upload != source
        binding = json.loads(Path(command[command.index("--binding") + 1]).read_text())
        result = Path(command[command.index("--result") + 1])
        h.json_write(result, {"binding": binding, "inventory": [{"path": n} for n in names],
                             "artifact": {"id": 123, "digest": "sha256:" + "d" * 64}})
        return ""
    monkeypatch.setattr(h, "execute", execute)
    # Result reads are bounded in production; the double accepts the same signature.
    obj.d.read = lambda path, *unused: read(path)
    assert obj.transfer("upload", "sign", source)["id"] == 123
    assert "sign-id=123" in (tmp_path / "outputs").read_text()


@pytest.mark.parametrize("fault", [None, "cleanup", "job", "plan", "mocked", "missing-case"])
def test_verify_only_publishes_complete_bound_real_evidence(tmp_path, fault):
    obj = h.Hosted.__new__(h.Hosted)
    obj.args, obj.root = args("verify"), tmp_path
    obj.binding, obj.tree, obj.tools = h.admit_host(obj.args, environment()), "b" * 40, {}
    obj.plan_hash = "c" * 64
    def context(job):
        return {**obj.binding, "tree": obj.tree, "tools": {}, "job": job, "runner": "fixture-" + job,
                "network_connected_disposable_signer_admitted": True}
    obj.context = context("verify")
    obj.env = {f"PPDS_{stage.upper()}_{field}": "123" if field == "ID" else "sha256:" + "d" * 64
               for stage in ("plan", "prepare", "sign") for field in ("ID", "DIGEST")}
    uploaded = []
    def transfer(operation, stage, directory, **kwargs):
        if operation == "upload":
            uploaded.append(stage)
            return
        assert kwargs["expected"]["id"] == "123"
        directory.mkdir()
        if stage == "plan":
            h.json_write(directory / "context.json", context("sign"))
        elif stage == "prepare":
            h.json_write(directory / "prepare-context.json", context("sign" if fault == "job" else "prepare"))
        else:
            h.json_write(directory / "custody.json", {"binding": obj.binding, "context": context("sign"),
                         "plan_sha256": "e" * 64 if fault == "plan" else obj.plan_hash,
                         "original_key_cleanup": "pending" if fault == "cleanup" else "completed",
                         "driver_copy_cleanup": "completed"})
    obj.transfer, obj.plan = transfer, lambda _: None
    pure_driver = h.load_driver()
    obj.d = SimpleNamespace(read=lambda path, *unused: path.read_bytes(), digest=lambda _: "f" * 64,
                            complete_cases=pure_driver.complete_cases)
    def driver(stage, output, *extras):
        output.mkdir()
        cases = {f"COMPAT-{n:02}": "PASS" for n in range(1, 7)}
        if fault == "missing-case":
            del cases["COMPAT-06"]
        h.json_write(output / "compatibility-receipt.json", {"cases": cases,
                     "evidence_class": "process-double" if fault == "mocked" else "real-process"})
    obj.driver = driver
    if fault:
        with pytest.raises((ValueError, pure_driver.Error)):
            obj.verify()
        assert uploaded == []
    else:
        obj.verify()
        assert uploaded == ["evidence"]


def test_workflow_is_manual_read_only_and_has_no_deadlock_or_unfinished_outputs():
    path = ROOT / ".github/workflows/plugin-signing-compatibility.yml"
    raw = path.read_text()
    workflow = yaml.safe_load(raw)
    event = workflow.get("on", workflow.get(True))
    assert set(event) == {"workflow_dispatch"}
    assert event["workflow_dispatch"]["inputs"]["admit_network_connected_disposable_signing"]["default"] is False
    assert event["workflow_dispatch"]["inputs"]["bootstrap_only"]["default"] is False
    assert workflow["permissions"] == {"contents": "read"}
    jobs = workflow["jobs"]
    assert set(jobs) == {"sign", "prepare", "verify"}
    assert "needs" not in jobs["sign"] and "needs" not in jobs["prepare"]
    assert set(jobs["verify"]["needs"]) == {"sign", "prepare"}
    assert "!inputs.bootstrap_only" in jobs["verify"]["if"]
    for name, job in jobs.items():
        assert job["runs-on"] == "windows-2025"
        assert 0 < job["timeout-minutes"] <= 75
        assert "inputs.admit_network_connected_disposable_signing" in job["if"]
        assert "permissions" not in job
        steps = job["steps"]
        assert steps[-2]["id"] == "bootstrap" and steps[-1]["id"] == "compat"
        for step in steps:
            if "uses" in step and not step["uses"].startswith("./"):
                assert len(step["uses"].split("@")[1]) == 40
        if name != "verify":
            assert "needs." not in json.dumps(job)
    for forbidden in ("secrets.", "id-token", "actions: write", "actions: read", "actions/cache", "nuget push", "git tag"):
        assert forbidden not in raw


def test_transport_offline_contracts():
    node = shutil.which("node")
    if not node:
        pytest.skip("Node unavailable; transport contract not run")
    package = ROOT / "scripts/ci/hosted-signing-artifacts"
    result = subprocess.run([node, "--test", str(package / "test/transport.test.mjs"), str(package / "test/action.test.mjs")],
                            stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=30, check=False)
    assert result.returncode == 0, result.stdout + result.stderr


@pytest.mark.parametrize("bootstrap_only", [False, True])
def test_bootstrap_selects_one_python_when_host_has_multiple_installations(tmp_path, bootstrap_only):
    pwsh = shutil.which("pwsh")
    if not pwsh:
        pytest.skip("PowerShell unavailable; actual bootstrap regression not run")
    workspace, runner_temp = tmp_path / "workspace", tmp_path / "runner"
    source = workspace / "scripts/ci/hosted-signing-artifacts"
    source.mkdir(parents=True)
    runner_temp.mkdir()
    for name in ("package.json", "package-lock.json", "cli.mjs", "transport.mjs"):
        (source / name).write_text("offline fixture", encoding="utf-8")
    harness = tmp_path / "bootstrap-harness.ps1"
    harness.write_text("""
function npm.cmd { $global:LASTEXITCODE = 0 }
function Get-Command {
    param($Name, $CommandType, $ErrorAction)
    if ($Name -ne 'python.exe') { throw 'Unexpected command discovery' }
    [pscustomobject]@{ Source = $env:PPDS_TEST_FIRST_PYTHON }
    [pscustomobject]@{ Source = $env:PPDS_TEST_SECOND_PYTHON }
}
$bootstrap = $args[0]
& $bootstrap -Stage sign
""", encoding="utf-8")
    first, second = str(tmp_path / "selected python.exe"), str(tmp_path / "other python.exe")
    output, environment_output = tmp_path / "output", tmp_path / "environment"
    env = {**os.environ, "PPDS_ADMISSION": "true", "GITHUB_EVENT_NAME": "workflow_dispatch",
           "GITHUB_RUN_ATTEMPT": "1", "PPDS_EXPECTED_SOURCE": "a" * 40, "GITHUB_SHA": "a" * 40,
           "GITHUB_REPOSITORY": h.REPOSITORY, "RUNNER_ENVIRONMENT": "github-hosted", "RUNNER_OS": "Windows",
           "RUNNER_TEMP": str(runner_temp), "GITHUB_WORKSPACE": str(workspace), "GITHUB_OUTPUT": str(output),
           "GITHUB_ENV": str(environment_output), "PPDS_TEST_FIRST_PYTHON": first, "PPDS_TEST_SECOND_PYTHON": second,
           "PPDS_EXPERIMENT": "fake-offline-only", "PPDS_AUTHORIZATION": "fake-offline-only"}
    env["PPDS_BOOTSTRAP_ONLY"] = str(bootstrap_only).lower()
    result = subprocess.run([pwsh, "-NoProfile", "-File", str(harness), str(ROOT / "scripts/ci/bootstrap_hosted_signing.ps1")],
                            env=env, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=30, check=False)
    assert result.returncode == 0, result.stdout + result.stderr
    outputs = dict(line.split("=", 1) for line in output.read_text(encoding="utf-8-sig").splitlines())
    assert outputs["python"] == first
    assert second not in outputs["python"]
    assert json.loads(outputs["arguments"])[0:2] == ["--stage", "sign"]
    assert ("--bootstrap-only" in json.loads(outputs["arguments"])) is bootstrap_only


@pytest.mark.parametrize("stage", ["sign", "prepare", "verify"])
def test_keyless_bootstrap_never_enters_a_stage(monkeypatch, capsys, stage):
    monkeypatch.setattr(h, "admit_host", lambda *a: {})
    monkeypatch.setattr(h, "load_driver", lambda: object())
    class KeylessContext:
        def __init__(self, *a):
            pass
        def __getattr__(self, name):
            pytest.fail("Keyless bootstrap entered stage " + name)
    monkeypatch.setattr(h, "Hosted", KeylessContext)
    assert h.main(["--stage", stage, "--source", "a" * 40, "--experiment", "offline", "--authorization", "offline-only",
                   "--admit-network-connected-disposable-signing", "--bootstrap-only"]) == 0
    assert "keyless-bootstrap-completed-no-stage-called" in capsys.readouterr().out


def test_command_failure_reports_only_exit_code_not_private_diagnostics(monkeypatch, capsys, tmp_path):
    monkeypatch.setattr(h, "admit_host", lambda *a: {})
    monkeypatch.setattr(h, "load_driver", lambda: object())
    monkeypatch.setattr(h.subprocess, "run", lambda *a, **kw: SimpleNamespace(returncode=19, stdout="PRIVATE_OUTPUT", stderr="PRIVATE_ERROR"))
    monkeypatch.setattr(h, "Hosted", lambda *a: h.execute(["PRIVATE_COMMAND"], cwd=tmp_path, env={}, timeout=1))
    assert h.main(["--stage", "sign", "--source", "a" * 40, "--experiment", "offline", "--authorization", "offline-only",
                   "--admit-network-connected-disposable-signing"]) == 1
    captured = capsys.readouterr()
    assert '"exit_code": 19' in captured.err
    assert '"hosted_compatibility_failure": "command"' in captured.err
    assert "PRIVATE_" not in captured.out + captured.err
