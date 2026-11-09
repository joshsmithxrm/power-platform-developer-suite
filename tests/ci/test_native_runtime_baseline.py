"""Cross-input contracts for distributed runtime and the no-publish artifact route."""
from fnmatch import fnmatchcase
from pathlib import Path
import xml.etree.ElementTree as ET

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
PATHS = [".github/workflows/release-cli.yml", ".github/workflows/test.yml",
         ".github/workflows/extension-publish.yml", "src/PPDS.Extension/tools/bundle-cli.mjs"]


PUBLISHER_CHANGE_PATHS = (
    '.claude/skills/implement/SKILL.md',
    '.claude/skills/pr/SKILL.md',
    '.claude/skills/release/SKILL.md',
    '.github/pull_request_template.md',
    '.github/workflows/publish-nuget.yml',
    'CONTRIBUTING.md',
    'docs/CI-PERMISSIONS.md',
    'docs/CONTRIBUTOR-LIFECYCLE.md',
    'docs/MERGE-POLICY.md',
    'docs/RELEASE.md',
    'scripts/ci/check_contributor_provenance.py',
    'specs/README.md',
    'specs/contributor-lifecycle.md',
    'specs/release-cycle.md',
    'tests/ci/test_contributor_lifecycle_guidance.py',
    'tests/ci/test_contributor_provenance.py',
    'tests/ci/test_nuget_trusted_publishing.py',
    'tests/ci/test_workflow_permissions.py',
)

ARTIFACT_ROUTING_PROBES = (
    ".github/workflows/publish-nuget.yml", "docs/RELEASE.md", "specs/release-cycle.md",
    "docs/releases/v1.5.0.md", "docs/releases/security/runtime-patch.md",
)


def extraction_routes(workflow, changed_paths):
    changes = workflow["jobs"]["changes"]
    steps = [s for s in changes["steps"] if str(s.get("uses", "")).startswith("dorny/paths-filter@")]
    assert len(steps) == 1
    step = steps[0]
    assert changes["outputs"]["extraction"] == "$" + "{{ steps." + step["id"] + ".outputs.extraction }}"
    selectors = yaml.safe_load(step["with"]["filters"])["extraction"]
    assert all(isinstance(pattern, str) for pattern in selectors)
    # These fixtures use literal paths and directory/** selectors from the real filter.
    selected = any(fnmatchcase(path, pattern) for path in changed_paths for pattern in selectors)
    routes = {}
    for name in ["single-file-extraction", "vsix-artifacts"]:
        job = workflow["jobs"][name]
        needs = [job["needs"]] if isinstance(job["needs"], str) else job["needs"]
        assert "changes" in needs
        assert job["if"] == "needs.changes.outputs.extraction == 'true'"
        routes[name] = len(job["strategy"]["matrix"]["include"]) if selected else 0
    return routes


def publisher_artifact_routing_contract(workflow, changed_paths=PUBLISHER_CHANGE_PATHS):
    expected = {"single-file-extraction": 5, "vsix-artifacts": 4}
    assert extraction_routes(workflow, changed_paths) == expected, "Publisher input set must select artifact gates"
    for path in ARTIFACT_ROUTING_PROBES:
        assert extraction_routes(workflow, [path]) == expected, f"Artifact routing missing for {path}"


def small_vsix_evidence_contract(vsix):
    marker = "$" + "{{ matrix.target }}"
    large = [step for step in vsix["steps"]
             if step.get("with", {}).get("name") == "candidate-vsix-" + marker]
    small = [step for step in vsix["steps"]
             if step.get("with", {}).get("name") == "candidate-vsix-evidence-" + marker]
    assert len(large) == len(small) == 1
    small = small[0]
    assert small["uses"] == large[0]["uses"] == "actions/upload-artifact@v7"
    assert small["if"] == "always()"
    assert small["with"]["retention-days"] == large[0]["with"]["retention-days"] == 7
    root = "$" + "{{ runner.temp }}"
    rid = "$" + "{{ matrix.rid }}"
    expected = {f"{root}/ppds-vsix-build-{rid}/*.json",
                f"{root}/ppds-vsix-{rid}/*.json", f"{root}/ppds-vsix-{rid}/*.log"}
    actual = small["with"]["path"].splitlines()
    assert len(actual) == len(expected) and set(actual) == expected
    assert ".vsix" not in small["with"]["path"] and "src/" not in small["with"]["path"]
    assert {row["target"] for row in vsix["strategy"]["matrix"]["include"]} == {
        "win32-x64", "linux-x64", "darwin-x64", "darwin-arm64"}


def contract(sources):
    release, tests, extension = [yaml.safe_load(sources[p]) for p in PATHS[:3]]
    native = release["jobs"]["build"]
    smoke = tests["jobs"]["single-file-extraction"]
    vsix = tests["jobs"]["vsix-artifacts"]
    expected = {"win-x64", "win-arm64", "linux-x64", "osx-x64", "osx-arm64"}
    assert {r["rid"] for r in native["strategy"]["matrix"]["include"]} == expected
    assert {r["rid"] for r in smoke["strategy"]["matrix"]["include"]} == expected
    for job in [native, smoke]:
        commands = "\n".join(s.get("run", "") for s in job["steps"])
        assert "-f net10.0" in commands and "--self-contained" in commands
        assert "obj/Release/net10.0/${{ matrix.rid }}/Net462ReferenceAssemblies.zip" in commands
        assert "ReferenceCacheProbe/bin/Release/net8.0/ReferenceCacheProbe.dll" in commands
        assert "-f net8.0" not in commands
    bundler = sources[PATHS[3]]
    assert "const CLI_TARGET_FRAMEWORK = 'net10.0';" in bundler
    assert "'-f', CLI_TARGET_FRAMEWORK" in bundler
    assert "'obj', 'Release', CLI_TARGET_FRAMEWORK, rid" in bundler
    assert "net8.0" not in bundler
    sdk = next(s for s in extension["jobs"]["publish"]["steps"] if s.get("name") == "Setup .NET")
    assert sdk["with"]["dotnet-version"] == "10.0.114"
    target_map = {"win32-x64": "win-x64", "linux-x64": "linux-x64", "darwin-x64": "osx-x64", "darwin-arm64": "osx-arm64"}
    assert {r["target"]: r["rid"] for r in vsix["strategy"]["matrix"]["include"]} == target_map
    assert {r["target"]: r["rid"] for r in extension["jobs"]["publish"]["strategy"]["matrix"]["include"]} == target_map
    assert {r["rid"]: r["os"] for r in vsix["strategy"]["matrix"]["include"]} == {
        "win-x64": "windows-latest", "linux-x64": "ubuntu-latest", "osx-x64": "macos-15-intel", "osx-arm64": "macos-15"}
    assert vsix["permissions"] == {"contents": "read"}
    assert vsix["if"] == "needs.changes.outputs.extraction == 'true'"
    assert "environment" not in vsix and "secrets." not in str(vsix) and "id-token" not in str(vsix)
    assert all(s.get("with", {}).get("persist-credentials") is False for s in vsix["steps"] if str(s.get("uses", "")).startswith("actions/checkout@"))
    commands = "\n".join(s.get("run", "") for s in vsix["steps"])
    assert "npm ci --ignore-scripts" in commands and "--expected-version" not in commands
    assert "verify_native_distribution.py manifest" in commands and "verify_native_distribution.py vsix" in commands
    assert "node node_modules/@vscode/vsce/vsce package" in commands
    for forbidden in ["vsce publish", "nuget push", "gh release", "NuGet/login", "PLUGINS_SNK", "workflow run"]:
        assert forbidden not in str(vsix)
    aggregate = tests["jobs"]["test-status"]
    assert "vsix-artifacts" in aggregate["needs"] and aggregate["if"] == "always()"
    assert "verify_native_distribution.py native" in str(smoke)
    assert "verify_native_distribution.py glibc" in str(smoke)
    publisher_artifact_routing_contract(tests)
    small_vsix_evidence_contract(vsix)


def sources():
    return {p: (ROOT / p).read_text() for p in PATHS}


def test_distribution_inputs_are_coherent_and_candidate_route_is_no_publish():
    contract(sources())


@pytest.mark.parametrize("path,old,new", [
    (PATHS[0], "-f net10.0", "-f net8.0"),
    (PATHS[1], "obj/Release/net10.0/", "obj/Release/net8.0/"),
    (PATHS[3], "'obj', 'Release', CLI_TARGET_FRAMEWORK, rid", "'obj', 'Release', 'net8.0', rid"),
    (PATHS[2], "dotnet-version: '10.0.114'", "dotnet-version: '8.0.x'"),
    (PATHS[1], "    permissions:\n      contents: read\n    runs-on: ${{ matrix.os }}", "    permissions:\n      contents: write\n    runs-on: ${{ matrix.os }}"),
    (PATHS[1], "needs: [changes, test, single-file-extraction, vsix-artifacts, python-tests]", "needs: [changes, test, single-file-extraction, python-tests]"),
])
def test_stale_or_unsafe_runtime_input_fails(path, old, new):
    value = sources()
    assert old in value[path]
    value[path] = value[path].replace(old, new, 1)
    with pytest.raises(AssertionError):
        contract(value)


def test_package_targets_and_independent_fixtures_are_retained():
    for name in ["Auth", "Dataverse", "Query", "Migration", "Cli", "Mcp"]:
        project = ET.parse(ROOT / f"src/PPDS.{name}/PPDS.{name}.csproj")
        assert project.findtext(".//TargetFrameworks") == "net8.0;net9.0;net10.0"
    assert ET.parse(ROOT / "src/PPDS.Plugins/PPDS.Plugins.csproj").findtext(".//TargetFramework") == "net462"
    assert ET.parse(ROOT / "tests/fixtures/ReferenceCacheProbe/ReferenceCacheProbe.csproj").findtext(".//TargetFramework") == "net8.0"
    assert ET.parse(ROOT / "tests/fixtures/SingleFilePlugin/SingleFilePlugin.csproj").findtext(".//TargetFramework") == "net462"


def test_quiet_daemon_guard_precedes_all_startup_status_cache_and_http_access():
    program = (ROOT / "src/PPDS.Cli/Program.cs").read_text()
    guard = program.index("&& !StartupUpdateNotifier.IsQuietDaemon(args)")
    first_io = program.index("ErrorOutput.WriteVersionHeader();")
    assert guard < first_io < program.index("ReadAndDeleteUpdateStatus();")
    assert guard < program.index("updateService.GetCachedResult()") < program.index("updateService.RefreshCacheInBackgroundIfStale")
    helper = (ROOT / "scripts/ci/verify_native_distribution.py").read_text()
    assert '[str(binary), "serve", "--quiet"]' in helper
    assert '"method": "_heartbeat"' in helper and '"auth/list"' not in helper


@pytest.mark.parametrize("selector", [
    ".github/workflows/publish-nuget.yml", "docs/RELEASE.md", "specs/release-cycle.md", "docs/releases/**",
])
def test_omitting_each_final_source_selector_breaks_artifact_routing(selector):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    step = next(s for s in workflow["jobs"]["changes"]["steps"]
                if str(s.get("uses", "")).startswith("dorny/paths-filter@"))
    filters = yaml.safe_load(step["with"]["filters"])
    filters["extraction"].remove(selector)
    step["with"]["filters"] = yaml.safe_dump(filters)
    with pytest.raises(AssertionError, match="Artifact routing missing"):
        publisher_artifact_routing_contract(workflow)


def test_unmatched_publisher_input_set_cannot_claim_fresh_artifact_coverage():
    workflow = yaml.safe_load(sources()[PATHS[1]])
    unmatched = [path for path in PUBLISHER_CHANGE_PATHS if path not in ARTIFACT_ROUTING_PROBES]
    assert unmatched and len(unmatched) == 15
    with pytest.raises(AssertionError, match="Publisher input set"):
        publisher_artifact_routing_contract(workflow, unmatched)


@pytest.mark.parametrize("job", ["single-file-extraction", "vsix-artifacts"])
def test_artifact_routing_requires_both_actual_extraction_job_gates(job):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    workflow["jobs"][job]["if"] = "false"
    with pytest.raises(AssertionError):
        publisher_artifact_routing_contract(workflow)


@pytest.mark.parametrize("path", ARTIFACT_ROUTING_PROBES)
def test_publisher_and_final_release_notes_route_to_all_native_and_vsix_lanes(path):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    assert extraction_routes(workflow, [path]) == {"single-file-extraction": 5, "vsix-artifacts": 4}


@pytest.mark.parametrize("mutation", ["vsix-binary", "repo-path", "name", "action", "condition", "matrix"])
def test_small_vsix_evidence_artifact_excludes_large_or_wrong_scope(mutation):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    vsix = workflow["jobs"]["vsix-artifacts"]
    small = next(step for step in vsix["steps"]
                 if step.get("with", {}).get("name", "").startswith("candidate-vsix-evidence-"))
    if mutation == "vsix-binary":
        small["with"]["path"] += "\nsrc/PPDS.Extension/ppds-validation-*.vsix"
    if mutation == "repo-path":
        small["with"]["path"] += "\nsrc/PPDS.Extension/package.json"
    if mutation == "name":
        small["with"]["name"] = "candidate-vsix-evidence-without-target"
    if mutation == "action":
        small["uses"] = "actions/upload-artifact@v6"
    if mutation == "condition":
        small["if"] = "false"
    if mutation == "matrix":
        vsix["strategy"]["matrix"]["include"].pop()
    with pytest.raises(AssertionError):
        small_vsix_evidence_contract(vsix)


def test_four_small_vsix_evidence_artifacts_keep_the_existing_archive_upload():
    workflow = yaml.safe_load(sources()[PATHS[1]])
    vsix = workflow["jobs"]["vsix-artifacts"]
    small_vsix_evidence_contract(vsix)
    marker = "$" + "{{ matrix.target }}"
    large = next(step for step in vsix["steps"]
                 if step.get("with", {}).get("name") == "candidate-vsix-" + marker)
    assert "src/PPDS.Extension/ppds-validation-" + marker + ".vsix" in large["with"]["path"]
