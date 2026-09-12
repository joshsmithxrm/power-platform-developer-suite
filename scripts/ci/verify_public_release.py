#!/usr/bin/env python3
"""Verify that a PPDS release is usable from its public distribution channel.

This script deliberately uses only public, unauthenticated endpoints.  It is run
after publishing has completed and is read-only: failures stop the workflow and
require human escalation; they never attempt to alter a published artifact.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
import time
from typing import Any, Callable, Mapping, NoReturn, Sequence
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen
import zipfile


POLL_INTERVAL_SECONDS = 120
MAX_POLL_ATTEMPTS = 30
HTTP_TIMEOUT_SECONDS = 60
PROCESS_TIMEOUT_SECONDS = 600

REPOSITORY = "joshsmithxrm/power-platform-developer-suite"
MARKETPLACE_PUBLISHER = "JoshSmithXRM"
MARKETPLACE_EXTENSION = "power-platform-developer-suite"
NUGET_SOURCE = "https://api.nuget.org/v3/index.json"

TAG_PACKAGES = {
    "Plugins": "PPDS.Plugins",
    "Dataverse": "PPDS.Dataverse",
    "Migration": "PPDS.Migration",
    "Auth": "PPDS.Auth",
    "Cli": "PPDS.Cli",
    "Query": "PPDS.Query",
    "Mcp": "PPDS.Mcp",
}

TOOL_COMMANDS = {
    "PPDS.Cli": "ppds",
    "PPDS.Mcp": "ppds-mcp-server",
}

GITHUB_BINARY_ASSETS = (
    "ppds-win-x64.exe",
    "ppds-win-arm64.exe",
    "ppds-osx-x64",
    "ppds-osx-arm64",
    "ppds-linux-x64",
)
GITHUB_CHECKSUM_ASSET = "checksums.sha256"

MARKETPLACE_TARGETS = {
    "win32-x64": "win-x64",
    "linux-x64": "linux-x64",
    "darwin-x64": "osx-x64",
    "darwin-arm64": "osx-arm64",
}

SEMVER_PATTERN = re.compile(
    r"(?P<version>"
    r"(?:0|[1-9]\d*)\."
    r"(?:0|[1-9]\d*)\."
    r"(?:0|[1-9]\d*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
    r")"
)


class ArtifactPending(Exception):
    """The public channel has not finished propagating an expected artifact."""


class VerificationError(Exception):
    """The published artifact is present but invalid, or polling was exhausted."""


HttpFetch = Callable[[str, str, Mapping[str, str], bytes | None], bytes]
CommandRunner = Callable[[Sequence[str], Path | None, Mapping[str, str] | None], subprocess.CompletedProcess[str]]


def release_version(version: str) -> str:
    """Return the SemVer identity used for release comparisons."""

    match = SEMVER_PATTERN.fullmatch(version)
    if not match:
        raise VerificationError(f"Invalid semantic version: {version!r}")
    return match.group("version").split("+", 1)[0]


def parse_release_tag(tag: str, expected_prefix: str | None = None) -> tuple[str, str]:
    """Parse and validate a PPDS ``Prefix-vSemVer`` release tag."""

    match = re.fullmatch(r"([A-Za-z][A-Za-z0-9]*)-v(.+)", tag)
    if not match:
        raise VerificationError(f"Invalid PPDS release tag: {tag!r}")
    prefix, version = match.groups()
    if expected_prefix is not None and prefix != expected_prefix:
        raise VerificationError(
            f"Expected a {expected_prefix}-v* tag, received {tag!r}"
        )
    release_version(version)
    return prefix, version


def package_from_tag(tag: str) -> tuple[str, str]:
    prefix, version = parse_release_tag(tag)
    try:
        return TAG_PACKAGES[prefix], version
    except KeyError as error:
        raise VerificationError(f"Unsupported NuGet release tag prefix: {prefix!r}") from error


def versions_match(actual: str, expected: str) -> bool:
    try:
        return release_version(actual).casefold() == release_version(expected).casefold()
    except VerificationError:
        return False


def extract_cli_version(output: str) -> str:
    """Extract one unambiguous SemVer from public CLI ``--version`` output."""

    matches = {match.group("version") for match in SEMVER_PATTERN.finditer(output)}
    if len(matches) != 1:
        raise VerificationError(
            "CLI --version output did not contain exactly one semantic version: "
            f"{output.strip()!r}"
        )
    return matches.pop()


def default_http_fetch(
    url: str,
    method: str = "GET",
    headers: Mapping[str, str] | None = None,
    body: bytes | None = None,
) -> bytes:
    request_headers = {
        "Accept": "application/json",
        "User-Agent": "PPDS-public-release-verifier",
        **(dict(headers) if headers else {}),
    }
    request = Request(url, data=body, headers=request_headers, method=method)
    try:
        with urlopen(request, timeout=HTTP_TIMEOUT_SECONDS) as response:  # noqa: S310
            return response.read()
    except HTTPError as error:
        if error.code in {404, 408, 425, 429, 500, 502, 503, 504}:
            raise ArtifactPending(f"{url} returned HTTP {error.code}") from error
        raise VerificationError(f"{url} returned HTTP {error.code}") from error
    except (TimeoutError, URLError) as error:
        raise ArtifactPending(f"Could not reach {url}: {error}") from error


def default_command_runner(
    command: Sequence[str],
    cwd: Path | None = None,
    env: Mapping[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            list(command),
            cwd=cwd,
            env=dict(env) if env is not None else None,
            capture_output=True,
            text=True,
            timeout=PROCESS_TIMEOUT_SECONDS,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ArtifactPending(f"Command could not complete: {command[0]}: {error}") from error


def fetch_json(
    fetch: HttpFetch,
    url: str,
    *,
    method: str = "GET",
    headers: Mapping[str, str] | None = None,
    body: bytes | None = None,
) -> Any:
    try:
        return json.loads(fetch(url, method, headers or {}, body).decode("utf-8"))
    except ArtifactPending:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ArtifactPending(f"Public endpoint returned incomplete JSON: {url}") from error


def poll_until_available(
    probe: Callable[[], Any],
    label: str,
    *,
    attempts: int = MAX_POLL_ATTEMPTS,
    interval_seconds: int = POLL_INTERVAL_SECONDS,
    sleep: Callable[[float], None] = time.sleep,
    notice: Callable[[str], None] = print,
) -> Any:
    """Run ``probe`` at a two-minute cadence until it succeeds or the bound expires."""

    if attempts < 1:
        raise VerificationError("Polling attempts must be at least one")
    if interval_seconds < 0:
        raise VerificationError("Polling interval cannot be negative")

    last_error = "artifact not visible"
    for attempt in range(1, attempts + 1):
        try:
            return probe()
        except ArtifactPending as error:
            last_error = str(error)
            if attempt == attempts:
                break
            notice(
                f"{label} is not ready ({last_error}); "
                f"attempt {attempt}/{attempts}. Retrying in {interval_seconds} seconds."
            )
            sleep(interval_seconds)
    raise VerificationError(
        f"{label} was not publicly usable after {attempts} attempts "
        f"at {interval_seconds}-second intervals: {last_error}"
    )


def _run_or_pending(
    runner: CommandRunner,
    command: Sequence[str],
    *,
    cwd: Path,
    env: Mapping[str, str],
    action: str,
) -> subprocess.CompletedProcess[str]:
    result = runner(command, cwd, env)
    if result.returncode != 0:
        detail = (result.stderr or result.stdout or "no command output").strip()
        raise ArtifactPending(f"{action} failed: {detail[-1000:]}")
    return result


def verify_nuget_once(
    package: str,
    version: str,
    *,
    fetch: HttpFetch = default_http_fetch,
    runner: CommandRunner = default_command_runner,
) -> dict[str, Any]:
    """Verify one NuGet package through the public feed and an isolated install."""

    if package not in TAG_PACKAGES.values():
        raise VerificationError(f"Unsupported NuGet package: {package}")
    release_version(version)

    index_url = (
        "https://api.nuget.org/v3-flatcontainer/"
        f"{quote(package.casefold())}/index.json"
    )
    payload = fetch_json(fetch, index_url)
    listed_versions = payload.get("versions") if isinstance(payload, dict) else None
    if not isinstance(listed_versions, list):
        raise ArtifactPending(f"NuGet registration for {package} is incomplete")
    if not any(isinstance(item, str) and versions_match(item, version) for item in listed_versions):
        raise ArtifactPending(f"NuGet does not list {package} {version} yet")

    with tempfile.TemporaryDirectory(prefix="ppds-public-nuget-") as temp_name:
        temp = Path(temp_name)
        packages = temp / "packages"
        cli_home = temp / "dotnet-home"
        config = temp / "NuGet.Config"
        config.write_text(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            "<configuration><packageSources><clear />"
            f"<add key=\"nuget.org\" value=\"{NUGET_SOURCE}\" />"
            "</packageSources></configuration>\n",
            encoding="utf-8",
        )
        env = {
            **os.environ,
            "DOTNET_CLI_HOME": str(cli_home),
            "NUGET_PACKAGES": str(packages),
            "NUGET_XMLDOC_MODE": "skip",
            "DOTNET_NOLOGO": "1",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        }

        if package in TOOL_COMMANDS:
            tool_dir = temp / "tool"
            install = [
                "dotnet",
                "tool",
                "install",
                package,
                "--tool-path",
                str(tool_dir),
                "--version",
                version,
                "--configfile",
                str(config),
                "--no-cache",
            ]
            _run_or_pending(
                runner,
                install,
                cwd=temp,
                env=env,
                action=f"Clean nuget.org install of {package} {version}",
            )
            if package == "PPDS.Cli":
                executable = tool_dir / ("ppds.exe" if os.name == "nt" else "ppds")
                result = _run_or_pending(
                    runner,
                    [str(executable), "--version"],
                    cwd=temp,
                    env=env,
                    action="Public PPDS CLI execution",
                )
                actual = extract_cli_version(f"{result.stdout}\n{result.stderr}")
                if not versions_match(actual, version):
                    raise VerificationError(
                        f"Public PPDS CLI version mismatch: expected {version}, got {actual}"
                    )
        else:
            target_framework = "net462" if package == "PPDS.Plugins" else "net8.0"
            project = temp / "verify.csproj"
            project.write_text(
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                f"<TargetFramework>{target_framework}</TargetFramework>"
                "</PropertyGroup><ItemGroup>"
                f"<PackageReference Include=\"{package}\" Version=\"{version}\" />"
                "</ItemGroup></Project>\n",
                encoding="utf-8",
            )
            restore = [
                "dotnet",
                "restore",
                str(project),
                "--configfile",
                str(config),
                "--packages",
                str(packages),
                "--no-cache",
            ]
            _run_or_pending(
                runner,
                restore,
                cwd=temp,
                env=env,
                action=f"Clean nuget.org restore of {package} {version}",
            )

    return {"channel": "nuget.org", "package": package, "version": version}


def verify_nuget(
    tag: str,
    package: str,
    *,
    fetch: HttpFetch = default_http_fetch,
    runner: CommandRunner = default_command_runner,
    attempts: int = MAX_POLL_ATTEMPTS,
    interval_seconds: int = POLL_INTERVAL_SECONDS,
    sleep: Callable[[float], None] = time.sleep,
) -> dict[str, Any]:
    expected_package, version = package_from_tag(tag)
    if package != expected_package:
        raise VerificationError(
            f"Tag {tag!r} maps to {expected_package}, not workflow package {package}"
        )
    return poll_until_available(
        lambda: verify_nuget_once(package, version, fetch=fetch, runner=runner),
        f"NuGet package {package} {version}",
        attempts=attempts,
        interval_seconds=interval_seconds,
        sleep=sleep,
    )


def parse_checksums(contents: str) -> dict[str, str]:
    checksums: dict[str, str] = {}
    for line in contents.splitlines():
        if not line.strip():
            continue
        match = re.fullmatch(r"([0-9a-fA-F]{64})\s+\*?(.+)", line.strip())
        if not match:
            raise VerificationError(f"Malformed checksum line: {line!r}")
        digest, name = match.groups()
        if name in checksums:
            raise VerificationError(f"Duplicate checksum entry for {name}")
        checksums[name] = digest.casefold()

    expected = set(GITHUB_BINARY_ASSETS)
    if set(checksums) != expected:
        missing = sorted(expected - set(checksums))
        unexpected = sorted(set(checksums) - expected)
        raise VerificationError(
            f"Checksum target coverage mismatch; missing={missing}, unexpected={unexpected}"
        )
    return checksums


def verify_github_release_once(
    tag: str,
    *,
    fetch: HttpFetch = default_http_fetch,
    runner: CommandRunner = default_command_runner,
) -> dict[str, Any]:
    _, version = parse_release_tag(tag, "Cli")
    api_url = f"https://api.github.com/repos/{REPOSITORY}/releases/tags/{quote(tag)}"
    release = fetch_json(fetch, api_url)
    if not isinstance(release, dict) or release.get("draft") is not False:
        raise ArtifactPending(f"GitHub release {tag} is not public yet")

    assets = release.get("assets")
    if not isinstance(assets, list):
        raise ArtifactPending(f"GitHub release {tag} has no public asset list yet")

    by_name: dict[str, str] = {}
    for asset in assets:
        if not isinstance(asset, dict):
            continue
        name, url = asset.get("name"), asset.get("browser_download_url")
        if not isinstance(name, str) or not isinstance(url, str):
            continue
        if name in by_name:
            raise VerificationError(f"GitHub release has duplicate asset {name}")
        by_name[name] = url

    expected_assets = {*GITHUB_BINARY_ASSETS, GITHUB_CHECKSUM_ASSET}
    missing = sorted(expected_assets - set(by_name))
    if missing:
        raise ArtifactPending(f"GitHub release {tag} is missing assets: {missing}")

    checksum_bytes = fetch(by_name[GITHUB_CHECKSUM_ASSET], "GET", {}, None)
    try:
        checksums = parse_checksums(checksum_bytes.decode("utf-8"))
    except UnicodeDecodeError as error:
        raise VerificationError("checksums.sha256 is not UTF-8 text") from error

    binaries: dict[str, bytes] = {}
    for name in GITHUB_BINARY_ASSETS:
        content = fetch(by_name[name], "GET", {}, None)
        actual_hash = hashlib.sha256(content).hexdigest()
        if actual_hash != checksums[name]:
            raise VerificationError(
                f"GitHub asset checksum mismatch for {name}: "
                f"expected {checksums[name]}, got {actual_hash}"
            )
        binaries[name] = content

    with tempfile.TemporaryDirectory(prefix="ppds-public-cli-") as temp_name:
        executable = Path(temp_name) / "ppds-linux-x64"
        executable.write_bytes(binaries["ppds-linux-x64"])
        executable.chmod(executable.stat().st_mode | stat.S_IXUSR)
        result = runner([str(executable), "--version"], executable.parent, os.environ)
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "no command output").strip()
            raise VerificationError(f"Public GitHub CLI execution failed: {detail[-1000:]}")
        actual_version = extract_cli_version(f"{result.stdout}\n{result.stderr}")
        if not versions_match(actual_version, version):
            raise VerificationError(
                f"Public GitHub CLI version mismatch: expected {version}, got {actual_version}"
            )

    return {
        "channel": "GitHub Releases",
        "tag": tag,
        "version": version,
        "assets": sorted(expected_assets),
    }


def verify_github_release(
    tag: str,
    *,
    fetch: HttpFetch = default_http_fetch,
    runner: CommandRunner = default_command_runner,
    attempts: int = MAX_POLL_ATTEMPTS,
    interval_seconds: int = POLL_INTERVAL_SECONDS,
    sleep: Callable[[float], None] = time.sleep,
) -> dict[str, Any]:
    return poll_until_available(
        lambda: verify_github_release_once(tag, fetch=fetch, runner=runner),
        f"GitHub release {tag}",
        attempts=attempts,
        interval_seconds=interval_seconds,
        sleep=sleep,
    )


def marketplace_query_body() -> bytes:
    return json.dumps(
        {
            "filters": [
                {
                    "criteria": [
                        {
                            "filterType": 7,
                            "value": f"{MARKETPLACE_PUBLISHER}.{MARKETPLACE_EXTENSION}",
                        }
                    ],
                    "pageNumber": 1,
                    "pageSize": 100,
                    "sortBy": 0,
                    "sortOrder": 0,
                }
            ],
            "assetTypes": [],
            "flags": 131,
        },
        separators=(",", ":"),
    ).encode("utf-8")


def marketplace_version_assets(payload: Any, version: str) -> dict[str, str]:
    try:
        extensions = payload["results"][0]["extensions"]
    except (KeyError, IndexError, TypeError) as error:
        raise ArtifactPending("Marketplace extension query response is incomplete") from error

    extension = next(
        (
            item
            for item in extensions
            if isinstance(item, dict)
            and str(item.get("extensionName", "")).casefold()
            == MARKETPLACE_EXTENSION.casefold()
            and str(item.get("publisher", {}).get("publisherName", "")).casefold()
            == MARKETPLACE_PUBLISHER.casefold()
        ),
        None,
    )
    if extension is None:
        raise ArtifactPending("PPDS extension is not visible in the Marketplace query")

    matching = [
        item
        for item in extension.get("versions", [])
        if isinstance(item, dict) and versions_match(str(item.get("version", "")), version)
    ]
    if not matching:
        raise ArtifactPending(f"Marketplace does not list extension version {version} yet")

    assets: dict[str, str] = {}
    expected_targets = set(MARKETPLACE_TARGETS)
    for item in matching:
        target = item.get("targetPlatform")
        if target not in expected_targets:
            continue
        if target in assets:
            raise VerificationError(
                f"Marketplace version {version} has duplicate target {target}"
            )
        package_files = [
            file
            for file in item.get("files", [])
            if isinstance(file, dict)
            and file.get("assetType") == "Microsoft.VisualStudio.Services.VSIXPackage"
            and isinstance(file.get("source"), str)
        ]
        if len(package_files) != 1:
            raise ArtifactPending(
                f"Marketplace target {target} does not expose one public VSIX package yet"
            )
        assets[target] = package_files[0]["source"]

    missing = sorted(expected_targets - set(assets))
    if missing:
        raise ArtifactPending(
            f"Marketplace version {version} is missing public targets: {missing}"
        )
    return assets


def verify_vsix(
    contents: bytes,
    *,
    target: str,
    extension_version: str,
    cli_version: str,
) -> None:
    binary_name = "ppds.exe" if target.startswith("win32-") else "ppds"
    try:
        with zipfile.ZipFile(io.BytesIO(contents)) as archive:
            package = json.loads(archive.read("extension/package.json").decode("utf-8"))
            cli_manifest = json.loads(
                archive.read("extension/bin/ppds.version.json").decode("utf-8")
            )
            bundled_cli = archive.read(f"extension/bin/{binary_name}")
    except (zipfile.BadZipFile, KeyError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise VerificationError(f"Marketplace VSIX for {target} is invalid: {error}") from error

    expected_identity = (MARKETPLACE_PUBLISHER, MARKETPLACE_EXTENSION)
    actual_identity = (package.get("publisher"), package.get("name"))
    if actual_identity != expected_identity:
        raise VerificationError(
            f"Marketplace VSIX identity mismatch for {target}: "
            f"expected {expected_identity}, got {actual_identity}"
        )
    if not versions_match(str(package.get("version", "")), extension_version):
        raise VerificationError(
            f"Marketplace VSIX version mismatch for {target}: "
            f"expected {extension_version}, got {package.get('version')!r}"
        )
    if not bundled_cli:
        raise VerificationError(f"Marketplace VSIX for {target} has an empty bundled CLI")

    expected_rid = MARKETPLACE_TARGETS[target]
    if cli_manifest.get("schemaVersion") != 1 or cli_manifest.get("rid") != expected_rid:
        raise VerificationError(
            f"Bundled CLI target mismatch for {target}: expected {expected_rid}, "
            f"got {cli_manifest.get('rid')!r}"
        )
    for field in ("releaseVersion", "informationalVersion"):
        actual = str(cli_manifest.get(field, ""))
        if not versions_match(actual, cli_version):
            raise VerificationError(
                f"Bundled CLI version mismatch for {target}: "
                f"expected {cli_version}, {field}={actual!r}"
            )


def verify_marketplace_once(
    extension_version: str,
    cli_version: str,
    *,
    fetch: HttpFetch = default_http_fetch,
) -> dict[str, Any]:
    query_url = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery"
    payload = fetch_json(
        fetch,
        query_url,
        method="POST",
        headers={
            "Accept": "application/json;api-version=7.2-preview.1",
            "Content-Type": "application/json",
        },
        body=marketplace_query_body(),
    )
    assets = marketplace_version_assets(payload, extension_version)
    for target, url in assets.items():
        verify_vsix(
            fetch(url, "GET", {}, None),
            target=target,
            extension_version=extension_version,
            cli_version=cli_version,
        )
    return {
        "channel": "VS Code Marketplace",
        "extensionVersion": extension_version,
        "cliVersion": cli_version,
        "targets": sorted(assets),
    }


def verify_marketplace(
    tag: str,
    cli_version: str,
    *,
    fetch: HttpFetch = default_http_fetch,
    attempts: int = MAX_POLL_ATTEMPTS,
    interval_seconds: int = POLL_INTERVAL_SECONDS,
    sleep: Callable[[float], None] = time.sleep,
) -> dict[str, Any]:
    _, extension_version = parse_release_tag(tag, "Extension")
    release_version(cli_version)
    return poll_until_available(
        lambda: verify_marketplace_once(extension_version, cli_version, fetch=fetch),
        f"Marketplace extension {extension_version}",
        attempts=attempts,
        interval_seconds=interval_seconds,
        sleep=sleep,
    )


def _positive_int(value: str) -> int:
    parsed = int(value)
    if parsed < 1:
        raise argparse.ArgumentTypeError("must be at least one")
    return parsed


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--attempts", type=_positive_int, default=MAX_POLL_ATTEMPTS)
    parser.add_argument(
        "--interval-seconds", type=int, default=POLL_INTERVAL_SECONDS, help=argparse.SUPPRESS
    )
    subparsers = parser.add_subparsers(dest="channel", required=True)

    nuget = subparsers.add_parser("nuget")
    nuget.add_argument("--tag", required=True)
    nuget.add_argument("--package", required=True, choices=sorted(TAG_PACKAGES.values()))

    github = subparsers.add_parser("github")
    github.add_argument("--tag", required=True)

    marketplace = subparsers.add_parser("marketplace")
    marketplace.add_argument("--tag", required=True)
    marketplace.add_argument("--cli-version", required=True)
    return parser


def fail(message: str) -> NoReturn:
    safe_message = message.replace("\r", " ").replace("\n", " ")
    print(f"::error title=Public artifact verification failed::{safe_message}", file=sys.stderr)
    print(
        "STOP AND ESCALATE: published artifacts were not modified. "
        "Do not automatically unpublish, delete, deprecate, or replace them.",
        file=sys.stderr,
    )
    raise SystemExit(1)


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.channel == "nuget":
            result = verify_nuget(
                args.tag,
                args.package,
                attempts=args.attempts,
                interval_seconds=args.interval_seconds,
            )
        elif args.channel == "github":
            result = verify_github_release(
                args.tag,
                attempts=args.attempts,
                interval_seconds=args.interval_seconds,
            )
        else:
            result = verify_marketplace(
                args.tag,
                args.cli_version,
                attempts=args.attempts,
                interval_seconds=args.interval_seconds,
            )
    except VerificationError as error:
        fail(str(error))
    except Exception as error:  # Defensive: every unexpected verifier failure must escalate.
        fail(f"Unexpected {type(error).__name__}: {error}")
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    main()
