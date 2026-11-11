#!/usr/bin/env python3
"""Offline preparation/comparison only. Never authenticate independence or admit signing.

No network, process execution, credential discovery, signing, or workflow mutation.
Request/admission hashes are caller pins, not proof of where those pins originated.
An independently controlled authentication and key-admission service is still needed.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone, timedelta
import hashlib
import io
import json
from pathlib import Path
import re
import stat
import struct
import sys
import xml.etree.ElementTree as ET
import zipfile

REPOSITORY = "joshsmithxrm/power-platform-developer-suite"
MAX_FILE = 256 * 1024 * 1024
MAX_JSON = 1024 * 1024
INPUTS = ("source_bundle", "git_history", "recipe", "toolchain", "dependencies", "settings")
ROLES = ("dll", "pdb", "xml", "package", "symbols")
DLL = "lib/net462/PPDS.Plugins.dll"
PDB = "lib/net462/PPDS.Plugins.pdb"
XML = "lib/net462/PPDS.Plugins.xml"


class Error(ValueError):
    pass


def require(value, reason):
    if not value:
        raise Error(reason)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def hex_value(value, length=64):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{%d}" % length, value) is not None


def fields(value, names, reason):
    require(isinstance(value, dict) and set(value) == set(names), reason)


def label(value):
    return isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/@-]{0,199}", value) is not None


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON key")
        result[key] = value
    return result


def decode(raw):
    require(len(raw) <= MAX_JSON, "JSON size bound")
    try:
        return json.loads(raw, object_pairs_hook=unique,
                          parse_constant=lambda _: (_ for _ in ()).throw(Error("Non-finite JSON")))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise Error("Invalid UTF-8 JSON") from exc


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()


def local(path):
    require(".." not in Path(path).parts, "Parent traversal refused")
    path = Path(path).absolute()
    require(sys.platform != "win32" or re.fullmatch(r"[A-Za-z]:", path.drive) is not None,
            "Local drive path required; UNC/device paths refused")
    # Never follow a link/junction while traversing a local input tree.
    for part in reversed((path, *path.parents)):
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400,
                "Linked path refused")
    return path


def read(path, bound=MAX_FILE):
    path = local(path)
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and 0 < info.st_size <= bound,
            "Bounded regular non-hardlinked file required")
    raw = path.read_bytes()
    after = path.stat()
    require(len(raw) == info.st_size and (info.st_size, info.st_mtime_ns) == (after.st_size, after.st_mtime_ns),
            "Input changed while reading")
    return raw


def pinned(path, expected):
    require(hex_value(expected), "Expected SHA256 required")
    raw = read(path, MAX_JSON)
    require(sha(raw) == expected, "Pinned JSON digest mismatch")
    return decode(raw)


def write_new(path, value):
    path = local(path)
    with path.open("xb") as stream:
        stream.write(canonical(value))


def utc(value):
    require(isinstance(value, str) and re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", value), "Canonical UTC timestamp required")
    try:
        return datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError as exc:
        raise Error("Invalid UTC timestamp") from exc


def validate_request(value, now=None):
    fields(value, ("schema", "mode", "repository", "source", "version", "nonce", "not_before", "expires",
                   "candidate", "reference", "inputs", "outputs", "signature_bytes"), "Unexpected request fields")
    require(type(value["schema"]) is int and value["schema"] == 1 and value["mode"] == "OFFLINE_COMPARISON_ONLY",
            "Preparation-only schema required")
    require(value["repository"] == REPOSITORY, "Wrong repository")
    fields(value["source"], ("commit", "tree"), "Exact source commit/tree required")
    require(all(hex_value(v, 40) for v in value["source"].values()), "Invalid source identity")
    require(hex_value(value["nonce"], 32), "Request nonce required")
    start, end = utc(value["not_before"]), utc(value["expires"])
    require(start < end <= start + timedelta(hours=24), "Request lifetime must be positive and at most 24 hours")
    require(start <= (now or datetime.now(timezone.utc)) < end, "Request not current")
    for name in ("candidate", "reference"):
        fields(value[name], ("controller", "invocation"), "Controller/invocation binding required")
        require(all(label(v) for v in value[name].values()), "Invalid controller/invocation label")
    require(value["candidate"]["controller"].casefold() != value["reference"]["controller"].casefold(),
            "Same declared control domain refused")
    require(value["candidate"]["invocation"] != value["reference"]["invocation"], "Same invocation refused")
    fields(value["inputs"], INPUTS, "Complete input-identity set required")
    require(all(hex_value(v) for v in value["inputs"].values()), "Exact input digests required")
    require(isinstance(value["version"], str) and re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", value["version"]),
            "Canonical version required")
    require(value["outputs"] == output_names(value["version"]), "Exact five pre-signing outputs required")
    require(type(value["signature_bytes"]) is int and value["signature_bytes"] in (64, 128, 256, 384, 512, 1024),
            "Expected strong-name reservation size required")
    return value


def output_names(version):
    return {"dll": "PPDS.Plugins.dll", "pdb": "PPDS.Plugins.pdb", "xml": "PPDS.Plugins.xml",
            "package": f"PPDS.Plugins.{version}.nupkg", "symbols": f"PPDS.Plugins.{version}.snupkg"}


def prepare(request_path, request_sha, inputs_directory, output, now=None):
    request = validate_request(pinned(request_path, request_sha), now)
    directory = local(inputs_directory)
    require(directory.is_dir() and {p.name for p in directory.iterdir()} == set(INPUTS), "Exact six input files required")
    observations = {}
    for name in INPUTS:
        raw = read(directory / name)
        require(sha(raw) == request["inputs"][name], "Prepared input bytes differ from request")
        observations[name] = {"sha256": sha(raw), "bytes": len(raw)}
    result = {"schema": 1, "status": "INPUT_BYTES_BOUND_ONLY", "request_sha256": request_sha,
              "inputs": observations, "source_or_history_semantics_verified": False,
              "independent_rebuild_verified": False, "signing_admission": False,
              "scope": "Opaque input digests measured; no rebuild, source extraction, key operation or authentication performed"}
    write_new(output, result)
    return result


def delay_signed(raw, reservation):
    """PE header checks adapted from current verify_plugin_signing.delay_signed_pe.

    Attribution: PPDS commit 3be21b1, scripts/ci/verify_plugin_signing.py.
    This never loads the assembly and is not a replacement for CLR compatibility.
    """
    def number(fmt, offset):
        size = struct.calcsize(fmt)
        require(0 <= offset <= len(raw) - size, "Truncated PE input")
        return struct.unpack_from(fmt, raw, offset)[0]
    u16 = lambda offset: number("<H", offset)
    u32 = lambda offset: number("<I", offset)
    require(len(raw) >= 256 and raw[:2] == b"MZ", "Expected PE assembly")
    pe = u32(60)
    require(pe + 24 <= len(raw) and raw[pe:pe + 4] == b"PE\0\0", "Invalid PE header")
    optional, size = pe + 24, u16(pe + 20)
    magic = u16(optional)
    require(magic in (0x10B, 0x20B) and optional + size <= len(raw), "Invalid PE optional header")
    directories = optional + (96 if magic == 0x10B else 112)
    require(directories + 15 * 8 <= optional + size, "Missing CLI directory")
    section, count = optional + size, u16(pe + 6)
    require(0 < count <= 96 and section + count * 40 <= len(raw), "Invalid PE sections")
    def offset(rva, length):
        matches = []
        for index in range(count):
            current = section + index * 40
            va, raw_size, raw_start = u32(current + 12), u32(current + 16), u32(current + 20)
            if va <= rva and rva + length <= va + raw_size and raw_start + rva - va + length <= len(raw):
                matches.append(raw_start + rva - va)
        require(len(matches) == 1, "Ambiguous/unbounded PE address")
        return matches[0]
    require(u32(directories + 14 * 8 + 4) >= 72, "Invalid CLI header")
    cli = offset(u32(directories + 14 * 8), 72)
    flags = u32(cli + 16)
    require(flags & 1 and not flags & 8, "IL-only delay-signed input required")
    require(u32(cli + 36) == reservation, "Strong-name reservation size mismatch")
    start = offset(u32(cli + 32), reservation)
    require(raw[start:start + reservation] == bytes(reservation), "Signature reservation is not empty")


def archive(raw):
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as z:
            infos = z.infolist()
            require(not z.comment and 0 < len(infos) <= 10000 and sum(i.file_size for i in infos) <= MAX_FILE,
                    "Archive size/comment bound")
            result, names = {}, set()
            for i in infos:
                n = i.filename
                require(n == i.orig_filename and not n.endswith("/") and "\\" not in n and ":" not in n and not n.startswith("/")
                        and all(s not in ("", ".", "..") for s in n.split("/")) and all(ord(c) >= 32 for c in n)
                        and n.casefold() not in names and not i.flag_bits & 1 and not i.comment
                        and i.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)
                        and stat.S_IFMT(i.external_attr >> 16) in (0, stat.S_IFREG)
                        and 0 <= i.file_size <= MAX_FILE, "Unsafe or duplicate archive member")
                names.add(n.casefold())
                result[n] = z.read(i)
            return result
    except (zipfile.BadZipFile, RuntimeError, NotImplementedError) as exc:
        raise Error("Unreadable package") from exc


def package_identity(members, request):
    specs = [raw for name, raw in members.items() if name.endswith(".nuspec")]
    require(len(specs) == 1 and len(specs[0]) <= MAX_JSON and b"<!DOCTYPE" not in specs[0].upper()
            and b"<!ENTITY" not in specs[0].upper(), "One bounded declaration-free nuspec required")
    try:
        root = ET.fromstring(specs[0])
    except ET.ParseError as exc:
        raise Error("Invalid nuspec") from exc
    def one(name):
        values = [n for n in root.iter() if n.tag.rsplit("}", 1)[-1] == name]
        require(len(values) == 1, "Ambiguous nuspec identity")
        return values[0]
    require(one("id").text == "PPDS.Plugins" and one("version").text == request["version"]
            and one("repository").get("commit") == request["source"]["commit"], "Package source/version identity mismatch")


def inventory(directory, request):
    directory = local(directory)
    require(directory.is_dir() and {p.name for p in directory.iterdir()} == set(request["outputs"].values()),
            "Unexpected, missing or extra build output")
    raw = {role: read(directory / name) for role, name in request["outputs"].items()}
    delay_signed(raw["dll"], request["signature_bytes"])
    require(raw["pdb"].startswith(b"BSJB"), "Portable PDB metadata required")
    package, symbols = archive(raw["package"]), archive(raw["symbols"])
    for members in (package, symbols):
        package_identity(members, request)
    require(package.get(DLL) == raw["dll"] and symbols.get(PDB) == raw["pdb"] and package.get(XML) == raw["xml"],
            "Standalone/package DLL, PDB or XML mismatch")
    executables = lambda m: {n for n in m if n.casefold().endswith((".dll", ".exe", ".netmodule"))}
    require(executables(package) == {DLL} and not executables(symbols), "Unexpected executable package member")
    return {role: {"name": request["outputs"][role], "bytes": len(data), "sha256": sha(data)} for role, data in raw.items()}


def receipt(value, request, request_sha, role, actual, observed):
    fields(value, ("schema", "request_sha256", "nonce", "source", "inputs", "controller", "invocation", "started", "completed", "phase", "outputs"),
           "Unexpected build-receipt fields")
    require(type(value["schema"]) is int and value["schema"] == 1 and value["request_sha256"] == request_sha
            and value["nonce"] == request["nonce"] and value["source"] == request["source"]
            and value["inputs"] == request["inputs"], "Receipt request/source/input mismatch")
    require(value["controller"] == request[role]["controller"] and value["invocation"] == request[role]["invocation"],
            "Receipt controller/invocation mismatch")
    require(utc(request["not_before"]) <= utc(value["started"]) < utc(value["completed"]) <= min(utc(request["expires"]), observed),
            "Receipt outside request lifetime")
    require(value["phase"] == "DELAY_SIGNED_PRE_SIGNING" and value["outputs"] == actual, "Receipt artifact/phase mismatch")


def compare(request_path, request_sha, admission_path, admission_sha, candidate_directory, candidate_receipt,
            reference_directory, reference_receipt, output, now=None):
    observed = now or datetime.now(timezone.utc)
    request = validate_request(pinned(request_path, request_sha), observed)
    admission = pinned(admission_path, admission_sha)
    fields(admission, ("schema", "mode", "request_sha256", "candidate_receipt_sha256", "reference_receipt_sha256"),
           "Unexpected external-admission pin fields")
    require(type(admission["schema"]) is int and admission["schema"] == 1 and admission["mode"] == "COMPARISON_PINS_ONLY"
            and admission["request_sha256"] == request_sha, "Wrong admission pin scope")
    candidate = pinned(candidate_receipt, admission["candidate_receipt_sha256"])
    reference = pinned(reference_receipt, admission["reference_receipt_sha256"])
    require(not local(candidate_directory).samefile(local(reference_directory)), "Distinct artifact directories required")
    left, right = inventory(candidate_directory, request), inventory(reference_directory, request)
    receipt(candidate, request, request_sha, "candidate", left, observed)
    receipt(reference, request, request_sha, "reference", right, observed)
    different = [role for role in ROLES if left[role] != right[role]]
    result = {"schema": 1, "status": "ARTIFACT_BYTES_DIFFER" if different else "BYTE_IDENTICAL_COMPARISON_ONLY",
              "request_sha256": request_sha, "admission_pins_sha256": admission_sha,
              "candidate_receipt_sha256": admission["candidate_receipt_sha256"],
              "reference_receipt_sha256": admission["reference_receipt_sha256"],
              "candidate_outputs": left, "reference_outputs": right, "different_roles": different,
              "normalization_applied": False, "controller_labels_authenticated": False,
              "rebuild_executed": False, "source_or_history_semantics_verified": False,
              "independent_rebuild_verified": False, "signing_admission": False, "release_admission": False,
              "remaining": "External controller ownership, authenticated result channel, trusted comparison execution and non-bypassable key admission are not implemented or established by this tool"}
    write_new(output, result)
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    comp = sub.add_parser("compare")
    for p in (prep, comp):
        p.add_argument("--request", type=Path, required=True)
        p.add_argument("--request-sha256", required=True)
        p.add_argument("--output", type=Path, required=True)
    prep.add_argument("--inputs", type=Path, required=True)
    for name in ("admission", "candidate-directory", "candidate-receipt", "reference-directory", "reference-receipt"):
        comp.add_argument("--" + name, type=Path, required=True)
    comp.add_argument("--admission-sha256", required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "prepare":
            result = prepare(args.request, args.request_sha256, args.inputs, args.output)
        else:
            result = compare(args.request, args.request_sha256, args.admission, args.admission_sha256,
                             args.candidate_directory, args.candidate_receipt, args.reference_directory,
                             args.reference_receipt, args.output)
        print(json.dumps({"status": result["status"], "independent_rebuild_verified": False}))
        return 1 if result["status"] == "ARTIFACT_BYTES_DIFFER" else 0
    except (Error, OSError, ValueError) as exc:
        print("Rebuild comparison refused: " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
