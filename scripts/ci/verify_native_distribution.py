"""Candidate-only native/VSIX validation. No tag, publisher, auth or provider route."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PureWindowsPath
import queue
import re
import shutil
import stat
import subprocess
import sys
import threading
import uuid
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


def windows_runtime_root():
    # Only metadata of trusted runner directories is inspected; no existing
    # PPDS profile, reference cache or credential file is opened.
    require(os.getenv("GITHUB_ACTIONS") == "true" and os.getenv("RUNNER_ENVIRONMENT") == "github-hosted",
            "Windows private runtime requires an ephemeral GitHub-hosted job")
    drive = os.environ.get("SYSTEMDRIVE", "").upper()
    require(drive == "C:", "Windows private runtime requires the trusted C: system drive")

    def local_path(key):
        value = os.environ.get(key, "")
        require(value and not value.startswith(("\\\\", "//")) and
                not re.search(r'[\x00-\x1f<>"|?*]', value) and ":" not in value[2:] and
                not any(p in {".", ".."} for p in re.split(r"[\\/]", value)),
                "Invalid Windows runner profile path")
        path = PureWindowsPath(value)
        require(path.is_absolute() and path.drive.upper() == drive and path.root == "\\",
                "Windows runner profile must be a local system-drive path")
        return path

    profile = local_path("USERPROFILE")
    parent_path = local_path("LOCALAPPDATA")
    require(len(profile.parts) >= 3 and parent_path != profile and parent_path.is_relative_to(profile),
            "Windows private runtime requires LOCALAPPDATA beneath the runner profile")
    parent = Path(str(parent_path))
    for path in reversed([parent, *parent.parents]):
        info = path.lstat()
        require(stat.S_ISDIR(info.st_mode) and not path.is_symlink() and
                not getattr(info, "st_file_attributes", 0) & 0x400 and path.resolve(strict=True) == path,
                "Windows private runtime ancestor is not a canonical plain directory")
    root = parent / ("ppds-native-ci-" + uuid.uuid4().hex)
    require(root.parent == parent and root.resolve(strict=False).is_relative_to(parent),
            "Windows private runtime escaped its profile parent")
    try:
        root.lstat()
    except FileNotFoundError:
        return root
    raise ValueError("Refuse existing Windows private runtime child")


WINDOWS_SECURITY_SCRIPT = r"""
function Write-SecurityPhase([string]$phase) {
    [Console]::Error.WriteLine('PPDS_SECURITY_PHASE_V1:' + $phase)
    [Console]::Error.Flush()
}
Write-SecurityPhase 'script_entered'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$observedRows = [System.Collections.Generic.List[object]]::new()
$requestedPaths = @()
$currentUserSidText = $null
$snapshotComplete = $false
$snapshotErrorType = $null
try {
    Write-SecurityPhase 'request_read_started'
    $requestText = [Console]::In.ReadToEnd()
    Write-SecurityPhase 'request_read_completed'
    if ([Text.Encoding]::UTF8.GetByteCount($requestText) -gt 16384) { throw 'request_bound' }
    $requestObject = ConvertFrom-Json -InputObject $requestText
    if ($requestObject.paths -isnot [System.Array]) { throw 'path_array_required' }
    $requestedPaths = @($requestObject.paths)
    if ($requestedPaths.Count -lt 1 -or $requestedPaths.Count -gt 16) { throw 'path_count_bound' }
    $uniquePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($securityPathText in $requestedPaths) {
        if ($securityPathText -isnot [string] -or $securityPathText.Length -gt 512 -or
            $securityPathText -notmatch '^C:\\' -or $securityPathText -match '(^|[\\/])\.\.?([\\/]|$)' -or
            -not $uniquePaths.Add($securityPathText)) { throw 'invalid_literal_path' }
    }
    Write-SecurityPhase 'request_validated'
    Write-SecurityPhase 'identity_started'
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $currentUserSidText = $currentIdentity.User.Value }
    finally { $currentIdentity.Dispose() }
    if ([string]::IsNullOrWhiteSpace($currentUserSidText)) { throw 'current_user_sid_unavailable' }
    Write-SecurityPhase 'identity_completed'
    $pathIndex = 0
    foreach ($securityPathText in $requestedPaths) {
        Write-SecurityPhase ('acl_started:' + $pathIndex)
        $securityObject = Get-Acl -LiteralPath $securityPathText -ErrorAction Stop
        $descriptorBytes = $securityObject.GetSecurityDescriptorBinaryForm()
        if ($descriptorBytes.Length -lt 1 -or $descriptorBytes.Length -gt 65536) { throw 'descriptor_bound' }
        $rawDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new($descriptorBytes, 0)
        $aceRows = [System.Collections.Generic.List[object]]::new()
        if ($null -ne $rawDescriptor.DiscretionaryAcl) {
            if ($rawDescriptor.DiscretionaryAcl.Count -gt 128) { throw 'ace_count_bound' }
            foreach ($rawAce in $rawDescriptor.DiscretionaryAcl) {
                $sidText = $null
                $unsignedMask = $null
                $qualifierValue = $null
                $isCallback = $null
                if ($rawAce -is [Security.AccessControl.KnownAce]) {
                    $sidText = $rawAce.SecurityIdentifier.Value
                    $unsignedMask = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$rawAce.AccessMask), 0)
                }
                if ($rawAce -is [Security.AccessControl.QualifiedAce]) {
                    $qualifierValue = [int]$rawAce.AceQualifier
                    $isCallback = [bool]$rawAce.IsCallback
                }
                $aceRows.Add([ordered]@{
                    type = [int]$rawAce.AceType
                    qualifier = $qualifierValue
                    callback = $isCallback
                    flags = [int]$rawAce.AceFlags
                    mask = $unsignedMask
                    sid = $sidText
                })
            }
        }
        $acesValue = $null
        if ($null -ne $rawDescriptor.DiscretionaryAcl) { $acesValue = @($aceRows.ToArray()) }
        $ownerSidText = $null
        if ($null -ne $rawDescriptor.Owner) { $ownerSidText = $rawDescriptor.Owner.Value }
        $observedRows.Add([ordered]@{
            path = $securityPathText
            ownerSid = $ownerSidText
            daclPresent = ([int]$rawDescriptor.ControlFlags -band 4) -ne 0
            daclNull = $null -eq $rawDescriptor.DiscretionaryAcl
            controlFlags = [int]$rawDescriptor.ControlFlags
            descriptorBytes = $descriptorBytes.Length
            aces = $acesValue
        })
        Write-SecurityPhase ('acl_completed:' + $pathIndex)
        $pathIndex++
    }
    $snapshotComplete = $true
}
catch { $snapshotErrorType = $_.Exception.GetType().FullName }
if ($snapshotComplete) { Write-SecurityPhase 'serialization_started' }
$snapshotPayload = [ordered]@{
    complete = $snapshotComplete
    currentUserSid = $currentUserSidText
    requestedCount = $requestedPaths.Count
    observedCount = $observedRows.Count
    errorType = $snapshotErrorType
    rows = @($observedRows.ToArray())
}
$snapshotJson = ConvertTo-Json -InputObject $snapshotPayload -Depth 8 -Compress
if ([Text.Encoding]::UTF8.GetByteCount($snapshotJson) -gt 131072) {
    $snapshotComplete = $false
    $snapshotJson = ConvertTo-Json -InputObject ([ordered]@{
        complete = $false
        currentUserSid = $currentUserSidText
        requestedCount = $requestedPaths.Count
        observedCount = $observedRows.Count
        metadataDroppedForSize = $true
        rows = @()
    }) -Compress
}
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if ($snapshotComplete) { Write-SecurityPhase 'serialization_completed' }
[Console]::Out.Write($snapshotJson)
[Console]::Out.Flush()
if ($snapshotComplete) { Write-SecurityPhase 'snapshot_written' }
if ($snapshotComplete) { exit 0 }
exit 1
"""


WINDOWS_READ_MASK = 0xA01200A9
WINDOWS_ANCESTOR_MASK = WINDOWS_READ_MASK | 0x6
WINDOWS_SYSTEM_SID = "S-1-5-18"
WINDOWS_ADMIN_SID = "S-1-5-32-544"
WINDOWS_INSTALLER_SID = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
WINDOWS_SID = re.compile(r"S-1-[0-9]+(?:-[0-9]+){1,15}\Z")


def valid_windows_sid(value):
    return isinstance(value, str) and len(value) <= 184 and WINDOWS_SID.fullmatch(value) is not None


def security_reader_failure(paths, kind, diagnostics=None):
    value = {"complete": False, "requestedCount": len(paths), "observedCount": 0,
             "currentUserSid": None, "rows": [], "errorType": kind}
    if diagnostics is not None:
        value["readerDiagnostics"] = diagnostics
    return value


def security_reader_diagnostics(stderr, stdout, count, *, timed_out=False):
    """Finite ordered ASCII phases only; partial output never establishes trust."""
    phases = ["script_entered", "request_read_started", "request_read_completed", "request_validated", "identity_started", "identity_completed"]
    phases += [f"acl_{stage}:{index}" for index in range(count) for stage in ["started", "completed"]]
    phases += ["serialization_started", "serialization_completed", "snapshot_written"]
    data = stderr if isinstance(stderr, bytes) else b""
    lines = data.splitlines(keepends=True) if len(data) <= 16384 else []
    tail = bool(lines and not lines[-1].endswith(b"\n"))
    records = lines[:-1] if tail else lines
    expected = [("PPDS_SECURITY_PHASE_V1:" + phase).encode("ascii") for phase in phases]
    valid = (len(data) <= 16384 and len(records) <= len(expected) and
             all(line in {expected[index] + b"\n", expected[index] + b"\r\n"} for index, line in enumerate(records)) and
             (not tail or timed_out and len(lines[-1]) <= 128))
    observed = len(records) if valid else 0
    status = "invalid" if not valid or tail and not timed_out else (
        "complete" if observed == len(expected) and not tail else "partial" if observed or tail else "not_observed")
    return {"status": status, "eventCount": observed, "lastObservedPhase": phases[observed - 1] if observed else "not_observed",
            "incompleteTail": tail, "timedOut": timed_out, "timeoutSeconds": 15,
            "stderrBytes": min(len(data), 16385), "stdoutBytes": min(len(stdout), 131073) if isinstance(stdout, bytes) else 0}


def read_windows_security(paths):
    """Read bounded security metadata only. No setters, shell or account-name lookup."""
    require(sys.platform == "win32" and os.getenv("GITHUB_ACTIONS") == "true" and
            os.getenv("RUNNER_ENVIRONMENT") == "github-hosted", "Windows security reader is hosted-CI only")
    require(isinstance(paths, (list, tuple)) and 0 < len(paths) <= 16, "Invalid security path count")
    names = [str(path) for path in paths]
    require(len(set(name.casefold() for name in names)) == len(names) and all(
        0 < len(name) <= 512 and PureWindowsPath(name).is_absolute() and
        PureWindowsPath(name).drive.upper() == "C:" and name == str(PureWindowsPath(name)) and
        not any(p in {".", ".."} for p in re.split(r"[\\/]", name)) for name in names),
        "Invalid canonical security paths")
    executable = Path("C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe")
    require(plain(executable).resolve(strict=True) == executable, "Noncanonical OS security reader")
    command = [str(executable), "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", WINDOWS_SECURITY_SCRIPT]
    env = {k: v for k, v in os.environ.items() if k.upper() in OS_KEYS}
    try:
        result = subprocess.run(command, input=json.dumps({"paths": names}, ensure_ascii=True).encode("ascii"),
                                env=env, stdin=None, capture_output=True, timeout=15, check=False,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except subprocess.TimeoutExpired as error:
        return security_reader_failure(paths, "TimeoutExpired", security_reader_diagnostics(error.stderr, error.output, len(paths), timed_out=True))
    except OSError:
        return security_reader_failure(paths, "OSError", security_reader_diagnostics(b"", b"", len(paths)))
    diagnostics = security_reader_diagnostics(result.stderr, result.stdout, len(paths))
    if len(result.stdout) > 131072 or len(result.stderr) > 16384:
        return security_reader_failure(paths, "SecurityMetadataOutputBound", diagnostics)
    try:
        value = json.loads(result.stdout.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return security_reader_failure(paths, "InvalidSecurityMetadataJson", diagnostics)
    if not isinstance(value, dict):
        return security_reader_failure(paths, "InvalidSecurityMetadataShape", diagnostics)
    if result.returncode != 0 or diagnostics["status"] != "complete":
        value["complete"] = False
    value["readerDiagnostics"] = diagnostics
    return value


def windows_descriptor_policy(row, current_sid, ancestor):
    reasons, mutation = [], []
    trusted = {current_sid, WINDOWS_SYSTEM_SID, WINDOWS_ADMIN_SID}
    if ancestor:
        trusted.add(WINDOWS_INSTALLER_SID)
    if not valid_windows_sid(row.get("ownerSid")) or row.get("ownerSid") not in trusted:
        reasons.append("untrusted_owner")
    control = row.get("controlFlags")
    if (type(control) is not int or not 0 <= control <= 0xFFFF or not control & 4 or
            row.get("daclPresent") is not True or row.get("daclNull") is not False):
        reasons.append("missing_invalid_or_null_dacl")
    size = row.get("descriptorBytes")
    if type(size) is not int or not 0 < size <= 65536:
        reasons.append("invalid_descriptor_size")
    aces = row.get("aces")
    if not isinstance(aces, list) or len(aces) > 128:
        return {"accepted": False, "reasons": reasons + ["unavailable_or_oversized_ace_inventory"],
                "ownerSid": row.get("ownerSid"), "allowMutation": mutation}
    permitted = WINDOWS_ANCESTOR_MASK if ancestor else WINDOWS_READ_MASK
    for index, ace in enumerate(aces):
        if not isinstance(ace, dict):
            reasons.append("unavailable_ace")
            continue
        kind, flags, mask, sid = (ace.get(k) for k in ["type", "flags", "mask", "sid"])
        if (type(kind) is int and kind in {0, 4, 5, 9, 11} and type(mask) is int and
                0 <= mask <= 0xFFFFFFFF and valid_windows_sid(sid) and mask & ~WINDOWS_READ_MASK):
            mutation.append({"index": index, "sid": sid, "mask": mask, "flags": flags})
        if not (type(kind) is int and kind in {0, 1} and type(flags) is int and 0 <= flags <= 0x1F and
                type(mask) is int and 0 <= mask <= 0xFFFFFFFF and valid_windows_sid(sid) and
                type(ace.get("qualifier")) is int and ace["qualifier"] == kind and ace.get("callback") is False):
            reasons.append("unsupported_or_invalid_ace")
            continue
        if ancestor and flags & 8:
            continue
        if kind == 1:
            continue
        if mask & ~permitted and sid not in trusted:
            reasons.append("untrusted_mutation_grant")
    return {"accepted": not reasons, "reasons": sorted(set(reasons)),
            "ownerSid": row.get("ownerSid"), "allowMutation": mutation}


def security_evidence_metadata(snapshot):
    """Keep only explicit security fields, including unsupported ACE metadata."""
    if not isinstance(snapshot, dict):
        return {"complete": False, "metadataIncomplete": True, "rows": []}
    result = {k: snapshot.get(k) if type(snapshot.get(k)) in {bool, int, type(None)} else None
              for k in ["complete", "requestedCount", "observedCount"]}
    result["currentUserSid"] = snapshot.get("currentUserSid") if valid_windows_sid(snapshot.get("currentUserSid")) else None
    error_type = snapshot.get("errorType")
    if isinstance(error_type, str) and re.fullmatch(r"[A-Za-z0-9_.]{1,200}", error_type):
        result["errorType"] = error_type
    if snapshot.get("metadataDroppedForSize") is True:
        result["metadataDroppedForSize"] = True
    diagnostics = snapshot.get("readerDiagnostics")
    if isinstance(diagnostics, dict):
        safe = {key: diagnostics.get(key) if type(diagnostics.get(key)) is bool else None for key in ["incompleteTail", "timedOut"]}
        for key, maximum in [("eventCount", 41), ("timeoutSeconds", 15), ("stderrBytes", 16385), ("stdoutBytes", 131073)]:
            value = diagnostics.get(key)
            safe[key] = value if type(value) is int and 0 <= value <= maximum else None
        status, phase = diagnostics.get("status"), diagnostics.get("lastObservedPhase")
        safe["status"] = status if isinstance(status, str) and status in {"invalid", "complete", "partial", "not_observed"} else "invalid"
        safe["lastObservedPhase"] = phase if isinstance(phase, str) and re.fullmatch(
            r"(?:not_observed|script_entered|request_read_(?:started|completed)|request_validated|identity_(?:started|completed)|acl_(?:started|completed):(?:[0-9]|1[0-5])|serialization_(?:started|completed)|snapshot_written)", phase) else "not_observed"
        result["readerDiagnostics"] = safe
    result["rows"] = []
    if not isinstance(snapshot.get("rows"), list) or len(snapshot["rows"]) > 16:
        result["metadataIncomplete"] = True
        return result
    for row in snapshot["rows"]:
        if not isinstance(row, dict):
            result["metadataIncomplete"] = True
            continue
        path = row.get("path")
        safe = {"path": path if isinstance(path, str) and len(path) <= 512 else None,
                "ownerSid": row.get("ownerSid") if valid_windows_sid(row.get("ownerSid")) else None}
        for key in ["daclPresent", "daclNull", "descriptorBytes", "controlFlags"]:
            safe[key] = row.get(key) if type(row.get(key)) in {bool, int, type(None)} else None
        aces = row.get("aces")
        safe["aces"] = None
        if isinstance(aces, list) and len(aces) <= 128:
            safe["aces"] = []
            for ace in aces:
                if not isinstance(ace, dict):
                    safe["aces"].append({"unsupported": True})
                    continue
                fields = {k: ace.get(k) if type(ace.get(k)) in {bool, int, type(None)} else None
                          for k in ["type", "qualifier", "callback", "flags", "mask"]}
                fields["sid"] = ace.get("sid") if valid_windows_sid(ace.get("sid")) else None
                safe["aces"].append(fields)
        elif aces is not None:
            safe["metadataIncomplete"] = True
            safe["omittedAceCount"] = len(aces) if isinstance(aces, list) else None
        result["rows"].append(safe)
    return result


def inspect_windows_paths(paths, destination, *, ancestor):
    snapshot = read_windows_security(paths)
    evidence = {"ancestor": ancestor, "snapshot": security_evidence_metadata(snapshot), "policies": []}
    names = [str(path) for path in paths]
    reasons = []
    if not isinstance(snapshot, dict) or snapshot.get("complete") is not True:
        reasons.append("incomplete_security_read")
    else:
        allowed = {"complete", "currentUserSid", "requestedCount", "observedCount", "rows", "errorType", "readerDiagnostics"}
        current = snapshot.get("currentUserSid")
        rows = snapshot.get("rows")
        if (set(snapshot) - allowed or not valid_windows_sid(current) or current.startswith(("S-1-3-", "S-1-1-")) or
                type(snapshot.get("requestedCount")) is not int or snapshot["requestedCount"] != len(names) or
                type(snapshot.get("observedCount")) is not int or snapshot["observedCount"] != len(names) or
                not isinstance(rows, list) or len(rows) != len(names) or
                any(not isinstance(row, dict) for row in rows) or [row.get("path") for row in rows] != names):
            reasons.append("invalid_security_snapshot")
        else:
            for row in rows:
                policy = windows_descriptor_policy(row, current, ancestor)
                evidence["policies"].append(dict(policy, path=row["path"]))
                if not policy["accepted"]:
                    reasons.append("unsafe_security_descriptor")
    evidence.update({"accepted": not reasons, "reasons": sorted(set(reasons))})
    if len(json.dumps(evidence, ensure_ascii=True).encode("ascii")) > 131072:
        evidence = {"accepted": False, "reasons": ["security_evidence_bound"], "metadataIncomplete": True,
                    "requestedCount": len(names), "observedCount": len(evidence["snapshot"].get("rows", []))}
    save(destination, evidence)
    require(evidence["accepted"], "Windows runtime security metadata is unsafe or incomplete; retained diagnostics")


def private_environment(root):
    require(not root.exists() and not root.is_symlink(), "Refuse existing runtime directory")
    names = ["home", "appdata", "localappdata", "config", "data", "temp", "docker"]
    if sys.platform == "win32":
        evidence = root.parent
        root = windows_runtime_root()
        ancestors = list(reversed([root.parent, *root.parent.parents]))
        require(len(ancestors) <= 8, "Windows runtime ancestor path bound exceeded")
        inspect_windows_paths(ancestors, evidence / "windows-security-parent.json", ancestor=True)
        # Windows0700 creates an OWNER_RIGHTS grant. Use default inheritance,
        # inspect actual descriptors, and never repair permissions or weaken trust.
        root.mkdir()
        private_paths = [root]
        for name in names:
            path = root / name
            path.mkdir()
            private_paths.append(path)
        for path in private_paths:
            info = path.lstat()
            require(stat.S_ISDIR(info.st_mode) and not path.is_symlink() and
                    not getattr(info, "st_file_attributes", 0) & 0x400 and path.resolve(strict=True) == path,
                    "New Windows private runtime path is not a canonical plain directory")
        inspect_windows_paths(private_paths, evidence / "windows-security-runtime.json", ancestor=False)
    else:
        root.mkdir(mode=0o700)
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
