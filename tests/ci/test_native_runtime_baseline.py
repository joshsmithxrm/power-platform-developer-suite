"""Cross-input contracts for distributed runtime and the no-publish artifact route."""
from fnmatch import fnmatchcase
import importlib.util
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

DOCUMENTATION_PROBES = (
    "docs/RELEASE.md", "specs/release-cycle.md",
    "docs/releases/v1.5.0.md", "docs/releases/security/runtime-patch.md",
)

NATIVE_INPUT_PROBES = {
    "src/**": "src/PPDS.Cli/Program.cs",
    "tests/PPDS.Cli.Tests/**": "tests/PPDS.Cli.Tests/Plugins/Extraction/PluginExtractorTests.cs",
    "tests/fixtures/SingleFilePlugin/**": "tests/fixtures/SingleFilePlugin/SingleFilePlugin.csproj",
    "tests/fixtures/ReferenceCacheProbe/**": "tests/fixtures/ReferenceCacheProbe/ReferenceCacheProbe.csproj",
    "scripts/ci/smoke_single_file_extraction.py": "scripts/ci/smoke_single_file_extraction.py",
    "scripts/ci/verify_native_distribution.py": "scripts/ci/verify_native_distribution.py",
    "scripts/ci/check_native_glibc.sh": "scripts/ci/check_native_glibc.sh",
    ".github/workflows/extension-publish.yml": ".github/workflows/extension-publish.yml",
    "Directory.*.props": "Directory.Packages.props",
    "global.json": "global.json",
    "NuGet.config": "NuGet.config",
    ".github/workflows/test.yml": ".github/workflows/test.yml",
    ".github/workflows/release-cli.yml": ".github/workflows/release-cli.yml",
    ".github/workflows/publish-nuget.yml": ".github/workflows/publish-nuget.yml",
}
ARTIFACT_ROUTING_PROBES = tuple(NATIVE_INPUT_PROBES.values()) + ("Directory.Build.props",)

DOTNET_INPUT_PROBES = (
    "global.json", "NuGet.config", "nuget.config", ".github/workflows/test.yml",
    "Directory.Build.props", "Directory.Packages.props", "Directory.Custom.props",
    "Directory.Build.targets", "src/PPDS.Cli/Build.props", "src/PPDS.Cli/Build.targets",
)


def classifications(workflow, changed_paths, event="pull_request"):
    """Evaluate finite path probes, not a general Picomatch or event emulator.

    fnmatchcase is sufficient for the literal paths/patterns exercised here.
    These tests follow supplied changed paths into the workflow's job selection;
    they do not reproduce dorny/paths-filter's GitHub PR/push change discovery.
    """
    changes = workflow["jobs"]["changes"]
    steps = [s for s in changes["steps"] if str(s.get("uses", "")).startswith("dorny/paths-filter@")]
    assert len(steps) == 1
    step = steps[0]
    assert step["if"] == "github.event_name != 'workflow_dispatch'"
    filters = yaml.safe_load(step["with"]["filters"])
    flags = {}
    for name, forced in (("code", "false"), ("extraction", "true"), ("python", "true")):
        expected = "${{ github.event_name == 'workflow_dispatch' && '" + forced + "' || steps." + step["id"] + ".outputs." + name + " }}"
        assert changes["outputs"][name] == expected
        assert all(isinstance(pattern, str) for pattern in filters[name])
        flags[name] = (forced == "true") if event == "workflow_dispatch" else any(
            fnmatchcase(path, pattern) for path in changed_paths for pattern in filters[name])
    return flags


def extraction_routes(workflow, changed_paths, event="pull_request"):
    # These fixtures use literal paths and directory/** selectors from the real filter.
    selected = classifications(workflow, changed_paths, event)["extraction"]
    routes = {}
    for name in ["single-file-extraction", "vsix-artifacts"]:
        job = workflow["jobs"][name]
        needs = [job["needs"]] if isinstance(job["needs"], str) else job["needs"]
        assert "changes" in needs
        assert job["if"] == "needs.changes.outputs.extraction == 'true'"
        routes[name] = len(job["strategy"]["matrix"]["include"]) if selected else 0
    return routes


def dotnet_routing_contract(workflow, path, event):
    flags = classifications(workflow, [path], event)
    assert flags["code"], f"Full .NET routing missing for {path}"
    test = workflow["jobs"]["test"]
    assert test["needs"] == "changes"
    assert test["if"] == "needs.changes.outputs.code == 'true'"
    # Follow the selected flag into the real aggregate, rather than just checking
    # that a glob string appears in the workflow.
    module_spec = importlib.util.spec_from_file_location("dotnet_route_status", ROOT / "scripts/ci/check_test_status.py")
    aggregate = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(aggregate)
    needs = {"changes": {"result": "success", "outputs": {k: str(v).lower() for k, v in flags.items()}}}
    for flag, job in aggregate.SUITES:
        needs[job] = {"result": "success" if flags[flag] else "skipped"}
    assert aggregate.failures(needs) == []
    for result in ("skipped", "failure", "cancelled", "missing"):
        needs["test"]["result"] = result
        assert aggregate.failures(needs), (path, event, result)


@pytest.mark.parametrize("event", ["push", "pull_request"])
@pytest.mark.parametrize("path", DOTNET_INPUT_PROBES)
def test_build_and_restore_inputs_require_full_dotnet_suite(path, event):
    dotnet_routing_contract(yaml.safe_load(sources()[PATHS[1]]), path, event)


@pytest.mark.parametrize("selector,path", [
    ("global.json", "global.json"),
    ("NuGet.config", "NuGet.config"),
    ("**.props", "src/PPDS.Cli/Build.props"),
    ("**.targets", "Directory.Build.targets"),
    (".github/workflows/test.yml", ".github/workflows/test.yml"),
])
def test_omitting_dotnet_input_selector_reopens_coverage_gap(selector, path):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    step = next(s for s in workflow["jobs"]["changes"]["steps"]
                if str(s.get("uses", "")).startswith("dorny/paths-filter@"))
    filters = yaml.safe_load(step["with"]["filters"])
    filters["code"].remove(selector)
    step["with"]["filters"] = yaml.safe_dump(filters)
    with pytest.raises(AssertionError, match="Full .NET routing missing"):
        dotnet_routing_contract(workflow, path, "pull_request")


@pytest.mark.parametrize("path", ["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json", "docs/RELEASE.md"])
def test_extension_dependencies_and_docs_do_not_select_dotnet(path):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    for event in ("push", "pull_request"):
        assert classifications(workflow, [path], event)["code"] is False


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
    for job in [native, smoke, vsix, extension["jobs"]["publish"]]:
        setup = [s for s in job["steps"] if str(s.get("uses", "")).startswith("actions/setup-dotnet@")]
        assert len(setup) == 1
        versions = setup[0]["with"]["dotnet-version"].splitlines()
        assert [v.strip() for v in versions if v.strip().startswith("10.")] == ["10.0.115"]
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
    (PATHS[2], "dotnet-version: '10.0.115'", "dotnet-version: '8.0.x'"),
    (PATHS[1], "    permissions:\n      contents: read\n    runs-on: ${{ matrix.os }}", "    permissions:\n      contents: write\n    runs-on: ${{ matrix.os }}"),
    (PATHS[1], "needs: [changes, test, single-file-extraction, vsix-artifacts, python-tests]", "needs: [changes, test, single-file-extraction, python-tests]"),
])
def test_stale_or_unsafe_runtime_input_fails(path, old, new):
    value = sources()
    assert old in value[path]
    value[path] = value[path].replace(old, new, 1)
    with pytest.raises(AssertionError):
        contract(value)


@pytest.mark.parametrize("path,job_name", [
    (PATHS[0], "build"),
    (PATHS[1], "single-file-extraction"),
    (PATHS[1], "vsix-artifacts"),
    (PATHS[2], "publish"),
])
@pytest.mark.parametrize("sdk_version", ["10.0.114", "10.0.404"])
def test_each_distribution_job_rejects_stale_or_different_band_sdk(path, job_name, sdk_version):
    value = sources()
    workflow = yaml.safe_load(value[path])
    setup = next(s for s in workflow["jobs"][job_name]["steps"]
                 if str(s.get("uses", "")).startswith("actions/setup-dotnet@"))
    assert "10.0.115" in setup["with"]["dotnet-version"]
    setup["with"]["dotnet-version"] = setup["with"]["dotnet-version"].replace("10.0.115", sdk_version)
    value[path] = yaml.safe_dump(workflow)
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


@pytest.mark.parametrize("selector", NATIVE_INPUT_PROBES)
def test_omitting_native_input_selector_breaks_artifact_routing(selector):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    step = next(s for s in workflow["jobs"]["changes"]["steps"]
                if str(s.get("uses", "")).startswith("dorny/paths-filter@"))
    filters = yaml.safe_load(step["with"]["filters"])
    filters["extraction"].remove(selector)
    step["with"]["filters"] = yaml.safe_dump(filters)
    with pytest.raises(AssertionError, match="Artifact routing missing|Publisher input set"):
        publisher_artifact_routing_contract(workflow)


def test_unmatched_publisher_input_set_cannot_claim_fresh_artifact_coverage():
    workflow = yaml.safe_load(sources()[PATHS[1]])
    unmatched = [path for path in PUBLISHER_CHANGE_PATHS if path not in ARTIFACT_ROUTING_PROBES]
    assert unmatched and len(unmatched) == len(PUBLISHER_CHANGE_PATHS) - 1
    with pytest.raises(AssertionError, match="Publisher input set"):
        publisher_artifact_routing_contract(workflow, unmatched)


@pytest.mark.parametrize("job", ["single-file-extraction", "vsix-artifacts"])
def test_artifact_routing_requires_both_actual_extraction_job_gates(job):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    workflow["jobs"][job]["if"] = "false"
    with pytest.raises(AssertionError):
        publisher_artifact_routing_contract(workflow)


@pytest.mark.parametrize("path", ARTIFACT_ROUTING_PROBES)
def test_native_inputs_route_to_all_native_and_vsix_lanes(path):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    assert extraction_routes(workflow, [path]) == {"single-file-extraction": 5, "vsix-artifacts": 4}


@pytest.mark.parametrize("paths", [[path] for path in DOCUMENTATION_PROBES] + [list(DOCUMENTATION_PROBES)])
def test_release_documentation_selects_python_without_artifact_matrix(paths):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    assert classifications(workflow, paths) == {"code": False, "extraction": False, "python": True}
    assert extraction_routes(workflow, paths) == {"single-file-extraction": 0, "vsix-artifacts": 0}


@pytest.mark.parametrize("path", ARTIFACT_ROUTING_PROBES)
def test_documentation_plus_native_input_keeps_complete_artifact_matrix(path):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    assert extraction_routes(workflow, [*DOCUMENTATION_PROBES, path]) == {"single-file-extraction": 5, "vsix-artifacts": 4}


def manual_artifact_contract(workflow):
    triggers = workflow.get("on", workflow.get(True))
    assert triggers["workflow_dispatch"] == {}
    assert workflow["permissions"] == {"contents": "read"}
    assert workflow["concurrency"]["group"] == "${{ github.workflow }}-${{ github.ref }}-${{ github.event_name }}"
    jobs = workflow["jobs"]
    assert classifications(workflow, [], "workflow_dispatch") == {"code": False, "extraction": True, "python": True}
    assert extraction_routes(workflow, [], "workflow_dispatch") == {"single-file-extraction": 5, "vsix-artifacts": 4}
    assert jobs["test"]["if"] == "needs.changes.outputs.code == 'true'"
    assert jobs["test"]["name"] == "${{ github.event_name == 'workflow_dispatch' && '.NET tests not selected for artifact qualification' || 'test' }}"
    assert jobs["python-tests"]["if"] == "needs.changes.outputs.python == 'true'"
    assert jobs["python-tests"]["uses"] == "./.github/workflows/workflow-tests.yml"
    assert "secrets" not in jobs["python-tests"]
    assert jobs["test-status"]["name"] == "${{ github.event_name == 'workflow_dispatch' && 'Native candidate qualification' || 'test-status' }}"
    for name in ("single-file-extraction", "vsix-artifacts"):
        job = jobs[name]
        assert job.get("permissions", workflow["permissions"]) == {"contents": "read"}
        assert "environment" not in job and "secrets." not in str(job) and "id-token" not in str(job)
        checkout = next(step for step in job["steps"] if str(step.get("uses", "")).startswith("actions/checkout@"))
        assert checkout["with"]["ref"] == "${{ github.sha }}"
        assert checkout["with"]["persist-credentials"] is False
        assert "--source ${{ github.sha }}" in str(job)


def test_manual_artifact_qualification_is_exact_ref_read_only_and_bounded():
    manual_artifact_contract(yaml.safe_load(sources()[PATHS[1]]))


@pytest.mark.parametrize("mutation", ["filter-on-dispatch", "missing-output", "full-dotnet", "moving-ref", "write-grant",
                                     "shared-concurrency", "automatic-check-name", "automatic-dotnet-name"])
def test_manual_artifact_route_rejects_unsafe_or_incomplete_wiring(mutation):
    workflow = yaml.safe_load(sources()[PATHS[1]])
    jobs = workflow["jobs"]
    if mutation == "filter-on-dispatch":
        jobs["changes"]["steps"][1]["if"] = "always()"
    if mutation == "missing-output":
        jobs["changes"]["outputs"]["python"] = "${{ steps.filter.outputs.python }}"
    if mutation == "full-dotnet":
        jobs["changes"]["outputs"]["code"] = jobs["changes"]["outputs"]["code"].replace("&& 'false'", "&& 'true'")
    if mutation == "moving-ref":
        jobs["vsix-artifacts"]["steps"][0]["with"]["ref"] = "main"
    if mutation == "write-grant":
        workflow["permissions"]["contents"] = "write"
    if mutation == "shared-concurrency":
        workflow["concurrency"]["group"] = "${{ github.workflow }}-${{ github.ref }}"
    if mutation == "automatic-check-name":
        jobs["test-status"]["name"] = "test-status"
    if mutation == "automatic-dotnet-name":
        jobs["test"]["name"] = "test"
    with pytest.raises(AssertionError):
        manual_artifact_contract(workflow)


def test_docs_and_manual_routes_preserve_fail_closed_aggregate():
    module_spec = importlib.util.spec_from_file_location("native_test_status", ROOT / "scripts/ci/check_test_status.py")
    aggregate = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(aggregate)
    workflow = yaml.safe_load(sources()[PATHS[1]])
    for event in ("pull_request", "workflow_dispatch"):
        flags = classifications(workflow, DOCUMENTATION_PROBES, event)
        needs = {"changes": {"result": "success", "outputs": {k: str(v).lower() for k, v in flags.items()}}}
        for flag, job in aggregate.SUITES:
            needs[job] = {"result": "success" if flags[flag] else "skipped"}
        assert aggregate.failures(needs) == []
        for flag, job in aggregate.SUITES:
            if flags[flag]:
                for result in ("failure", "cancelled", "skipped", "missing"):
                    needs[job]["result"] = result
                    assert aggregate.failures(needs), (event, job, result)
                needs[job]["result"] = "success"


def test_manual_skipped_dotnet_cannot_replace_real_dependency_evidence():
    from scripts.ci import check_major_bump_tested as major

    real = {"workflow": "Test", "name": "test", "state": "SUCCESS", "startedAt": "2026-01-01T00:00:00Z",
            "runId": 1, "runAttempt": 1, "jobId": 1}
    manual = {**real, "name": ".NET tests not selected for artifact qualification", "state": "SKIPPED",
              "startedAt": "2026-01-02T00:00:00Z", "runId": 2, "jobId": 2}
    evidence = major.REQUIRED_EVIDENCE_BY_ECOSYSTEM["nuget"]
    assert major.check_required_evidence([real, manual], evidence)[0]
    assert not major.check_required_evidence([{**real, "state": "FAILURE"}, manual], evidence)[0]
    assert not major.check_required_evidence([manual], evidence)[0]


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
