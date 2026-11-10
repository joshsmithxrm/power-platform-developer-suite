#!/usr/bin/env python3
"""Real package preparation and verification. This helper never reads a signing key or signs.

The initial source policy refuses admission. Subprocess mocks only establish helper
behavior; real sn.exe compatibility is a separate required activation receipt.
"""
from __future__ import annotations

import argparse
import io
import json
import os
from pathlib import Path
import stat
import struct
import subprocess
import sys
import zipfile

if __package__ in {None, ""}:
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from scripts.ci import verify_nuget_handoff as h

DLL = "lib/net462/PPDS.Plugins.dll"
RECEIPT = "signing-receipt.json"
POLICY = Path(__file__).with_name("plugin-signing-policy.json")
PUBLIC_KEY = Path(__file__).resolve().parents[2] / "src/PPDS.Plugins/PPDS.Plugins.PublicKey"


def write_json(path, value):
    raw = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()
    with path.open("xb") as stream:
        stream.write(raw)
    return h.sha(raw)


def fresh_directory(path):
    for parent in (path, *path.parents):
        if parent.exists():
            info = parent.lstat()
            h.require(not parent.is_symlink() and not getattr(info, "st_file_attributes", 0) & 0x400,
                      "Linked signing directory refused")
    path.mkdir(parents=True, exist_ok=False)


def members(raw):
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        infos = archive.infolist()
        h.require(not archive.comment and 0 < len(infos) <= 10000
                  and sum(x.file_size for x in infos) <= 2 * 1024**3, "Package archive bound/comment")
        names = set()
        result = []
        for entry in infos:
            name = entry.filename
            h.require(name == entry.orig_filename and "\\" not in name and ":" not in name
                      and not name.startswith("/") and all(p not in {"", ".", ".."} for p in name.rstrip("/").split("/"))
                      and all(ord(c) >= 32 for c in name) and name.casefold() not in names
                      and not entry.flag_bits & 1 and not entry.comment
                      and stat.S_IFMT(entry.external_attr >> 16) in {0, stat.S_IFREG, stat.S_IFDIR}
                      and 0 <= entry.file_size <= h.MAX_PACKAGE, "Unsafe/duplicate package member")
            names.add(name.casefold())
            result.append((entry, archive.read(entry)))
        return result


def snapshot(directory, tag, source):
    rows = h.package_rows(directory, tag, source)
    packages = {}
    for row in rows:
        raw = h.regular_bytes(directory / row["name"], h.MAX_PACKAGE)
        h.require(h.sha(raw) == row["sha256"], "Package changed during snapshot")
        packages[row["name"]] = raw
    regular = [name for name in packages if name.endswith(".nupkg")]
    h.require(len(regular) == 1, "One Plugins package required")
    entries = dict((i.filename, raw) for i, raw in members(packages[regular[0]]))
    h.require({n for n in entries if n.lower().endswith((".dll", ".exe", ".netmodule"))} == {DLL},
              "Plugins executable member inventory changed")
    return rows, packages, entries[DLL]


def delay_signed_pe(raw, public_key):
    """Read PE/CLI headers only; require reserved zero signature bytes, never load code."""
    def u16(offset):
        return struct.unpack_from("<H", raw, offset)[0]
    def u32(offset):
        return struct.unpack_from("<I", raw, offset)[0]
    h.require(len(raw) >= 256 and raw[:2] == b"MZ", "Expected PE assembly")
    pe = u32(60)
    h.require(pe + 24 <= len(raw) and raw[pe:pe + 4] == b"PE\0\0", "Invalid PE header")
    optional = pe + 24
    size = u16(pe + 20)
    magic = u16(optional)
    h.require(magic in {0x10b, 0x20b} and optional + size <= len(raw), "Invalid PE optional header")
    directories = optional + (96 if magic == 0x10b else 112)
    h.require(directories + 15 * 8 <= optional + size, "Missing CLI header directory")
    section = optional + size
    section_count = u16(pe + 6)
    h.require(0 < section_count <= 96 and section + section_count * 40 <= len(raw), "Invalid PE sections")
    def offset(rva, length):
        matches = []
        for index in range(section_count):
            current = section + index * 40
            va, raw_size, raw_start = u32(current + 12), u32(current + 16), u32(current + 20)
            if va <= rva and rva + length <= va + raw_size and raw_start + rva - va + length <= len(raw):
                matches.append(raw_start + rva - va)
        h.require(len(matches) == 1, "Unbounded/ambiguous PE address")
        return matches[0]
    cli_rva, cli_size = u32(directories + 14 * 8), u32(directories + 14 * 8 + 4)
    h.require(cli_size >= 72, "Invalid CLI header")
    cli = offset(cli_rva, 72)
    h.require(not u32(cli + 16) & 8, "Keyless input must be delay-signed, not public/fully signed")
    signature_rva, signature_size = u32(cli + 32), u32(cli + 36)
    h.require(len(public_key) >= 32, "Invalid expected public key")
    bits = struct.unpack_from("<I", public_key, 24)[0]
    h.require(bits >= 512 and bits % 8 == 0 and signature_size == bits // 8, "Signature reservation size mismatch")
    start = offset(signature_rva, signature_size)
    h.require(raw[start:start + signature_size] == bytes(signature_size), "Keyless signature reservation is not empty")


def run(command):
    result = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=120, check=False)
    h.require(result.returncode == 0, "Plugins signature/metadata tool failed")
    return result.stdout


def inspect(dll, key, tag, source, *, delay=False, runner=run):
    command = ["pwsh", "-NoProfile", "-File", str(Path(__file__).with_name("inspect_release_assemblies.ps1")),
               "-Directory", str(dll.parent), "-ExpectedPublicKey", str(key)]
    if delay:
        command.append("-DelaySignedInput")
    rows = json.loads(runner(command))
    h.require(isinstance(rows, list) and len(rows) == 1 and rows[0].get("name") == "PPDS.Plugins"
              and rows[0].get("version") == tag.removeprefix("Plugins-v") + "+" + source,
              "Plugins assembly source/version mismatch")


def context(tag, source, run_id, attempt):
    h.require(h.identity(tag, source, run_id, attempt)[0] == "PPDS.Plugins", "Plugins tag required")
    return {"schema": 1, "repository": h.REPO, "source": source, "tag": tag,
            "run_id": str(run_id), "run_attempt": str(attempt)}


def prepare(directory, tag, source, run_id, attempt, *, key=PUBLIC_KEY):
    value = context(tag, source, run_id, attempt)
    rows, _, dll = snapshot(directory, tag, source)
    public_key = h.regular_bytes(key, 4096)
    delay_signed_pe(dll, public_key)
    value.update(kind="plugins-keyless", files=rows, input_dll_sha256=h.sha(dll), public_key_sha256=h.sha(public_key))
    return write_json(directory / h.MANIFEST, value)


def keyless(directory, tag, source, run_id, attempt, manifest_sha):
    raw = h.regular_bytes(directory / h.MANIFEST, 2 * 1024**2)
    h.require(h.digest(manifest_sha) and h.sha(raw) == manifest_sha, "Keyless manifest hash mismatch")
    value = h.decode(raw)
    expected = context(tag, source, run_id, attempt)
    h.require(set(value) == set(expected) | {"kind", "files", "input_dll_sha256", "public_key_sha256"}
              and all(value[k] == v and type(value[k]) is type(v) for k, v in expected.items())
              and value["kind"] == "plugins-keyless", "Keyless source/run binding mismatch")
    rows, packages, dll = snapshot(directory, tag, source)
    h.require(value["files"] == rows and value["input_dll_sha256"] == h.sha(dll), "Keyless payload changed")
    return value, packages, dll


def admit(directory, output, tag, source, run_id, attempt, artifact_id, artifact_digest, manifest_sha,
          *, policy_path=POLICY, key=PUBLIC_KEY, fetch=h.fetch_json, runner=run):
    policy, policy_sha = h.signing_policy(policy_path)  # Before any output or credential-bearing step.
    value, _, dll = keyless(directory, tag, source, run_id, attempt, manifest_sha)
    public_key = h.regular_bytes(key, 4096)
    h.require(h.sha(public_key) == value["public_key_sha256"] == policy["public_key_sha256"], "Public key binding mismatch")
    delay_signed_pe(dll, public_key)
    h.artifact_job(artifact_id, artifact_digest, source, run_id, attempt, "nuget-keyless", h.JOB_NAME, fetch=fetch)
    fresh_directory(output)
    target = output / "PPDS.Plugins.dll"
    target.write_bytes(dll)
    inspect(target, key, tag, source, delay=True, runner=runner)
    request = {k: value[k] for k in ("schema", "source", "tag", "run_id", "run_attempt", "input_dll_sha256", "public_key_sha256")}
    request["policy_sha256"] = policy_sha
    write_json(output / "signing-request.json", request)


def repackage(packages, signed_dll, output):
    fresh_directory(output)
    for name, original in packages.items():
        destination = output / name
        if name.endswith(".snupkg"):
            with destination.open("xb") as stream:
                stream.write(original)
            continue
        old = members(original)
        h.require(sum(info.filename == DLL for info, _ in old) == 1, "Exact Plugins DLL missing")
        with zipfile.ZipFile(destination, "x") as archive:
            for info, raw in old:
                archive.writestr(info, signed_dll if info.filename == DLL else raw)
        new = members(h.regular_bytes(destination, h.MAX_PACKAGE))
        h.require([i.filename for i, _ in old] == [i.filename for i, _ in new]
                  and all(after == (signed_dll if before_info.filename == DLL else before)
                          for (before_info, before), (_, after) in zip(old, new)), "Package transform changed unrelated bytes")


def verify_signed(directory, signed, output, chain_output, tag, source, run_id, attempt, build, sign,
                  *, policy_path=POLICY, key=PUBLIC_KEY, fetch=h.fetch_json, runner=run):
    policy, policy_sha = h.signing_policy(policy_path)
    value, packages, dll = keyless(directory, tag, source, run_id, attempt, build["manifest_sha256"])
    h.require({p.name for p in signed.iterdir()} == {"PPDS.Plugins.dll", RECEIPT}, "Unexpected signer output inventory")
    receipt_raw = h.regular_bytes(signed / RECEIPT, 65536)
    h.require(h.sha(receipt_raw) == sign["manifest_sha256"], "Signer receipt hash mismatch")
    receipt = h.decode(receipt_raw)
    expected = {k: value[k] for k in ("schema", "source", "tag", "run_id", "run_attempt", "input_dll_sha256", "public_key_sha256")}
    expected.update(policy_sha256=policy_sha, signer_sha256=policy["signer"]["sha256"])
    h.require(all(receipt.get(k) == v and type(receipt.get(k)) is type(v) for k, v in expected.items()), "Signer request binding mismatch")
    signed_dll = h.regular_bytes(signed / "PPDS.Plugins.dll", h.MAX_PACKAGE)
    h.require(receipt.get("signed_dll_sha256") == h.sha(signed_dll), "Signed DLL hash mismatch")
    public_key = h.regular_bytes(key, 4096)
    h.require(h.sha(public_key) == value["public_key_sha256"] == policy["public_key_sha256"], "Public key binding mismatch")
    delay_signed_pe(dll, public_key)
    chain = {"policy_sha256": policy_sha, "public_key_sha256": policy["public_key_sha256"],
             "signer_sha256": policy["signer"]["sha256"], "input_dll_sha256": h.sha(dll),
             "signed_dll_sha256": h.sha(signed_dll), "build": build, "sign": sign}
    h.verify_plugin_chain(chain, source, run_id, attempt, fetch=fetch)
    signer = Path(policy["signer"]["path"])
    h.require(h.sha(h.regular_bytes(signer, 16 * 1024**2)) == policy["signer"]["sha256"], "Signing verifier tool changed")
    # The fresh verifier does not accept the signer's own success assertion.
    inspect(signed / "PPDS.Plugins.dll", key, tag, source, runner=runner)
    baseline = output.with_name(output.name + "-baseline")
    fresh_directory(baseline)
    (baseline / "PPDS.Plugins.dll").write_bytes(dll)
    runner([str(signer), "-vf", str(signed / "PPDS.Plugins.dll")])
    runner([str(signer), "-D", str(baseline / "PPDS.Plugins.dll"), str(signed / "PPDS.Plugins.dll")])
    repackage(packages, signed_dll, output)
    write_json(chain_output, chain)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["policy", "prepare", "admit", "verify"])
    for name in ("directory", "output", "signed", "chain-output"):
        parser.add_argument("--" + name, type=Path)
    for name in ("tag", "source", "run-id", "attempt", "artifact-id", "artifact-digest", "manifest-sha", "sign-artifact-id", "sign-artifact-digest", "sign-manifest-sha"):
        parser.add_argument("--" + name)
    args = parser.parse_args(argv)
    try:
        if args.command == "policy":
            h.signing_policy(POLICY)
            return 0
        common = (args.directory, args.tag, args.source, args.run_id, args.attempt)
        if args.command == "prepare":
            digest = prepare(*common)
            if os.environ.get("GITHUB_OUTPUT"):
                with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                    stream.write("manifest-sha256=" + digest + "\n")
        elif args.command == "admit":
            admit(args.directory, args.output, args.tag, args.source, args.run_id, args.attempt,
                  args.artifact_id, args.artifact_digest, args.manifest_sha)
        else:
            build = {"artifact_id": args.artifact_id, "artifact_digest": args.artifact_digest, "manifest_sha256": args.manifest_sha}
            sign = {"artifact_id": args.sign_artifact_id, "artifact_digest": args.sign_artifact_digest, "manifest_sha256": args.sign_manifest_sha}
            verify_signed(args.directory, args.signed, args.output, args.chain_output, args.tag, args.source, args.run_id, args.attempt, build, sign)
        return 0
    except Exception:
        print("Plugins preparation/verification refused; no signing key was accessed by this helper.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
