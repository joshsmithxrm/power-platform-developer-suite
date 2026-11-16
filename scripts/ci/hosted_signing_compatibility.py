#!/usr/bin/env python3
"""Opt-in, one-attempt hosted orchestration for the unchanged COMPAT driver.

This coordinator trusts reviewed source/bootstrap and the hosted administrator.
It does not claim independent rebuilding, an air gap, or secure media erasure.
Default invocation refuses before environment, filesystem or process access.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
DRIVER = ROOT / "tests/compatibility/plugin_signing_compatibility.py"
REPOSITORY = "joshsmithxrm/power-platform-developer-suite"
PLAN_FILES = {"plan.json", "public.bin", "context.json"}
PRODUCT_PROBE = 'param([string]$Path)\n[Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion\n'
PUBLIC_ENV_PROBE = r'''$ErrorActionPreference = 'Stop'
$result = [ordered]@{ process_bits = [IntPtr]::Size * 8; fips = [Security.Cryptography.CryptoConfig]::AllowOnlyFipsAlgorithms }
foreach ($name in @('ApplicationData', 'LocalApplicationData', 'CommonApplicationData', 'UserProfile', 'System')) {
    $result[$name] = [Environment]::GetFolderPath([Environment+SpecialFolder]::$name)
}
$result | ConvertTo-Json -Compress
'''
WINDOWS_FOLDER_ENV = ('APPDATA', 'LOCALAPPDATA', 'ProgramData', 'USERPROFILE', 'HOMEDRIVE', 'HOMEPATH')


def checkpoint(phase):
    # Call sites supply fixed literals only. Never print commands, paths,
    # exception messages, process output, environment values or key material.
    print(json.dumps({"hosted_compatibility_phase": phase}), flush=True)


class StageCommandError(ValueError):
    def __init__(self, code, refusal=None):
        super().__init__("Hosted stage command failed")
        self.code = code
        self.refusal = refusal


def command_refusal(stdout, stderr):
    # Emit only a fixed classification, never any captured private-phase text.
    text = (stdout + "\n" + stderr).lower()
    known = (("access is denied", "access-denied"),
             ("keyset is not defined", "keyset-undefined"),
             ("keyset does not exist", "keyset-missing"),
             ("the system cannot find the file specified", "file-not-found"),
             ("invalid command line", "command-line"),
             ("invalid option", "command-line"),
             ("failed to generate a strong name key pair", "key-generation"))
    return next((label for phrase, label in known if phrase in text), None)


def require(ok, reason):
    if not ok:
        raise ValueError(reason)


def authorize(args):
    # Pure admission first. A reference records authority; it does not grant it.
    require(args.stage in {"sign", "prepare", "verify"} and args.admit_network_connected_disposable_signing,
            "Explicit hosted-network disposable-key admission required")
    require(bool(re.fullmatch(r"[0-9a-f]{40}", args.source or "")), "Exact source required")
    require(bool(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,39}", args.experiment or "")), "Experiment required")
    require(bool(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/# -]{2,199}", args.authorization or "")),
            "Applicable execution authority reference required")


def admit_host(args, env):
    expected = {"GITHUB_REPOSITORY": REPOSITORY, "GITHUB_EVENT_NAME": "workflow_dispatch",
                "GITHUB_RUN_ATTEMPT": "1", "GITHUB_SHA": args.source,
                "RUNNER_ENVIRONMENT": "github-hosted", "RUNNER_OS": "Windows", "RUNNER_ARCH": "X64"}
    require(all(env.get(k) == v for k, v in expected.items()), "Fresh hosted manual first attempt required")
    require(bool(re.fullmatch(r"[1-9][0-9]{0,19}", env.get("GITHUB_RUN_ID", ""))), "Run identity required")
    require(env.get("ACTIONS_RUNTIME_TOKEN"), "Current-run artifact action context required")
    return {"repository": REPOSITORY, "run_id": env["GITHUB_RUN_ID"], "run_attempt": "1",
            "source_sha": args.source, "experiment_id": args.experiment}


def load_driver():
    spec = importlib.util.spec_from_file_location("ppds_hosted_compat_driver", DRIVER)
    driver = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = driver
    spec.loader.exec_module(driver)
    return driver


def execute(command, *, cwd, env, timeout):
    # Never propagate command/diagnostics: the same path handles private phases.
    result = subprocess.run(command, cwd=cwd, env=env, timeout=timeout,
                            capture_output=True, text=True, shell=False, check=False)
    if result.returncode != 0:
        raise StageCommandError(result.returncode, command_refusal(result.stdout, result.stderr))
    return result.stdout.strip()


def json_write(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, sort_keys=True, indent=2)
        stream.write("\n")


def stage_files(stage, version):
    if stage == "plan":
        return PLAN_FILES
    if stage == "prepare":
        return {"prepare-receipt.json", "prepare-context.json", "test-public.bin", "delay.dll", "public.dll", "public.pdb",
                "PPDS.Plugins.pdb", f"PPDS.Plugins.{version}.nupkg", f"PPDS.Plugins.{version}.snupkg"}
    if stage == "sign":
        return {"sign-receipt.json", "input.dll", "signed.dll", "custody.json"}
    require(stage == "evidence", "Unexpected public stage")
    return {"compatibility-receipt.json", "hosted-receipt.json"}


def cleanup_original(path):
    # Exact owned file only, never parent/recursive cleanup. Also covers partial -k.
    path.unlink(missing_ok=True)
    require(not path.exists() and not path.is_symlink(), "Original key cleanup unproven")


class Hosted:
    def __init__(self, args, env, driver):
        self.args, self.env, self.d = args, dict(env), driver
        self.binding = admit_host(args, env)
        self.version = "0.0.0-compat." + self.binding["run_id"]
        require(sys.platform == "win32" and Path(env["GITHUB_WORKSPACE"]).resolve() == ROOT.resolve(),
                "Reviewed Windows checkout required")
        self.root = Path(env["RUNNER_TEMP"]) / ("ppds-compat-" + args.stage)
        driver.local_path(self.root)
        require(not self.root.resolve().is_relative_to(ROOT.resolve()), "External stage root required")
        self.root.mkdir()  # Refuse reuse, including failed attempts.
        (self.root / "temp").mkdir()
        self.node = Path(env["PPDS_ARTIFACT_NODE"])
        self.adapter = Path(env["PPDS_ARTIFACT_ADAPTER"])
        driver.local_path(self.node)
        driver.local_path(self.adapter)
        require(self.node.is_absolute() and self.adapter.is_absolute()
                and not self.adapter.resolve().is_relative_to(ROOT.resolve()), "External trusted adapter required")
        self.bootstrap()

    def bootstrap(self):
        d = self.d
        probe = self.root / "product-version.ps1"
        d.write(probe, PRODUCT_PROBE.encode())
        paths = {name: shutil.which(name + ".exe") for name in ("git", "dotnet", "pwsh")}
        paths["sn"] = d.FIXED_SIGNER
        checkpoint("bootstrap-tool-discovery")
        require(all(paths.values()), "Required tool unavailable")
        for path in paths.values():
            d.local_path(Path(path))
        # The only inherited values needed before public tool admission.
        public_env = {k: self.env[k] for k in ("SYSTEMROOT", "WINDIR", "TEMP", "TMP", "PATH") if k in self.env}
        public_env.update(DOTNET_GENERATE_ASPNET_CERTIFICATE="false", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
        self.tools = {}
        for name, path in paths.items():
            checkpoint("bootstrap-tool-" + name + "-start")
            self.tools[name] = {"path": str(Path(path)), "sha256": d.digest(d.read(path, 256 * 1024**2)),
                                "version": execute([paths["pwsh"], "-NoProfile", "-File", str(probe), path],
                                                   cwd=self.root, env=public_env, timeout=120)}
        checkpoint("bootstrap-tool-identities-verified")
        def git(*args):
            return execute([paths["git"], "-C", str(ROOT), *args], cwd=self.root, env=public_env, timeout=120)
        require(git("rev-parse", "HEAD") == self.args.source and not git("status", "--porcelain"),
                "Clean exact reviewed source required")
        self.tree = git("rev-parse", "HEAD^{tree}")
        checkpoint("bootstrap-source-verified")
        helper_names = ("cli.mjs", "transport.mjs", "package.json", "package-lock.json")
        self.pins = {str(self.node): d.record(self.node), str(Path(sys.executable)): d.record(Path(sys.executable)),
                     str(Path(__file__)): d.record(Path(__file__)), str(DRIVER): d.record(DRIVER)}
        for name in helper_names:
            staged = self.adapter.parent / name
            original = ROOT / "scripts/ci/hosted-signing-artifacts" / name
            require(d.read(staged) == d.read(original), "Staged transport source differs")
            self.pins[str(staged)] = d.record(staged)
        self.context = {**self.binding, "tree": self.tree, "job": self.env.get("GITHUB_JOB"),
                        "runner": self.env.get("RUNNER_NAME"), "image_version": self.env.get("ImageVersion"),
                        "tools": self.tools, "bootstrap_files": self.pins,
                        "authorization_reference": self.args.authorization,
                        "network_connected_disposable_signer_admitted": True}

    def transfer(self, operation, stage, directory, *, expected=None):
        d = self.d
        for path, pin in self.pins.items():
            require(d.record(Path(path)) == pin, "Trusted bootstrap bytes changed")
        binding = {**self.binding, "stage": stage}
        name = "ppds-compat-{run_id}-{run_attempt}-{source_sha}-{experiment_id}-{stage}".format(**binding)
        label = stage + "-" + operation
        bind_path, result = self.root / (label + "-binding.json"), self.root / (label + "-result.json")
        json_write(bind_path, binding)
        command = [str(self.node), str(self.adapter), operation, "--name", name,
                   "--binding", str(bind_path), "--result", str(result)]
        if operation == "upload":
            # Driver output includes source/build/temp trees. Only copy this
            # closed public inventory into a fresh transport root.
            public = self.root / ("upload-" + stage)
            public.mkdir()
            for name in sorted(stage_files(stage, self.version)):
                d.write(public / name, d.read(directory / name))
            inventory = self.root / (label + "-inventory.json")
            json_write(inventory, sorted(stage_files(stage, self.version)))
            command += ["--root", str(public), "--inventory", str(inventory), "--timeout-seconds", "180"]
            timeout = 180
        else:
            wait = 2700 if stage == "prepare" and self.args.stage == "sign" else 600
            command += ["--dest", str(directory), "--timeout-seconds", str(wait)]
            if expected:
                command += ["--expected-id", str(expected["id"]), "--expected-digest", expected["digest"]]
            timeout = wait + 120
        execute(command, cwd=self.root, env=self.env, timeout=timeout)
        value = json.loads(d.read(result, 65536))
        require(value["binding"] == binding and {row["path"] for row in value["inventory"]}
                == stage_files(stage, self.version), "Exact public handoff inventory required")
        if operation == "upload":
            artifact = value["artifact"]
            require(type(artifact["id"]) is int and artifact["id"] > 0
                    and re.fullmatch(r"sha256:[0-9a-f]{64}", artifact["digest"]), "Artifact identity missing")
            with Path(self.env["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as stream:
                stream.write(f"{stage}-id={artifact['id']}\n{stage}-digest={artifact['digest']}\n")
        return value["artifact"]

    def keyless_diagnostics(self):
        # Read-only public host metadata. Never call -k/-p/-R, acquire a key
        # container, change CSP/machine policy, or inherit tokens into a process.
        d = self.d
        minimal = d.environment({"tools": self.tools, "windows_directory": self.env["SYSTEMROOT"]}, self.root / "temp")
        folders = {**minimal, **{k: self.env[k] for k in WINDOWS_FOLDER_ENV if k in self.env}}
        probe = self.root / "public-environment.ps1"
        d.write(probe, PUBLIC_ENV_PROBE.encode())
        public_shell = Path(self.tools["pwsh"]["path"])
        print(json.dumps({"keyless_tool_identity": {k: {f: v[f] for f in ("sha256", "version")}
                                                   for k, v in self.tools.items()}}), flush=True)
        contexts = {}
        for label, environment in (("minimal", minimal), ("windows-folders", folders)):
            d.pin({"tools": self.tools}, ["pwsh"])
            checkpoint("keyless-public-folders-" + label)
            values = json.loads(execute([str(public_shell), "-NoProfile", "-NonInteractive", "-File", str(probe)],
                                        cwd=self.root, env=environment, timeout=120))
            # -m without a value only reads the setting. Never use -c (which
            # resets CSP configuration even without a value).
            d.pin({"tools": self.tools}, ["sn"])
            checkpoint("keyless-sn-policy-" + label)
            policy = execute([self.tools["sn"]["path"], "-m"], cwd=self.root, env=environment, timeout=120)
            contexts[label] = {"folders": values, "sn_machine_policy": policy[:2048]}
        print(json.dumps({"keyless_public_diagnostics": contexts,
                          "tools": {k: {f: v[f] for f in ("sha256", "version")} for k, v in self.tools.items()},
                          "source": self.args.source}), flush=True)

    def plan(self, directory):
        d = self.d
        raw = d.read(directory / "plan.json", 65536)
        self.plan_hash = d.digest(raw)
        self.plan_value = d.load_plan(directory / "plan.json", self.plan_hash)
        p = self.plan_value
        require(p["source"] == self.args.source and p["tree"] == self.tree and p["tools"] == self.tools
                and p["version"] == self.version and p["experiment"] == self.args.experiment,
                "Local tool/source identity differs from authoritative plan")
        public = d.read(directory / "public.bin", 4096)
        d.public_shape(public)
        require(d.digest(public) == p["public_key_sha256"], "Public identity differs")
        self.plan_directory = directory

    def driver(self, stage, output, *extras):
        d = self.d
        command = [sys.executable, "-I", "-B", str(DRIVER), stage, "--execute-candidate",
                   "--plan", str(self.plan_directory / "plan.json"), "--plan-sha256", self.plan_hash,
                   "--output", str(output), *map(str, extras)]
        # Candidate build/sign subprocesses receive no Actions runtime token.
        execute(command, cwd=self.root, env=d.environment(self.plan_value, self.root / "temp", build=True),
                timeout=2700 if stage == "prepare" else 600)

    def sign(self):
        d = self.d
        public = self.root / "public"
        public.mkdir()
        key = self.root / "original-disposable-key.snk"
        signed, prepared = self.root / "signed", self.root / "prepared"
        partial_plan = {"tools": self.tools, "windows_directory": self.env["SYSTEMROOT"]}
        # Ownership starts before -k; any partial file must be removed too.
        try:
            for options in (("-k", "1024", key), ("-p", key, public / "public.bin")):
                checkpoint("key-generation-start" if options[0] == "-k" else "public-key-export-start")
                d.pin(partial_plan, ["sn"])
                execute([self.tools["sn"]["path"], *map(str, options)], cwd=self.root,
                        env=d.environment(partial_plan, self.root / "temp"), timeout=120)
                checkpoint("key-generation-completed" if options[0] == "-k" else "public-key-export-completed")
            blob = d.read(public / "public.bin", 4096)
            d.public_shape(blob)
            plan = {"schema": 1, "kind": "ppds-signing-compatibility-plan", "experiment": self.args.experiment,
                    "source": self.args.source, "tree": self.tree, "version": self.version,
                    "public_key_sha256": d.digest(blob), **partial_plan}
            json_write(public / "plan.json", plan)
            json_write(public / "context.json", self.context)
            self.plan(public)  # Authoritative local bytes; never replaced by PREPARE.
            checkpoint("public-plan-validated")
            self.transfer("upload", "plan", public)
            checkpoint("public-plan-uploaded")
            self.transfer("download", "prepare", prepared)
            checkpoint("prepared-handoff-received")
            self.driver("sign", signed, "--prepared", prepared, "--disposable-key", key,
                        "--key-use-authorization", self.args.authorization)
            checkpoint("sign-driver-completed")
        finally:
            cleanup_original(key)
            checkpoint("original-key-cleanup-completed")
        receipt = json.loads(d.read(signed / "sign-receipt.json"))
        require(receipt["key_cleanup"] == "completed", "Driver-owned copy cleanup unproven")
        json_write(signed / "custody.json", {"schema": 1, "binding": self.binding, "plan_sha256": self.plan_hash,
                   "original_key_cleanup": "completed", "driver_copy_cleanup": "completed",
                   "context": self.context, "nonclaim": "File absence is not secure-media erasure; hosted provider trusted"})
        self.transfer("upload", "sign", signed)

    def prepare(self):
        public, prepared = self.root / "public", self.root / "prepared"
        self.transfer("download", "plan", public)
        self.plan(public)
        self.driver("prepare", prepared, "--source-root", ROOT, "--public-key", public / "public.bin")
        json_write(prepared / "prepare-context.json", self.context)
        self.transfer("upload", "prepare", prepared)

    def verify(self):
        inputs = {}
        for stage in ("plan", "prepare", "sign"):
            expected = {"id": self.env.get("PPDS_" + stage.upper() + "_ID", ""),
                        "digest": self.env.get("PPDS_" + stage.upper() + "_DIGEST", "")}
            require(re.fullmatch(r"[1-9][0-9]*", expected["id"])
                    and re.fullmatch(r"sha256:[0-9a-f]{64}", expected["digest"]), "Completed job artifact binding required")
            inputs[stage] = self.root / stage
            self.transfer("download", stage, inputs[stage], expected=expected)
        self.plan(inputs["plan"])
        custody = json.loads(self.d.read(inputs["sign"] / "custody.json", 65536))
        require(custody["binding"] == self.binding and custody["plan_sha256"] == self.plan_hash
                and custody["original_key_cleanup"] == custody["driver_copy_cleanup"] == "completed",
                "Original/copy key custody evidence required")
        prepared_context = json.loads(self.d.read(inputs["prepare"] / "prepare-context.json", 65536))
        plan_context = json.loads(self.d.read(inputs["plan"] / "context.json", 65536))
        for context, job in ((plan_context, "sign"), (custody["context"], "sign"),
                             (prepared_context, "prepare"), (self.context, "verify")):
            require(all(context.get(key) == value for key, value in self.binding.items())
                    and context.get("tree") == self.tree and context.get("tools") == self.tools
                    and context.get("job") == job and context.get("runner")
                    and context.get("network_connected_disposable_signer_admitted") is True,
                    "Hosted stage context binding differs")
        output = self.root / "verified"
        self.driver("verify", output, "--source-root", ROOT, "--prepared", inputs["prepare"], "--signed", inputs["sign"])
        receipt = json.loads(self.d.read(output / "compatibility-receipt.json"))
        self.d.complete_cases(receipt["cases"], receipt["evidence_class"])
        json_write(output / "hosted-receipt.json", {"schema": 1, "result": "PASS", "binding": self.binding,
                   "plan_sha256": self.plan_hash, "context": self.context, "prepare_context": prepared_context,
                   "custody": custody,
                   "compatibility_receipt_sha256": self.d.digest(self.d.read(output / "compatibility-receipt.json")),
                   "nonclaims": ["Not production signing/release authorization", "Not an independently administered rebuild",
                                 "Hosted networking/provider remnants trusted; no air-gap or secure-erasure claim"]})
        self.transfer("upload", "evidence", output)


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--stage", choices=("sign", "prepare", "verify"))
    result.add_argument("--source")
    result.add_argument("--experiment")
    result.add_argument("--authorization")
    result.add_argument("--admit-network-connected-disposable-signing", action="store_true")
    result.add_argument("--bootstrap-only", action="store_true")
    return result


def main(argv=None):
    try:
        args = parser().parse_args(argv)
        authorize(args)
        admit_host(args, os.environ)
        checkpoint("bootstrap-start")
        context = Hosted(args, os.environ, load_driver())
        checkpoint("bootstrap-completed")
        if args.bootstrap_only:
            context.keyless_diagnostics()
            checkpoint("keyless-bootstrap-completed-no-stage-called")
            return 0
        getattr(context, args.stage)()
        print("Hosted compatibility stage completed; no release authorization.")
        return 0
    except Exception as ex:
        category = "command" if isinstance(ex, StageCommandError) else "filesystem" if isinstance(ex, OSError) else "validation"
        diagnostic = {"hosted_compatibility_failure": category}
        safe_refusals = {"Fresh hosted manual first attempt required": "host-admission",
                         "Current-run artifact action context required": "runtime-context",
                         "Reviewed Windows checkout required": "checkout",
                         "Linked input/output refused": "linked-path",
                         "Required tool unavailable": "missing-tool",
                         "Clean exact reviewed source required": "source-state",
                         "Tool hash/version required": "tool-identity",
                         "Original key cleanup unproven": "key-cleanup"}
        if str(ex) in safe_refusals:
            diagnostic["refusal"] = safe_refusals[str(ex)]
        if isinstance(ex, StageCommandError):
            diagnostic["exit_code"] = ex.code
            if ex.refusal:
                diagnostic["command_refusal"] = ex.refusal
        print(json.dumps(diagnostic), file=sys.stderr, flush=True)
        print("Hosted compatibility refused or failed; no passing claim. Private diagnostics omitted.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
