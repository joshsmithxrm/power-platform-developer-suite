#!/usr/bin/env python3
"""Data-only NuGet job handoff. Never restore/build/load assemblies, publish or request OIDC."""
from __future__ import annotations
import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

REPO = "joshsmithxrm/power-platform-developer-suite"
REPO_ID = 1116020474
PRODUCER = "build-package"
JOB_NAME = "Build and validate NuGet package"
MANIFEST = "nuget-handoff.json"
PACKAGES = {"Plugins": "PPDS.Plugins", "Dataverse": "PPDS.Dataverse", "Migration": "PPDS.Migration",
            "Auth": "PPDS.Auth", "Cli": "PPDS.Cli", "Query": "PPDS.Query", "Mcp": "PPDS.Mcp"}
MAX_PACKAGE = 512 * 1024 * 1024

class Error(Exception):
    pass

def require(value, message):
    if not value:
        raise Error(message)

def sha(raw):
    return hashlib.sha256(raw).hexdigest()

def digest(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None

def nuget_inventory_key(value):
    """Vacancy equality for normalized feed identities and legacy revisions; not release-tag grammar."""
    require(isinstance(value, str) and len(value) <= 256, "Invalid NuGet version inventory")
    match = re.fullmatch(r"([0-9]+(?:\.[0-9]+){2,3})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?", value)
    require(match is not None, "Invalid NuGet version inventory")
    numbers = tuple(int(part) for part in match[1].split('.'))
    require(all(part <= 2147483647 for part in numbers), "Invalid NuGet numeric component")
    release = match[2] or ""
    require(all(not label.isdigit() or len(label) == 1 or label[0] != '0' for label in release.split('.')),
            "Invalid NuGet prerelease component")
    # NuGet ignores metadata, normalizes numeric zeroes and compares prerelease text without case.
    return numbers + (0,) * (4 - len(numbers)), release.casefold()

def regular_bytes(path, bound):
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and not path.is_symlink() and info.st_nlink == 1
            and not getattr(info, "st_file_attributes", 0) & 0x400 and 0 < info.st_size <= bound,
            "Bounded regular handoff file required")
    raw = path.read_bytes()
    require(len(raw) == info.st_size, "Handoff file changed while reading")
    return raw

def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON key")
        result[key] = value
    return result

def decode(raw):
    require(len(raw) <= 2 * 1024 * 1024, "JSON bound")
    return json.loads(raw, object_pairs_hook=unique)

def identity(tag, source, run_id, attempt):
    match = re.fullmatch(r"(Plugins|Dataverse|Migration|Auth|Cli|Query|Mcp)-v((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?)", tag)
    require(match and re.fullmatch(r"[0-9a-f]{40}", source), "Canonical component tag/source required")
    require(re.fullmatch(r"[1-9][0-9]*", str(run_id)) and re.fullmatch(r"[1-9][0-9]*", str(attempt)), "Run/attempt required")
    return PACKAGES[match[1]], match[2]

def package_rows(directory, tag, source):
    package, version = identity(tag, source, "1", "1")
    expected = {f"{package}.{version}.nupkg", f"{package}.{version}.snupkg"}
    rows = []
    for path in sorted(directory.iterdir()):
        if path.name == MANIFEST:
            continue
        info = path.lstat()
        require(path.name in expected and stat.S_ISREG(info.st_mode) and not path.is_symlink()
                and not getattr(info, "st_file_attributes", 0) & 0x400 and info.st_nlink == 1,
                "Unexpected/link/nonregular package output")
        require(0 < info.st_size <= MAX_PACKAGE, "Package size bound")
        raw = path.read_bytes()
        require(len(raw) == info.st_size, "Package changed while reading")
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            require(0 < len(entries) <= 10000 and sum(x.file_size for x in entries) <= 2 * 1024**3,
                    "Package archive bound")
            names = []
            for entry in entries:
                name = entry.filename
                require(name == entry.orig_filename and "\\" not in name and ":" not in name
                        and not name.startswith("/") and all(p not in {"", ".", ".."} for p in name.rstrip("/").split("/"))
                        and all(ord(c) >= 32 for c in name), "Unsafe package archive member")
                require(not entry.flag_bits & 1 and stat.S_IFMT(entry.external_attr >> 16) in {0, stat.S_IFREG, stat.S_IFDIR}
                        and 0 <= entry.file_size <= MAX_PACKAGE, "Unsafe package archive entry")
                names.append(name)
            require(len(set(n.casefold() for n in names)) == len(names), "Duplicate package member")
            specs = [n for n in names if n.endswith(".nuspec")]
            require(len(specs) == 1 and archive.getinfo(specs[0]).file_size <= 1024**2, "One bounded nuspec required")
            spec = archive.read(specs[0])
            require(b"<!DOCTYPE" not in spec.upper() and b"<!ENTITY" not in spec.upper(), "Nuspec declarations refused")
            root = ET.fromstring(spec)
            def one(name):
                values = [n for n in root.iter() if n.tag.rsplit("}", 1)[-1] == name]
                require(len(values) == 1, "Unambiguous nuspec identity required")
                return values[0]
            require(one("id").text == package and one("version").text == version
                    and one("repository").get("commit") == source, "Package tag/source identity mismatch")
        rows.append({"name": path.name, "bytes": len(raw), "sha256": sha(raw)})
    require(len(rows) in {1, 2} and any(r["name"].endswith(".nupkg") for r in rows), "One package and optional symbols required")
    return rows

def create(directory, tag, source, run_id, attempt):
    identity(tag, source, run_id, attempt)
    result = {"schema": 1, "repository": REPO, "source": source, "tag": tag, "run_id": str(run_id),
              "run_attempt": str(attempt), "producer_job": PRODUCER,
              "files": package_rows(directory, tag, source)}
    raw = (json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n").encode()
    with (directory / MANIFEST).open("xb") as stream:
        stream.write(raw)
    return sha(raw)

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise Error("Metadata redirect refused")

def fetch_json(url):
    parsed = urllib.parse.urlparse(url)
    require(parsed.scheme == "https" and not parsed.username and not parsed.password and parsed.port in {None, 443}
            and parsed.hostname in {"api.github.com", "api.nuget.org"}, "Unapproved metadata host")
    headers = {"Accept": "application/json", "User-Agent": "PPDS-NuGet-job-handoff"}
    if parsed.hostname == "api.github.com":
        token = os.environ.get("GH_TOKEN")
        require(token, "Read-only workflow token required")
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(url, headers=headers, method="GET")
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=30) as response:
            require(response.status == 200, "Metadata lookup unavailable")
            raw = response.read(2 * 1024 * 1024 + 1)
        return decode(raw)
    except Error:
        raise
    except Exception:
        raise Error("Read-only source or registry lookup failed") from None

def verify(directory, tag, source, run_id, attempt, artifact_id, artifact_digest, manifest_sha, *, fetch=fetch_json):
    package, version = identity(tag, source, run_id, attempt)
    require(re.fullmatch(r"[1-9][0-9]*", str(artifact_id)) and digest(artifact_digest) and digest(manifest_sha), "Exact artifact/hash binding required")
    raw = regular_bytes(directory / MANIFEST, 2 * 1024 * 1024)
    require(sha(raw) == manifest_sha, "Handoff manifest hash mismatch")
    data = decode(raw)
    require(set(data) == {"schema", "repository", "source", "tag", "run_id", "run_attempt", "producer_job", "files"}
            and type(data["schema"]) is int and data["schema"] == 1 and data["repository"] == REPO and data["source"] == source and data["tag"] == tag
            and data["run_id"] == str(run_id) and data["run_attempt"] == str(attempt) and data["producer_job"] == PRODUCER,
            "Handoff source/run/job mismatch")
    require(data["files"] == package_rows(directory, tag, source), "Package hashes or inventory changed after validation")
    base = "https://api.github.com/repos/" + REPO
    artifact = fetch(base + "/actions/artifacts/" + str(artifact_id))
    require(artifact.get("id") == int(artifact_id) and artifact.get("name") == f"nuget-package-{run_id}-{attempt}-{source}"
            and artifact.get("digest") == "sha256:" + artifact_digest and artifact.get("expired") is False
            and type(artifact.get("size_in_bytes")) is int and 0 < artifact["size_in_bytes"] <= MAX_PACKAGE,
            "Artifact service identity/digest mismatch")
    run = artifact.get("workflow_run", {})
    require(run.get("id") == int(run_id) and run.get("head_sha") == source
            and run.get("repository_id") == REPO_ID and run.get("head_repository_id") == REPO_ID,
            "Artifact service run/source mismatch")
    jobs = fetch(base + f"/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100&page=1")
    require(isinstance(jobs.get("jobs"), list) and jobs.get("total_count") == len(jobs["jobs"]) <= 100,
            "Incomplete producer job inventory")
    matches = [j for j in jobs["jobs"] if j.get("name") == JOB_NAME]
    require(len(matches) == 1, "Producer job absent/ambiguous")
    job = matches[0]
    require(job.get("status") == "completed" and job.get("conclusion") == "success" and job.get("head_sha") == source
            and job.get("run_id") == int(run_id) and job.get("run_attempt") == int(attempt)
            and type(job.get("id")) is int and job["id"] > 0, "Producer job not successful for exact source/attempt")
    timestamp = lambda value: datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(timestamp(job["started_at"]) <= timestamp(artifact["created_at"]) <= timestamp(job["completed_at"]),
            "Artifact outside producer job interval")
    reference = fetch(base + "/git/ref/tags/" + urllib.parse.quote(tag, safe=""))
    require(reference.get("ref") == "refs/tags/" + tag, "Exact release tag reference required")
    obj, seen = reference.get("object", {}), set()
    for _ in range(5):
        require(re.fullmatch(r"[0-9a-f]{40}", str(obj.get("sha", ""))) and obj["sha"] not in seen, "Malformed/cyclic release tag")
        seen.add(obj["sha"])
        if obj.get("type") == "commit":
            break
        require(obj.get("type") == "tag", "Release tag must peel to commit")
        obj = fetch(base + "/git/tags/" + obj["sha"]).get("object", {})
    require(obj.get("type") == "commit" and obj.get("sha") == source, "Release tag moved from event source")
    registry = fetch("https://api.nuget.org/v3-flatcontainer/" + package.casefold() + "/index.json")
    versions = registry.get("versions")
    require(isinstance(versions, list) and versions, "Incomplete NuGet version inventory")
    require(nuget_inventory_key(version) not in {nuget_inventory_key(v) for v in versions}, "NuGet version already occupied")
    return {"schema": 1, "source": source, "tag": tag, "run_id": str(run_id), "run_attempt": str(attempt),
            "producer_job_id": job["id"], "artifact_id": int(artifact_id), "artifact_service_digest": artifact_digest,
            "manifest_sha256": manifest_sha, "packages": data["files"], "vacant": True,
            "package_code_assembly_MSBuild_or_OIDC_executed": False}

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["create", "verify"])
    for name in ["directory", "tag", "source", "run-id", "attempt"]:
        parser.add_argument("--" + name, required=True, type=Path if name == "directory" else str)
    for name in ["artifact-id", "artifact-digest", "manifest-sha", "output"]:
        parser.add_argument("--" + name)
    args = parser.parse_args(argv)
    try:
        if args.command == "create":
            digest_value = create(args.directory, args.tag, args.source, args.run_id, args.attempt)
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                stream.write("manifest-sha256=" + digest_value + "\n")
        else:
            result = verify(args.directory, args.tag, args.source, args.run_id, args.attempt,
                            args.artifact_id, args.artifact_digest, args.manifest_sha)
            require(args.output, "Fresh verification receipt required")
            with Path(args.output).open("x", encoding="utf-8") as stream:
                json.dump(result, stream, indent=2)
                stream.write("\n")
            package, _ = identity(args.tag, args.source, args.run_id, args.attempt)
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                stream.write("package=" + package + "\ntag=" + args.tag + "\n")
        return 0
    except Exception:
        print("STOP before NuGet login: package handoff validation failed.", file=__import__("sys").stderr)
        return 1

if __name__ == "__main__":
    raise SystemExit(main())
