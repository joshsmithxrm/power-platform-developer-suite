"""Adversarial pre-upload guards; no publishing commands or live service calls."""
from __future__ import annotations

import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

import pytest
import yaml

from scripts.ci import verify_release_inputs as guard
from scripts.ci import verify_public_release as public
from scripts.ci.release_model import ReleaseGraph

ROOT = Path(__file__).resolve().parents[2]
SHA = "a" * 40
VERSIONS = {"PPDS.Dataverse": "1.4.0", "PPDS.Query": "1.1.0", "PPDS.Cli": "1.5.0"}


def fixture_graph(root: Path) -> ReleaseGraph:
    for name, dependency in (("PPDS.Dataverse", None), ("PPDS.Query", "PPDS.Dataverse"), ("PPDS.Cli", "PPDS.Query")):
        directory = root / "src" / name
        directory.mkdir(parents=True)
        reference = f'<ItemGroup><ProjectReference Include="../{dependency}/{dependency}.csproj" /></ItemGroup>' if dependency else ""
        tool = "<PackAsTool>true</PackAsTool><AssemblyName>ppds</AssemblyName>" if name == "PPDS.Cli" else ""
        (directory / f"{name}.csproj").write_text(f'<Project><PropertyGroup><PackageId>{name}</PackageId><MinVerTagPrefix>{name[5:]}-v</MinVerTagPrefix><TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>{tool}</PropertyGroup>{reference}</Project>', encoding="utf-8")
    return ReleaseGraph.discover(root)


def package_fixture(path: Path, package="PPDS.Query", *, version=None, source=SHA, dependency="1.4.0", missing_assembly=False, tool_version=None, suffix="") -> Path:
    version = version or VERSIONS[package]
    metadata = ET.Element("metadata")
    ET.SubElement(metadata, "id").text = package
    ET.SubElement(metadata, "version").text = version
    ET.SubElement(metadata, "repository", {"commit": source})
    deps = ET.SubElement(metadata, "dependencies")
    for tfm in ("net8.0", "net9.0", "net10.0"):
        group = ET.SubElement(deps, "group", {"targetFramework": tfm})
        if package == "PPDS.Query" and dependency is not None:
            ET.SubElement(group, "dependency", {"id": "PPDS.Dataverse", "version": dependency})
    spec = ET.Element("package")
    spec.append(metadata)
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr(package + ".nuspec", ET.tostring(spec))
        if path.suffix == ".snupkg":
            return path
        for tfm in ("net8.0", "net9.0", "net10.0"):
            if package == "PPDS.Cli":
                for name in VERSIONS:
                    assembly = "ppds" if name == "PPDS.Cli" else name
                    archive.writestr(f"tools/{tfm}/any/{assembly}.dll", f"{tool_version or VERSIONS[name]}+{source}")
            elif not (missing_assembly and tfm == "net10.0"):
                archive.writestr(f"lib/{tfm}/{package}.dll", f"{version}+{source}{suffix}")
    return path


def fake_inspect(directory, key):
    return [{"name": path.stem, "version": path.read_text()} for path in sorted(directory.glob("*.dll"))]


class TestNugetInputs:
    @pytest.mark.parametrize("package", ["PPDS.Query", "PPDS.Cli"])
    def test_exact_package_and_internal_graph(self, tmp_path, package):
        graph = fixture_graph(tmp_path)
        path = package_fixture(tmp_path / "fixture.nupkg", package)
        guard.verify_nuget(path, f"{package[5:]}-v{VERSIONS[package]}", tmp_path, SHA, VERSIONS, graph, inspect=fake_inspect)

    @pytest.mark.parametrize("changes,match", [
        ({"version": "9.9.9"}, "ID/version"),
        ({"source": "b" * 40}, "repository commit"),
        ({"dependency": "1.4.1-alpha.0.1"}, "same-commit tag"),
        ({"dependency": "[1.3.0,2.0.0)"}, "same-commit tag"),
        ({"dependency": None}, "dependency graph"),
        ({"missing_assembly": True}, "assembly inventory"),
        ({"suffix": "dirty"}, "informational version"),
    ])
    def test_wrong_package_graph_or_payload_stops(self, tmp_path, changes, match):
        graph = fixture_graph(tmp_path)
        path = package_fixture(tmp_path / "fixture.nupkg", **changes)
        with pytest.raises(guard.Error, match=match):
            guard.verify_nuget(path, "Query-v1.1.0", tmp_path, SHA, VERSIONS, graph, inspect=fake_inspect)

    def test_tool_metadata_rejects_placeholder_or_alpha_versions(self, tmp_path):
        graph = fixture_graph(tmp_path)
        path = package_fixture(tmp_path / "fixture.nupkg", "PPDS.Cli", tool_version="1.0.0")
        with pytest.raises(guard.Error, match="informational version"):
            guard.verify_nuget(path, "Cli-v1.5.0", tmp_path, SHA, VERSIONS, graph, inspect=fake_inspect)

    @pytest.mark.parametrize("mutation", ["missing", "duplicate", "wrong"])
    def test_dependency_groups_cover_every_framework(self, tmp_path, mutation):
        graph = fixture_graph(tmp_path)
        path = package_fixture(tmp_path / "fixture.nupkg")
        with zipfile.ZipFile(path) as archive:
            contents = {name: archive.read(name) for name in archive.namelist()}
        spec = ET.fromstring(contents["PPDS.Query.nuspec"])
        dependencies = spec.find("metadata/dependencies")
        if mutation == "missing": dependencies.remove(dependencies[1])
        elif mutation == "duplicate": dependencies[1].set("targetFramework", "net8.0")
        else: dependencies[1].set("targetFramework", "net11.0")
        contents["PPDS.Query.nuspec"] = ET.tostring(spec)
        with zipfile.ZipFile(path, "w") as archive:
            for name, content in contents.items(): archive.writestr(name, content)
        with pytest.raises(guard.Error, match="expected frameworks"):
            guard.verify_nuget(path, "Query-v1.1.0", tmp_path, SHA, VERSIONS, graph, inspect=fake_inspect)

    def test_symbols_still_require_correct_package_identity(self, tmp_path):
        graph = fixture_graph(tmp_path)
        path = package_fixture(tmp_path / "fixture.snupkg")
        guard.verify_nuget(path, "Query-v1.1.0", tmp_path, SHA, VERSIONS, graph, inspect=fake_inspect)
        with pytest.raises(guard.Error, match="ID/version"):
            guard.verify_nuget(path, "Dataverse-v1.4.0", tmp_path, SHA, VERSIONS, graph, inspect=fake_inspect)

    def test_duplicate_archive_paths_are_rejected(self, tmp_path):
        graph = fixture_graph(tmp_path)
        path = package_fixture(tmp_path / "fixture.nupkg")
        with zipfile.ZipFile(path, "a") as archive:
            archive.writestr("LIB/net8.0/PPDS.Query.dll", b"replacement")
        with pytest.raises(guard.Error, match="duplicate archive"):
            guard.verify_nuget(path, "Query-v1.1.0", tmp_path, SHA, VERSIONS, graph, inspect=fake_inspect)

    @pytest.mark.parametrize("tags", [
        ["Cli-v1.5.0", "Query-v1.1.0"],
        ["Cli-v1.5.0", "Query-v1.1.0", "Dataverse-v1.4.0", "Dataverse-v1.4.1"],
        ["Cli-v1.5.0", "Query-v1.1.0", "Dataverse-v1.4.0-alpha.1"],
        ["Cli-v1.5.0", "Query-v1.1.0", "Dataverse-vgarbage"],
        ["Cli-v1.5.1", "Query-v1.1.0", "Dataverse-v1.4.0"],
    ])
    def test_missing_ambiguous_prerelease_or_wrong_commit_tags_fail(self, tmp_path, tags):
        fixture_graph(tmp_path)
        def runner(command, cwd=None):
            return SHA if command[1] == "rev-parse" else "\n".join(tags)
        with pytest.raises(guard.Error):
            guard.tagged_graph(tmp_path, "Cli-v1.5.0", runner=runner)

    def test_tagged_graph_uses_actual_project_closure(self, tmp_path):
        fixture_graph(tmp_path)
        def runner(command, cwd=None):
            return SHA if command[1] == "rev-parse" else "\n".join(name[5:]+"-v"+version for name, version in VERSIONS.items())
        sha, versions, graph = guard.tagged_graph(tmp_path, "Cli-v1.5.0", runner=runner)
        assert sha == SHA and versions == VERSIONS
        assert graph.surfaces["PPDS.Cli"].project_dependencies == {"PPDS.Query"}


class TestPluginSignature:
    def test_inspector_invokes_metadata_only_script_and_key(self, tmp_path):
        seen = []
        def runner(command):
            seen.append(command)
            return '[{"name":"PPDS.Plugins","version":"3.1.0+'+SHA+'"}]'
        key = tmp_path / "public-key"
        rows = guard.inspect_assemblies(tmp_path, key, runner=runner)
        assert rows[0]["name"] == "PPDS.Plugins"
        assert seen[0][-2:] == ["-ExpectedPublicKey", str(key)]
        assert "-File" in seen[0] and seen[0][0] == "pwsh"

    @pytest.mark.parametrize("failure", [guard.Error("signature failed"), subprocess.TimeoutExpired("pwsh", 120), OSError("missing SDK")])
    def test_signature_failure_never_becomes_success(self, tmp_path, failure):
        def runner(command):
            raise failure
        with pytest.raises(type(failure)):
            guard.inspect_assemblies(tmp_path, tmp_path / "key", runner=runner)

    @pytest.mark.skipif(os.name != "nt" or not os.environ.get("PPDS_PUBLISHER_TEST_SIGNED_PLUGINS"), reason="Windows signed public fixture supplied by local release validation")
    @pytest.mark.parametrize("variant", ["signed", "wrong-key", "tampered", "public-only", "unsigned"])
    def test_real_windows_signature_gate(self, tmp_path, variant):
        signed = Path(os.environ["PPDS_PUBLISHER_TEST_SIGNED_PLUGINS"])
        key = ROOT / "src/PPDS.Plugins/PPDS.Plugins.PublicKey"
        source = signed
        if variant == "public-only":
            source = Path(os.environ["PPDS_PUBLISHER_TEST_PUBLIC_PLUGINS"])
        elif variant == "unsigned":
            source = Path(os.environ["PPDS_PUBLISHER_TEST_UNSIGNED"])
        destination = tmp_path / "PPDS.Plugins.dll"
        shutil.copyfile(source, destination)
        if variant == "wrong-key":
            key = tmp_path / "wrong-public-key"
            key.write_bytes(b"not the expected public key")
        elif variant == "tampered":
            contents = bytearray(destination.read_bytes())
            contents[len(contents)//2] ^= 1
            destination.write_bytes(contents)
        if variant == "signed":
            assert guard.inspect_assemblies(tmp_path, key)[0]["name"] == "PPDS.Plugins"
        else:
            with pytest.raises(guard.Error):
                guard.inspect_assemblies(tmp_path, key)


def gallery(versions=None):
    return {"results": [{"extensions": [{"extensionName": public.MARKETPLACE_EXTENSION, "publisher": {"publisherName": public.MARKETPLACE_PUBLISHER}, "versions": versions or [{"version":"1.6.2","targetPlatform":"win32-x64"}]}]}]}


class TestVacancy:
    @pytest.mark.parametrize("payload", [{}, [], {"versions":[]}, {"versions":[None]}, {"versions":["garbage"]}, {"versions":["1.5.0"]}])
    def test_nuget_occupied_or_malformed_never_means_vacant(self, payload):
        with pytest.raises((guard.Error, ValueError)):
            guard.nuget_vacant("PPDS.Cli", "1.5.0", fetch=lambda *args: json.dumps(payload).encode())

    def test_nuget_vacant(self):
        guard.nuget_vacant("PPDS.Cli", "1.5.0", fetch=lambda *args: b'{"versions":["1.4.2"]}')

    @pytest.mark.parametrize("failure", [public.ArtifactPending("HTTP 404"), public.ArtifactPending("timeout"), guard.Error("HTTP 401")])
    def test_lookup_failure_is_not_absence(self, failure):
        def fetch(*args):
            raise failure
        with pytest.raises(type(failure)):
            guard.nuget_vacant("PPDS.Cli", "1.5.0", fetch=fetch)
        with pytest.raises(type(failure)):
            guard.marketplace_vacant("1.8.0", fetch=fetch)

    @pytest.mark.parametrize("payload", [{}, {"results":[]}, {"results":[{}]}, {"results":[{"extensions":[]}]}, gallery([{"version":"garbage"}]), gallery([{"version":"1.8.0","targetPlatform":"linux-x64"}])])
    def test_marketplace_occupied_or_malformed_fails(self, payload):
        with pytest.raises(guard.Error):
            guard.marketplace_vacant("1.8.0", fetch=lambda *args: json.dumps(payload).encode())

    def test_matrix_target_check_tolerates_completed_sibling_only(self):
        payload = gallery([{"version":"1.8.0","targetPlatform":"linux-x64"}])
        fetch = lambda *args: json.dumps(payload).encode()
        guard.marketplace_vacant("1.8.0", "win32-x64", fetch=fetch)
        with pytest.raises(guard.Error, match="occupied"):
            guard.marketplace_vacant("1.8.0", "linux-x64", fetch=fetch)
        with pytest.raises(guard.Error, match="occupied"):
            guard.marketplace_vacant("1.8.0", fetch=fetch)

    @pytest.mark.parametrize("target", [None, "universal"])
    def test_universal_artifact_blocks_any_target(self, target):
        with pytest.raises(guard.Error, match="occupied"):
            guard.marketplace_vacant("1.8.0", "linux-x64", fetch=lambda *args: json.dumps(gallery([{"version":"1.8.0","targetPlatform":target}])).encode())


def vsix_fixture(root, *, version="1.8.0", cli="1.5.0", source=SHA, target="linux-x64", xml_target=None, binary=b"same CLI", package_version=None):
    (root / "bin").mkdir()
    (root / "bin/ppds").write_bytes(b"same CLI")
    package = {"name":public.MARKETPLACE_EXTENSION,"publisher":public.MARKETPLACE_PUBLISHER,"version":version}
    (root / "package.json").write_text(json.dumps(package))
    (root / "package-lock.json").write_text(json.dumps({"name":public.MARKETPLACE_EXTENSION,"version":version,"packages":{"":{"version":version}}}))
    path = root / "release.vsix"
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("extension/package.json", json.dumps({**package,"version":package_version or version}))
        archive.writestr("extension/bin/ppds.version.json", json.dumps({"schemaVersion":1,"rid":public.MARKETPLACE_TARGETS[target],"releaseVersion":cli,"informationalVersion":cli+"+"+source}))
        archive.writestr("extension/bin/ppds", binary)
        archive.writestr("extension.vsixmanifest", f'<PackageManifest><Metadata><Identity Id="{public.MARKETPLACE_EXTENSION}" Publisher="{public.MARKETPLACE_PUBLISHER}" Version="{version}" TargetPlatform="{xml_target or target}" /></Metadata></PackageManifest>')
    return path


class TestVsixInputs:
    @pytest.mark.parametrize("tags", [
        ["Extension-v1.8.0"],
        ["Cli-v1.5.0"],
        ["Extension-v1.8.0", "Cli-v1.5.0", "Cli-v1.5.1"],
        ["Extension-v1.8.0", "Cli-v1.5.1-alpha.1"],
        ["Extension-v1.8.0", "Cli-vgarbage"],
    ])
    def test_cli_cotag_must_be_unique_stable_and_colocated(self, tags):
        with pytest.raises(guard.Error):
            guard.resolve_cli_version(tags, "Extension-v1.8.0")

    def test_valid_cli_cotag_and_prerelease_channel(self):
        assert guard.resolve_cli_version(["Extension-v1.8.0", "Cli-v1.5.0"], "Extension-v1.8.0") == "1.5.0"
        assert guard.resolve_cli_version(["Extension-v1.9.0", "Cli-v1.6.0-alpha.1"], "Extension-v1.9.0") == "1.6.0-alpha.1"

    def test_validated_archive_matches_source_cli_bytes(self, tmp_path):
        path = vsix_fixture(tmp_path)
        guard.verify_vsix(path, "Extension-v1.8.0", "1.5.0", "linux-x64", SHA, tmp_path, prerelease=False)

    def test_stable_archive_cannot_bundle_prerelease_cli(self, tmp_path):
        path = vsix_fixture(tmp_path, cli="1.5.1-alpha.1")
        with pytest.raises(guard.Error, match="Stable Extension cannot bundle"):
            guard.verify_vsix(path, "Extension-v1.8.0", "1.5.1-alpha.1", "linux-x64", SHA, tmp_path, prerelease=False)

    @pytest.mark.parametrize("changes,match", [
        ({"source":"b"*40}, "source commit"),
        ({"binary":b"substituted"}, "bytes differ"),
        ({"cli":"1.4.2"}, "CLI version"),
        ({"xml_target":"darwin-arm64"}, "XML identity"),
        ({"package_version":"1.6.2"}, "VSIX version"),
    ])
    def test_payload_mismatch_is_rejected(self, tmp_path, changes, match):
        path = vsix_fixture(tmp_path, **changes)
        with pytest.raises(guard.Error, match=match):
            guard.verify_vsix(path, "Extension-v1.8.0", "1.5.0", "linux-x64", SHA, tmp_path, prerelease=False)

    @pytest.mark.parametrize("mutation", ["lock", "source", "channel"])
    def test_source_and_channel_must_agree(self, tmp_path, mutation):
        path = vsix_fixture(tmp_path)
        if mutation in {"lock", "source"}:
            file = tmp_path / ("package-lock.json" if mutation=="lock" else "package.json")
            value = json.loads(file.read_text())
            value["version"] = "1.6.2"
            file.write_text(json.dumps(value))
        with pytest.raises(guard.Error):
            guard.verify_vsix(path, "Extension-v1.8.0", "1.5.0", "linux-x64", SHA, tmp_path, prerelease=mutation=="channel")


def cli_assets(directory):
    for name in public.GITHUB_BINARY_ASSETS:
        (directory / name).write_bytes(name.encode())
    (directory / public.GITHUB_CHECKSUM_ASSET).write_text("".join(hashlib.sha256((directory/name).read_bytes()).hexdigest()+"  "+name+"\n" for name in public.GITHUB_BINARY_ASSETS))


class TestCliReleasePlan:
    def test_exact_section_excludes_neighbor_and_link_definitions(self):
        text = "## [Unreleased]\n- future\n## [1.5.0] - 2026-11-01\n\n### Added\n\n- Approved.\n\n## [1.4.2]\n- old\n"
        assert guard.changelog_notes(text, "1.5.0") == "### Added\n\n- Approved.\n"
        assert guard.changelog_notes("## [1.5.0]\n- approved\n[1.5.0]: url", "1.5.0") == "- approved\n"

    @pytest.mark.parametrize("text", ["## [Unreleased]\n- future", "## [1.5.0]\n## [1.5.0]\n- duplicate", "## [1.5.0]\n### Added\n", "## [1.5.0-beta.1]\n- prerelease"])
    def test_missing_empty_ambiguous_or_wrong_version_notes_fail(self, text):
        with pytest.raises(guard.Error):
            guard.changelog_notes(text, "1.5.0")

    def test_new_release_plans_all_assets_and_pins_notes(self, tmp_path):
        cli_assets(tmp_path)
        plan = guard.cli_release_plan([[]], "Cli-v1.5.0", SHA, "123", "- See https://github.com/"+public.REPOSITORY+"/blob/main/docs/release.md", tmp_path)
        assert plan["existing_draft"] is False
        assert len(plan["missing_assets"]) == 6
        assert "/blob/"+SHA+"/docs/" in plan["body"]
        assert "run=123" in plan["body"]

    def test_owned_partial_draft_resumes_only_missing_assets(self, tmp_path):
        cli_assets(tmp_path)
        plan = guard.cli_release_plan([[]], "Cli-v1.5.0", SHA, "123", "- approved", tmp_path)
        path = tmp_path / public.GITHUB_BINARY_ASSETS[0]
        release = {"tag_name":"Cli-v1.5.0","draft":True,"prerelease":False,"target_commitish":SHA,"body":plan["body"],"assets":[{"name":path.name,"state":"uploaded","size":path.stat().st_size,"digest":"sha256:"+hashlib.sha256(path.read_bytes()).hexdigest()}]}
        resumed = guard.cli_release_plan([[release]], "Cli-v1.5.0", SHA, "123", "- approved", tmp_path)
        assert resumed["existing_draft"] is True and len(resumed["missing_assets"]) == 5
        assert str(path) not in resumed["missing_assets"]

    @pytest.mark.parametrize("mutation", ["published","foreign-run","foreign-source","notes","unknown-digest","wrong-size","duplicate","unexpected","prerelease","missing-channel","string-channel"])
    def test_occupied_or_mismatched_draft_never_resumes(self, tmp_path, mutation):
        cli_assets(tmp_path)
        plan = guard.cli_release_plan([[]], "Cli-v1.5.0", SHA, "123", "- approved", tmp_path)
        name = public.GITHUB_BINARY_ASSETS[0]
        data = (tmp_path/name).read_bytes()
        asset = {"name":name,"state":"uploaded","size":len(data),"digest":"sha256:"+hashlib.sha256(data).hexdigest()}
        release = {"tag_name":"Cli-v1.5.0","draft":True,"prerelease":False,"target_commitish":SHA,"body":plan["body"],"assets":[asset]}
        if mutation=="published": release["draft"]=False
        elif mutation=="foreign-run": release["body"]=plan["body"].replace("run=123","run=456")
        elif mutation=="foreign-source": release["target_commitish"]="b"*40
        elif mutation=="notes": release["body"]+="changed"
        elif mutation=="unknown-digest": asset["digest"]=None
        elif mutation=="wrong-size": asset["size"]+=1
        elif mutation=="duplicate": release["assets"].append(copy.deepcopy(asset))
        elif mutation=="unexpected": asset["name"]="unexpected.exe"
        elif mutation=="prerelease": release["prerelease"]=True
        elif mutation=="missing-channel": del release["prerelease"]
        elif mutation=="string-channel": release["prerelease"]="false"
        with pytest.raises(guard.Error):
            guard.cli_release_plan([[release]], "Cli-v1.5.0", SHA, "123", "- approved", tmp_path)

    def test_postupload_requires_complete_reverified_draft(self, tmp_path):
        cli_assets(tmp_path)
        plan = guard.cli_release_plan([[]], "Cli-v1.5.0", SHA, "123", "- approved", tmp_path)
        release = {"tag_name":"Cli-v1.5.0","draft":True,"prerelease":False,"target_commitish":SHA,"body":plan["body"],"assets":[]}
        with pytest.raises(guard.Error, match="not complete"):
            guard.cli_release_plan([[release]], "Cli-v1.5.0", SHA, "123", "- approved", tmp_path, require_complete=True)
        for path in tmp_path.iterdir():
            release["assets"].append({"name":path.name,"state":"uploaded","size":path.stat().st_size,"digest":"sha256:"+hashlib.sha256(path.read_bytes()).hexdigest()})
        complete = guard.cli_release_plan([[release]], "Cli-v1.5.0", SHA, "123", "- approved", tmp_path, require_complete=True)
        assert complete["existing_draft"] and complete["missing_assets"] == []

    @pytest.mark.parametrize("pages", [None, [], {}, [{}], [[None]], [[{"tag_name":None}]]])
    def test_incomplete_release_inventory_fails(self, tmp_path, pages):
        cli_assets(tmp_path)
        with pytest.raises(guard.Error):
            guard.cli_release_plan(pages, "Cli-v1.5.0", SHA, "123", "- approved", tmp_path)

    def test_changed_local_asset_stops_before_planning(self, tmp_path):
        cli_assets(tmp_path)
        (tmp_path/public.GITHUB_BINARY_ASSETS[0]).write_bytes(b"changed")
        with pytest.raises(guard.Error, match="checksum"):
            guard.cli_release_plan([[]], "Cli-v1.5.0", SHA, "123", "- approved", tmp_path)


class TestPublisherOrdering:
    def workflow(self, name):
        return yaml.safe_load((ROOT/".github/workflows"/name).read_text())

    def test_nuget_guards_precede_every_push_without_duplicate_bypass(self):
        steps = self.workflow("publish-nuget.yml")["jobs"]["publish"]["steps"]
        gate = next(i for i,s in enumerate(steps) if "verify_release_inputs.py nuget" in s.get("run",""))
        pushes = [i for i,s in enumerate(steps) if "dotnet nuget push" in s.get("run","")]
        assert pushes and all(gate < i for i in pushes)
        assert "--skip-duplicate" not in json.dumps(steps)
        assert "--no-symbols" in steps[pushes[0]]["run"]
        assert "shopt -s nullglob" in steps[pushes[1]]["run"]

    def test_marketplace_publishes_only_validated_archive(self):
        workflow = self.workflow("extension-publish.yml")
        assert "marketplace-vacant" in json.dumps(workflow["jobs"]["preflight"])
        steps = workflow["jobs"]["publish"]["steps"]
        gate = next(i for i,s in enumerate(steps) if "verify_release_inputs.py vsix" in s.get("run",""))
        for i, step in enumerate(steps):
            if "vsce publish" in step.get("run",""):
                assert i > gate
                assert "--packagePath ppds-"+chr(36)+"{{ matrix.target }}.vsix" in step["run"]
                assert "--target" not in step["run"]
        assert any("marketplace-vacant" in s.get("run","") and "--target" in s["run"] for s in steps)

    def test_cli_guards_and_curated_notes_precede_mutations(self):
        workflow = self.workflow("release-cli.yml")
        steps = workflow["jobs"]["release"]["steps"]
        gate = next(i for i,s in enumerate(steps) if "verify_release_inputs.py cli-plan" in s.get("run",""))
        for i, step in enumerate(steps):
            if any(command in step.get("run","") for command in ("gh release create","gh release upload","gh release edit")):
                assert i > gate
        serialized = json.dumps(steps)
        assert "--generate-notes" not in serialized and "--clobber" not in serialized
        assert "--notes-file release-notes.md" in serialized
        complete = next(i for i,s in enumerate(steps) if "--require-complete" in s.get("run", ""))
        upload_assets = next(i for i,s in enumerate(steps) if "gh release upload" in s.get("run", ""))
        publish = next(i for i,s in enumerate(steps) if "gh release edit" in s.get("run", ""))
        assert upload_assets < complete < publish
        build = workflow["jobs"]["build"]["steps"]
        binary_gate = next(i for i,s in enumerate(build) if "verify_release_inputs.py binary" in s.get("run",""))
        upload = next(i for i,s in enumerate(build) if str(s.get("uses","")).startswith("actions/upload-artifact@"))
        assert binary_gate < upload and "--source" in build[binary_gate]["run"]

    def test_native_version_mismatch_has_no_mutating_command(self, monkeypatch, tmp_path, capsys):
        calls = []
        def runner(command, cwd=None):
            calls.append(command)
            return "1.5.0+"+"b"*40
        monkeypatch.setattr(guard, "run", runner)
        assert guard.main(["binary","--path",str(tmp_path/"ppds"),"--tag","Cli-v1.5.0","--source",SHA]) == 1
        assert calls == [[str((tmp_path/"ppds").resolve()),"--version"]]
        assert "STOP before publication" in capsys.readouterr().err

