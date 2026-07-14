"""Unit tests for scripts/dependabot/classify.py.

Run with: python -m pytest tests/scripts/dependabot/test_classify.py -v

These tests use sample dependabot PR JSON fixtures to verify each classifier
branch. They are pure functions — no network, no gh CLI, no git.
"""
from __future__ import annotations

import os
import re
import sys
import unittest
from pathlib import Path

# Ensure scripts/ is importable
REPO_ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "dependabot"))

import classify  # noqa: E402


def make_pr(
    number: int = 1,
    title: str = "",
    body: str = "",
    labels=None,
    head_ref: str = "",
    files=None,
) -> dict:
    """Build a PR dict matching the gh pr view --json shape."""
    return {
        "number": number,
        "title": title,
        "body": body,
        "labels": [{"name": n} for n in (labels or [])],
        "headRefName": head_ref,
        "files": [{"path": p} for p in (files or [])],
    }


class TestParseTitle(unittest.TestCase):
    def test_simple_npm_bump(self):
        pkg, fv, tv = classify.parse_title("Bump knip from 6.1.0 to 6.3.0")
        self.assertEqual(pkg, "knip")
        self.assertEqual(fv, "6.1.0")
        self.assertEqual(tv, "6.3.0")

    def test_scoped_npm_bump(self):
        pkg, fv, tv = classify.parse_title("Bump @types/node from 22.5.0 to 22.6.0")
        self.assertEqual(pkg, "@types/node")
        self.assertEqual(fv, "22.5.0")
        self.assertEqual(tv, "22.6.0")

    def test_nuget_bump(self):
        pkg, fv, tv = classify.parse_title("Bump Microsoft.Identity.Client from 4.55.0 to 4.56.0")
        self.assertEqual(pkg, "Microsoft.Identity.Client")
        self.assertEqual(fv, "4.55.0")
        self.assertEqual(tv, "4.56.0")

    def test_grouped_bump(self):
        pkg, fv, tv = classify.parse_title("Bump the npm_and_yarn group across 1 directory with 2 updates")
        self.assertEqual(pkg, "group:npm_and_yarn")
        self.assertIsNone(fv)
        self.assertIsNone(tv)

    def test_conventional_commit_prefix_scoped(self):
        # dependabot prepends "deps(extension): " per .github/dependabot.yml
        pkg, fv, tv = classify.parse_title(
            "deps(extension): Bump eslint from 10.4.0 to 10.5.0 in /src/PPDS.Extension"
        )
        self.assertEqual(pkg, "eslint")
        self.assertEqual(fv, "10.4.0")
        self.assertEqual(tv, "10.5.0")

    def test_conventional_commit_prefix_bare(self):
        pkg, fv, tv = classify.parse_title("deps: Bump the microsoft-extensions group with 12 updates")
        self.assertEqual(pkg, "group:microsoft-extensions")
        self.assertIsNone(fv)
        self.assertIsNone(tv)

    def test_conventional_commit_prefix_dev(self):
        pkg, fv, tv = classify.parse_title(
            "chore(deps-dev): Bump vite from 8.0.8 to 8.0.16 in /tests/PPDS.DocsGen.Extension.Tests"
        )
        self.assertEqual(pkg, "vite")
        self.assertEqual(fv, "8.0.8")
        self.assertEqual(tv, "8.0.16")

    def test_unparseable(self):
        pkg, fv, tv = classify.parse_title("docs: tweak readme")
        self.assertIsNone(pkg)
        self.assertIsNone(fv)
        self.assertIsNone(tv)

    def test_v_prefix_github_actions(self):
        # GitHub Actions commonly uses "vN" tags — make sure the bump-title
        # regex captures both versions even when prefixed.
        pkg, fv, tv = classify.parse_title("Bump actions/checkout from v3 to v4")
        self.assertEqual(pkg, "actions/checkout")
        self.assertEqual(fv, "v3")
        self.assertEqual(tv, "v4")


class TestClassifyUpdateType(unittest.TestCase):
    def test_patch(self):
        self.assertEqual(classify.classify_update_type("1.2.3", "1.2.4"), "patch")

    def test_minor(self):
        self.assertEqual(classify.classify_update_type("1.2.3", "1.3.0"), "minor")

    def test_major(self):
        self.assertEqual(classify.classify_update_type("1.2.3", "2.0.0"), "major")

    def test_major_downgrade_treated_as_major(self):
        # Downgrade is suspicious — treat as major (manual review).
        self.assertEqual(classify.classify_update_type("2.0.0", "1.9.5"), "major")

    def test_v_prefix_stripped(self):
        self.assertEqual(classify.classify_update_type("v1.2.3", "v1.2.4"), "patch")

    def test_two_part_minor(self):
        self.assertEqual(classify.classify_update_type("1.19", "1.20"), "minor")

    def test_two_part_major(self):
        self.assertEqual(classify.classify_update_type("1.19", "2.0"), "major")

    def test_prerelease_suffix_stripped(self):
        self.assertEqual(classify.classify_update_type("1.0.0-beta.1", "1.0.0-beta.2"), "patch")

    def test_unknown_when_missing(self):
        self.assertEqual(classify.classify_update_type(None, "1.0.0"), "unknown")
        self.assertEqual(classify.classify_update_type("1.0.0", None), "unknown")
        self.assertEqual(classify.classify_update_type(None, None), "unknown")

    def test_minor_downgrade_treated_as_major(self):
        # Any downgrade — including minor (1.2.0 -> 1.1.0) — must classify as
        # 'major' so it routes to Group C / manual review and never auto-merges.
        self.assertEqual(classify.classify_update_type("1.2.0", "1.1.0"), "major")

    def test_patch_downgrade_treated_as_major(self):
        # Patch-level downgrade (1.0.2 -> 1.0.1) is also suspicious.
        self.assertEqual(classify.classify_update_type("1.0.2", "1.0.1"), "major")


class TestDetectEcosystem(unittest.TestCase):
    def test_nuget_label(self):
        self.assertEqual(classify.detect_ecosystem(["nuget", "dependencies"], ""), "nuget")

    def test_npm_label(self):
        self.assertEqual(classify.detect_ecosystem(["npm", "dependencies"], ""), "npm")

    def test_javascript_label_means_npm(self):
        self.assertEqual(classify.detect_ecosystem(["javascript"], ""), "npm")

    def test_github_actions_label(self):
        self.assertEqual(classify.detect_ecosystem(["github_actions"], ""), "github-actions")

    def test_head_ref_fallback_nuget(self):
        self.assertEqual(classify.detect_ecosystem([], "dependabot/nuget/Foo-1.2.3"), "nuget")

    def test_head_ref_fallback_npm(self):
        self.assertEqual(classify.detect_ecosystem([], "dependabot/npm_and_yarn/foo-1.2.3"), "npm")

    def test_head_ref_fallback_actions(self):
        self.assertEqual(classify.detect_ecosystem([], "dependabot/github_actions/actions/checkout-v4"), "github-actions")

    def test_unknown(self):
        self.assertEqual(classify.detect_ecosystem(["random"], "feature/foo"), "unknown")


class TestClassifyPR(unittest.TestCase):
    # Group A — auto-merge eligible

    def test_patch_npm_tooling_is_group_a(self):
        pr = make_pr(
            number=820,
            title="Bump knip from 6.1.0 to 6.3.0",
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/knip-6.3.0",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")  # 6.1->6.3 is minor
        # but knip is tooling, so it's still A
        self.assertIn("tooling", c.reason)

    def test_minor_stylelint_is_group_a(self):
        # stylelint is a dev-time CSS linter (same class as eslint/knip/prettier)
        # — a minor bump must be Group A (tooling), not Group B. Real case: PR #1279.
        pr = make_pr(
            number=1279,
            title="Bump stylelint from 17.11.1 to 17.14.0",
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/stylelint-17.14.0",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("tooling", c.reason)

    def test_minor_eslint_plugin_import_x_is_group_a(self):
        # eslint-plugin-import-x is a dev-only ESLint plugin (devDependencies in
        # src/PPDS.Extension) — definitionally lint tooling, never runtime. Real
        # case: PR #1342 (4.16.2 -> 4.17.1) misclassified as Group B "verify-
        # then-merge for runtime change" (issue #1350). Covered by the
        # "eslint-plugin-" prefix: ESLint resolves plugins by that naming
        # convention, so any eslint-plugin-* package is lint tooling.
        pr = make_pr(
            number=1342,
            title="deps(extension): Bump eslint-plugin-import-x from 4.16.2 to 4.17.1 in /src/PPDS.Extension",
            labels=["dependencies"],
            head_ref="dependabot/npm_and_yarn/src/PPDS.Extension/eslint-plugin-import-x-4.17.1",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("tooling", c.reason)

    def test_minor_stylelint_config_standard_is_group_a(self):
        # stylelint shareable configs are named stylelint-config-* by stylelint's
        # resolution convention — pure lint configuration, dev-only (issue #1350).
        pr = make_pr(
            number=1343,
            title="deps(extension): Bump stylelint-config-standard from 40.0.0 to 40.1.0 in /src/PPDS.Extension",
            labels=["dependencies"],
            head_ref="dependabot/npm_and_yarn/src/PPDS.Extension/stylelint-config-standard-40.1.0",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("tooling", c.reason)

    def test_minor_vscode_test_electron_is_group_a(self):
        # @vscode/test-electron downloads VS Code and runs extension tests —
        # test-harness tooling only, never packaged into the VSIX (issue #1350).
        pr = make_pr(
            number=1344,
            title="deps(extension): Bump @vscode/test-electron from 3.0.0 to 3.1.0 in /src/PPDS.Extension",
            labels=["dependencies"],
            head_ref="dependabot/npm_and_yarn/src/PPDS.Extension/vscode/test-electron-3.1.0",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("tooling", c.reason)

    def test_minor_xunit_skippablefact_is_group_a(self):
        # Xunit.SkippableFact is an xunit extension referenced only by test
        # projects (tests/PPDS.Auth.IntegrationTests) — test tooling (issue #1350).
        pr = make_pr(
            number=1345,
            title="deps: Bump Xunit.SkippableFact from 1.5.61 to 1.6.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Xunit.SkippableFact-1.6.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("tooling", c.reason)

    def test_minor_codeanalysis_analyzers_is_group_a(self):
        # Microsoft.CodeAnalysis.Analyzers is a compile-time analyzer consumed
        # with PrivateAssets=all (src/PPDS.Analyzers) — a C# linter; it ships no
        # runtime bytes, and a bad bump fails the build (CI-visible). Issue #1350.
        pr = make_pr(
            number=1346,
            title="deps: Bump Microsoft.CodeAnalysis.Analyzers from 3.11.0 to 3.12.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Microsoft.CodeAnalysis.Analyzers-3.12.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("tooling", c.reason)

    def test_minor_publicapi_analyzers_is_group_a(self):
        # Microsoft.CodeAnalysis.PublicApiAnalyzers gates PublicAPI.*.txt at
        # compile time, PrivateAssets=all — analyzer/linter tooling (issue #1350).
        pr = make_pr(
            number=1347,
            title="deps: Bump Microsoft.CodeAnalysis.PublicApiAnalyzers from 3.11.0 to 3.12.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Microsoft.CodeAnalysis.PublicApiAnalyzers-3.12.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("tooling", c.reason)

    def test_patch_nuget_test_sdk_is_group_a(self):
        pr = make_pr(
            number=824,
            title="Bump Microsoft.NET.Test.Sdk from 17.10.0 to 17.10.1",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Microsoft.NET.Test.Sdk-17.10.1",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "patch")

    def test_github_actions_minor_is_group_a(self):
        pr = make_pr(
            number=830,
            title="Bump actions/checkout from 4.1.0 to 4.2.0",
            labels=["github_actions", "dependencies"],
            head_ref="dependabot/github_actions/actions/checkout-4.2.0",
            files=[".github/workflows/ci.yml"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "minor")

    def test_lockfile_only_is_group_a(self):
        pr = make_pr(
            number=831,
            title="Bump some-transitive-dep from 1.0.0 to 1.0.1",
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/some-transitive-dep-1.0.1",
            files=["src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")

    def test_patch_non_critical_nuget_is_group_a(self):
        pr = make_pr(
            number=832,
            title="Bump Newtonsoft.Json from 13.0.1 to 13.0.2",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Newtonsoft.Json-13.0.2",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "patch")

    # Group B — verify-then-merge

    def test_minor_auth_critical_is_group_b(self):
        pr = make_pr(
            number=840,
            title="Bump Microsoft.Identity.Client from 4.55.0 to 4.56.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Microsoft.Identity.Client-4.56.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("auth-critical", c.reason)

    def test_patch_auth_critical_is_group_b(self):
        # Regression for real PR #1288: an auth-critical PATCH bump
        # (System.Security.Cryptography.Pkcs 10.0.5 -> 10.0.9) must go through
        # verify-then-merge (Group B), NOT auto-merge (Group A). Per
        # docs/MERGE-POLICY.md, auth-critical packages route to Group B at any
        # non-major version — patch included. Before the fix this fell through
        # to the generic "patch bump on non-critical path" branch and became A.
        pr = make_pr(
            number=1288,
            title="Bump System.Security.Cryptography.Pkcs from 10.0.5 to 10.0.9",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/System.Security.Cryptography.Pkcs-10.0.9",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertEqual(c.update_type, "patch")
        self.assertIn("auth-critical", c.reason)
        # Reason should reflect the actual update type, not be hard-coded to "minor".
        self.assertIn("patch", c.reason)
        self.assertNotIn("minor", c.reason)

    def test_minor_azure_identity_is_group_b(self):
        pr = make_pr(
            number=841,
            title="Bump Azure.Identity from 1.12.0 to 1.13.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Azure.Identity-1.13.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")

    def test_minor_runtime_lib_is_group_b(self):
        # Terminal.Gui — not auth-critical, not tooling; minor bump = Group B
        pr = make_pr(
            number=842,
            title="Bump Terminal.Gui from 1.19.0 to 1.20.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Terminal.Gui-1.20.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("verify-then-merge", c.reason)

    def test_patch_touching_auth_path_is_group_b(self):
        pr = make_pr(
            number=843,
            title="Bump SomePkg from 1.0.0 to 1.0.1",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/SomePkg-1.0.1",
            files=["src/PPDS.Auth/PPDS.Auth.csproj"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertIn("auth-critical path", c.reason)

    def test_grouped_unparseable_defaults_to_group_c(self):
        # No body to parse members from — fall back to Group C ("when unsure,
        # default to manual merge"), matching the single-PR unparseable
        # fallback. Previously defaulted to B; tightened to C per gemini
        # second-pass review on PR #819.
        pr = make_pr(
            number=844,
            title="Bump the npm_and_yarn group across 1 directory with 2 updates",
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/group-update",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertIn("grouped", c.reason)
        self.assertIn("manual review", c.reason)

    # Grouped PR most-conservative-wins (issue #817) — three cases:
    # all-patch -> A, any-minor (no major) -> B, any-major -> C.

    def test_grouped_all_patch_is_group_a(self):
        # All members are patch bumps -> Group A (auto-merge eligible).
        pr = make_pr(
            number=860,
            title="Bump the npm_and_yarn group across 1 directory with 3 updates",
            body=(
                "Bumps the npm_and_yarn group with 3 updates:\n\n"
                "Updates `foo` from 1.0.0 to 1.0.1\n"
                "Updates `bar` from 2.3.4 to 2.3.5\n"
                "Updates `baz` from 0.9.0 to 0.9.1\n"
            ),
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/group-update",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "patch")
        self.assertIn("most-conservative=patch", c.reason)

    def test_grouped_real_body_markdown_link_past_tense(self):
        # Real dependabot grouped-PR body: past-tense "Updated", markdown-link
        # package names, trailing period (regression for #1255/#1256 format).
        pr = make_pr(
            number=1255,
            title="deps: Bump the microsoft-extensions group with 2 updates",
            body=(
                "Bumps the microsoft-extensions group with 2 updates:\n\n"
                "Updated [Microsoft.Extensions.Configuration](https://github.com/dotnet/dotnet) from 10.0.8 to 10.0.9.\n"
                "Updated [Microsoft.Extensions.Logging](https://github.com/dotnet/dotnet) from 10.0.8 to 10.0.9.\n"
            ),
            labels=["dependencies"],
            head_ref="dependabot/nuget/microsoft-extensions-151a844973",
            files=["Directory.Packages.props"],
        )
        members = classify.parse_group_members(pr["body"])
        self.assertEqual(len(members), 2)
        self.assertEqual(members[0][0], "Microsoft.Extensions.Configuration")
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "patch")

    def test_grouped_with_minor_is_group_b(self):
        # Mix of patch + minor (no major) -> Group B (verify-then-merge).
        pr = make_pr(
            number=861,
            title="Bump the npm_and_yarn group across 1 directory with 2 updates",
            body=(
                "Bumps the npm_and_yarn group with 2 updates:\n\n"
                "Updates `foo` from 1.0.0 to 1.0.1\n"
                "Updates `bar` from 2.3.0 to 2.4.0\n"
            ),
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/group-update",
            files=["src/PPDS.Extension/package.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("most-conservative=minor", c.reason)

    def test_grouped_with_major_is_group_c(self):
        # Any major bump in the group -> Group C (manual review).
        pr = make_pr(
            number=862,
            title="Bump the npm_and_yarn group across 1 directory with 3 updates",
            body=(
                "Bumps the npm_and_yarn group with 3 updates:\n\n"
                "Updates `foo` from 1.0.0 to 1.0.1\n"
                "Updates `bar` from 2.3.0 to 2.4.0\n"
                "Updates `baz` from 3.0.0 to 4.0.0\n"
            ),
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/group-update",
            files=["src/PPDS.Extension/package.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertEqual(c.update_type, "major")
        self.assertIn("most-conservative=major", c.reason)

    def test_grouped_with_auth_critical_patch_is_group_b(self):
        # All-patch group containing an auth-critical member must elevate to
        # Group B per MERGE-POLICY.md "Auth-Critical Packages" override.
        # Without the override this would incorrectly classify as Group A.
        pr = make_pr(
            number=863,
            title="Bump the nuget group across 1 directory with 3 updates",
            body=(
                "Bumps the nuget group with 3 updates:\n\n"
                "Updates `Foo` from 1.0.0 to 1.0.1\n"
                "Updates `Microsoft.Identity.Client` from 4.55.0 to 4.55.1\n"
                "Updates `Bar` from 2.3.4 to 2.3.5\n"
            ),
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/group-update",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertIn("auth-critical", c.reason)
        self.assertIn("Microsoft.Identity.Client", c.reason)

    def test_grouped_with_downgrade_member_is_group_c(self):
        # A grouped PR where one member is a downgrade (1.0.2 -> 1.0.1)
        # must classify as Group C — per-member group resolution treats the
        # downgrade as 'major' (per classify_update_type) which routes to C.
        pr = make_pr(
            number=865,
            title="Bump the npm_and_yarn group across 1 directory with 3 updates",
            body=(
                "Bumps the npm_and_yarn group with 3 updates:\n\n"
                "Updates `foo` from 1.0.0 to 1.0.1\n"
                "Updates `bar` from 2.3.4 to 2.3.5\n"
                "Updates `baz` from 1.0.2 to 1.0.1\n"
            ),
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/group-update",
            files=["src/PPDS.Extension/package.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertEqual(c.update_type, "major")
        self.assertIn("most-conservative=major", c.reason)

    def test_grouped_with_unknown_member_is_group_c(self):
        # A grouped PR with a member whose update_type can't be classified
        # (non-numeric versions) must default to Group C per "when unsure,
        # default to manual merge". Per-member resolver maps unknown -> C.
        pr = make_pr(
            number=866,
            title="Bump the npm_and_yarn group across 1 directory with 2 updates",
            body=(
                "Bumps the npm_and_yarn group with 2 updates:\n\n"
                "Updates `foo` from 1.0.0 to 1.0.1\n"
                "Updates `bar` from 0abc to 0xyz\n"
            ),
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/group-update",
            files=["src/PPDS.Extension/package.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        # The unknown-typed member is the trigger; its update_type is "unknown".
        self.assertEqual(c.update_type, "unknown")

    def test_grouped_with_auth_critical_minor_is_group_b(self):
        # Mix of patch + an auth-critical minor bump -> Group B.
        # (Even without the auth-critical override this is B because of the
        # minor; with the override the reason should still flag auth-critical.)
        pr = make_pr(
            number=864,
            title="Bump the nuget group across 1 directory with 2 updates",
            body=(
                "Bumps the nuget group with 2 updates:\n\n"
                "Updates `Foo` from 1.0.0 to 1.0.1\n"
                "Updates `Azure.Identity` from 1.12.0 to 1.13.0\n"
            ),
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/group-update",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertIn("auth-critical", c.reason)
        self.assertIn("Azure.Identity", c.reason)

    # Multi-package "Bump X and Y" titles (no version in title) — issue #1299.
    # parse_title yields no package; members are recovered from the body and run
    # through the same most-conservative-wins logic as grouped PRs.

    def test_multi_package_auth_critical_minor_is_group_b(self):
        # Real PR #1299: title bumps two auth-critical packages with no version;
        # the body enumerates both as minor bumps. Must classify Group B
        # (auth-critical), NOT default to Group C "could not classify".
        pr = make_pr(
            number=1299,
            title="deps: Bump Microsoft.Identity.Client and Microsoft.Identity.Client.Extensions.Msal",
            body=(
                "Bumps Microsoft.Identity.Client and Microsoft.Identity.Client.Extensions.Msal.\n\n"
                "Updated [Microsoft.Identity.Client](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet) from 4.84.2 to 4.85.2.\n"
                "Updated [Microsoft.Identity.Client.Extensions.Msal](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet) from 4.84.2 to 4.85.2.\n"
            ),
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/microsoft.identity.client-and-msal",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertEqual(c.update_type, "minor")
        self.assertIn("multi-package", c.reason)
        self.assertIn("auth-critical", c.reason)
        self.assertIn("Microsoft.Identity.Client", c.reason)

    def test_multi_package_with_major_member_is_group_c(self):
        # A multi-package title where one member is a major bump — most
        # conservative wins, so the whole PR is Group C.
        pr = make_pr(
            number=1300,
            title="Bump Foo and Bar",
            body=(
                "Bumps Foo and Bar.\n\n"
                "Updated [Foo](https://example.com/foo) from 1.0.0 to 1.0.1.\n"
                "Updated [Bar](https://example.com/bar) from 2.0.0 to 3.0.0.\n"
            ),
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/foo-and-bar",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertEqual(c.update_type, "major")
        self.assertIn("most-conservative=major", c.reason)

    def test_multi_package_all_patch_non_critical_is_group_a(self):
        # Two non-critical patch members via a multi-package title — Group A.
        pr = make_pr(
            number=1301,
            title="Bump Foo and Bar",
            body=(
                "Bumps Foo and Bar.\n\n"
                "Updated [Foo](https://example.com/foo) from 1.0.0 to 1.0.1.\n"
                "Updated [Bar](https://example.com/bar) from 2.3.4 to 2.3.5.\n"
            ),
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/foo-and-bar",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertEqual(c.update_type, "patch")
        self.assertIn("multi-package", c.reason)

    def test_multi_package_unparseable_body_is_group_c(self):
        # A title that parse_title can't resolve AND a body with no parseable
        # member lines must still fall through to the Group-C "could not
        # classify" default (guards the multi-package path from over-reaching).
        pr = make_pr(
            number=1302,
            title="Bump Foo and Bar",
            body="This body has no dependabot 'Updated pkg from A to B' lines.",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/foo-and-bar",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertIn("could not classify", c.reason)

    # Member-set safety guards — the grouped/multi-package path must apply the
    # same body/path checks as the single-package path (regression from #1311
    # review: _classify_member_set() returned before these guards ran).

    def test_grouped_breaking_marker_forces_group_c(self):
        # An all-patch group (would be Group A) whose body carries a BREAKING
        # CHANGE marker must be forced to Group C, exactly like a single PR.
        pr = make_pr(
            number=1310,
            title="Bump the npm_and_yarn group across 1 directory with 2 updates",
            body=(
                "Bumps the npm_and_yarn group with 2 updates:\n\n"
                "Updates `foo` from 1.0.0 to 1.0.1\n"
                "Updates `bar` from 2.3.4 to 2.3.5\n\n"
                "> BREAKING CHANGE: the foo API was removed.\n"
            ),
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/group-update",
            files=["src/PPDS.Extension/package.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertIn("BREAKING CHANGE", c.reason)

    def test_multi_package_breaking_marker_forces_group_c(self):
        # Same guard on the multi-package ("Bump X and Y") path.
        pr = make_pr(
            number=1311,
            title="Bump Foo and Bar",
            body=(
                "Bumps Foo and Bar.\n\n"
                "Updated [Foo](https://example.com/foo) from 1.0.0 to 1.0.1.\n"
                "Updated [Bar](https://example.com/bar) from 2.3.4 to 2.3.5.\n\n"
                "Includes a BREAKING CHANGE in Foo.\n"
            ),
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/foo-and-bar",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertIn("BREAKING CHANGE", c.reason)

    def test_member_set_auth_critical_path_escalates_to_group_b(self):
        # An all-patch, non-critical group (would be Group A) whose diff touches
        # an auth-critical path (src/PPDS.Auth/**) must escalate to at least B.
        pr = make_pr(
            number=1312,
            title="Bump the nuget group across 1 directory with 2 updates",
            body=(
                "Bumps the nuget group with 2 updates:\n\n"
                "Updates `Foo` from 1.0.0 to 1.0.1\n"
                "Updates `Bar` from 2.3.4 to 2.3.5\n"
            ),
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/group-update",
            files=["Directory.Packages.props", "src/PPDS.Auth/Credentials/Foo.cs"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertIn("auth-critical path", c.reason)

    # Group C — manual review

    def test_major_npm_is_group_c(self):
        pr = make_pr(
            number=850,
            title="Bump eslint from 8.57.0 to 9.0.0",
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/eslint-9.0.0",
            files=["src/PPDS.Extension/package.json"],
        )
        c = classify.classify_pr(pr)
        # Even though eslint is tooling, MAJOR is universal exclusion.
        self.assertEqual(c.group, "C")
        self.assertEqual(c.update_type, "major")
        self.assertIn("major", c.reason)

    def test_major_eslint_plugin_is_group_c(self):
        # The "eslint-plugin-" tooling prefix must not swallow majors — the
        # universal major exclusion outranks the tooling allowlist, exactly as
        # it does for exact-name tooling entries like eslint itself.
        pr = make_pr(
            number=1348,
            title="deps(extension): Bump eslint-plugin-import-x from 4.17.1 to 5.0.0 in /src/PPDS.Extension",
            labels=["dependencies"],
            head_ref="dependabot/npm_and_yarn/src/PPDS.Extension/eslint-plugin-import-x-5.0.0",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertEqual(c.update_type, "major")
        self.assertIn("major", c.reason)

    def test_major_nuget_is_group_c(self):
        pr = make_pr(
            number=851,
            title="Bump Terminal.Gui from 1.19.0 to 2.0.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Terminal.Gui-2.0.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")

    def test_major_auth_critical_is_group_c(self):
        # An auth-critical MAJOR bump must still be Group C (universal major
        # exclusion outranks the auth-critical Group-B override). Guards against
        # the patch/minor override broadening to swallow majors.
        pr = make_pr(
            number=1289,
            title="Bump Microsoft.Identity.Client from 4.56.0 to 5.0.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Microsoft.Identity.Client-5.0.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertEqual(c.update_type, "major")
        self.assertIn("major", c.reason)

    def test_major_github_actions_is_group_c(self):
        # actions/checkout v3 -> v4 is a major and should be Group C
        pr = make_pr(
            number=852,
            title="Bump actions/checkout from 3 to 4",
            labels=["github_actions", "dependencies"],
            head_ref="dependabot/github_actions/actions/checkout-4",
            files=[".github/workflows/ci.yml"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertEqual(c.update_type, "major")

    def test_breaking_in_body_is_group_c_even_if_minor(self):
        pr = make_pr(
            number=853,
            title="Bump Foo from 1.5.0 to 1.6.0",
            body="## Release notes\n\n### BREAKING CHANGE\nRemoved deprecated API.",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/Foo-1.6.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")
        self.assertIn("BREAKING", c.reason)

    def test_unparseable_title_defaults_to_c(self):
        pr = make_pr(
            number=854,
            title="Some random title",
            labels=["dependencies"],
            head_ref="dependabot/nuget/something",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "C")

    def test_v_prefix_github_actions_major_is_group_c(self):
        # actions/checkout v3 -> v4 must parse and route to Group C.
        pr = make_pr(
            number=855,
            title="Bump actions/checkout from v3 to v4",
            labels=["github_actions", "dependencies"],
            head_ref="dependabot/github_actions/actions/checkout-v4",
            files=[".github/workflows/ci.yml"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.package, "actions/checkout")
        self.assertEqual(c.from_version, "v3")
        self.assertEqual(c.to_version, "v4")
        self.assertEqual(c.update_type, "major")
        self.assertEqual(c.group, "C")

    def test_minor_downgrade_is_group_c(self):
        # 1.2.0 -> 1.1.0 was previously classified as 'minor' and could slip
        # through auto-merge. Now any downgrade is Group C.
        pr = make_pr(
            number=856,
            title="Bump SomePkg from 1.2.0 to 1.1.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/SomePkg-1.1.0",
            files=["Directory.Packages.props"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.update_type, "major")
        self.assertEqual(c.group, "C")

    def test_pnpm_lockfile_detected(self):
        # pnpm-lock.yaml-only diffs should be Group A (lockfile-only).
        pr = make_pr(
            number=857,
            title="Bump some-transitive-dep from 1.0.0 to 1.0.1",
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/some-transitive-dep-1.0.1",
            files=["pnpm-lock.yaml"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "A")
        self.assertIn("lockfile", c.reason.lower())

    def test_lockfile_lookalike_not_detected(self):
        # A file like custom-package-lock.json must NOT count as a lockfile.
        # (Was a false-positive risk under the old endswith-based check.)
        pr = make_pr(
            number=858,
            title="Bump SomePkg from 2.0.0 to 3.0.0",
            labels=["nuget", "dependencies"],
            head_ref="dependabot/nuget/SomePkg-3.0.0",
            files=["docs/custom-package-lock.json"],
        )
        # Major bump; lockfile-only short-circuit must NOT fire.
        c = classify.classify_pr(pr)
        self.assertEqual(c.update_type, "major")
        self.assertEqual(c.group, "C")


class TestAuthCriticalDocCodeDrift(unittest.TestCase):
    """Drift detection (issue #818).

    docs/MERGE-POLICY.md is the single source of truth for the auth-critical
    package list. This test parses the list from the doc (between
    AUTH_CRITICAL_PACKAGES:BEGIN / :END markers) and compares it to the
    AUTH_CRITICAL_PACKAGES set in scripts/dependabot/classify.py. They MUST
    agree — if you add or remove a package, update both places.
    """

    DOC_PATH = REPO_ROOT / "docs" / "MERGE-POLICY.md"
    BEGIN_MARKER = "<!-- AUTH_CRITICAL_PACKAGES:BEGIN -->"
    END_MARKER = "<!-- AUTH_CRITICAL_PACKAGES:END -->"

    def _parse_doc_list(self) -> set[str]:
        text = self.DOC_PATH.read_text(encoding="utf-8")
        try:
            block = text.split(self.BEGIN_MARKER, 1)[1].split(self.END_MARKER, 1)[0]
        except IndexError:
            self.fail(
                f"docs/MERGE-POLICY.md is missing the {self.BEGIN_MARKER} / "
                f"{self.END_MARKER} markers around the auth-critical package list."
            )
        # Lines look like: "- `Microsoft.Identity.Client`"
        item_re = re.compile(r"^\s*-\s*`([^`]+)`\s*$")
        out: set[str] = set()
        for line in block.splitlines():
            m = item_re.match(line)
            if m:
                out.add(m.group(1).lower())
        return out

    def test_doc_list_matches_code_list(self):
        doc_set = self._parse_doc_list()
        code_set = set(classify.AUTH_CRITICAL_PACKAGES)
        self.assertEqual(
            doc_set,
            code_set,
            (
                "Auth-critical package list drift between docs/MERGE-POLICY.md and "
                "scripts/dependabot/classify.py.\n"
                f"  Only in doc:  {sorted(doc_set - code_set)}\n"
                f"  Only in code: {sorted(code_set - doc_set)}\n"
                "Update both places to match (see issue #818)."
            ),
        )


class TestCLIRoundTrip(unittest.TestCase):
    def test_classification_to_dict_round_trip(self):
        pr = make_pr(
            number=900,
            title="Bump knip from 6.1.0 to 6.2.0",
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/knip-6.2.0",
            files=["src/PPDS.Extension/package.json"],
        )
        c = classify.classify_pr(pr)
        d = c.to_dict()
        self.assertEqual(d["pr_number"], 900)
        self.assertEqual(d["group"], "A")
        self.assertEqual(d["package"], "knip")
        self.assertEqual(d["from_version"], "6.1.0")
        self.assertEqual(d["to_version"], "6.2.0")


if __name__ == "__main__":
    unittest.main()
