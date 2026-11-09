"""Candidate-only native/VSIX validation. No tag, publisher, auth or provider route."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shutil
import stat
import subprocess
import sys
import threading
import xml.etree.ElementTree as ET
import zipfile

import verify_public_release as public

ROOT = Path(__file__).resolve().parents[2]
TARGETS = {"win32-x64": "win-x64", "linux-x64": "linux-x64", "darwin-x64": "osx-x64", "darwin-arm64": "osx-arm64"}
RIDS = set(TARGETS.values()) | {"win-arm64"}
IMAGE = "registry.access.redhat.com/ubi8/dotnet-80-runtime:latest"
OS_KEYS = {"PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "SYSTEMDRIVE", "PROGRAMFILES",
           "PROGRAMFILES(X86)", "PROGRAMDATA", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def plain(path):
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and not path.is_symlink() and
            not getattr(info, "st_file_attributes", 0) & 0x400, f"Not a plain input: {path}")
    return path


def digest(path):
    with plain(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def compiled_identity(assembly_info, runtime_config, deps, source, rid):
    require(re.fullmatch(r"[0-9a-f]{40}", source) and rid in RIDS, "Invalid source/RID")
    matches = re.findall(r'AssemblyInformationalVersionAttribute\("([^"\r\n]+)"\)', assembly_info)
    require(len(matches) == 1 and public.SEMVER_PATTERN.fullmatch(matches[0]), "Invalid compiled MinVer identity")
    version = matches[0]
    require(version.endswith("+" + source), "Compiled source differs from checkout")
    options = runtime_config.get("runtimeOptions", {})
    frameworks = [f for f in options.get("includedFrameworks", []) if f.get("name") == "Microsoft.NETCore.App"]
    require(options.get("tfm") == "net10.0" and len(frameworks) == 1, "Not a self-contained net10 runtime")
    runtime_version = frameworks[0].get("version", "")
    require(re.fullmatch(r"10\.0\.[0-9]+", runtime_version), "Unexpected runtime pack version")
    require(int(runtime_version.rsplit(".", 1)[1]) >= 14, "Runtime pack must meet the 10.0.14 security baseline")
    require(deps.get("runtimeTarget", {}).get("name") == f".NETCoreApp,Version=v10.0/{rid}", "Wrong runtime target")
    pack = f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}/{runtime_version}"
    require(pack in deps.get("libraries", {}), "Runtime pack provenance missing")
    return {"candidateOnly": True, "source": source, "rid": rid, "targetFramework": "net10.0",
            "runtimePackVersion": runtime_version, "informationalVersion": version,
            "releaseVersion": public.release_version(version)}


def identity(source, rid):
    paths = [ROOT / f"src/PPDS.Cli/obj/Release/net10.0/{rid}/PPDS.Cli.AssemblyInfo.cs",
             ROOT / f"src/PPDS.Cli/bin/Release/net10.0/{rid}/ppds.runtimeconfig.json",
             ROOT / f"src/PPDS.Cli/bin/Release/net10.0/{rid}/ppds.deps.json"]
    value = compiled_identity(plain(paths[0]).read_text(encoding="utf-8-sig"),
                              json.loads(plain(paths[1]).read_text()), json.loads(plain(paths[2]).read_text()), source, rid)
    value["buildInputSha256"] = {str(p.relative_to(ROOT)): digest(p) for p in paths}
    return value


def retain_compiled_metadata(value, evidence):
    pending = []
    for name in ["ppds.deps.json", "ppds.runtimeconfig.json"]:
        source = ROOT / f"src/PPDS.Cli/bin/Release/net10.0/{value['rid']}/{name}"
        source_key = str(source.relative_to(ROOT))
        expected = value["buildInputSha256"][source_key]
        data = plain(source).read_bytes()
        require(hashlib.sha256(data).hexdigest() == expected, "Compiled metadata changed before retention")
        destination = evidence / name
        require(not destination.exists() and not destination.is_symlink(), "Refuse existing compiled metadata evidence")
        pending.append((name, source_key, expected, data, destination))
    retained = {}
    for name, source_key, expected, data, destination in pending:
        with destination.open("xb") as stream:
            stream.write(data)
        require(digest(destination) == expected, "Compiled metadata evidence copy hash mismatch")
        retained[name] = {"fileName": name, "sourcePath": source_key, "sha256": expected}
    return retained


def bundle_manifest(value):
    return {"schemaVersion": 1, "rid": value["rid"], "releaseVersion": value["releaseVersion"],
            "informationalVersion": value["informationalVersion"]}


def private_environment(root):
    require(not root.exists() and not root.is_symlink(), "Refuse existing runtime directory")
    root.mkdir(mode=0o700)
    names = ["home", "appdata", "localappdata", "config", "data", "temp", "docker"]
    for name in names:
        (root / name).mkdir(mode=0o700)
    env = {k: v for k, v in os.environ.items() if k.upper() in OS_KEYS}
    env.update({"HOME": str(root / "home"), "USERPROFILE": str(root / "home"),
                "APPDATA": str(root / "appdata"), "LOCALAPPDATA": str(root / "localappdata"),
                "XDG_CONFIG_HOME": str(root / "config"), "XDG_DATA_HOME": str(root / "data"),
                "PPDS_CONFIG_DIR": str(root / "config"), "TEMP": str(root / "temp"),
                "TMP": str(root / "temp"), "TMPDIR": str(root / "temp"),
                "DOTNET_CLI_HOME": str(root / "home"), "DOCKER_CONFIG": str(root / "docker"),
                "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(root / "temp/bundle"),
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1", "DOTNET_NOLOGO": "1"})
    return env


def run(command, env, evidence, label, timeout=120, cwd=None):
    result = subprocess.run(command, env=env, cwd=cwd or evidence, stdin=subprocess.DEVNULL,
                            capture_output=True, timeout=timeout, check=False,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    (evidence / f"{label}.stdout.log").write_bytes(result.stdout)
    (evidence / f"{label}.stderr.log").write_bytes(result.stderr)
    require(result.returncode == 0, f"{label} failed (exit {result.returncode}); retained raw logs")
    return result.stdout.decode("utf-8")


def read_frame(stream):
    header = bytearray()
    while not header.endswith(b"\r\n\r\n"):
        part = stream.read(1)
        require(part and len(header) < 4096, "Invalid/oversized RPC header")
        header.extend(part)
    lengths = re.findall(rb"(?im)^Content-Length: ([0-9]+)\r?$", bytes(header[:-4]))
    require(len(lengths) == 1 and 0 < int(lengths[0]) <= 65536, "Invalid RPC content length")
    remaining, body = int(lengths[0]), bytearray()
    while remaining:
        part = stream.read(remaining)
        require(part, "Truncated RPC body")
        body.extend(part)
        remaining -= len(part)
    return json.loads(body)


def validate_heartbeat(reply):
    require(reply.get("jsonrpc") == "2.0" and reply.get("id") == 1 and "error" not in reply, "Invalid heartbeat reply")
    result = reply.get("result", {})
    require(result.get("ok") is True and type(result.get("heartbeatCount")) is int and result["heartbeatCount"] == 1 and
            type(result.get("uptimeSeconds")) is int and result["uptimeSeconds"] >= 0 and
            all(isinstance(result.get(k), str) and result[k].strip() for k in
                ["correlationId", "startedAt", "daemonVersion"]), "Invalid heartbeat payload")
    return result


def heartbeat(binary, env, evidence):
    # Only this owned process may be terminated at a deadline. No profile/auth RPC is sent.
    process = subprocess.Popen([str(binary), "serve", "--quiet"], env=env, cwd=evidence,
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    replies, errors = queue.Queue(), []
    def receive():
        try:
            replies.put(read_frame(process.stdout))
        except Exception as error:
            replies.put(error)
    reader = threading.Thread(target=receive, daemon=True)
    stderr = threading.Thread(target=lambda: errors.append(process.stderr.read(65537)), daemon=True)
    reader.start()
    stderr.start()
    try:
        payload = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "_heartbeat", "params": []}).encode()
        process.stdin.write(f"Content-Length: {len(payload)}\r\n\r\n".encode() + payload)
        process.stdin.flush()
        reply = replies.get(timeout=20)
        if isinstance(reply, Exception):
            raise reply
        result = validate_heartbeat(reply)
        process.stdin.close()
        require(process.wait(timeout=10) == 0, "Daemon did not shut down cleanly on stdin EOF")
        stderr.join(timeout=2)
        require(errors and len(errors[0]) <= 65536, "Missing/oversized daemon stderr")
        require(not process.stdout.read(1), "Unexpected extra RPC output")
        save(evidence / "heartbeat.json", {"ownedPid": process.pid, "transport": "private stdio", "response": result})
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        stderr.join(timeout=2)
        (evidence / "heartbeat.stderr.log").write_bytes(errors[0] if errors else b"")
        for stream in [process.stdin, process.stdout, process.stderr]:
            if stream and not stream.closed:
                stream.close()


def verify_vsix(path, target, value, binary, destination):
    require(TARGETS.get(target) == value["rid"], "VSIX target/RID mismatch")
    package = json.loads((ROOT / "src/PPDS.Extension/package.json").read_text())
    name = "ppds.exe" if target.startswith("win32-") else "ppds"
    with zipfile.ZipFile(plain(path)) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), "Duplicate VSIX entries")
        packaged = json.loads(archive.read("extension/package.json"))
        require(all(packaged.get(k) == package.get(k) for k in ["name", "publisher", "version"]), "Candidate extension identity changed")
        xml = ET.fromstring(archive.read("extension.vsixmanifest"))
        identities = [n for n in xml.iter() if n.tag.rsplit("}", 1)[-1] == "Identity"]
        require(len(identities) == 1 and identities[0].get("TargetPlatform") == target and
                all(identities[0].get(field) == package[key] for field, key in
                    [("Id", "name"), ("Publisher", "publisher"), ("Version", "version")]), "Wrong VSIX XML identity/platform")
        require(json.loads(archive.read("extension/bin/ppds.version.json")) == bundle_manifest(value), "Wrong bundled CLI identity")
        info = archive.getinfo(f"extension/bin/{name}")
        require(0 < info.file_size <= 512 * 1024 * 1024, "Invalid bundled binary size")
        destination.mkdir(mode=0o700)
        output = destination / name
        with archive.open(info) as source, output.open("xb") as stream:
            shutil.copyfileobj(source, stream)
        output.chmod(0o700)
    require(digest(output) == digest(binary), "Archived binary differs from compiled bundle")
    return output


def docker_command(binary, fixture, script):
    paths = [p.resolve(strict=True) for p in [binary, fixture, script]]
    require(all("," not in str(p) and "\n" not in str(p) for p in paths), "Unsupported bind path")
    return ["docker", "run", "--rm", "--pull=never", "--network=none", "--read-only", "--cap-drop=ALL",
            "--security-opt=no-new-privileges", "--user=10001:10001", "--tmpfs=/tmp:rw,nosuid,nodev,mode=1777",
            "--mount", f"type=bind,src={paths[0]},dst=/native/ppds,readonly",
            "--mount", f"type=bind,src={paths[1]},dst=/fixture,readonly",
            "--mount", f"type=bind,src={paths[2]},dst=/check.sh,readonly", "--entrypoint=/bin/bash", IMAGE, "/check.sh"]


def validate_extraction(envelope):
    require(envelope.get("success") is True, "Container extraction failed")
    assemblies = envelope.get("data", {}).get("assemblies", [])
    require(len(assemblies) == 1 and assemblies[0].get("name") == "SingleFilePlugin" and
            assemblies[0].get("type") == "Assembly" and
            assemblies[0].get("allTypeNames") == ["SingleFileFixture.AccountPlugin"], "Wrong extracted fixture")
    types = assemblies[0].get("types", [])
    require(len(types) == 1 and len(types[0].get("steps", [])) == 1 and
            types[0]["steps"][0].get("entity") == "account" and types[0]["steps"][0].get("message") == "Update", "Missing registration metadata")
    step = types[0]["steps"][0]
    require(all(step.get(k) == v for k, v in {"stage": "PreOperation", "mode": "Synchronous",
            "executionOrder": 7, "filteringAttributes": "name,telephone1"}.items()), "Wrong fixture step")
    images = step.get("images", [])
    require(len(images) == 1 and all(images[0].get(k) == v for k, v in
            {"name": "Before", "imageType": "PreImage", "attributes": "name,telephone1"}.items()), "Wrong fixture image")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["manifest", "native", "vsix", "glibc"])
    parser.add_argument("--rid", required=True, choices=sorted(RIDS))
    parser.add_argument("--source", required=True)
    parser.add_argument("--binary", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--target", choices=sorted(TARGETS))
    parser.add_argument("--vsix", type=Path)
    args = parser.parse_args()
    evidence = args.evidence.absolute()
    require(os.getenv("GITHUB_ACTIONS") == "true" and os.getenv("RUNNER_ENVIRONMENT") == "github-hosted",
            "Artifact commands are restricted to reviewed ephemeral GitHub-hosted jobs")
    runner_temp = Path(os.environ["RUNNER_TEMP"]).resolve(strict=True)
    require(evidence.resolve(strict=False).is_relative_to(runner_temp) and evidence != runner_temp,
            "Evidence must be a fresh child of the ephemeral runner TEMP")
    require(not evidence.exists() and not evidence.is_symlink(), "Refuse existing evidence")
    evidence.mkdir(mode=0o700, parents=True)
    try:
        value = identity(args.source, args.rid)
        value["compiledMetadataEvidence"] = retain_compiled_metadata(value, evidence)
        binary = plain(args.binary.absolute())
        value["binarySha256"] = digest(binary)
        env = private_environment(evidence / "runtime")
        sdk = run(["dotnet", "--version"], env, evidence, "selected-sdk", 30, cwd=ROOT).strip()
        require(re.fullmatch(r"10\.0\.1[0-9]{2}", sdk), "SDK differs from global.json's existing feature band")
        value["selectedSdkVersionAtValidation"] = sdk
        if args.mode == "manifest":
            save(binary.parent / "ppds.version.json", bundle_manifest(value))
        elif args.mode == "glibc":
            require(sys.platform == "linux" and args.rid == "linux-x64" and os.getenv("GITHUB_ACTIONS") == "true", "Public image pull is CI-only")
            run(["docker", "pull", IMAGE], env, evidence, "image-pull", 180)
            value["containerImage"] = run(["docker", "image", "inspect", "--format", "{{json .RepoDigests}}", IMAGE], env, evidence, "image-digest", 15).strip()
            output = run(docker_command(binary, ROOT / "tests/fixtures/SingleFilePlugin/bin/Release/net462",
                                        ROOT / "scripts/ci/check_native_glibc.sh"), env, evidence, "glibc", 120)
            validate_extraction(json.loads(output))
            diagnostic = (evidence / "glibc.stderr.log").read_text()
            require("PPDS_GLIBC=glibc 2.28" in diagnostic, "Wrong ABI fixture")
            versions = re.findall(r"^PPDS_NATIVE_VERSION=(.+)$", diagnostic, re.MULTILINE)
            require(len(versions) == 1 and public.versions_match(public.extract_cli_version(versions[0]),
                                                               value["informationalVersion"]), "Wrong container binary version")
            value["containerExecutedVersion"] = public.extract_cli_version(versions[0])
        else:
            if args.mode == "vsix":
                require(args.vsix is not None and args.target is not None, "VSIX inputs required")
                binary = verify_vsix(args.vsix, args.target, value, binary, evidence / "archived-binary")
                value["vsixSha256"] = digest(args.vsix)
                package = json.loads((ROOT / "src/PPDS.Extension/package.json").read_text())
                value.update({"vsixTarget": args.target, "extensionVersion": package["version"],
                              "extensionIdentity": f"{package['publisher']}.{package['name']}"})
            else:
                isolated = evidence / "binary-only"
                isolated.mkdir(mode=0o700)
                shutil.copy2(binary, isolated / binary.name)
                binary = isolated / binary.name
                require(digest(binary) == value["binarySha256"], "Copied native bytes changed")
            version = public.extract_cli_version(run([str(binary), "--version"], env, evidence, "version", 30))
            require(public.release_version(version) == value["releaseVersion"] and
                    ("+" not in version or version == value["informationalVersion"]), "Actual binary version mismatch")
            value["executedVersion"] = version
            value["executedVersionIncludesSource"] = version == value["informationalVersion"]
            heartbeat(binary, env, evidence)
            if args.mode == "vsix":
                run([sys.executable, str(ROOT / "scripts/ci/smoke_single_file_extraction.py"), "--binary", str(binary),
                     "--fixture", str(ROOT / "tests/fixtures/SingleFilePlugin/bin/Release/net462"),
                     "--cache-probe", str(ROOT / "tests/fixtures/ReferenceCacheProbe/bin/Release/net8.0/ReferenceCacheProbe.dll"),
                     "--reference-archive", str(ROOT / f"src/PPDS.Cli/obj/Release/net10.0/{args.rid}/Net462ReferenceAssemblies.zip")],
                    env, evidence, "archived-extraction", 480)
        require(digest(binary) == value["binarySha256"], "Validated binary bytes changed")
        require(not (Path(env["PPDS_CONFIG_DIR"]) / "profiles.json").exists(), "Unexpected profile file")
        save(evidence / "result.json", {"passed": True, **value, "noPublisherOrProviderRequested": True})
        return 0
    except Exception as error:
        save(evidence / "failure.json", {"passed": False, "type": type(error).__name__, "message": str(error)})
        raise


if __name__ == "__main__":
    raise SystemExit(main())
