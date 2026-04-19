"""Unit tests for scripts/dependabot/classify.py.

Run with: python -m pytest tests/scripts/dependabot/test_classify.py -v

These tests use sample dependabot PR JSON fixtures to verify each classifier
branch. They are pure functions — no network, no gh CLI, no git.
"""
from __future__ import annotations

import os
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

    def test_grouped_bump_defaults_to_b(self):
        pr = make_pr(
            number=844,
            title="Bump the npm_and_yarn group across 1 directory with 2 updates",
            labels=["npm", "dependencies"],
            head_ref="dependabot/npm_and_yarn/group-update",
            files=["src/PPDS.Extension/package.json", "src/PPDS.Extension/package-lock.json"],
        )
        c = classify.classify_pr(pr)
        self.assertEqual(c.group, "B")
        self.assertIn("grouped", c.reason)

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
