#!/usr/bin/env python3
"""Explicit candidate-only Windows experiment; never a release or signing permission.

No arguments/default invocation performs experiment I/O. Separate prepare/sign/
verify processes are NOT OS-enforced isolation. An operator must obtain separate
authorization and provide an externally isolated, trusted signing context before
using a disposable key. This program does not generate keys or activate policy.
"""
from __future__ import annotations

import argparse
import base64
import ctypes
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path, PureWindowsPath
import re
import stat
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
from scripts.ci import verify_nuget_handoff as h
from scripts.ci import verify_plugin_signing as p
from scripts.ci import verify_release_inputs as release
from scripts.ci.release_model import ReleaseGraph

PRODUCTION_PUBLIC_HASH = "3433c28fcbad31568819c43f91a890f4426d358fbf8aadef1ddef156478b4d01"
FIXED_SIGNER = "C:/Program Files (x86)/Microsoft SDKs/Windows/v10.0A/bin/NETFX 4.8 Tools/sn.exe"
DLL = "PPDS.Plugins.dll"
CASES = tuple(f"COMPAT-{n:02}" for n in range(1, 7))
HELPERS = (
    "scripts/ci/verify_nuget_handoff.py", "scripts/ci/verify_plugin_signing.py",
    "scripts/ci/verify_release_inputs.py", "scripts/ci/verify_public_release.py",
    "scripts/ci/release_model.py", "scripts/ci/inspect_release_assemblies.ps1",
    "tests/compatibility/plugin_signing_compatibility.py",
)
Error = h.Error

# This trusted, metadata-only probe never loads target code. Its bytes are bound
# in receipts. All parameters are passed separately, not interpolated as code.
PROBE = r"""
param([string]$Dll, [string]$Pdb, [string]$Tool)
$ErrorActionPreference = 'Stop'
if ($Tool) {
  [Diagnostics.FileVersionInfo]::GetVersionInfo($Tool).ProductVersion
  exit
}
$stream = [IO.File]::OpenRead($Dll)
$pe = $null
$provider = $null
$pdbStream = $null
try {
  $identity = [Reflection.AssemblyName]::GetAssemblyName($Dll)
  $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
  if (!$pe.HasMetadata -or !$pe.PEHeaders.CorHeader) { throw 'Managed PE required' }
  $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
  if (!$reader.IsAssembly -or $reader.AssemblyFiles.Count -ne 0) { throw 'Single module required' }
  $cor = $pe.PEHeaders.CorHeader
  if (($cor.Flags -band 1) -eq 0 -or ($pe.PEHeaders.CoffHeader.Characteristics -band 0x2000) -eq 0 -or $cor.ManagedNativeHeaderDirectory.Size -ne 0) { throw 'IL-only DLL required' }
  $frameworks = @()
  foreach ($handle in $reader.GetAssemblyDefinition().GetCustomAttributes()) {
    $a = $reader.GetCustomAttribute($handle)
    if ($a.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
    $m = $reader.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$a.Constructor)
    if ($m.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
    $t = $reader.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$m.Parent)
    if ($reader.GetString($t.Namespace) -ceq 'System.Runtime.Versioning' -and $reader.GetString($t.Name) -ceq 'TargetFrameworkAttribute') {
      $blob = $reader.GetBlobReader($a.Value)
      if ($blob.ReadUInt16() -ne 1) { throw 'Invalid framework attribute' }
      $frameworks += $blob.ReadSerializedString()
    }
  }
  if ($frameworks.Count -ne 1) { throw 'One framework required' }
  $mutation = $null
  foreach ($handle in $reader.TypeDefinitions) {
    $definition = $reader.GetTypeDefinition($handle)
    $name = $reader.GetString($definition.Name)
    if ($name -cmatch '^[A-Za-z]') {
      $mutation = @{name=$name; heap_offset=[Reflection.Metadata.Ecma335.MetadataTokens]::GetHeapOffset($definition.Name)}
      break
    }
  }
  if (!$mutation) { throw 'No representative metadata mutation location' }
  $debug = @()
  foreach ($entry in $pe.ReadDebugDirectory()) {
    if ($entry.Type -eq [Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) {
      $cv = $pe.ReadCodeViewDebugDirectoryData($entry)
      $debug += @{guid=$cv.Guid.ToString(); age=$cv.Age; stamp=$entry.Stamp}
    }
  }
  if ($debug.Count -ne 1) { throw 'One CodeView identity required' }
  $pdbStream = [IO.File]::OpenRead($Pdb)
  $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($pdbStream, [Reflection.Metadata.MetadataStreamOptions]::Default, 0)
  $pdbReader = $provider.GetMetadataReader([Reflection.Metadata.MetadataReaderOptions]::Default, $null)
  $id = $pdbReader.DebugMetadataHeader.Id
  $guidBytes = [byte[]]::new(16)
  for ($i=0; $i -lt 16; $i++) { $guidBytes[$i]=$id[$i] }
  $stampBytes = [byte[]]::new(4)
  for ($i=0; $i -lt 4; $i++) { $stampBytes[$i]=$id[$i+16] }
  $pdbGuid = [Guid]::new($guidBytes).ToString()
  $pdbStamp = [BitConverter]::ToUInt32($stampBytes,0)
  if ($pdbGuid -cne $debug[0].guid -or $pdbStamp -ne $debug[0].stamp -or $debug[0].age -ne 1) { throw 'PDB identity mismatch' }
  @{
    name=$identity.Name; assembly_version=$identity.Version.ToString(); culture=$identity.CultureName
    public_key_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identity.GetPublicKey())).ToLowerInvariant()
    mvid=$reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString()
    framework=$frameworks[0]; informational_version=[Diagnostics.FileVersionInfo]::GetVersionInfo($Dll).ProductVersion
    codeview=$debug[0]; pdb=@{guid=$pdbGuid; stamp=$pdbStamp}; mutation=$mutation
  } | ConvertTo-Json -Depth 5 -Compress
} finally {
  if ($provider) { $provider.Dispose() }
  if ($pdbStream) { $pdbStream.Dispose() }
  if ($pe) { $pe.Dispose() }
  $stream.Dispose()
}
""".strip() + "\n"


def require(condition, reason):
    h.require(condition, reason)


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def read(path, limit=h.MAX_PACKAGE):
    path = Path(path)
    local_path(path)
    return h.regular_bytes(path, limit)


def record(path):
    raw = read(path)
    return {"sha256": digest(raw), "bytes": len(raw)}


def write(path, raw):
    with Path(path).open("xb") as stream:
        stream.write(raw)


def json_write(path, value):
    write(path, (json.dumps(value, sort_keys=True, indent=2) + "\n").encode())


def check_hex(value, length=64):
    return isinstance(value, str) and re.fullmatch("[0-9a-f]{" + str(length) + "}", value) is not None


def windows_path(value):
    require(isinstance(value, str) and bool(re.fullmatch(r"[A-Za-z]:[/\\][^:\r\n]+", value)),
            "Absolute local Windows path required")
    require(".." not in PureWindowsPath(value).parts, "Parent traversal refused")
    return value


def local_path(path):
    if sys.platform == "win32":
        windows_path(str(path))
    # Walk from the local root before probing children of a possible junction.
    for parent in reversed((Path(path), *Path(path).parents)):
        try:
            info = parent.lstat()
        except FileNotFoundError:
            break
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400,
                "Linked input/output refused")


def product_version(value):
    return isinstance(value, str) and 0 < len(value) <= 256 and bool(value.strip()) and all(32 <= ord(c) < 127 for c in value)


def load_plan(path, expected_hash):
    raw = read(path, 65536)
    require(check_hex(expected_hash) and digest(raw) == expected_hash, "Plan hash mismatch")
    value = h.decode(raw)
    require(set(value) == {"schema", "kind", "experiment", "source", "tree", "version",
                          "public_key_sha256", "windows_directory", "tools"}, "Unexpected plan fields")
    require(value["schema"] == 1 and value["kind"] == "ppds-signing-compatibility-plan", "Candidate plan required")
    require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}", value["experiment"]) is not None, "Experiment ID required")
    require(check_hex(value["source"], 40) and check_hex(value["tree"], 40), "Source identity required")
    release.strict_version(value["version"])
    require(check_hex(value["public_key_sha256"]) and value["public_key_sha256"] != PRODUCTION_PUBLIC_HASH,
            "Distinct disposable public identity required")
    windows_path(value["windows_directory"])
    require(set(value["tools"]) == {"git", "dotnet", "pwsh", "sn"}, "Exact tool inventory required")
    for name, tool in value["tools"].items():
        require(set(tool) == {"path", "sha256", "version"}, "Exact tool identity required")
        windows_path(tool["path"])
        require(check_hex(tool["sha256"]) and product_version(tool["version"]), "Tool hash/version required")
        require(PureWindowsPath(tool["path"]).name.lower() == {"git":"git.exe", "dotnet":"dotnet.exe",
                "pwsh":"pwsh.exe", "sn":"sn.exe"}[name], "Unexpected tool executable")
    require(PureWindowsPath(value["tools"]["sn"]["path"]) == PureWindowsPath(FIXED_SIGNER),
            "Inspector and accepted signer must use the same fixed binary")
    return value


def public_shape(raw):
    require(len(raw) == 160 and struct.unpack_from("<II", raw, 0) == (0x2400, 0x8004)
            and struct.unpack_from("<I", raw, 24)[0] == 1024, "Representative public-key shape required")


def utc():
    return datetime.now(timezone.utc).isoformat()


class NativeSystemInfo(ctypes.Structure):
    _fields_ = [("architecture", ctypes.c_uint16), ("reserved", ctypes.c_uint16),
                ("page_size", ctypes.c_uint32), ("minimum_address", ctypes.c_void_p),
                ("maximum_address", ctypes.c_void_p), ("processor_mask", ctypes.c_size_t),
                ("processor_count", ctypes.c_uint32), ("processor_type", ctypes.c_uint32),
                ("allocation_granularity", ctypes.c_uint32), ("processor_level", ctypes.c_uint16),
                ("processor_revision", ctypes.c_uint16)]


def host_observation(*, kernel32=None):
    # Called only in an authorized real stage; offline tests replace this probe.
    require(sys.platform == "win32", "Windows host required")
    version = sys.getwindowsversion()
    native = (kernel32 if kernel32 is not None else ctypes.WinDLL("kernel32.dll", use_last_error=True)).GetNativeSystemInfo
    native.argtypes = [ctypes.POINTER(NativeSystemInfo)]
    native.restype = None
    info = NativeSystemInfo()
    info.architecture = 0xFFFF
    native(ctypes.byref(info))
    architecture = {0: "X86", 9: "AMD64", 12: "ARM64"}.get(info.architecture)
    require(architecture is not None, "Unknown native Windows architecture")
    return {"platform": sys.platform, "process_bits": struct.calcsize("P") * 8,
            "native_architecture": architecture, "windows_major": version.major,
            "windows_minor": version.minor, "windows_build": version.build}


@dataclass
class Runtime:
    """Only this boundary launches processes/copies a later-authorized key."""
    evidence_class: str = "real-process"
    calls: list | None = None
    owned_output: Path | None = None
    key_cleanup: str = "not_used"

    def __post_init__(self):
        if self.calls is None:
            self.calls = []

    def run(self, command, *, cwd, environment, operation, timeout=120):
        started = utc()
        try:
            result = subprocess.run(command, cwd=cwd, env=environment, stdin=subprocess.DEVNULL,
                                    capture_output=True, text=True, timeout=timeout, check=False,
                                    shell=False)
        except Exception as failure:
            # Do not surface exceptions/argv/stdout/stderr: sign can contain key paths.
            self.calls.append({"operation": operation, "exit_code": None, "started_utc": started,
                               "completed_utc": utc(), "status": "timeout" if isinstance(failure, subprocess.TimeoutExpired)
                               else "launch-failure"})
            raise Error("Candidate tool did not complete") from None
        row = {"operation": operation, "exit_code": result.returncode, "started_utc": started, "completed_utc": utc()}
        # Retain public verification diagnostics only. Never retain signing argv,
        # exception text or private-key-operation output, even on success.
        if self.key_cleanup == "not_used" and operation in {"delay-forced-rejection", "signed-forced-verification", "signature-only-comparison",
                         "fresh-forced-verification", "fresh-signature-comparison", "corrupt-signature-rejection",
                         "non-signature-change-rejection", "packaged-forced-verification"}:
            diagnostics = result.stdout + result.stderr
            row.update(public_diagnostics=diagnostics[:8192], omitted_diagnostic_characters=max(0, len(diagnostics)-8192))
        elif self.key_cleanup != "not_used" or operation == "sign":
            row["diagnostics_omitted"] = "private-key phase"
        self.calls.append(row)
        return result

    def copy_key(self, supplied, owned):
        # Called ONLY after the sign-stage authorization/public admission checks.
        # Never log bytes, their hash, or either private path.
        raw = read(supplied, 16384)
        require(512 <= len(raw) <= 16384, "Disposable key input bound")
        write(owned, raw)

    def remove_key(self, owned):
        Path(owned).unlink()


def environment(plan, temporary, *, build=False):
    # No os.environ, PATH inheritance, token lookup or secret API.
    result = {"SYSTEMROOT": plan["windows_directory"], "WINDIR": plan["windows_directory"],
              "TEMP": str(temporary), "TMP": str(temporary),
              "ProgramFiles(x86)": str(PureWindowsPath(*PureWindowsPath(FIXED_SIGNER).parts[:2]))}
    if build:
        result.update(DOTNET_CLI_HOME=str(temporary / "dotnet-home"),
                      NUGET_PACKAGES=str(temporary / "packages"),
                      DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
                      DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
                      DOTNET_GENERATE_ASPNET_CERTIFICATE="false")
        result["PATH"] = ";".join(dict.fromkeys(str(PureWindowsPath(tool["path"]).parent)
                                                for tool in plan["tools"].values()))
    return result


def pin(plan, names):
    for name in names:
        require(digest(read(plan["tools"][name]["path"], 256 * 1024**2)) == plan["tools"][name]["sha256"],
                "Pinned tool bytes differ")


def invoke(rt, plan, tool, args, output, operation, *, zero=True, build=False, timeout=120, cwd=None):
    pin(plan, [tool])
    result = rt.run([plan["tools"][tool]["path"], *map(str, args)], cwd=cwd or output,
                    environment=environment(plan, output / "temp", build=build),
                    operation=operation, timeout=timeout)
    require((result.returncode == 0) if zero else (result.returncode != 0),
            "Candidate tool result differs from the required outcome")
    # Strong-name diagnostics are intentionally not retained, even on success.
    return "" if tool == "sn" else result.stdout


def fresh_output(path):
    path = Path(path)
    require(path.is_absolute(), "Absolute output required")
    local_path(path)
    require(not path.resolve().is_relative_to(ROOT.resolve()), "Candidate output must be outside the source checkout")
    p.fresh_directory(path)
    (path / "temp").mkdir()
    return path


def helper_bindings():
    return {name: digest(read(ROOT / name, 2 * 1024**2)) for name in HELPERS}


def common(plan, plan_hash, stage, rt):
    return {"schema": 1, "kind": "ppds-signing-compatibility-" + stage, "experiment": plan["experiment"],
            "source": plan["source"], "tree": plan["tree"], "version": plan["version"],
            "plan_sha256": plan_hash, "driver_sha256": digest(read(Path(__file__), 1024**2)),
            "probe_sha256": digest(PROBE.encode()), "evidence_class": rt.evidence_class,
            "helper_sha256": helper_bindings(),
            "python": {"version": sys.version, "executable": sys.executable,
                       "sha256": digest(read(Path(sys.executable), 256 * 1024**2))},
            "host": host_observation(), "recorded_utc": utc(),
            "tools": plan["tools"], "cases": {}, "commands": rt.calls,
            "nonclaims": ["Candidate experiment only; no release clearance or producer authenticity proof",
                          "Local stages/minimal environment do not prove OS/network or fresh-host isolation",
                          "No production-key or production-entrypoint lifecycle evidence"]}


def bind_files(directory, names):
    return {name: record(directory / name) for name in sorted(names)}


def expected_files(plan, stage):
    if stage == "prepare":
        return {f"PPDS.Plugins.{plan['version']}.nupkg", f"PPDS.Plugins.{plan['version']}.snupkg",
                "test-public.bin", "delay.dll", "public.dll", "public.pdb", "PPDS.Plugins.pdb"}
    require(stage == "sign", "Unexpected handoff stage")
    return {"input.dll", "signed.dll"}


def required_operation_counts(stage):
    if stage == "sign":
        return {name: 1 for name in ("sign", "signed-forced-verification", "signature-only-comparison")}
    require(stage == "prepare", "Unexpected observation stage")
    counts = {"inventory-" + name: 1 for name in ("git", "dotnet", "pwsh", "sn")}
    counts.update({name: 1 for name in ("delay-restore", "delay-build", "delay-pack", "public-restore",
                                      "public-build", "delay-forced-rejection")})
    counts.update({"source-identity": 5, "evaluated-properties": 2, "selected-sdk": 2,
                   "compiler-product-version": 4, "metadata": 2})
    return counts


def validate_build_record(value, plan, control):
    require(isinstance(value, dict) and {"sdk", "evaluated_properties", "binaries", "assets_sha256", "packages"} <= value.keys(),
            "Required build observations missing")
    props = value["evaluated_properties"]
    expected = {"NETCoreSdkVersion": value["sdk"], "TargetFramework": "net462", "SignAssembly": "true",
                "PublicSign": str(control).lower(), "DelaySign": str(not control).lower(),
                "Configuration": "Release", "PackageVersion": plan["version"],
                "InformationalVersion": plan["version"] + "+" + plan["source"]}
    require(isinstance(value["sdk"], str) and value["sdk"] and isinstance(props, dict)
            and all(props.get(k) == v for k, v in expected.items()) and props.get("MSBuildVersion")
            and props.get("AssemblyOriginatorKeyFile") and props.get("AssemblyVersion") and props.get("FileVersion"),
            "Required evaluated build observations differ")
    require(check_hex(value["assets_sha256"]) and set(value["binaries"]) == {"msbuild", "roslyn"},
            "Actual assets/compiler observations missing")
    for row in value["binaries"].values():
        require(isinstance(row, dict) and check_hex(row.get("sha256")) and row.get("bytes", 0) > 0
                and row.get("path") and product_version(row.get("product_version")), "Compiler observation incomplete")
    packages = value["packages"]
    require(isinstance(packages, list) and 0 < len(packages) <= 1000, "Resolved dependency observations missing")
    ids = set()
    for row in packages:
        require(isinstance(row, dict) and isinstance(row.get("id"), str) and row["id"]
                and isinstance(row.get("version"), str) and row["version"] and check_hex(row.get("sha256")),
                "Resolved dependency observation incomplete")
        require(isinstance(row.get("sha512"), str) and re.fullmatch(r"[A-Za-z0-9+/]{86}==", row["sha512"]),
                "Resolved dependency content hash missing")
        require(row["id"].lower() not in ids, "Duplicate resolved dependency observation")
        ids.add(row["id"].lower())
    require({"minver", "microsoft.sourcelink.github", "microsoft.netframework.referenceassemblies.net462"} <= ids,
            "Required build dependencies missing")


def validate_observations(value, stage, plan):
    require(isinstance(value.get("commands"), list), "Required command observations missing")
    expected = required_operation_counts(stage)
    counts = {}
    for command in value["commands"]:
        require(isinstance(command, dict) and command.get("operation") in expected
                and type(command.get("exit_code")) is int, "Unexpected/missing command observation")
        name = command["operation"]
        require((command["exit_code"] != 0) if name == "delay-forced-rejection" else command["exit_code"] == 0,
                "Failed command observation cannot pass")
        require(isinstance(command.get("started_utc"), str) and isinstance(command.get("completed_utc"), str),
                "Command timestamps missing")
        start = datetime.fromisoformat(command["started_utc"])
        end = datetime.fromisoformat(command["completed_utc"])
        require(start.tzinfo is not None and end.tzinfo is not None and end >= start, "Command timestamps invalid")
        counts[name] = counts.get(name, 0) + 1
    require(counts == expected, "Required command observations incomplete")
    require(isinstance(value.get("recorded_utc"), str) and isinstance(value.get("host"), dict)
            and isinstance(value.get("python"), dict) and check_hex(value["python"].get("sha256")),
            "Runtime observations missing")
    require(value["host"].get("platform") == "win32" and value["host"].get("native_architecture") in {"AMD64", "ARM64", "X86"}
            and isinstance(value["host"].get("windows_build"), int), "Windows host observations incomplete")
    if stage == "sign":
        require(value.get("cases") == {"COMPAT-02": "SIGNATURE_OPERATIONS_PASSED_METADATA_PENDING"}
                and value.get("key_cleanup") == "completed" and check_hex(value.get("prepare_receipt_sha256"))
                and value.get("authorization_reference"), "Signing observations incomplete")
        return
    require(value.get("cases") == {"COMPAT-01": "PASS", "COMPAT-03": "PASS"}
            and value.get("tool_product_versions") == {name: row["version"] for name, row in plan["tools"].items()},
            "Prepared case/tool observations incomplete")
    require(isinstance(value.get("build_evidence"), dict) and set(value["build_evidence"]) == {"delay", "public"},
            "Both build observations required")
    for name in ("delay", "public"):
        validate_build_record(value["build_evidence"][name], plan, name == "public")
    require(isinstance(value.get("build_overlay"), list) and {"-p:SignAssembly=true", "-p:DebugType=portable",
            "-p:Version=" + plan["version"], "-p:InformationalVersion=" + plan["version"] + "+" + plan["source"]}
            <= set(value["build_overlay"]), "Public build overlay observation incomplete")
    for field in ("metadata", "control_metadata"):
        row = value.get(field)
        require(isinstance(row, dict) and {"name", "assembly_version", "culture", "public_key_sha256", "mvid",
                "framework", "informational_version", "codeview", "pdb", "mutation"} <= row.keys(),
                "Assembly/PDB observations missing")
        require(row["name"] == "PPDS.Plugins" and row["public_key_sha256"] == plan["public_key_sha256"]
                and row["framework"] == ".NETFramework,Version=v4.6.2"
                and row["informational_version"] == plan["version"] + "+" + plan["source"],
                "Assembly observation identity differs")
        require(isinstance(row["pdb"], dict) and isinstance(row["codeview"], dict)
                and row["pdb"].get("guid") and row["pdb"].get("guid") == row["codeview"].get("guid")
                and type(row["pdb"].get("stamp")) is int and row["pdb"]["stamp"] == row["codeview"].get("stamp")
                and row["codeview"].get("age") == 1, "PDB observation identity differs")


def receipt(directory, stage, plan, plan_hash):
    raw = read(Path(directory) / (stage + "-receipt.json"), 1024**2)
    value = h.decode(raw)
    require(value["schema"] == 1 and value["kind"] == "ppds-signing-compatibility-" + stage,
            "Wrong candidate receipt")
    for key in ("experiment", "source", "tree", "version"):
        require(value[key] == plan[key], "Candidate receipt identity mismatch")
    require(value["plan_sha256"] == plan_hash and value["tools"] == plan["tools"]
            and value["driver_sha256"] == digest(read(Path(__file__), 1024**2))
            and value["probe_sha256"] == digest(PROBE.encode()), "Candidate receipt source/tool mismatch")
    require(value["helper_sha256"] == helper_bindings(), "Candidate helper/inspector source mismatch")
    if stage == "prepare":
        require(all(value["source_inputs"].get(name) == sha for name, sha in value["helper_sha256"].items()),
                "Prepared source differs from executed helper source")
    require(value["evidence_class"] == "real-process", "Process-double evidence cannot admit real signing")
    validate_observations(value, stage, plan)
    names = value["files"]
    require(isinstance(names, dict) and set(names) == expected_files(plan, stage), "Exact receipt inventory required")
    for name, expected in names.items():
        require("/" not in name and "\\" not in name and name not in {"", ".", ".."}, "Unsafe receipt file")
        require(record(Path(directory) / name) == expected, "Candidate artifact hash mismatch")
    return value, digest(raw)


def install_probe(output):
    path = output / "metadata-probe.ps1"
    write(path, PROBE.encode())
    return path


def tool_versions(rt, plan, output, probe):
    versions = {}
    for name in ("git", "dotnet", "pwsh", "sn"):
        actual = invoke(rt, plan, "pwsh", ["-NoProfile", "-File", probe, "-Tool", plan["tools"][name]["path"]],
                        output, "inventory-" + name).rstrip("\r\n")
        require(actual == plan["tools"][name]["version"], "Tool product version mismatch")
        versions[name] = actual
    return versions


def metadata(rt, plan, output, probe, dll, pdb):
    raw = invoke(rt, plan, "pwsh", ["-NoProfile", "-File", probe, "-Dll", dll, "-Pdb", pdb],
                 output, "metadata")
    value = h.decode(raw.encode())
    require(value["name"] == "PPDS.Plugins" and value["framework"] == ".NETFramework,Version=v4.6.2"
            and value["public_key_sha256"] == plan["public_key_sha256"]
            and value["informational_version"] == plan["version"] + "+" + plan["source"],
            "Actual assembly metadata differs from candidate identity")
    return value


def identity(value):
    return {key: item for key, item in value.items() if key != "mutation"}


def pe_layout(raw):
    """Bounded PE metadata/signature offsets; no target execution."""
    def u16(offset):
        return struct.unpack_from("<H", raw, offset)[0]
    def u32(offset):
        return struct.unpack_from("<I", raw, offset)[0]
    require(raw[:2] == b"MZ", "PE header required")
    pe = u32(60)
    require(raw[pe:pe+4] == b"PE\0\0", "PE signature required")
    opt = pe + 24
    dirs = opt + (96 if u16(opt) == 0x10b else 112)
    section = opt + u16(pe + 20)
    def offset(rva, size):
        found = []
        for n in range(u16(pe + 6)):
            row = section + n * 40
            va, length, start = u32(row+12), u32(row+16), u32(row+20)
            if va <= rva and rva + size <= va + length and start + rva - va + size <= len(raw):
                found.append(start + rva - va)
        require(len(found) == 1, "Ambiguous PE address")
        return found[0]
    cli = offset(u32(dirs+112), 72)
    signature_size = u32(cli+36)
    signature = offset(u32(cli+32), signature_size)
    md = offset(u32(cli+8), u32(cli+12))
    require(raw[md:md+4] == b"BSJB", "Metadata root required")
    pos = (md + 16 + u32(md+12) + 3) & ~3
    streams = u16(pos+2)
    pos += 4
    strings = None
    for _ in range(streams):
        start, size = u32(pos), u32(pos+4)
        end = raw.index(b"\0", pos+8, min(pos+40, len(raw)))
        name = raw[pos+8:end]
        if name == b"#Strings":
            strings = (md+start, size)
        pos = (end+4) & ~3
    require(strings is not None, "Metadata string heap required")
    return signature, signature_size, strings


def mutations(raw, meta):
    signature, length, (strings, size) = pe_layout(raw)
    require(length > 0, "Signature reservation required")
    broken_signature = bytearray(raw)
    broken_signature[signature] ^= 1
    offset = strings + meta["mutation"]["heap_offset"]
    expected = meta["mutation"]["name"].encode()
    require(0 <= meta["mutation"]["heap_offset"] < size and raw[offset:offset+len(expected)+1] == expected+b"\0"
            and expected[:1].isascii() and chr(expected[0]).isalpha(), "Valid metadata name mutation required")
    require(not signature <= offset < signature+length, "Mutation must be outside signature")
    changed_name = bytearray(raw)
    changed_name[offset] ^= 0x20  # ASCII case change: valid same-length metadata name.
    return bytes(broken_signature), bytes(changed_name), {"signature_offset": signature, "metadata_offset": offset}


def source_copy(rt, plan, source, output):
    source = Path(source)
    require(source.resolve() == ROOT.resolve(), "Source root must be the reviewed driver checkout")
    pin(plan, ["git"])
    def git(args):
        result = rt.run([plan["tools"]["git"]["path"], "-C", str(source), *args], cwd=output,
                        environment=environment(plan, output / "temp", build=True), operation="source-identity")
        require(result.returncode == 0, "Source inspection failed")
        return result.stdout
    require(git(["rev-parse", "HEAD"]).strip() == plan["source"] and
            git(["rev-parse", "HEAD^{tree}"]).strip() == plan["tree"] and
            not git(["status", "--porcelain"]).strip(), "Clean exact source required")
    copied = output / "source"
    copied.mkdir()
    names = git(["ls-files", "-z"]).split("\0")
    inputs = {}
    for name in filter(None, names):
        require(not Path(name).is_absolute() and "\\" not in name and ".." not in Path(name).parts
                and Path(name).suffix.lower() != ".snk", "Unsafe source inventory")
        data = read(source / name)
        target = copied / name
        target.parent.mkdir(parents=True, exist_ok=True)
        write(target, data)
        inputs[name] = digest(data)
    require(not git(["status", "--porcelain"]).strip(), "Source changed during copy")
    require(all(inputs.get(name) == sha for name, sha in helper_bindings().items()),
            "Source copy differs from executing helpers")
    return copied, inputs


def build_evidence(rt, plan, output, source, project, work, props, probe, control):
    fields = ("NETCoreSdkVersion", "MSBuildVersion", "MSBuildBinPath", "RoslynTargetsPath",
              "CscToolPath", "CscToolExe", "TargetFramework", "SignAssembly", "PublicSign",
              "DelaySign", "AssemblyOriginatorKeyFile", "AssemblyVersion", "FileVersion",
              "InformationalVersion", "PackageVersion", "Configuration")
    result = invoke(rt, plan, "dotnet", ["msbuild", project, "-getProperty:" + ",".join(fields), "-p:Configuration=Release", *props],
                    output, "evaluated-properties", build=True, cwd=source)
    observed = h.decode(result.encode())["Properties"]
    require(set(fields) <= observed.keys(), "Evaluated build evidence incomplete")
    expected = {"TargetFramework": "net462", "SignAssembly": "true", "PublicSign": str(control).lower(),
                "DelaySign": str(not control).lower(), "PackageVersion": plan["version"],
                "InformationalVersion": plan["version"] + "+" + plan["source"], "Configuration": "Release"}
    require(all(observed[key] == value for key, value in expected.items()), "Evaluated signing/version properties differ")
    require(PureWindowsPath(observed["AssemblyOriginatorKeyFile"]) == PureWindowsPath(str(output / "test-public.bin")),
            "Build used a different public-key input")
    sdk = invoke(rt, plan, "dotnet", ["--version"], output, "selected-sdk", build=True, cwd=source).strip()
    require(sdk == observed["NETCoreSdkVersion"] and observed["MSBuildVersion"], "Actual SDK/MSBuild identity incomplete")
    compiler = (Path(observed["CscToolPath"]) if observed["CscToolPath"] else
                Path(observed["RoslynTargetsPath"]) / "bincore") / (observed["CscToolExe"] or "csc.dll")
    binaries = {}
    for name, path in {"msbuild": Path(observed["MSBuildBinPath"]) / "MSBuild.dll", "roslyn": compiler}.items():
        require(path.is_absolute(), "Actual compiler path required")
        version = invoke(rt, plan, "pwsh", ["-NoProfile", "-File", probe, "-Tool", path],
                         output, "compiler-product-version").rstrip("\r\n")
        require(version, "Actual compiler product version missing")
        binaries[name] = {"path": str(path), **record(path), "product_version": version}
    assets_path = work / "obj/project.assets.json"
    assets_raw = read(assets_path, 16 * 1024**2)
    assets = h.decode(assets_raw)
    packages = []
    for name, library in sorted(assets["libraries"].items()):
        require(library["type"] == "package", "Unexpected non-package build dependency")
        package_id, version = name.split("/", 1)
        expected_path = package_id.lower() + "/" + version.lower()
        require(library["path"] == expected_path and ".." not in Path(expected_path).parts,
                "Resolved package path differs")
        archive = output / "temp/packages" / expected_path / (package_id.lower() + "." + version.lower() + ".nupkg")
        data = read(archive)
        sha512 = base64.b64encode(hashlib.sha512(data).digest()).decode()
        require(sha512 == library["sha512"], "Resolved package content hash differs")
        packages.append({"id": package_id, "version": version, "sha256": digest(data), "sha512": sha512})
    ids = {row["id"].lower() for row in packages}
    require({"minver", "microsoft.sourcelink.github", "microsoft.netframework.referenceassemblies.net462"} <= ids,
            "Required resolved build inputs missing")
    return {"sdk": sdk, "evaluated_properties": observed, "binaries": binaries,
            "assets_sha256": digest(assets_raw), "packages": packages}


def prepare(args, plan, rt):
    output = fresh_output(args.output)
    rt.owned_output = output
    pin(plan, ["git", "dotnet", "pwsh", "sn"])
    probe = install_probe(output)
    versions = tool_versions(rt, plan, output, probe)
    public = read(args.public_key, 4096)
    public_shape(public)
    require(digest(public) == plan["public_key_sha256"], "Test public identity mismatch")
    write(output / "test-public.bin", public)
    source, inputs = source_copy(rt, plan, args.source_root, output)
    # Owned copy only. Global properties provide an explicit version/source overlay;
    # no Git tag or activated signing policy is fabricated.
    project = source / "src/PPDS.Plugins/PPDS.Plugins.csproj"
    version_numbers = plan["version"].split("-")[0].split(".")
    require(all(int(x) <= 65534 for x in version_numbers), "Assembly version component bound")
    overlay = [
        "-p:SignAssembly=true", "-p:AssemblyOriginatorKeyFile=" + str(output / "test-public.bin"),
        "-p:Version=" + plan["version"], "-p:PackageVersion=" + plan["version"],
        "-p:AssemblyVersion=" + ".".join(version_numbers) + ".0",
        "-p:FileVersion=" + ".".join(version_numbers) + ".0",
        "-p:InformationalVersion=" + plan["version"] + "+" + plan["source"],
        "-p:IncludeSourceRevisionInInformationalVersion=false", "-p:RepositoryCommit=" + plan["source"],
        "-p:SourceRevisionId=" + plan["source"], "-p:ContinuousIntegrationBuild=true",
        "-p:DebugType=portable"
    ]
    build_records = {}
    for control in (False, True):
        name = "public" if control else "delay"
        work = output / name
        work.mkdir()
        props = overlay + ["-p:PublicSign=" + str(control).lower(), "-p:DelaySign=" + str(not control).lower(),
                           "-p:BaseOutputPath=" + str(work / "bin") + "/",
                           "-p:BaseIntermediateOutputPath=" + str(work / "obj") + "/"]
        invoke(rt, plan, "dotnet", ["restore", project, *props], output, name+"-restore", build=True, timeout=600, cwd=source)
        invoke(rt, plan, "dotnet", ["build", project, "--configuration", "Release", "--no-restore", *props],
               output, name+"-build", build=True, timeout=600, cwd=source)
        build_records[name] = build_evidence(rt, plan, output, source, project, work, props, probe, control)
        built = work / "bin/Release/net462"
        write(output / (name+".dll"), read(built / DLL))
        if control:
            write(output / "public.pdb", read(built / "PPDS.Plugins.pdb"))
        if not control:
            write(output / "PPDS.Plugins.pdb", read(built / "PPDS.Plugins.pdb"))
            invoke(rt, plan, "dotnet", ["pack", project, "--configuration", "Release", "--no-build",
                   "--output", output / "packages", *props], output, "delay-pack", build=True, timeout=600, cwd=source)
    packages = output / "packages"
    expected = {f"PPDS.Plugins.{plan['version']}.{suffix}" for suffix in ("nupkg", "snupkg")}
    require({x.name for x in packages.iterdir()} == expected, "Exact package/symbol pair required")
    for name in expected:
        write(output / name, read(packages / name))
    nupkg = next(name for name in expected if name.endswith(".nupkg"))
    entries = dict((i.filename, b) for i, b in p.members(read(output / nupkg)))
    require(entries[p.DLL] == read(output / "delay.dll"), "Pack must use the inspected build")
    p.delay_signed_pe(read(output / "delay.dll"), public)
    before = metadata(rt, plan, output, probe, output / "delay.dll", output / "PPDS.Plugins.pdb")
    public_meta = metadata(rt, plan, output, probe, output / "public.dll", output / "public.pdb")
    signature, size, _ = pe_layout(read(output / "public.dll"))
    require(size == 128 and read(output / "public.dll")[signature:signature+size] == bytes(size),
            "Actual public-sign control reservation differs")
    invoke(rt, plan, "sn", ["-vf", output / "delay.dll"], output, "delay-forced-rejection", zero=False)
    try:
        p.delay_signed_pe(read(output / "public.dll"), public)
    except Error as error:
        require(str(error) == "Keyless input must be delay-signed, not public/fully signed",
                "Public-sign control failed for a different reason")
    else:
        raise Error("Public-sign control was admitted")
    result = common(plan, args.plan_sha256, "prepare", rt)
    result.update(metadata=before, control_metadata=public_meta, source_inputs=inputs,
                  tool_product_versions=versions, build_evidence=build_records,
                  build_overlay=overlay, source_copy_note="Tracked source bytes copied; .git omitted; explicit version/source properties",
                  files=bind_files(output, expected | {"test-public.bin", "delay.dll", "public.dll", "public.pdb", "PPDS.Plugins.pdb"}))
    result["cases"] = {"COMPAT-01": "PASS", "COMPAT-03": "PASS"}
    json_write(output / "prepare-receipt.json", result)
    return result


def sign(args, plan, rt):
    # authorize() has already refused missing approval before any input access.
    prepared, prepared_hash = receipt(args.prepared, "prepare", plan, args.plan_sha256)
    require(set(prepared["cases"]) == {"COMPAT-01", "COMPAT-03"} and
            all(x == "PASS" for x in prepared["cases"].values()), "Prepared cases incomplete")
    pin(plan, ["sn"])
    require(prepared["tool_product_versions"]["sn"] == plan["tools"]["sn"]["version"], "Prepared signer version mismatch")
    public = read(Path(args.prepared) / "test-public.bin", 4096)
    public_shape(public)
    require(digest(public) == plan["public_key_sha256"], "Prepared public identity mismatch")
    raw = read(Path(args.prepared) / "delay.dll")
    p.delay_signed_pe(raw, public)
    output = fresh_output(args.output)
    rt.owned_output = output
    write(output / "input.dll", raw)
    write(output / "signed.dll", raw)
    owned = output / "temp/disposable-key.snk"
    key_cleanup_required = False
    completed = False
    try:
        key_cleanup_required = True  # Covers a copy failure that created a partial file.
        rt.key_cleanup = "pending"
        rt.copy_key(Path(args.disposable_key), owned)
        invoke(rt, plan, "sn", ["-R", output / "signed.dll", owned], output, "sign")
        rt.remove_key(owned)
        key_cleanup_required = False
        rt.key_cleanup = "completed"
        invoke(rt, plan, "sn", ["-vf", output / "signed.dll"], output, "signed-forced-verification")
        invoke(rt, plan, "sn", ["-D", output / "input.dll", output / "signed.dll"], output, "signature-only-comparison")
        result = common(plan, args.plan_sha256, "sign", rt)
        result.update(prepare_receipt_sha256=prepared_hash, key_cleanup="completed",
                      authorization_reference=args.key_use_authorization,
                      files=bind_files(output, {"input.dll", "signed.dll"}))
        # Metadata equality is deliberately left to the fresh keyless verifier.
        result["cases"] = {"COMPAT-02": "SIGNATURE_OPERATIONS_PASSED_METADATA_PENDING"}
        json_write(output / "sign-receipt.json", result)
        completed = True
        return result
    finally:
        cleanup_failed = False
        if key_cleanup_required:
            try:
                rt.remove_key(owned)
            except FileNotFoundError:
                pass
            except Exception:
                cleanup_failed = True
            rt.key_cleanup = "failed" if cleanup_failed else "completed"
        if not completed:
            for name in ("signed.dll", "sign-receipt.json"):
                path = output / name
                if path.exists():
                    path.unlink()
        if cleanup_failed:
            raise Error("Owned key cleanup failed; discard worker and outputs") from None


def package_adapter(rt, plan, output, public_key):
    inspector = ROOT / "scripts/ci/inspect_release_assemblies.ps1"
    def inspect(directory, _production_key):
        # Existing local package rules use this explicit test-public-key adapter.
        # It performs the real inspector and pinned forced verification, no mock success.
        pin(plan, ["sn", "pwsh"])
        for dll in directory.glob("*.dll"):
            invoke(rt, plan, "sn", ["-vf", dll], output, "packaged-forced-verification")
        result = invoke(rt, plan, "pwsh", ["-NoProfile", "-File", inspector, "-Directory", directory,
                         "-ExpectedPublicKey", public_key], output, "packaged-metadata")
        rows = json.loads(result)
        require(isinstance(rows, list) and rows, "Real package inspection required")
        return rows
    return inspect


def complete_cases(cases, evidence_class):
    require(evidence_class == "real-process" and set(cases) == set(CASES)
            and all(cases[name] == "PASS" for name in CASES), "All six real cases must pass")
    return "PASS"


def verify(args, plan, rt):
    prepared, prepared_hash = receipt(args.prepared, "prepare", plan, args.plan_sha256)
    signed, signed_hash = receipt(args.signed, "sign", plan, args.plan_sha256)
    require(signed["prepare_receipt_sha256"] == prepared_hash and signed["key_cleanup"] == "completed",
            "Signing chain/cleanup mismatch")
    require(read(Path(args.prepared) / "delay.dll") == read(Path(args.signed) / "input.dll"), "Input changed across stages")
    pin(plan, ["sn", "pwsh", "git"])
    output = fresh_output(args.output)
    rt.owned_output = output
    probe = install_probe(output)
    versions = tool_versions(rt, plan, output, probe)
    public = read(Path(args.prepared) / "test-public.bin", 4096)
    require(digest(public) == plan["public_key_sha256"], "Public identity mismatch")
    write(output / "test-public.bin", public)
    raw = read(Path(args.signed) / "signed.dll")
    write(output / DLL, raw)
    pdb = read(Path(args.prepared) / "PPDS.Plugins.pdb")
    write(output / "PPDS.Plugins.pdb", pdb)
    after = metadata(rt, plan, output, probe, output / DLL, output / "PPDS.Plugins.pdb")
    require(identity(after) == identity(prepared["metadata"]), "Assembly/PDB identity changed by signing")
    invoke(rt, plan, "sn", ["-vf", output / DLL], output, "fresh-forced-verification")
    invoke(rt, plan, "sn", ["-D", Path(args.prepared) / "delay.dll", output / DLL], output, "fresh-signature-comparison")
    broken, changed, offsets = mutations(raw, after)
    write(output / "corrupt-signature.dll", broken)
    write(output / "changed-metadata.dll", changed)
    invoke(rt, plan, "sn", ["-vf", output / "corrupt-signature.dll"], output, "corrupt-signature-rejection", zero=False)
    changed_meta = metadata(rt, plan, output, probe, output / "changed-metadata.dll", output / "PPDS.Plugins.pdb")
    require(identity(changed_meta) == identity(after) and changed_meta["mutation"]["name"] != after["mutation"]["name"],
            "Mutation must remain readable and alter the selected metadata name")
    invoke(rt, plan, "sn", ["-D", Path(args.prepared) / "delay.dll", output / "changed-metadata.dll"],
           output, "non-signature-change-rejection", zero=False)
    packages = {name: read(Path(args.prepared) / name) for name in prepared["files"]
                if name.endswith((".nupkg", ".snupkg"))}
    require(len(packages) == 2, "Package and symbol pair required")
    final = output / "packages"
    p.repackage(packages, raw, final)
    graph_source, source_inputs = source_copy(rt, plan, args.source_root, output)
    require(source_inputs == prepared["source_inputs"], "Verification source differs from prepared source")
    graph = ReleaseGraph.discover(graph_source)
    adapter = package_adapter(rt, plan, output, output / "test-public.bin")
    inventories = {}
    for name, original in packages.items():
        transformed = read(final / name)
        before_members = [(i.filename, digest(b)) for i, b in p.members(original)]
        after_members = [(i.filename, digest(b)) for i, b in p.members(transformed)]
        if name.endswith(".snupkg"):
            require(transformed == original, "Symbol package changed")
            entries = dict((i.filename, b) for i, b in p.members(transformed))
            require(entries.get("lib/net462/PPDS.Plugins.pdb") == pdb, "Package PDB differs from verified identity")
        else:
            entries = dict((i.filename, b) for i, b in p.members(transformed))
            require(entries[p.DLL] == raw, "Packaged signed DLL differs")
        release.verify_nuget(final / name, "Plugins-v" + plan["version"], graph_source,
                             plan["source"], {"PPDS.Plugins": plan["version"]}, graph, inspect=adapter)
        inventories[name] = {"before_sha256": digest(original), "after_sha256": digest(transformed),
                             "members_before": before_members, "members_after": after_members}
    result = common(plan, args.plan_sha256, "verify", rt)
    cases = dict(prepared["cases"])
    cases.update({name: "PASS" for name in ("COMPAT-02", "COMPAT-04", "COMPAT-05", "COMPAT-06")})
    result.update(cases=cases, result=complete_cases(cases, rt.evidence_class),
                  prepare_receipt_sha256=prepared_hash, sign_receipt_sha256=signed_hash,
                  metadata=after, tool_product_versions=versions, mutation_offsets=offsets,
                  mutations={"signature":digest(broken), "metadata":digest(changed)},
                  packages=inventories, files=bind_files(output, {DLL, "PPDS.Plugins.pdb", "test-public.bin"}))
    json_write(output / "compatibility-receipt.json", result)
    return result


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("stage", nargs="?", choices=("prepare", "sign", "verify"))
    result.add_argument("--execute-candidate", action="store_true",
                        help="Explicit operator opt-in; does not grant key-use/release permission")
    for name in ("plan", "output", "source-root", "public-key", "prepared", "signed", "disposable-key"):
        result.add_argument("--" + name, type=Path)
    result.add_argument("--plan-sha256")
    result.add_argument("--key-use-authorization", help="Later owner approval reference; sign only")
    return result


def authorize(args):
    # Keep this pure and first: no path.exists, reads, environment or process calls.
    require(args.stage is not None and args.execute_candidate, "Explicit candidate stage and opt-in required")
    require(args.plan is not None and check_hex(args.plan_sha256) and args.output is not None,
            "Plan/hash and fresh output required")
    if args.stage == "sign":
        require(args.disposable_key is not None and isinstance(args.key_use_authorization, str)
                and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/# -]{2,199}", args.key_use_authorization),
                "Separate disposable-key authorization reference required")
        require(args.prepared is not None and args.source_root is None and args.public_key is None
                and args.signed is None, "Sign admits only prepared public data and disposable key")
    else:
        require(args.disposable_key is None and args.key_use_authorization is None,
                "Key inputs are forbidden outside sign")
        require(args.source_root is not None, "Exact source required")
        if args.stage == "prepare":
            require(args.public_key is not None and args.prepared is None and args.signed is None,
                    "Prepare accepts only source/public inputs")
        else:
            require(args.prepared is not None and args.signed is not None and args.public_key is None,
                    "Verify requires both public handoffs")
    for name in ("plan", "output", "source_root", "public_key", "prepared", "signed", "disposable_key"):
        value = getattr(args, name)
        if value is not None:
            windows_path(str(value))


def main(argv=None, *, runtime=None):
    args = parser().parse_args(argv)
    rt = None
    plan = None
    try:
        authorize(args)
        require(sys.platform == "win32", "Explicit Windows experiment required")
        plan = load_plan(args.plan, args.plan_sha256)
        rt = runtime if runtime is not None else Runtime()
        {"prepare": prepare, "sign": sign, "verify": verify}[args.stage](args, plan, rt)
        print("Candidate stage completed; this is not release or activation authorization.")
        return 0
    except Exception:
        # Input/tool exceptions may contain private paths; never emit them.
        if rt is not None and rt.owned_output is not None and plan is not None:
            try:
                json_write(rt.owned_output / "failure-receipt.json", {
                    "schema": 1, "kind": "ppds-signing-compatibility-failure", "result": "FAIL",
                    "stage": args.stage, "experiment": plan["experiment"], "source": plan["source"],
                    "tree": plan["tree"], "plan_sha256": args.plan_sha256, "recorded_utc": utc(),
                    "evidence_class": rt.evidence_class, "commands": rt.calls,
                    "key_cleanup": rt.key_cleanup,
                    "reason": "Stage failed; no passing compatibility claim. Inspect public command outcomes; private diagnostics omitted."})
            except Exception:
                pass  # Never replace failure with success if even the failure record cannot be retained.
        print("Candidate experiment refused or failed; no passing compatibility claim is available.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
