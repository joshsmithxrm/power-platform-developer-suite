"""Behavioral tests for the shared SemVer and release-impact model."""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

from release_model import (  # noqa: E402
    FileChange,
    ReleaseGraph,
    SemVer,
    Surface,
    build_release_plan,
    render_markdown,
    select_latest_tag,
)


@pytest.fixture(scope="module")
def graph() -> ReleaseGraph:
    return ReleaseGraph.discover(REPO_ROOT)


def _change(path: str) -> FileChange:
    return FileChange(path=path, before="old runtime content", after="new runtime content")


class TestStrictSemVer:
    def test_stable_has_higher_precedence_than_prerelease(self):
        assert SemVer.parse("1.0.0") > SemVer.parse("1.0.0-rc.99")

    def test_multidigit_numeric_prerelease_identifiers_are_numeric(self):
        assert SemVer.parse("1.0.0-beta.10") > SemVer.parse("1.0.0-beta.2")

    def test_build_metadata_does_not_change_precedence(self):
        left = SemVer.parse("1.2.3+build.1")
        right = SemVer.parse("1.2.3+build.999")
        assert left.compare_precedence(right) == 0
        assert left == right

    @pytest.mark.parametrize(
        "value",
        ["1.0", "v1.0.0", "01.0.0", "1.0.0-beta.01", "1.0.0+bad_metadata"],
    )
    def test_malformed_versions_are_rejected(self, value: str):
        with pytest.raises(ValueError):
            SemVer.parse(value)

    def test_latest_tag_uses_semver_not_refname_sorting(self):
        selection = select_latest_tag(
            ["Query-v1.0.0-beta.2", "Query-v1.0.0-beta.10", "Query-v1.0.0"],
            "Query-v",
        )
        assert selection.latest == "Query-v1.0.0"

    def test_malformed_release_tag_is_a_diagnostic(self):
        selection = select_latest_tag(
            ["Query-v1.0", "Query-v1.0.0-beta.2"],
            "Query-v",
        )
        assert selection.latest == "Query-v1.0.0-beta.2"
        assert len(selection.diagnostics) == 1
        assert "Malformed release tag 'Query-v1.0'" in selection.diagnostics[0]


class TestProjectGraphDiscovery:
    def test_publishable_projects_and_extension_are_discovered(self, graph: ReleaseGraph):
        assert set(graph.surfaces) == {
            "PPDS.Auth",
            "PPDS.Cli",
            "PPDS.Dataverse",
            "PPDS.Extension",
            "PPDS.Mcp",
            "PPDS.Migration",
            "PPDS.Plugins",
            "PPDS.Query",
        }

    def test_project_references_drive_dependency_graph(self, graph: ReleaseGraph):
        assert graph.surfaces["PPDS.Query"].project_dependencies == {"PPDS.Dataverse"}
        assert graph.surfaces["PPDS.Migration"].project_dependencies == {"PPDS.Dataverse"}
        assert "PPDS.Plugins" in graph.surfaces["PPDS.Cli"].project_dependencies

    def test_delivery_manifest_declares_extension_bundle(self, graph: ReleaseGraph):
        assert graph.surfaces["PPDS.Extension"].bundles == {"PPDS.Cli"}

    def test_direct_consumer_repropagates_new_upstream_reasons(self):
        graph = ReleaseGraph(
            surfaces={
                "Z.Upstream": Surface("Z.Upstream", "z", "Z-v", "z.csproj"),
                "A.Consumer": Surface(
                    "A.Consumer",
                    "a",
                    "A-v",
                    "a.csproj",
                    project_dependencies=frozenset({"Z.Upstream"}),
                ),
                "Final": Surface(
                    "Final",
                    "final",
                    "Final-v",
                    "final.csproj",
                    project_dependencies=frozenset({"A.Consumer"}),
                ),
            }
        )

        downstream = graph.downstream_of({"Z.Upstream", "A.Consumer"})

        assert downstream["Final"] == {"Z.Upstream", "A.Consumer"}


class TestImpactAnalysis:
    def test_pr_1402_fixture_yields_seven_surfaces_excluding_plugins(self, graph: ReleaseGraph):
        fixture_path = Path(__file__).parent / "fixtures" / "pr_1402_changes.json"
        fixture = json.loads(fixture_path.read_text(encoding="utf-8"))
        changes = [FileChange(**item) for item in fixture]

        plan = build_release_plan(graph, changes, release_kind="patch", channel="stable")

        assert [entry["surface"] for entry in plan["direct_product_changes"]] == [
            "PPDS.Auth",
            "PPDS.Dataverse",
            "PPDS.Migration",
        ]
        assert plan["affected_surfaces"] == [
            "PPDS.Auth",
            "PPDS.Cli",
            "PPDS.Dataverse",
            "PPDS.Extension",
            "PPDS.Mcp",
            "PPDS.Migration",
            "PPDS.Query",
        ]
        assert "PPDS.Plugins" not in plan["affected_surfaces"]
        downstream = {
            entry["surface"]: entry["reasons"]
            for entry in plan["downstream_deliverables"]
        }
        assert downstream == {
            "PPDS.Cli": [
                "consumes changed surface(s): PPDS.Auth, PPDS.Dataverse, PPDS.Migration"
            ],
            "PPDS.Extension": [
                "consumes changed surface(s): PPDS.Auth, PPDS.Dataverse, PPDS.Migration"
            ],
            "PPDS.Mcp": [
                "consumes changed surface(s): PPDS.Auth, PPDS.Dataverse, PPDS.Migration"
            ],
            "PPDS.Query": ["consumes changed surface(s): PPDS.Dataverse"],
        }

    @pytest.mark.parametrize("surface", ["PPDS.Query", "PPDS.Migration"])
    def test_stable_library_plan_includes_dataverse_minver_prerequisite(
        self,
        graph: ReleaseGraph,
        surface: str,
    ):
        plan = build_release_plan(
            graph,
            [_change(f"src/{surface}/Runtime.cs")],
            release_kind="patch",
            channel="stable",
        )
        prerequisites = {
            item["surface"]: item for item in plan["minver_tag_prerequisites"]
        }
        assert prerequisites["PPDS.Dataverse"]["required_by"] == [surface]
        assert "same commit" in prerequisites["PPDS.Dataverse"]["reason"]

    def test_prerelease_plan_does_not_require_stable_minver_prerequisites(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [_change("src/PPDS.Query/Runtime.cs")],
            release_kind="patch",
            channel="prerelease",
        )
        assert plan["minver_tag_prerequisites"] == []

    def test_non_product_changes_have_no_impact(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [
                _change("docs/RELEASE.md"),
                _change("specs/release-cycle.md"),
                _change("tests/PPDS.Query.Tests/RuntimeTests.cs"),
                _change("src/PPDS.Query/CHANGELOG.md"),
                _change("src/PPDS.Extension/src/__tests__/extension.test.ts"),
            ],
        )
        assert plan["direct_product_changes"] == []
        assert plan["downstream_deliverables"] == []
        assert plan["affected_surfaces"] == []
        assert plan["release_targets"] == []
        assert plan["release_needed"] is False

    def test_xml_comment_only_project_change_has_no_impact(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [
                FileChange(
                    path="src/PPDS.Auth/PPDS.Auth.csproj",
                    before="<Project><!-- old --><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>",
                    after="<Project><!-- new --><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>",
                )
            ],
        )
        assert plan["release_targets"] == []
        assert "semantic content unchanged" in plan["ignored_changes"][0]["reason"]

    def test_msbuild_formatting_only_change_has_no_impact(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [
                FileChange(
                    path="src/PPDS.Auth/PPDS.Auth.csproj",
                    before="<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>",
                    after=(
                        "<Project>\n  <PropertyGroup>\n    <Nullable>enable</Nullable>\n"
                        "  </PropertyGroup>\n</Project>"
                    ),
                )
            ],
        )
        assert plan["release_targets"] == []

    @pytest.mark.parametrize(
        ("before", "after"),
        [
            (
                '<Project><Target><Exec Command="a b" /></Target></Project>',
                '<Project><Target><Exec Command="a  b" /></Target></Project>',
            ),
            (
                "<Project><PropertyGroup><AssemblyTitle>one space</AssemblyTitle></PropertyGroup></Project>",
                "<Project><PropertyGroup><AssemblyTitle>one  space</AssemblyTitle></PropertyGroup></Project>",
            ),
            (
                '<Project xmlns="urn:one"><PropertyGroup /></Project>',
                '<Project xmlns="urn:two"><PropertyGroup /></Project>',
            ),
        ],
        ids=["exec-command-whitespace", "property-text-whitespace", "namespace-uri"],
    )
    def test_msbuild_semantic_whitespace_and_namespace_changes_require_release(
        self,
        graph: ReleaseGraph,
        before: str,
        after: str,
    ):
        plan = build_release_plan(
            graph,
            [
                FileChange(
                    path="src/PPDS.Auth/PPDS.Auth.csproj",
                    before=before,
                    after=after,
                )
            ],
        )
        assert "PPDS.Auth" in plan["release_targets"]

    def test_csharp_xml_documentation_change_counts_conservatively(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [
                FileChange(
                    path="src/PPDS.Auth/AuthService.cs",
                    before="/// <summary>Old.</summary>\npublic sealed class AuthService {}",
                    after="/// <summary>New.</summary>\npublic sealed class AuthService {}",
                )
            ],
        )
        assert "PPDS.Auth" in plan["release_targets"]

    def test_csharp_raw_string_lines_that_look_like_xml_docs_are_not_suppressed(
        self,
        graph: ReleaseGraph,
    ):
        plan = build_release_plan(
            graph,
            [
                FileChange(
                    path="src/PPDS.Auth/AuthService.cs",
                    before='var text = """\n/// old runtime value\n""";',
                    after='var text = """\n/// new runtime value\n""";',
                )
            ],
        )
        assert "PPDS.Auth" in plan["release_targets"]

    def test_msbuild_mixed_content_tail_change_is_not_suppressed(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [
                FileChange(
                    path="src/PPDS.Auth/PPDS.Auth.csproj",
                    before="<Project><PropertyGroup />old runtime tail</Project>",
                    after="<Project><PropertyGroup />new runtime tail</Project>",
                )
            ],
        )
        assert "PPDS.Auth" in plan["release_targets"]

    def test_dot_directory_path_is_preserved_and_explained(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [_change("./.github/workflows/release.yml")],
        )
        assert plan["release_targets"] == []
        assert plan["ignored_changes"] == [
            {
                "path": ".github/workflows/release.yml",
                "reason": "automation/tooling change",
            }
        ]

    def test_uncertain_source_change_is_included_conservatively(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [FileChange(path="src/PPDS.Auth/NewCredential.cs", before=None, after=None)],
        )
        assert "PPDS.Auth" in plan["release_targets"]
        reasons = plan["direct_product_changes"][0]["reasons"]
        assert any("included conservatively" in reason for reason in reasons)

    def test_unmapped_source_change_cannot_silently_suppress_release_review(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [_change("src/PPDS.NewSurface/Runtime.cs")],
        )
        assert set(graph.dotnet_surfaces) <= set(plan["release_targets"])
        assert "all .NET surfaces were conservatively included" in plan["diagnostics"][0]

    def test_coordinated_minor_plans_every_surface(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [_change("src/PPDS.Auth/AuthService.cs")],
            release_kind="minor",
            channel="stable",
        )
        assert plan["release_targets"] == sorted(graph.surfaces)
        assert plan["minver_tag_prerequisites"] == []
        assert "every release surface" in plan["release_scope_explanation"]

    def test_explained_output_keeps_categories_separate(self, graph: ReleaseGraph):
        plan = build_release_plan(
            graph,
            [_change("src/PPDS.Query/Runtime.cs")],
            tags=["Query-v1.0.0-beta.2", "Query-v1.0.0"],
        )
        markdown = render_markdown(plan)
        assert "### Direct Product Changes" in markdown
        assert "### Downstream Deliverables" in markdown
        assert "### Same-Commit MinVer Tag Prerequisites" in markdown
        assert "PPDS.Query" in markdown
        assert "PPDS.Dataverse" in markdown
        assert plan["safety"].startswith("This plan is advisory only")
