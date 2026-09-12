"""Behavior tests for public post-publish artifact verification (issue #1269)."""

from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path
import subprocess
from typing import Any
import zipfile

import pytest
import yaml

from scripts.ci import verify_public_release as verifier


REPO_ROOT = Path(__file__).resolve().parents[2]


def completed(
    command: list[str] | tuple[str, ...],
    *,
    returncode: int = 0,
    stdout: str = "",
    stderr: str = "",
) -> subprocess.CompletedProcess[str]:
    return subprocess.CompletedProcess(command, returncode, stdout, stderr)


def make_vsix(
    *,
    target: str,
    extension_version: str = "1.6.1",
    cli_version: str = "1.6.1",
    informational_version: str | None = None,
    rid: str | None = None,
) -> bytes:
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        archive.writestr(
            "extension/package.json",
            json.dumps(
                {
                    "publisher": verifier.MARKETPLACE_PUBLISHER,
                    "name": verifier.MARKETPLACE_EXTENSION,
                    "version": extension_version,
                }
            ),
        )
        archive.writestr(
            "extension/bin/ppds.version.json",
            json.dumps(
                {
                    "schemaVersion": 1,
                    "rid": rid or verifier.MARKETPLACE_TARGETS[target],
                    "releaseVersion": cli_version,
                    "informationalVersion": informational_version or f"{cli_version}+abc1234",
                }
            ),
        )
        binary_name = "ppds.exe" if target.startswith("win32-") else "ppds"
        archive.writestr(f"extension/bin/{binary_name}", b"public bundled CLI")
    return stream.getvalue()


def marketplace_payload(version: str, targets: list[str]) -> dict[str, Any]:
    return {
        "results": [
            {
                "extensions": [
                    {
                        "extensionName": verifier.MARKETPLACE_EXTENSION,
                        "publisher": {"publisherName": verifier.MARKETPLACE_PUBLISHER},
                        "versions": [
                            {
                                "version": version,
                                "targetPlatform": target,
                                "files": [
                                    {
                                        "assetType": "Microsoft.VisualStudio.Services.VSIXPackage",
                                        "source": f"https://public.example/{target}.vsix",
                                    }
                                ],
                            }
                            for target in targets
                        ],
                    }
                ]
            }
        ]
    }


@pytest.mark.parametrize(
    ("tag", "expected"),
    [
        ("Cli-v1.6.1", ("Cli", "1.6.1")),
        ("Extension-v1.7.0-beta.2", ("Extension", "1.7.0-beta.2")),
        ("Query-v2.0.0-rc.1+build.7", ("Query", "2.0.0-rc.1+build.7")),
    ],
)
def test_release_tag_parsing(tag: str, expected: tuple[str, str]) -> None:
    assert verifier.parse_release_tag(tag) == expected


@pytest.mark.parametrize(
    "tag",
    ["v1.6.1", "Cli-1.6.1", "Cli-v01.6.1", "Cli-v1.6", "Cli-v1.6.1!"],
)
def test_release_tag_parsing_fails_closed(tag: str) -> None:
    with pytest.raises(verifier.VerificationError):
        verifier.parse_release_tag(tag)


def test_tag_must_match_selected_nuget_package() -> None:
    with pytest.raises(verifier.VerificationError, match="maps to PPDS.Query"):
        verifier.verify_nuget("Query-v1.6.1", "PPDS.Auth", attempts=1)


def test_polling_retries_at_two_minute_intervals_then_succeeds() -> None:
    calls = 0
    sleeps: list[float] = []

    def probe() -> str:
        nonlocal calls
        calls += 1
        if calls < 3:
            raise verifier.ArtifactPending("still propagating")
        return "ready"

    result = verifier.poll_until_available(
        probe,
        "artifact",
        attempts=3,
        sleep=sleeps.append,
        notice=lambda _: None,
    )

    assert result == "ready"
    assert calls == 3
    assert sleeps == [120, 120]


def test_polling_timeout_is_bounded_and_does_not_sleep_after_last_attempt() -> None:
    calls = 0
    sleeps: list[float] = []

    def probe() -> None:
        nonlocal calls
        calls += 1
        raise verifier.ArtifactPending("not indexed")

    with pytest.raises(verifier.VerificationError, match="after 4 attempts"):
        verifier.poll_until_available(
            probe,
            "artifact",
            attempts=4,
            sleep=sleeps.append,
            notice=lambda _: None,
        )

    assert calls == 4
    assert sleeps == [120, 120, 120]


def test_nuget_library_restore_uses_only_clean_public_feed() -> None:
    observed: dict[str, Any] = {}

    def fetch(url: str, method: str, headers: dict[str, str], body: bytes | None) -> bytes:
        assert url.endswith("/ppds.query/index.json")
        return json.dumps({"versions": ["1.6.1"]}).encode()

    def runner(command: list[str], cwd: Path, env: dict[str, str]) -> subprocess.CompletedProcess[str]:
        observed["command"] = command
        observed["config"] = Path(command[command.index("--configfile") + 1]).read_text()
        observed["project"] = Path(command[2]).read_text()
        observed["packages"] = env["NUGET_PACKAGES"]
        return completed(command)

    result = verifier.verify_nuget_once(
        "PPDS.Query", "1.6.1", fetch=fetch, runner=runner
    )

    assert result["package"] == "PPDS.Query"
    assert observed["command"][:2] == ["dotnet", "restore"]
    assert "--no-cache" in observed["command"]
    assert "<clear />" in observed["config"]
    assert observed["config"].count("<add ") == 1
    assert verifier.NUGET_SOURCE in observed["config"]
    assert 'PackageReference Include="PPDS.Query" Version="1.6.1"' in observed["project"]
    assert observed["packages"] in observed["command"]


def test_public_cli_is_installed_to_temp_and_version_checked() -> None:
    commands: list[list[str]] = []

    def fetch(url: str, method: str, headers: dict[str, str], body: bytes | None) -> bytes:
        return json.dumps({"versions": ["1.6.1"]}).encode()

    def runner(command: list[str], cwd: Path, env: dict[str, str]) -> subprocess.CompletedProcess[str]:
        commands.append(command)
        if command[:3] == ["dotnet", "tool", "install"]:
            return completed(command)
        return completed(command, stdout="ppds 1.6.1\n")

    verifier.verify_nuget_once("PPDS.Cli", "1.6.1", fetch=fetch, runner=runner)

    assert commands[0][:4] == ["dotnet", "tool", "install", "PPDS.Cli"]
    assert "--tool-path" in commands[0]
    assert "--global" not in commands[0]
    assert commands[1][-1] == "--version"


def test_public_cli_version_mismatch_fails() -> None:
    def fetch(url: str, method: str, headers: dict[str, str], body: bytes | None) -> bytes:
        return json.dumps({"versions": ["1.6.1"]}).encode()

    def runner(command: list[str], cwd: Path, env: dict[str, str]) -> subprocess.CompletedProcess[str]:
        if command[:3] == ["dotnet", "tool", "install"]:
            return completed(command)
        return completed(command, stdout="ppds 9.9.9\n")

    with pytest.raises(verifier.VerificationError, match="version mismatch"):
        verifier.verify_nuget_once("PPDS.Cli", "1.6.1", fetch=fetch, runner=runner)


def test_checksum_parser_requires_exact_binary_coverage() -> None:
    digest = "a" * 64
    text = "\n".join(f"{digest}  {name}" for name in verifier.GITHUB_BINARY_ASSETS[:-1])
    with pytest.raises(verifier.VerificationError, match="coverage mismatch"):
        verifier.parse_checksums(text)


def test_github_release_checksum_mismatch_fails_before_execution() -> None:
    contents = {name: f"content:{name}".encode() for name in verifier.GITHUB_BINARY_ASSETS}
    checksums = {
        name: hashlib.sha256(content).hexdigest() for name, content in contents.items()
    }
    checksums["ppds-linux-x64"] = "0" * 64
    checksum_text = "\n".join(
        f"{checksums[name]}  {name}" for name in verifier.GITHUB_BINARY_ASSETS
    ).encode()
    release = {
        "draft": False,
        "assets": [
            {"name": name, "browser_download_url": f"https://public.example/{name}"}
            for name in (*verifier.GITHUB_BINARY_ASSETS, verifier.GITHUB_CHECKSUM_ASSET)
        ],
    }

    def fetch(url: str, method: str, headers: dict[str, str], body: bytes | None) -> bytes:
        name = url.rsplit("/", 1)[-1]
        if "/releases/tags/" in url:
            return json.dumps(release).encode()
        if name == verifier.GITHUB_CHECKSUM_ASSET:
            return checksum_text
        return contents[name]

    def should_not_run(*args: Any) -> subprocess.CompletedProcess[str]:
        pytest.fail("a mismatched binary must not be executed")

    with pytest.raises(verifier.VerificationError, match="checksum mismatch"):
        verifier.verify_github_release_once(
            "Cli-v1.6.1", fetch=fetch, runner=should_not_run
        )


def test_marketplace_requires_all_four_public_targets() -> None:
    targets = list(verifier.MARKETPLACE_TARGETS)[:-1]
    with pytest.raises(verifier.ArtifactPending, match="missing public targets"):
        verifier.marketplace_version_assets(marketplace_payload("1.6.1", targets), "1.6.1")


def test_marketplace_duplicate_target_fails_closed() -> None:
    targets = [*verifier.MARKETPLACE_TARGETS, "win32-x64"]
    with pytest.raises(verifier.VerificationError, match="duplicate target"):
        verifier.marketplace_version_assets(marketplace_payload("1.6.1", targets), "1.6.1")


def test_marketplace_downloads_and_validates_every_target() -> None:
    targets = list(verifier.MARKETPLACE_TARGETS)
    payload = marketplace_payload("1.6.1", targets)
    downloads: list[str] = []

    def fetch(url: str, method: str, headers: dict[str, str], body: bytes | None) -> bytes:
        if method == "POST":
            assert body == verifier.marketplace_query_body()
            return json.dumps(payload).encode()
        target = Path(url).stem
        downloads.append(target)
        return make_vsix(target=target)

    result = verifier.verify_marketplace_once("1.6.1", "1.6.1", fetch=fetch)

    assert result["targets"] == sorted(targets)
    assert sorted(downloads) == sorted(targets)


@pytest.mark.parametrize(
    ("kwargs", "message"),
    [
        ({"extension_version": "9.9.9"}, "VSIX version mismatch"),
        ({"cli_version": "9.9.9"}, "Bundled CLI version mismatch"),
        ({"rid": "wrong-rid"}, "Bundled CLI target mismatch"),
    ],
)
def test_marketplace_version_and_target_mismatches_fail(
    kwargs: dict[str, str], message: str
) -> None:
    target = "linux-x64"
    contents = make_vsix(target=target, **kwargs)
    with pytest.raises(verifier.VerificationError, match=message):
        verifier.verify_vsix(
            contents,
            target=target,
            extension_version="1.6.1",
            cli_version="1.6.1",
        )


def test_failure_exits_with_escalation_and_no_rollback(monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]) -> None:
    def fail_verification(*args: Any, **kwargs: Any) -> dict[str, Any]:
        raise verifier.VerificationError("public artifact is corrupt")

    monkeypatch.setattr(verifier, "verify_github_release", fail_verification)
    with pytest.raises(SystemExit) as exit_info:
        verifier.main(["--attempts", "1", "github", "--tag", "Cli-v1.6.1"])

    assert exit_info.value.code == 1
    error = capsys.readouterr().err
    assert "::error title=Public artifact verification failed::" in error
    assert "STOP AND ESCALATE" in error
    assert "were not modified" in error
    assert "Do not automatically unpublish, delete, deprecate, or replace" in error


def load_workflow(name: str) -> dict[str, Any]:
    return yaml.safe_load(
        (REPO_ROOT / ".github" / "workflows" / name).read_text(encoding="utf-8")
    )


def test_publish_workflows_run_verification_after_publication() -> None:
    nuget = load_workflow("publish-nuget.yml")
    nuget_steps = nuget["jobs"]["publish"]["steps"]
    assert nuget_steps[-1]["name"] == "Verify public NuGet artifact"
    assert "verify_public_release.py nuget" in nuget_steps[-1]["run"]

    cli = load_workflow("release-cli.yml")
    cli_steps = cli["jobs"]["release"]["steps"]
    assert cli_steps[-1]["name"] == "Verify public GitHub release"
    assert "verify_public_release.py github" in cli_steps[-1]["run"]


def test_marketplace_verification_waits_for_full_publish_matrix() -> None:
    workflow = load_workflow("extension-publish.yml")
    publish = workflow["jobs"]["publish"]
    matrix_targets = {
        item["target"] for item in publish["strategy"]["matrix"]["include"]
    }
    assert matrix_targets == set(verifier.MARKETPLACE_TARGETS)

    verification = workflow["jobs"]["verify-public-artifacts"]
    assert verification["needs"] == "publish"
    assert "!inputs.dry_run" in verification["if"]
    verify_step = verification["steps"][-1]
    assert "verify_public_release.py marketplace" in verify_step["run"]
    assert "--cli-version" in verify_step["run"]
    assert "env" not in verify_step, "public verification must not receive publishing secrets"


def test_polling_defaults_are_bounded_two_minute_checks() -> None:
    assert verifier.POLL_INTERVAL_SECONDS == 120
    assert 1 < verifier.MAX_POLL_ATTEMPTS <= 30
