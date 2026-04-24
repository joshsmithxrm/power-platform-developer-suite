"""Behavioral tests for the patch release detection workflow.

Covers:
  AC-01  Workflow opens issue on release:patch label merge
  AC-02  Mapping changed file paths to package names
  AC-10  Unknown-package warning when no src/PPDS.* paths found
  AC-12  Multi-package detection

Run with: python -m pytest tests/ci/test_post_merge_release_check.py -v
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import map_files_to_packages as mfp  # noqa: E402

# Path to the workflow file under test.
WORKFLOW_PATH = REPO_ROOT / ".github" / "workflows" / "post-merge-release-check.yml"


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _load_workflow() -> dict:
    """Load the workflow YAML, using PyYAML if available."""
    try:
        import yaml  # type: ignore
        with WORKFLOW_PATH.open(encoding="utf-8") as f:
            return yaml.safe_load(f)
    except ImportError:
        pytest.skip("PyYAML not installed; cannot parse workflow YAML")


def _workflow_text() -> str:
    return WORKFLOW_PATH.read_text(encoding="utf-8")


# ---------------------------------------------------------------------------
# AC-01  Workflow structure: opens issue on release:patch label + merged state
# ---------------------------------------------------------------------------

class TestOpensIssueOnPatchLabel:
    """AC-01 — workflow opens issue when merged PR has release:patch label."""

    def test_opens_issue_on_patch_label(self):
        """Parse the YAML and assert the job conditional and issue creation step
        are both present and reference the expected conditions."""
        wf = _load_workflow()

        # Find the job
        jobs = wf.get("jobs", {})
        assert jobs, "Workflow has no jobs"

        job = next(iter(jobs.values()))

        # The job-level `if` must check both merged state and the label.
        job_if = str(job.get("if", ""))
        assert "merged" in job_if, (
            f"Job `if` condition does not reference merged state: {job_if!r}"
        )
        assert "release:patch" in job_if, (
            f"Job `if` condition does not reference 'release:patch' label: {job_if!r}"
        )

        # At least one step must use `gh issue create`.
        steps = job.get("steps", [])
        issue_create_steps = [
            s for s in steps
            if "run" in s and "gh issue create" in str(s.get("run", ""))
        ]
        assert issue_create_steps, (
            "No step found that calls 'gh issue create'"
        )

    def test_workflow_trigger_is_pr_closed_on_main(self):
        """Workflow must fire on pull_request closed events targeting main."""
        wf = _load_workflow()
        on = wf.get("on") or wf.get(True)  # YAML parses `on` as True in some loaders
        pr_trigger = on.get("pull_request", {}) if isinstance(on, dict) else {}
        assert "closed" in (pr_trigger.get("types") or []), (
            "Workflow trigger must include pull_request type: closed"
        )
        assert "main" in (pr_trigger.get("branches") or []), (
            "Workflow trigger must target branch: main"
        )

    def test_issue_created_with_patch_label(self):
        """gh issue create command must include --label release:patch."""
        text = _workflow_text()
        assert "--label" in text and "release:patch" in text, (
            "gh issue create must use --label release:patch"
        )

    def test_workflow_uses_github_token(self):
        """Workflow must use GITHUB_TOKEN for gh CLI auth."""
        text = _workflow_text()
        assert "GITHUB_TOKEN" in text, (
            "Workflow must reference secrets.GITHUB_TOKEN"
        )


# ---------------------------------------------------------------------------
# AC-02  map_files_to_packages — basic path mapping
# ---------------------------------------------------------------------------

class TestMapsPathsToPackages:
    """AC-02 — issue body maps changed paths to package names."""

    def test_maps_paths_to_packages(self):
        result = mfp.map_files_to_packages(["src/PPDS.Query/Foo.cs"])
        assert result == ["PPDS.Query"]

    def test_maps_auth_package(self):
        result = mfp.map_files_to_packages(["src/PPDS.Auth/AuthService.cs"])
        assert result == ["PPDS.Auth"]

    def test_maps_cli_package(self):
        result = mfp.map_files_to_packages(["src/PPDS.Cli/Program.cs"])
        assert result == ["PPDS.Cli"]

    def test_maps_dataverse_package(self):
        result = mfp.map_files_to_packages(["src/PPDS.Dataverse/Client.cs"])
        assert result == ["PPDS.Dataverse"]

    def test_maps_extension_package(self):
        result = mfp.map_files_to_packages(["src/PPDS.Extension/Extension.cs"])
        assert result == ["PPDS.Extension"]

    def test_maps_mcp_package(self):
        result = mfp.map_files_to_packages(["src/PPDS.Mcp/Server.cs"])
        assert result == ["PPDS.Mcp"]

    def test_maps_migration_package(self):
        result = mfp.map_files_to_packages(["src/PPDS.Migration/Runner.cs"])
        assert result == ["PPDS.Migration"]

    def test_maps_plugins_package(self):
        result = mfp.map_files_to_packages(["src/PPDS.Plugins/Plugin.cs"])
        assert result == ["PPDS.Plugins"]

    def test_result_is_sorted(self):
        result = mfp.map_files_to_packages([
            "src/PPDS.Query/A.cs",
            "src/PPDS.Auth/B.cs",
        ])
        assert result == sorted(result), "Result must be sorted alphabetically"

    def test_result_is_deduplicated(self):
        """Same package appearing twice returns only one entry."""
        result = mfp.map_files_to_packages([
            "src/PPDS.Query/Foo.cs",
            "src/PPDS.Query/Bar.cs",
        ])
        assert result == ["PPDS.Query"]


# ---------------------------------------------------------------------------
# AC-10  Unknown-package warning
# ---------------------------------------------------------------------------

class TestUnknownPackageWarning:
    """AC-10 — unknown warning when no src/PPDS.* paths found."""

    def test_unknown_package_warning(self):
        """Non-PPDS paths return ["unknown"] from the mapping script."""
        result = mfp.map_files_to_packages(["docs/README.md"])
        assert result == ["unknown"]

    def test_workflow_contains_unknown_warning_string(self):
        """Workflow YAML must contain the warning text for the unknown case."""
        text = _workflow_text()
        assert "No recognized PPDS package paths found" in text, (
            "Workflow must include the 'No recognized PPDS package paths found' warning text"
        )

    def test_empty_input_returns_unknown(self):
        """Empty input list returns ["unknown"]."""
        result = mfp.map_files_to_packages([])
        assert result == ["unknown"]

    def test_empty_string_path_returns_unknown(self):
        result = mfp.map_files_to_packages([""])
        assert result == ["unknown"]

    def test_bare_ppds_prefix_no_name_returns_unknown(self):
        """Path like src/PPDS./foo.cs has no name segment — must return unknown."""
        result = mfp.map_files_to_packages(["src/PPDS./foo.cs"])
        assert result == ["unknown"]

    def test_unrecognized_ppds_subdir_returns_unknown(self):
        """src/PPDS.UnknownLib/X.cs is not one of the 8 known packages."""
        result = mfp.map_files_to_packages(["src/PPDS.UnknownLib/X.cs"])
        assert result == ["unknown"]

    def test_test_paths_do_not_map_to_packages(self):
        result = mfp.map_files_to_packages(["tests/PPDS.Query.Tests/FooTests.cs"])
        assert result == ["unknown"]

    def test_docs_path_returns_unknown(self):
        result = mfp.map_files_to_packages(["docs/release-cycle.md"])
        assert result == ["unknown"]


# ---------------------------------------------------------------------------
# AC-12  Multi-package detection
# ---------------------------------------------------------------------------

class TestMultiPackageDetection:
    """AC-12 — multiple packages detected when PR touches multiple src/PPDS.* dirs."""

    def test_multi_package_detection(self):
        result = mfp.map_files_to_packages([
            "src/PPDS.Query/Foo.cs",
            "src/PPDS.Cli/Bar.cs",
        ])
        assert result == ["PPDS.Cli", "PPDS.Query"]

    def test_three_packages(self):
        result = mfp.map_files_to_packages([
            "src/PPDS.Auth/X.cs",
            "src/PPDS.Mcp/Y.cs",
            "src/PPDS.Plugins/Z.cs",
        ])
        assert result == ["PPDS.Auth", "PPDS.Mcp", "PPDS.Plugins"]

    def test_mixed_known_and_unknown_paths(self):
        """Only recognized paths contribute to the result."""
        result = mfp.map_files_to_packages([
            "src/PPDS.Query/Foo.cs",
            "docs/README.md",
            ".github/workflows/ci.yml",
        ])
        assert result == ["PPDS.Query"]

    def test_all_eight_packages(self):
        paths = [
            "src/PPDS.Auth/A.cs",
            "src/PPDS.Cli/B.cs",
            "src/PPDS.Dataverse/C.cs",
            "src/PPDS.Extension/D.cs",
            "src/PPDS.Mcp/E.cs",
            "src/PPDS.Migration/F.cs",
            "src/PPDS.Plugins/G.cs",
            "src/PPDS.Query/H.cs",
        ]
        result = mfp.map_files_to_packages(paths)
        assert result == sorted([
            "PPDS.Auth", "PPDS.Cli", "PPDS.Dataverse", "PPDS.Extension",
            "PPDS.Mcp", "PPDS.Migration", "PPDS.Plugins", "PPDS.Query",
        ])
