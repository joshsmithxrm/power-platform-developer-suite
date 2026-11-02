#!/usr/bin/env python3
"""Fail closed before publishing. Read artifacts/registries; never publish or repair."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from typing import Any
import xml.etree.ElementTree as ET
import zipfile

if __package__ in {None, ""}:
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from scripts.ci.release_model import ReleaseGraph
from scripts.ci import verify_public_release as public

Error = public.VerificationError


def run(command: list[str], cwd: Path | None = None) -> str:
    # Preflight commands never prompt or inherit a caller's interactive stdin.
    result = subprocess.run(command, cwd=cwd, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=120, check=False)
    if result.returncode:
        raise Error(f"Read-only command failed ({command[0]}): {result.stderr[-1500:]}")
    return result.stdout


def object_json(text: str | bytes) -> dict[str, Any]:
    result = json.loads(text)
    if not isinstance(result, dict):
        raise Error("Expected a JSON object")
    return result


def strict_version(value: str) -> str:
    version = public.release_version(value)
    if version != value:
        raise Error("Release tag/package versions must not contain build metadata")
    return version


def resolve_cli_version(tags: list[str], extension_tag: str) -> str:
    _, extension_version = public.parse_release_tag(extension_tag, "Extension")
    strict_version(extension_version)
    matches = [tag for tag in tags if tag.startswith("Cli-v")]
    if extension_tag not in tags or len(matches) != 1:
        raise Error("Extension requires one unambiguous same-commit CLI tag")
    _, version = public.parse_release_tag(matches[0], "Cli")
    strict_version(version)
    if int(extension_version.split(".")[1]) % 2 == 0 and "-" in version:
        raise Error("Stable Extension cannot bundle a prerelease CLI")
    return version


def tagged_graph(root: Path, tag: str, *, runner=run) -> tuple[str, dict[str, str], ReleaseGraph]:
    package, version = public.package_from_tag(tag)
    strict_version(version)
    graph = ReleaseGraph.discover(root)
    sha = runner(["git", "rev-parse", "HEAD"], root).strip()
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise Error("Invalid source commit")
    tags = runner(["git", "tag", "--points-at", sha], root).splitlines()
    if tag not in tags:
        raise Error("Requested release tag does not point at the checkout")
    needed: set[str] = set()
    frontier = [package]
    while frontier:
        name = frontier.pop()
        if name in needed:
            continue
        needed.add(name)
        frontier.extend(graph.nodes[name].project_dependencies)
    versions = {}
    for name in sorted(needed & graph.surfaces.keys()):
        prefix = graph.surfaces[name].tag_prefix
        matches = [candidate for candidate in tags if candidate.startswith(prefix)]
        if len(matches) != 1:
            raise Error(f"Expected one same-commit tag for {name}; found {matches}")
        dep_version = strict_version(matches[0][len(prefix):])
        if "-" not in version and "-" in dep_version:
            raise Error(f"Stable release has a prerelease internal dependency: {name}")
        versions[name] = dep_version
    return sha, versions, graph


def checked_zip(path: Path) -> zipfile.ZipFile:
    archive = zipfile.ZipFile(path)
    names = archive.namelist()
    if len(set(name.casefold() for name in names)) != len(names):
        archive.close()
        raise Error("Artifact contains duplicate archive paths")
    return archive


def xml_children(node: ET.Element, name: str) -> list[ET.Element]:
    return [child for child in node if child.tag.rsplit("}", 1)[-1] == name]


def one_xml(node: ET.Element, name: str) -> ET.Element:
    matches = xml_children(node, name)
    if len(matches) != 1:
        raise Error(f"Expected one XML {name}")
    return matches[0]


def inspect_assemblies(directory: Path, public_key: Path | None, *, runner=run) -> list[dict]:
    command = ["pwsh", "-NoProfile", "-File", str(Path(__file__).with_name("inspect_release_assemblies.ps1")), "-Directory", str(directory)]
    if public_key:
        command += ["-ExpectedPublicKey", str(public_key)]
    result = json.loads(runner(command))
    if not isinstance(result, list) or not result:
        raise Error("Assembly inspection did not return a nonempty inventory")
    return result


def verify_nuget(path: Path, tag: str, root: Path, sha: str, versions: dict[str, str], graph: ReleaseGraph, *, inspect=inspect_assemblies) -> None:
    package, version = public.package_from_tag(tag)
    surface = graph.surfaces[package]
    project = ET.parse(root / surface.project_path).getroot()
    frameworks = next((node.text for node in project.iter() if node.tag in {"TargetFramework", "TargetFrameworks"}), "").split(";")
    dependencies = surface.project_dependencies & graph.surfaces.keys()
    with checked_zip(path) as archive:
        specs = [name for name in archive.namelist() if name.endswith(".nuspec")]
        if len(specs) != 1:
            raise Error("Expected exactly one package nuspec")
        metadata = one_xml(ET.fromstring(archive.read(specs[0])), "metadata")
        if one_xml(metadata, "id").text != package or one_xml(metadata, "version").text != version:
            raise Error("Package ID/version differs from release tag")
        if one_xml(metadata, "repository").get("commit") != sha:
            raise Error("Package repository commit differs from release source")
        groups = [node for node in metadata.iter() if node.tag.rsplit("}", 1)[-1] == "group"]
        if dependencies and not surface.is_tool:
            target_frameworks = [group.get("targetFramework") for group in groups]
            if len(target_frameworks) != len(frameworks) or set(target_frameworks) != set(frameworks):
                raise Error("Internal dependency groups do not cover the expected frameworks")
        for group in groups:
            if group.tag.rsplit("}", 1)[-1] != "group":
                continue
            internal = {}
            for dep in xml_children(group, "dependency"):
                name = dep.get("id", "")
                if name.casefold().startswith("ppds."):
                    if name in internal:
                        raise Error("Duplicate internal package dependency")
                    internal[name] = dep.get("version")
            if set(internal) != (set() if surface.is_tool else dependencies):
                raise Error("Package internal dependency graph differs from project graph")
            for name, actual in internal.items():
                if actual not in {versions[name], f"[{versions[name]}]"}:
                    raise Error(f"Internal dependency {name} does not match its same-commit tag")
        if path.suffix == ".snupkg":
            return
        assembly_map = {}
        for name in versions if surface.is_tool else [package]:
            dep_project = ET.parse(root / graph.surfaces[name].project_path).getroot()
            assembly = next((node.text for node in dep_project.iter() if node.tag == "AssemblyName"), name)
            assembly_map[assembly] = name
        prefix = "tools" if surface.is_tool else "lib"
        expected_paths = {f"{prefix}/{tfm}/" + ("any/" if surface.is_tool else "") + f"{assembly}.dll" for tfm in frameworks for assembly in assembly_map}
        actual_paths = {name for name in archive.namelist() if name.endswith(".dll") and (Path(name).stem in assembly_map or Path(name).stem.startswith("PPDS."))}
        if actual_paths != expected_paths:
            raise Error("Package framework/assembly inventory differs from expected graph")
        with tempfile.TemporaryDirectory(prefix="ppds-release-inspect-") as temporary:
            for tfm in frameworks:
                directory = Path(temporary) / tfm
                directory.mkdir()
                for assembly in assembly_map:
                    entry = f"{prefix}/{tfm}/" + ("any/" if surface.is_tool else "") + f"{assembly}.dll"
                    (directory / f"{assembly}.dll").write_bytes(archive.read(entry))
                key = root / "src/PPDS.Plugins/PPDS.Plugins.PublicKey" if package == "PPDS.Plugins" else None
                rows = inspect(directory, key)
                names = [row.get("name") for row in rows]
                if len(names) != len(set(names)) or set(names) != set(assembly_map):
                    raise Error("Assembly metadata inventory differs from package graph")
                for row in rows:
                    expected = f"{versions[assembly_map[row['name']]]}+{sha}"
                    if row.get("version") != expected:
                        raise Error(f"Assembly {row['name']} informational version differs from tag/source: expected {expected}")


def nuget_vacant(package: str, version: str, *, fetch=public.default_http_fetch) -> None:
    data = object_json(fetch(f"https://api.nuget.org/v3-flatcontainer/{package.casefold()}/index.json", "GET", {}, None))
    versions = data.get("versions")
    if not isinstance(versions, list) or not versions or not all(isinstance(item, str) for item in versions):
        raise Error("Incomplete NuGet version inventory")
    for item in versions:
        strict_version(item)
    if any(public.versions_match(item, version) for item in versions):
        raise Error(f"NuGet version is already occupied: {package} {version}")


def marketplace_vacant(version: str, target: str | None = None, *, fetch=public.default_http_fetch) -> None:
    data = object_json(fetch("https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery", "POST", {"Accept":"application/json;api-version=7.2-preview.1", "Content-Type":"application/json"}, public.marketplace_query_body()))
    results = data.get("results")
    if not isinstance(results, list) or len(results) != 1 or not isinstance(results[0], dict):
        raise Error("Incomplete Marketplace query")
    extensions = results[0].get("extensions")
    if not isinstance(extensions, list) or len(extensions) != 1:
        raise Error("Expected the existing Marketplace extension")
    extension = extensions[0]
    if not isinstance(extension, dict) or extension.get("extensionName") != public.MARKETPLACE_EXTENSION or not isinstance(extension.get("publisher"), dict) or extension["publisher"].get("publisherName") != public.MARKETPLACE_PUBLISHER:
        raise Error("Marketplace returned an unexpected identity")
    versions = extension.get("versions")
    if not isinstance(versions, list) or not versions:
        raise Error("Incomplete Marketplace version inventory")
    for item in versions:
        if not isinstance(item, dict) or not isinstance(item.get("version"), str):
            raise Error("Malformed Marketplace version")
        strict_version(item["version"])
        if item["version"] == version and (target is None or item.get("targetPlatform") in {target, "universal", None}):
            raise Error(f"Marketplace version/target is already occupied: {version} {target or 'any'}")


def verify_vsix(path: Path, tag: str, cli_version: str, target: str, source: str, extension_root: Path, *, prerelease: bool) -> None:
    _, version = public.parse_release_tag(tag, "Extension")
    strict_version(version)
    if prerelease != (int(version.split(".")[1]) % 2 == 1):
        raise Error("Extension tag/channel parity mismatch")
    strict_version(cli_version)
    if not prerelease and "-" in cli_version:
        raise Error("Stable Extension cannot bundle a prerelease CLI")
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise Error("Invalid bundled CLI source commit")
    package = object_json((extension_root / "package.json").read_text(encoding="utf-8"))
    lock = object_json((extension_root / "package-lock.json").read_text(encoding="utf-8"))
    if package.get("publisher") != public.MARKETPLACE_PUBLISHER or package.get("name") != public.MARKETPLACE_EXTENSION or lock.get("name") != public.MARKETPLACE_EXTENSION:
        raise Error("Extension source identity mismatch")
    if package.get("version") != version or lock.get("version") != version or lock.get("packages", {}).get("", {}).get("version") != version:
        raise Error("Extension source manifest/lock/tag mismatch")
    public.verify_vsix(path.read_bytes(), target=target, extension_version=version, cli_version=cli_version)
    with checked_zip(path) as archive:
        manifest = ET.fromstring(archive.read("extension.vsixmanifest"))
        identity = next((node for node in manifest.iter() if node.tag.rsplit("}", 1)[-1] == "Identity"), None)
        if identity is None or identity.get("Id") != public.MARKETPLACE_EXTENSION or identity.get("Publisher") != public.MARKETPLACE_PUBLISHER or identity.get("Version") != version or identity.get("TargetPlatform") != target:
            raise Error("VSIX XML identity/platform differs from tag")
        pre = [node.get("Value") for node in manifest.iter() if node.get("Id") == "Microsoft.VisualStudio.Code.PreRelease"]
        if pre != (["true"] if prerelease else []):
            raise Error("VSIX prerelease channel mismatch")
        bundled = object_json(archive.read("extension/bin/ppds.version.json"))
        if bundled.get("informationalVersion") != f"{cli_version}+{source}":
            raise Error("Bundled CLI source commit differs from release source")
        binary = "ppds.exe" if target.startswith("win32-") else "ppds"
        if archive.read(f"extension/bin/{binary}") != (extension_root / "bin" / binary).read_bytes():
            raise Error("VSIX bundled CLI bytes differ from validated build output")


def changelog_notes(text: str, version: str) -> str:
    headings = list(re.finditer(r"^## \[([^\]\r\n]+)\][^\r\n]*$", text, re.MULTILINE))
    matches = [index for index, heading in enumerate(headings) if heading.group(1) == version]
    if len(matches) != 1:
        raise Error("Expected one exact-version CHANGELOG section")
    index = matches[0]
    end = headings[index + 1].start() if index + 1 < len(headings) else len(text)
    notes = text[headings[index].end():end].strip()
    notes = re.split(r"(?m)^\[[^\]]+\]:", notes)[0].strip()
    if not notes or not re.search(r"(?m)^[-*] \S", notes):
        raise Error("Release CHANGELOG section has no release notes")
    return notes + "\n"


def cli_release_plan(pages: Any, tag: str, sha: str, run_id: str, notes: str, directory: Path, *, require_complete: bool = False) -> dict:
    _, version = public.parse_release_tag(tag, "Cli")
    strict_version(version)
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or not re.fullmatch(r"[0-9]+", run_id):
        raise Error("Invalid release source/workflow identity")
    expected = {*public.GITHUB_BINARY_ASSETS, public.GITHUB_CHECKSUM_ASSET}
    if {path.name for path in directory.iterdir()} != expected:
        raise Error("CLI release assets differ from expected inventory")
    checksums = public.parse_checksums((directory / public.GITHUB_CHECKSUM_ASSET).read_text(encoding="utf-8"))
    for name, digest in checksums.items():
        if hashlib.sha256((directory / name).read_bytes()).hexdigest() != digest:
            raise Error("Local CLI asset checksum mismatch")
    notes = notes.replace(f"https://github.com/{public.REPOSITORY}/blob/main/", f"https://github.com/{public.REPOSITORY}/blob/{sha}/")
    body = notes.rstrip() + f"\n\n<!-- ppds-release source={sha} run={run_id} -->\n"
    if not isinstance(pages, list) or not pages or not all(isinstance(page, list) for page in pages):
        raise Error("Incomplete GitHub release inventory")
    releases = [release for page in pages for release in page]
    if any(not isinstance(release, dict) or not isinstance(release.get("tag_name"), str) for release in releases):
        raise Error("Malformed GitHub release inventory")
    matches = [release for release in releases if release["tag_name"] == tag]
    if len(matches) > 1:
        raise Error("Ambiguous GitHub release inventory")
    missing = set(expected)
    if matches:
        release = matches[0]
        if release.get("draft") is not True or release.get("target_commitish") != sha or release.get("body") != body or release.get("prerelease") is not ("-" in version):
            raise Error("GitHub version occupied by a published or unowned/changed draft release")
        assets = release.get("assets")
        if not isinstance(assets, list):
            raise Error("Incomplete GitHub draft asset inventory")
        for asset in assets:
            if not isinstance(asset, dict) or asset.get("name") not in missing:
                raise Error("Unexpected or duplicate draft asset")
            name = asset["name"]
            content = (directory / name).read_bytes()
            if asset.get("state") != "uploaded" or asset.get("size") != len(content) or asset.get("digest") != "sha256:" + hashlib.sha256(content).hexdigest():
                raise Error("Existing draft asset has unknown or mismatched bytes")
            missing.remove(name)
    if require_complete and (not matches or missing):
        raise Error("Draft is not complete; stop before public release")
    return {"existing_draft": bool(matches), "body": body, "missing_assets": [str(directory / name) for name in sorted(missing)]}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    nuget = commands.add_parser("nuget")
    nuget.add_argument("--tag", required=True)
    nuget.add_argument("--directory", type=Path, required=True)
    nuget.add_argument("--root", type=Path, default=Path.cwd())
    vacant = commands.add_parser("marketplace-vacant")
    vacant.add_argument("--tag", required=True)
    vacant.add_argument("--target", choices=list(public.MARKETPLACE_TARGETS))
    resolve = commands.add_parser("resolve-cli")
    resolve.add_argument("--tag", required=True)
    resolve.add_argument("--source", required=True)
    resolve.add_argument("--output", type=Path, required=True)
    vsix = commands.add_parser("vsix")
    for arg in ("tag", "cli-version", "target", "source", "channel"):
        vsix.add_argument("--" + arg, required=True)
    vsix.add_argument("--path", type=Path, required=True)
    vsix.add_argument("--extension-root", type=Path, required=True)
    binary = commands.add_parser("binary")
    binary.add_argument("--path", type=Path, required=True)
    binary.add_argument("--tag", required=True)
    binary.add_argument("--source", required=True)
    cli = commands.add_parser("cli-plan")
    for arg in ("tag", "source", "run-id"):
        cli.add_argument("--" + arg, required=True)
    for arg in ("changelog", "directory", "output", "notes"):
        cli.add_argument("--" + arg, type=Path, required=True)
    cli.add_argument("--require-complete", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.command == "nuget":
            sha, versions, graph = tagged_graph(args.root, args.tag)
            package, version = public.package_from_tag(args.tag)
            files = sorted(args.directory.iterdir())
            if len([path for path in files if path.suffix == ".nupkg"]) != 1 or len([path for path in files if path.suffix == ".snupkg"]) > 1 or any(path.suffix not in {".nupkg", ".snupkg"} for path in files):
                raise Error("Unexpected package output inventory")
            for path in files:
                verify_nuget(path, args.tag, args.root, sha, versions, graph)
            nuget_vacant(package, version)
        elif args.command == "marketplace-vacant":
            _, version = public.parse_release_tag(args.tag, "Extension")
            marketplace_vacant(strict_version(version), args.target)
        elif args.command == "resolve-cli":
            if not re.fullmatch(r"[0-9a-f]{40}", args.source) or run(["git", "rev-parse", "HEAD"]).strip() != args.source:
                raise Error("CLI resolution source does not match checkout")
            tags = run(["git", "tag", "--points-at", args.source]).splitlines()
            version = resolve_cli_version(tags, args.tag)
            args.output.write_text(json.dumps({"version": version}) + "\n", encoding="utf-8")
        elif args.command == "vsix":
            if args.channel not in {"true", "false"}:
                raise Error("Invalid channel value")
            verify_vsix(args.path, args.tag, args.cli_version, args.target, args.source, args.extension_root, prerelease=args.channel == "true")
        elif args.command == "binary":
            _, version = public.parse_release_tag(args.tag, "Cli")
            actual = public.extract_cli_version(run([str(args.path.resolve()), "--version"]))
            if actual != f"{strict_version(version)}+{args.source}" or not re.fullmatch(r"[0-9a-f]{40}", args.source):
                raise Error("Native CLI binary version differs from release tag")
        elif args.command == "cli-plan":
            _, version = public.parse_release_tag(args.tag, "Cli")
            notes = changelog_notes(args.changelog.read_text(encoding="utf-8"), strict_version(version))
            pages = json.loads(run(["gh", "api", f"repos/{public.REPOSITORY}/releases?per_page=100", "--paginate", "--slurp"]))
            plan = cli_release_plan(pages, args.tag, args.source, args.run_id, notes, args.directory, require_complete=args.require_complete)
            args.notes.write_text(plan["body"], encoding="utf-8")
            args.output.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
        print(f"Release input gate passed: {args.command}")
        return 0
    except (Error, public.ArtifactPending, OSError, ValueError, KeyError, TypeError, ET.ParseError, zipfile.BadZipFile, subprocess.SubprocessError) as error:
        print(f"::error title=Release input gate failed::STOP before publication: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
