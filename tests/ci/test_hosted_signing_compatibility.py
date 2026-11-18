"""Offline admission/lifecycle contracts. Fake bytes only; no signer or network."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from types import SimpleNamespace

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("hosted_signing_contract", ROOT / "scripts/ci/hosted_signing_compatibility.py")
h = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(h)


# Hang guard only, not a speed assertion. The bootstrap test launches pwsh with every external command stubbed,
# so the work is process start-up (about 0.3 s idle) plus a few file copies; on a loaded hosted runner start-up
# can stretch to many seconds, and a tight wall-clock budget would fail it with the behaviour still correct.
# 300 s is ~1000x the idle time yet still ends a genuinely wedged shell well inside the job timeout.
PWSH_HANG_BOUND_SECONDS = 300


@pytest.fixture(autouse=True)
def isolated_driver_module(monkeypatch):
    # driver_vocabulary/refusal_code read sys.modules; start every test without the driver entry
    # so a test sees the driver only when it loads it itself. monkeypatch restores the entry.
    monkeypatch.delitem(sys.modules, h.DRIVER_MODULE, raising=False)


def args(stage="sign"):
    return SimpleNamespace(stage=stage, source="a" * 40, experiment="offline-test", authorization="offline-test-only",
                           admit_network_connected_disposable_signing=True)


@pytest.mark.parametrize("signal_fails", [False, True])
def test_prepare_failure_signal_contains_no_diagnostics_and_preserves_original_error(tmp_path, signal_fails):
    obj = h.Hosted.__new__(h.Hosted)
    obj.root = tmp_path
    original = ValueError("PRIVATE_ERROR_AND_PATH")
    def fail():
        raise original
    obj.prepare_public = fail
    calls = []
    def transfer(operation, stage, folder):
        calls.append((operation, stage))
        assert {p.name for p in folder.iterdir()} == {"failure.json"}
        assert json.loads((folder / "failure.json").read_text()) == {"stage": "prepare", "result": "FAIL"}
        if signal_fails:
            raise OSError("PRIVATE_TRANSPORT_ERROR")
    obj.transfer = transfer
    with pytest.raises(ValueError) as caught:
        obj.prepare()
    assert caught.value is original
    assert calls == [("upload", "prepare-failed")]
    assert h.stage_files("prepare-failed", "unused") == {"failure.json"}


def environment():
    return {"GITHUB_REPOSITORY": h.REPOSITORY, "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_RUN_ATTEMPT": "1",
            "GITHUB_SHA": "a" * 40, "GITHUB_RUN_ID": "12345", "RUNNER_ENVIRONMENT": "github-hosted",
            "RUNNER_OS": "Windows", "RUNNER_ARCH": "X64", "ACTIONS_RUNTIME_TOKEN": "fake-runtime-token"}


@pytest.mark.parametrize("diagnostic,expected", [
    ("\nPPDS_ARTIFACT_REFUSAL=raw-size\nPRIVATE_VALUE", "transport-raw-size"),
    ("PPDS_ARTIFACT_REFUSAL=PRIVATE_VALUE", None),
    ("Artifact transport failed; no successful result was written", "artifact-transport"),
    ("Candidate experiment refused or failed; no passing compatibility claim is available.", "candidate-driver"),
    ("\nPPDS_COMPAT_REFUSAL=tool-hash\nPRIVATE_VALUE", "driver-tool-hash"),
    ("PPDS_COMPAT_REFUSAL=secret-value", None),
    ("Failed to generate a strong name key pair -- Access is denied.", "access-denied"),
    ("Failed to generate a strong name key pair -- The keyset is not defined.", "keyset-undefined"),
    ("Keyset does not exist", "keyset-missing"),
    ("The system cannot find the file specified", "file-not-found"),
    ("Invalid command line", "command-line"),
    ("Invalid option", "command-line"),
    ("Failed to generate a strong name key pair", "key-generation"),
    ("private-unknown-command-secret", None),
])
def test_command_refusal_is_fixed_enum_not_private_diagnostics(diagnostic, expected):
    h.load_driver()
    assert h.command_refusal("private-prefix " + diagnostic, "private-suffix") == expected


def test_command_refusal_accepts_exactly_the_driver_registry_codes():
    driver = h.load_driver()
    # The accepted set is the registry's codes plus the fixed buckets, and nothing else.
    assert driver.REFUSAL_CODES == set(driver.REFUSALS.values()) | {"unregistered", "exception-other"} | {
        "exception-" + name.lower() for name in driver.EXCEPTION_CLASSES}
    for code in driver.REFUSAL_CODES:
        assert h.command_refusal("PRIVATE_OUTPUT", "PRIVATE\nPPDS_COMPAT_REFUSAL=" + code + "\nPRIVATE") == "driver-" + code
    for unknown in ("unclassified", "C:/PRIVATE/path", "tool-hash PRIVATE", "TOOL-HASH", ""):
        assert h.command_refusal("", "PPDS_COMPAT_REFUSAL=" + unknown) is None
    # Two refusal lines are ambiguous; neither is passed through.
    assert h.command_refusal("", "PPDS_COMPAT_REFUSAL=tool-hash\nPPDS_COMPAT_REFUSAL=plan-hash") is None


@pytest.mark.parametrize("text,operation,status", [
    ("PPDS_COMPAT_OPERATION=delay-restore\nPPDS_COMPAT_STATUS=17", "delay-restore", "17"),
    ("PPDS_COMPAT_OPERATION=package-signature-verification\r\nPPDS_COMPAT_STATUS=0\r\n", "package-signature-verification", "0"),
    ("PPDS_COMPAT_OPERATION=sign\nPPDS_COMPAT_STATUS=timeout", "sign", "timeout"),
    ("PPDS_COMPAT_OPERATION=sign\nPPDS_COMPAT_STATUS=launch-failure", "sign", "launch-failure"),
    ("PPDS_COMPAT_OPERATION=-3221225477\nPPDS_COMPAT_STATUS=-1073741819", None, "-1073741819"),
    ("PPDS_COMPAT_OPERATION=C:/PRIVATE/argv\nPPDS_COMPAT_STATUS=PRIVATE", None, None),
    ("PPDS_COMPAT_OPERATION=unlisted-operation\nPPDS_COMPAT_STATUS=017", None, None),
    ("PPDS_COMPAT_OPERATION=sign PRIVATE\nPPDS_COMPAT_STATUS=1 PRIVATE", None, None),
    ("no diagnostic lines at all", None, None),
])
def test_driver_operation_and_status_pass_only_fixed_values(text, operation, status):
    h.load_driver()
    assert h.command_operation("PRIVATE_STDOUT", text) == operation
    assert h.command_status("PRIVATE_STDOUT", text) == status


@pytest.mark.parametrize("text,index", [
    ("PPDS_COMPAT_OPERATION_INDEX=2", "2"), ("PPDS_COMPAT_OPERATION_INDEX=9999", "9999"),
    ("PPDS_COMPAT_OPERATION_INDEX=0", None), ("PPDS_COMPAT_OPERATION_INDEX=10000", None),
    ("PPDS_COMPAT_OPERATION_INDEX=C:/PRIVATE", None), ("PPDS_COMPAT_OPERATION_INDEX=1\nPPDS_COMPAT_OPERATION_INDEX=2", None),
])
def test_driver_operation_index_passes_only_a_bounded_integer(text, index):
    h.load_driver()
    assert h.command_operation_index("PRIVATE", text) == index


def test_inspector_diagnostic_passes_only_a_registered_code():
    driver = h.load_driver()
    code = driver.INSPECTOR_DIAGNOSTICS[0][1]
    assert h.command_diagnostic("PRIVATE_STDOUT", "PRIVATE\nPPDS_COMPAT_DIAGNOSTIC=" + code + "\nPRIVATE") == code
    for registered in driver.DIAGNOSTIC_CODES:
        assert h.command_diagnostic("", "PPDS_COMPAT_DIAGNOSTIC=" + registered) == registered
    for text in ("PPDS_COMPAT_DIAGNOSTIC=inspector-made-up", "PPDS_COMPAT_DIAGNOSTIC=C:/PRIVATE",
                 "PPDS_COMPAT_DIAGNOSTIC=" + "a" * 65, "PPDS_COMPAT_DIAGNOSTIC=",
                 "PPDS_COMPAT_DIAGNOSTIC=" + code + " PRIVATE",
                 "PPDS_COMPAT_DIAGNOSTIC=inspector-sn-no-result\nPPDS_COMPAT_DIAGNOSTIC=inspector-framework",
                 "no diagnostic lines at all"):
        assert h.command_diagnostic("PRIVATE", text) is None
    # A refusal code is not a diagnostic code, and the reverse.
    assert h.command_diagnostic("", "PPDS_COMPAT_DIAGNOSTIC=tool-hash") is None
    assert h.command_refusal("", "PPDS_COMPAT_REFUSAL=inspector-sn-no-result") is None


def test_pwsh_needs_pathext_to_launch_a_native_tool_under_the_driver_environment(tmp_path):
    # Real-process regression for hosted run r12: under the driver's minimal environment pwsh did
    # not observe `& <full path to .exe>` ($LASTEXITCODE stayed $null; the executable is started
    # detached, see test_inspect_release_assemblies.py) until PATHEXT was set.
    pwsh = shutil.which("pwsh")
    if os.name != "nt":
        pytest.skip("Windows PowerShell 7 host required for this real-process regression")
    if not pwsh:
        # The windows job in workflow-tests.yml sets this so the test cannot silently stop running.
        if os.environ.get("PPDS_REQUIRE_WINDOWS_TOOLS") == "1":
            pytest.fail("pwsh is required on this runner but missing")
        pytest.skip("Windows PowerShell 7 host required for this real-process regression")
    driver = h.load_driver()
    plan = {"windows_directory": os.environ["SYSTEMROOT"], "windows_common_data_directory": os.environ["PROGRAMDATA"],
            "tools": {}}
    script = tmp_path / "native.ps1"
    script.write_text("& (Join-Path $env:SYSTEMROOT 'System32\\cmd.exe') /c exit 3\n'code=' + $LASTEXITCODE\n",
                      encoding="utf-8")
    outcomes = {}
    for native in (False, True):
        result = subprocess.run([pwsh, "-NoProfile", "-File", str(script)], cwd=tmp_path,
                                env=driver.environment(plan, tmp_path, native=native), stdin=subprocess.DEVNULL,
                                capture_output=True, text=True, timeout=60, check=False)
        outcomes[native] = result.stdout.strip().splitlines()[-1] if result.stdout.strip() else ""
    assert outcomes[True] == "code=3"
    if outcomes[False] == "code=3":
        # Characterisation only: a newer PowerShell may observe the exit code without PATHEXT. The
        # environment setting is then harmless, and this must not block unrelated changes.
        pytest.skip("this pwsh reports native exit codes without PATHEXT")


@pytest.mark.parametrize("stage", ["prepare", "sign", "verify"])
def test_driver_failure_diagnostic_carries_fixed_codes_only(monkeypatch, capsys, tmp_path, stage):
    h.load_driver()
    stderr = ("C:/Users/PRIVATE/secret.snk\nPPDS_COMPAT_REFUSAL=package-content-hash\n"
              "PPDS_COMPAT_OPERATION=package-signature-verification\nPPDS_COMPAT_STATUS=0\nPPDS_COMPAT_OPERATION_INDEX=3\n"
              "PPDS_COMPAT_DIAGNOSTIC=inspector-sn-no-result\nPRIVATE_TRACE")
    monkeypatch.setattr(h, "admit_host", lambda *a: {})
    monkeypatch.setattr(h.subprocess, "run", lambda *a, **kw: SimpleNamespace(returncode=1, stdout="PRIVATE_OUT", stderr=stderr))
    monkeypatch.setattr(h, "Hosted", lambda *a: h.execute(["PRIVATE_COMMAND"], cwd=tmp_path, env={}, timeout=1))
    monkeypatch.setattr(h, "load_driver", lambda: object())
    assert h.main(["--stage", stage, "--source", "a" * 40, "--experiment", "offline", "--authorization", "offline-only",
                   "--admit-network-connected-disposable-signing"]) == 1
    captured = capsys.readouterr()
    diagnostic = json.loads(captured.err.splitlines()[0])
    assert diagnostic == {"hosted_compatibility_failure": "command", "refusal": "stage-command", "exit_code": 1,
                          "command_refusal": "driver-package-content-hash",
                          "driver_operation": "package-signature-verification", "driver_status": "0",
                          "driver_operation_index": "3", "driver_diagnostic": "inspector-sn-no-result"}
    assert "PRIVATE" not in captured.out + captured.err


def test_coordinator_registry_covers_every_reason_with_a_distinct_code():
    import ast
    tree = ast.parse((ROOT / "scripts/ci/hosted_signing_compatibility.py").read_text(encoding="utf-8"))
    reasons, dynamic = set(), []
    for node in ast.walk(tree):
        if isinstance(node, ast.Call) and getattr(node.func, "id", None) == "require" and len(node.args) >= 2:
            argument = node.args[1]
            if isinstance(argument, ast.Constant) and isinstance(argument.value, str):
                reasons.add(argument.value)
            else:
                dynamic.append(node.lineno)
    assert len(reasons) > 20 and sorted(reasons - set(h.REFUSALS)) == [] and dynamic == []
    codes = list(h.REFUSALS.values())
    assert len(codes) == len(set(codes))
    assert h.REFUSALS[str(h.StageCommandError(1))] == "stage-command"


@pytest.mark.parametrize("error,expected", [
    (ValueError("Clean exact reviewed source required"), "source-state"),
    (ValueError("Clean exact reviewed source required PRIVATE"), "exception-valueerror"),
    (OSError(5, "Access is denied", "C:/PRIVATE/key.snk"), "exception-oserror"),
    (json.JSONDecodeError("PRIVATE", "PRIVATE", 0), "exception-jsondecodeerror"),
    (KeyError("PRIVATE"), "exception-keyerror"),
    (type("PrivateError", (Exception,), {})("PRIVATE"), "exception-other"),
])
def test_coordinator_refusal_codes_never_carry_exception_text(error, expected):
    assert h.refusal_code(error) == expected
    h.load_driver()
    assert h.refusal_code(error) == expected


def test_without_the_driver_module_nothing_from_the_driver_passes_through(monkeypatch):
    monkeypatch.setattr(h, "load_driver", lambda: object())
    assert h.command_refusal("", "PPDS_COMPAT_REFUSAL=tool-hash") is None
    assert h.command_operation("", "PPDS_COMPAT_OPERATION=sign") is None
    assert h.command_status("", "PPDS_COMPAT_STATUS=0") is None
    assert h.command_diagnostic("", "PPDS_COMPAT_DIAGNOSTIC=inspector-sn-no-result") is None


def test_coordinator_maps_driver_refusals_through_the_driver_registry():
    driver = h.load_driver()
    assert h.refusal_code(driver.Error("Linked input/output refused")) == "driver-linked-path"
    assert h.refusal_code(driver.Error("Linked C:/PRIVATE refused")) == "driver-unregistered"


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
    obj.common_data = "C:/ProgramData"
    obj.binding, obj.context = h.admit_host(args(), environment()), {"test_double": True}
    obj.tools = {"sn": {"path": "fake-never-invoked-sn"}, "pwsh": {"path": "fake-never-invoked-pwsh"}}
    events = []

    def execute(command, **kwargs):
        kind = "generate" if "-Output" in command else "export"
        events.append(kind)
        if kind == "generate":
            Path(command[-1]).write_bytes(b"synthetic-not-a-key")
        if kind == fail:
            raise ValueError("injected process failure")
        if kind == "export":
            Path(command[-1]).write_bytes(b"synthetic-public-only")

    monkeypatch.setattr(h, "execute", execute)
    obj.d = SimpleNamespace(pin=lambda *a: None, environment=lambda *a, **kw: {},
                            write=lambda p, raw: Path(p).write_bytes(raw),
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


def test_disposable_generator_is_ephemeral_and_does_not_change_csp_policy():
    assert 'CreateEphemeralKey' in h.DISPOSABLE_KEYGEN
    assert 'KeyNumber = 2' in h.DISPOSABLE_KEYGEN
    assert 'PersistKeyInCsp = $false' in h.DISPOSABLE_KEYGEN
    assert 'FileMode]::CreateNew' in h.DISPOSABLE_KEYGEN
    assert '$rsa.Dispose()' in h.DISPOSABLE_KEYGEN and '[Array]::Clear' in h.DISPOSABLE_KEYGEN
    for forbidden in ('KeyContainerName', 'UseExistingKey', 'UseMachineKeyStore', 'Set-ItemProperty', 'GetEnvironmentVariable'):
        assert forbidden not in h.DISPOSABLE_KEYGEN


@pytest.mark.parametrize('number,expected', [('-2146893813', -2146893813), ('9999999999', None), ('PRIVATE', None)])
def test_generator_hresult_is_numeric_and_never_copies_private_output(monkeypatch, tmp_path, number, expected):
    monkeypatch.setattr(h.subprocess, 'run', lambda *a, **kw: SimpleNamespace(returncode=1,
        stdout='PRIVATE_KEY_OUTPUT', stderr='Disposable key generation failed HRESULT=' + number + '\nPRIVATE_ERROR'))
    with pytest.raises(h.StageCommandError) as failure:
        h.execute(['PRIVATE_COMMAND'], cwd=tmp_path, env={}, timeout=1)
    assert failure.value.hresult == expected
    assert 'PRIVATE' not in str(failure.value)


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
def test_verify_only_publishes_complete_bound_real_evidence(tmp_path, fault, capsys):
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
    phases = lambda: [json.loads(line)["hosted_compatibility_phase"] for line in capsys.readouterr().out.splitlines()]
    expected = ["verify-plan-download-completed", "verify-prepare-download-completed", "verify-sign-download-completed",
                "verify-plan-validated", "verify-custody-validated", "verify-context-validated",
                "verify-driver-completed", "verify-cases-validated", "verify-evidence-uploaded"]
    if fault:
        with pytest.raises((ValueError, pure_driver.Error)):
            obj.verify()
        assert uploaded == []
        # The last marker names the phase that completed before the refusal.
        stop = {"cleanup": "verify-plan-validated", "plan": "verify-plan-validated",
                "job": "verify-custody-validated", "mocked": "verify-driver-completed",
                "missing-case": "verify-driver-completed"}[fault]
        assert phases() == expected[:expected.index(stop) + 1]
    else:
        obj.verify()
        assert uploaded == ["evidence"]
        assert phases() == expected


# Exactly the environment every coordinator-launched driver process had before NuGet folders were
# added for build children (commit 1524e4192). The sign driver handles the disposable key.
DRIVER_LAUNCH_ENVIRONMENT = {"SYSTEMROOT", "WINDIR", "TEMP", "TMP", "ProgramFiles(x86)", "PROGRAMDATA",
                             "ALLUSERSPROFILE", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "DOTNET_CLI_TELEMETRY_OPTOUT",
                             "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
                             "DOTNET_GENERATE_ASPNET_CERTIFICATE", "PATH"}


@pytest.mark.parametrize("stage,extras", [
    ("sign", ["--prepared", "C:/fixture/prepared", "--disposable-key", "C:/fixture/never-read.snk",
              "--key-use-authorization", "offline-only"]),
    ("prepare", ["--source-root", "C:/fixture/source", "--public-key", "C:/fixture/public.bin"]),
    ("verify", ["--source-root", "C:/fixture/source", "--prepared", "C:/fixture/p", "--signed", "C:/fixture/s"]),
])
def test_driver_launch_environment_is_exact_and_has_no_nuget_folders(tmp_path, monkeypatch, stage, extras):
    obj = h.Hosted.__new__(h.Hosted)
    obj.root, obj.d = tmp_path, h.load_driver()
    obj.plan_directory, obj.plan_hash = tmp_path / "plan", "c" * 64
    obj.plan_value = {"windows_directory": "C:/Windows", "windows_common_data_directory": "C:/ProgramData",
                      "tools": {name: {"path": f"C:/fixture/{name}/{name}.exe"} for name in ("git", "dotnet", "pwsh")}
                      | {"sn": {"path": obj.d.FIXED_SIGNER}}}
    launched = []
    monkeypatch.setattr(h, "execute", lambda command, **kwargs: launched.append((command, kwargs)))
    obj.driver(stage, tmp_path / "out", *extras)
    (command, kwargs), = launched
    assert command[:5] == [h.sys.executable, "-I", "-B", str(h.DRIVER), stage]
    assert set(kwargs["env"]) == DRIVER_LAUNCH_ENVIRONMENT
    assert not {"APPDATA", "LOCALAPPDATA", "DOTNET_CLI_UI_LANGUAGE", "ACTIONS_RUNTIME_TOKEN"} & set(kwargs["env"])
    assert kwargs["env"]["TEMP"] == str(tmp_path / "temp")


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
                            stdin=subprocess.DEVNULL, capture_output=True, text=True,
                            timeout=PWSH_HANG_BOUND_SECONDS, check=False)
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
                            env=env, stdin=subprocess.DEVNULL, capture_output=True, text=True,
                            timeout=PWSH_HANG_BOUND_SECONDS, check=False)
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
        def keyless_diagnostics(self):
            pass
        def __getattr__(self, name):
            pytest.fail("Keyless bootstrap entered stage " + name)
    monkeypatch.setattr(h, "Hosted", KeylessContext)
    assert h.main(["--stage", stage, "--source", "a" * 40, "--experiment", "offline", "--authorization", "offline-only",
                   "--admit-network-connected-disposable-signing", "--bootstrap-only"]) == 0
    assert "keyless-bootstrap-completed-no-stage-called" in capsys.readouterr().out


def test_keyless_public_diagnostics_never_generate_keys_or_inherit_tokens(tmp_path, monkeypatch, capsys):
    obj = h.Hosted.__new__(h.Hosted)
    obj.args, obj.root = args(), tmp_path
    obj.common_data = 'C:/ProgramData'
    obj.env = {'SYSTEMROOT': 'C:/Windows', 'APPDATA': 'C:/Public/Roaming',
               'PROGRAMDATA': 'C:/ProgramData', 'ALLUSERSPROFILE': 'C:/ProgramData',
               'ACTIONS_RUNTIME_TOKEN': 'PRIVATE_TOKEN', 'UNRELATED': 'PRIVATE_VALUE'}
    obj.tools = {'sn': {'path': 'public-sn.exe', 'sha256': 'a'*64, 'version': '4.8'},
                 'pwsh': {'path': 'public-pwsh.exe', 'sha256': 'b'*64, 'version': '7'}}
    obj.d = SimpleNamespace(environment=lambda *a: {'SYSTEMROOT': 'C:/Windows'},
        write=lambda p, raw: p.write_bytes(raw), local_path=lambda p: None,
        record=lambda p: {'sha256': 'b'*64, 'bytes': 1}, pin=lambda *a: None)
    commands = []
    def execute(command, *, env, **kw):
        commands.append(command)
        assert set(env) <= {'SYSTEMROOT', 'APPDATA', 'PROGRAMDATA', 'ALLUSERSPROFILE'}
        if 'APPDATA' in env:
            assert env['PROGRAMDATA'] == env['ALLUSERSPROFILE'] == 'C:/ProgramData'
        assert 'PRIVATE' not in str(env)
        if command[0] == 'public-sn.exe':
            assert command == ['public-sn.exe', '-m']
            return 'Keys stored in machine key store'
        assert command[1:4] == ['-NoProfile', '-NonInteractive', '-File']
        return '{"process_bits":32,"ApplicationData":""}'
    monkeypatch.setattr(h, 'execute', execute)
    obj.keyless_diagnostics()
    assert len(commands) == 4
    assert 'PRIVATE' not in capsys.readouterr().out
    assert all(token not in h.PUBLIC_ENV_PROBE for token in ['RSACryptoServiceProvider', 'CryptAcquire', 'CryptGen', 'Set-Item', 'SetEnvironmentVariable'])


@pytest.mark.parametrize('data,alias', [(None, 'C:/ProgramData'), ('C:/ProgramData', None),
    ('relative', 'relative'), ('C:/ProgramData', 'D:/ProgramData'), ('C:/x/../ProgramData', 'C:/x/../ProgramData')])
def test_common_data_context_refuses_missing_or_inconsistent_paths(data, alias):
    driver = h.load_driver()
    with pytest.raises((ValueError, driver.Error)):
        h.common_data_directory({'PROGRAMDATA': data, 'ALLUSERSPROFILE': alias}, driver)


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
