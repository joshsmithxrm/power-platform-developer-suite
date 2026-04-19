#!/usr/bin/env python3
"""Classify dependabot PRs into Group A / B / C per docs/MERGE-POLICY.md.

The /dependabot-triage skill calls this module either as a CLI (one PR's JSON
on stdin, single classification on stdout) or imports `classify_pr()` directly.

Group A — auto-merge eligible (green CI is sufficient evidence to ship):
  - Patch bumps on non-critical paths
  - Tooling/test bumps regardless of patch/minor (eslint, knip, vitest, esbuild,
    @types/*, @playwright/test, Microsoft.NET.Test.Sdk)
  - github-actions group bumps (patch/minor)
  - Lockfile-only diffs

Group B — verify-then-merge (run package-specific tests locally first):
  - Minor bumps on user-visible runtime libraries (Terminal.Gui, etc.)
  - Bumps to auth-critical packages (Microsoft.Identity.Client, Azure.Identity,
    Microsoft.PowerPlatform.Dataverse.Client) at minor
  - Anything bumping security-flagged packages
  - Bumps that touch src/PPDS.Auth/** at any version

Group C — manual review required (do NOT auto-merge):
  - ANY major-version bump (universal exclusion per v1-prelaunch retro)
  - Bumps with "Breaking" / "BREAKING CHANGE" / removed APIs in the changelog
  - Bumps to CI/CD pipelines via actions/* major
  - Anything in MERGE-POLICY's "When NOT to use auto-merge" list that isn't Group B

The ground truth for the policy is docs/MERGE-POLICY.md. If this file and that
file disagree, the doc wins and this file needs a patch.
"""
from __future__ import annotations

import json
import re
import sys
from dataclasses import dataclass
from typing import Iterable, Optional


# Packages whose bumps go through the verify-then-merge gate at any non-major version.
# Sourced from docs/MERGE-POLICY.md "When NOT to use auto-merge" plus the v1-prelaunch
# triage session's discovered list.
AUTH_CRITICAL_PACKAGES = frozenset({
    "microsoft.identity.client",
    "microsoft.identity.client.extensions.msal",
    "azure.identity",
    "azure.core",
    "microsoft.powerplatform.dataverse.client",
    "system.security.cryptography.xml",
    "system.security.cryptography.pkcs",
})

# Packages whose patch/minor bumps are tooling (Group A regardless of update type).
# These touch dev-time tooling, never runtime.
TOOLING_PACKAGES = frozenset({
    "eslint",
    "knip",
    "vitest",
    "esbuild",
    "@playwright/test",
    "playwright",
    "prettier",
    "typescript",
    "typescript-eslint",
    "@typescript-eslint/parser",
    "@typescript-eslint/eslint-plugin",
    "microsoft.net.test.sdk",
    "xunit",
    "xunit.runner.visualstudio",
    "coverlet.collector",
    "moq",
    "fluentassertions",
    "fakeitem",
    "fakexrmeasy",
    "fakexrmeasy.v9",
})

# Tooling package name *prefixes* (e.g., @types/* are all tooling)
TOOLING_PREFIXES = (
    "@types/",
    "fakexrmeasy",
)

# Paths that elevate any bump to at least Group B.
AUTH_CRITICAL_PATH_PREFIXES = (
    "src/PPDS.Auth/",
    "src/PPDS.Plugins/",  # strong-name signing surface
)


@dataclass(frozen=True)
class Classification:
    """Result of classifying a single dependabot PR."""

    pr_number: int
    group: str  # "A" | "B" | "C"
    reason: str
    ecosystem: str  # "npm" | "nuget" | "github-actions" | "unknown"
    update_type: str  # "patch" | "minor" | "major" | "unknown"
    package: Optional[str]
    from_version: Optional[str]
    to_version: Optional[str]

    def to_dict(self) -> dict:
        return {
            "pr_number": self.pr_number,
            "group": self.group,
            "reason": self.reason,
            "ecosystem": self.ecosystem,
            "update_type": self.update_type,
            "package": self.package,
            "from_version": self.from_version,
            "to_version": self.to_version,
        }


# Regex for "Bump <pkg> from <a> to <b>" (case-insensitive on Bump).
# Captures package and both versions. Handles npm-scoped (@org/pkg), NuGet
# (Foo.Bar.Baz), and GitHub Actions style "v"-prefixed versions (e.g. v3 -> v4).
_BUMP_TITLE_RE = re.compile(
    r"^[Bb]ump\s+(?P<pkg>[@A-Za-z0-9._/-]+)\s+from\s+(?P<from>v?[0-9][^\s]*)\s+to\s+(?P<to>v?[0-9][^\s]*)",
)

# Regex for grouped bumps: "Bump the <group> group ..."
_GROUP_TITLE_RE = re.compile(r"^[Bb]ump\s+the\s+(?P<group>[A-Za-z0-9_.-]+)\s+group", re.IGNORECASE)

# Regex for individual member updates inside a grouped dependabot PR body.
# Dependabot writes each member as "Updates `<pkg>` from <from> to <to>" (markdown
# code-fenced package name) or "Updates <pkg> from <from> to <to>" (plain).
_GROUP_MEMBER_RE = re.compile(
    r"Updates\s+`?(?P<pkg>[@A-Za-z0-9._/-]+)`?\s+from\s+(?P<from>v?[0-9][^\s]*)\s+to\s+(?P<to>v?[0-9][^\s]*)",
)


def parse_group_members(body: str) -> list[tuple[str, str, str]]:
    """Extract individual (package, from_version, to_version) tuples from a grouped PR body.

    Dependabot grouped PR bodies enumerate member updates with lines like
    ``Updates `foo` from 1.0.0 to 1.0.1``. Returns one tuple per member
    bump found; deduplicated on (package, from, to). Empty list if none parsed.
    """
    if not body:
        return []
    seen: set[tuple[str, str, str]] = set()
    out: list[tuple[str, str, str]] = []
    for m in _GROUP_MEMBER_RE.finditer(body):
        key = (m.group("pkg"), m.group("from"), m.group("to"))
        if key in seen:
            continue
        seen.add(key)
        out.append(key)
    return out


def most_conservative_group(
    member_types: Iterable[str],
    has_auth_critical: bool = False,
) -> Optional[str]:
    """Return the most-conservative classification group ("A" / "B" / "C") for a list of member update types.

    Per docs/MERGE-POLICY.md "Grouped Dependabot PRs":
      - any 'major' -> C
      - any auth-critical member at non-major -> B (regardless of patch/minor mix)
      - any 'minor' (no majors, no auth-critical) -> B
      - only 'patch' (no auth-critical) -> A
      - any 'unknown' (and no major/minor/auth-critical) -> None (caller must decide)
      - empty input -> None

    The ``has_auth_critical`` flag should be True when any group member's package
    name returns True from ``is_auth_critical_package``. This implements the
    auth-critical override: a group of all-patch bumps is normally Group A, but
    if any member is auth-critical (e.g., Microsoft.Identity.Client patch), the
    group is elevated to Group B per MERGE-POLICY.md "Auth-Critical Packages".
    """
    types = list(member_types)
    if not types:
        return None
    if "major" in types:
        return "C"
    if has_auth_critical:
        # Any non-major auth-critical bump elevates the whole group to B.
        return "B"
    if "minor" in types:
        return "B"
    if all(t == "patch" for t in types):
        return "A"
    # Mix of patch and unknown — caller decides
    return None


def parse_title(title: str) -> tuple[Optional[str], Optional[str], Optional[str]]:
    """Parse a dependabot PR title.

    Returns (package, from_version, to_version). Any may be None if parsing failed.
    For grouped bumps ("Bump the X group ..."), package is "group:<name>" and
    versions are None (the caller must inspect the body / files for individual bumps).
    """
    m = _BUMP_TITLE_RE.match(title.strip())
    if m:
        return m.group("pkg"), m.group("from"), m.group("to")
    g = _GROUP_TITLE_RE.match(title.strip())
    if g:
        return f"group:{g.group('group')}", None, None
    return None, None, None


def classify_update_type(from_v: Optional[str], to_v: Optional[str]) -> str:
    """Compare two version strings and return 'patch' / 'minor' / 'major' / 'unknown'.

    Strips a leading 'v' if present. Handles SemVer-ish strings (X.Y.Z, X.Y, X)
    and pre-release suffixes (1.0.0-beta.1) by comparing only the numeric prefix.
    """
    if not from_v or not to_v:
        return "unknown"

    def parts(v: str) -> list[int]:
        v = v.lstrip("v").split("-")[0].split("+")[0]  # strip prerelease/build
        out = []
        for p in v.split("."):
            try:
                out.append(int(p))
            except ValueError:
                # Non-numeric segment — treat as a hard stop (not comparable as semver)
                break
        return out

    fp = parts(from_v)
    tp = parts(to_v)
    if not fp or not tp:
        return "unknown"

    # Pad to length 3
    while len(fp) < 3:
        fp.append(0)
    while len(tp) < 3:
        tp.append(0)

    # Any downgrade (lexicographic on the version tuple) is treated as
    # major-equivalent — requires manual review per docs/MERGE-POLICY.md.
    # Catches not only major downgrades (2.x -> 1.x) but also minor (1.2 -> 1.1)
    # and patch (1.0.2 -> 1.0.1) downgrades that would otherwise be classified
    # as 'minor'/'patch' and slip through auto-merge gates.
    if tp < fp:
        return "major"
    if tp[0] > fp[0]:
        return "major"
    if tp[1] > fp[1]:
        return "minor"
    return "patch"


def detect_ecosystem(labels: Iterable[str], head_ref: str) -> str:
    """Determine ecosystem from labels first, then headRefName fallback."""
    label_set = {lbl.lower() for lbl in labels}
    if "nuget" in label_set:
        return "nuget"
    if "npm" in label_set or "javascript" in label_set or "npm_and_yarn" in label_set:
        return "npm"
    if "github_actions" in label_set or "github-actions" in label_set:
        return "github-actions"

    # Fallback — dependabot/<ecosystem>/...
    parts = head_ref.split("/")
    if len(parts) >= 2 and parts[0] == "dependabot":
        eco = parts[1].lower()
        if eco == "nuget":
            return "nuget"
        if eco in ("npm_and_yarn", "npm"):
            return "npm"
        if eco == "github_actions":
            return "github-actions"
    return "unknown"


def is_tooling_package(pkg: Optional[str]) -> bool:
    if not pkg:
        return False
    p = pkg.lower()
    if p in TOOLING_PACKAGES:
        return True
    return any(p.startswith(prefix) for prefix in TOOLING_PREFIXES)


def is_auth_critical_package(pkg: Optional[str]) -> bool:
    if not pkg:
        return False
    return pkg.lower() in AUTH_CRITICAL_PACKAGES


def touches_auth_critical_path(files: Iterable[str]) -> bool:
    for f in files:
        for prefix in AUTH_CRITICAL_PATH_PREFIXES:
            if f.startswith(prefix):
                return True
    return False


_LOCKFILE_BASENAMES = frozenset({
    "package-lock.json",
    "packages.lock.json",
    "yarn.lock",
    "pnpm-lock.yaml",
})


def is_lockfile_only(files: Iterable[str]) -> bool:
    """True if the diff only touches well-known lock files.

    Uses exact basename matching to avoid false positives like
    ``custom-package-lock.json`` or ``docs/package-lock.json.md`` slipping
    through ``str.endswith`` checks.
    """
    files = list(files)
    if not files:
        return False
    return all(f.split("/")[-1] in _LOCKFILE_BASENAMES for f in files)


def changelog_signals_breaking(body: str) -> bool:
    """Detect breaking-change markers in the PR body."""
    if not body:
        return False
    needles = ("BREAKING CHANGE", "Breaking change", "Breaking Changes", "[breaking]")
    return any(n in body for n in needles)


def classify_pr(pr: dict) -> Classification:
    """Classify a single dependabot PR.

    `pr` is a dict shaped like the output of `gh pr view --json
    number,title,labels,headRefName,files,body`. The classifier is defensive:
    missing fields default to safe (Group C with reason "ambiguous").
    """
    number = int(pr.get("number", 0))
    title = pr.get("title", "")
    body = pr.get("body", "") or ""
    labels = [lbl.get("name", "") for lbl in pr.get("labels", [])]
    head_ref = pr.get("headRefName", "")
    files = [f.get("path", "") for f in pr.get("files", [])]

    pkg, from_v, to_v = parse_title(title)
    ecosystem = detect_ecosystem(labels, head_ref)
    update_type = classify_update_type(from_v, to_v)

    # Grouped bumps — apply most-conservative-wins per docs/MERGE-POLICY.md
    # "Grouped Dependabot PRs": any major -> C, any minor (no majors) -> B,
    # only patch -> A. Members are parsed from the PR body.
    if pkg and pkg.startswith("group:"):
        members = parse_group_members(body)
        if not members:
            # No member info parseable — fall back to Group B so the operator
            # inspects manually rather than auto-merging blind.
            return Classification(
                pr_number=number,
                group="B",
                reason=f"grouped bump '{pkg}' — could not parse members from body, defaulting to verify-then-merge",
                ecosystem=ecosystem,
                update_type="unknown",
                package=pkg,
                from_version=None,
                to_version=None,
            )

        member_types = [classify_update_type(fv, tv) for (_p, fv, tv) in members]
        # Auth-critical override: any non-major auth-critical member elevates
        # the whole group to Group B per MERGE-POLICY.md.
        auth_critical_member = next(
            (m for m in members if is_auth_critical_package(m[0])),
            None,
        )
        has_auth_critical = auth_critical_member is not None and "major" not in member_types
        chosen = most_conservative_group(member_types, has_auth_critical=has_auth_critical)
        if chosen is None:
            # Mixed patch/unknown — be conservative.
            return Classification(
                pr_number=number,
                group="B",
                reason=f"grouped bump '{pkg}' — {len(members)} members with unparseable versions, defaulting to verify-then-merge",
                ecosystem=ecosystem,
                update_type="unknown",
                package=pkg,
                from_version=None,
                to_version=None,
            )

        # Determine which member triggered the chosen group, for the reason string.
        # Special case: Group B chosen via auth-critical override on an all-patch
        # group has no "minor" trigger — pick the auth-critical member as the
        # trigger and label its actual update type.
        type_to_label = {"A": "patch", "B": "minor", "C": "major"}
        if chosen == "B" and has_auth_critical and "minor" not in member_types:
            trigger = auth_critical_member
            trigger_type = classify_update_type(trigger[1], trigger[2])
        else:
            trigger_type = type_to_label[chosen]
            trigger = next(
                (m for m, t in zip(members, member_types) if t == trigger_type),
                members[0],
            )
        reason = (
            f"grouped bump '{pkg}' — {len(members)} members; most-conservative={trigger_type} "
            f"({trigger[0]} {trigger[1]} -> {trigger[2]})"
        )
        # When an auth-critical member contributed to the Group B classification,
        # name it explicitly in the reason so the operator knows why the override
        # applied (per MERGE-POLICY.md "Auth-Critical Packages").
        if chosen == "B" and has_auth_critical:
            ac_type = classify_update_type(auth_critical_member[1], auth_critical_member[2])
            reason += (
                f"; auth-critical override "
                f"({auth_critical_member[0]} {auth_critical_member[1]} -> {auth_critical_member[2]}, {ac_type})"
            )
        return Classification(
            pr_number=number,
            group=chosen,
            reason=reason,
            ecosystem=ecosystem,
            update_type=trigger_type,
            package=pkg,
            from_version=None,
            to_version=None,
        )

    # Hard exclusion — major bumps are always Group C, no exceptions (v1-prelaunch retro).
    if update_type == "major":
        return Classification(
            pr_number=number,
            group="C",
            reason=f"major-version bump ({from_v} -> {to_v}) — universal manual-review exclusion",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # Breaking-change markers in body — Group C even if version looks minor.
    if changelog_signals_breaking(body):
        return Classification(
            pr_number=number,
            group="C",
            reason="PR body signals BREAKING CHANGE — manual review",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # Touches auth-critical path — at least Group B regardless of version.
    if touches_auth_critical_path(files):
        return Classification(
            pr_number=number,
            group="B",
            reason=f"diff touches auth-critical path (e.g., src/PPDS.Auth) — verify-then-merge",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # Auth-critical package at minor — Group B.
    if is_auth_critical_package(pkg) and update_type == "minor":
        return Classification(
            pr_number=number,
            group="B",
            reason=f"auth-critical package {pkg} minor bump ({from_v} -> {to_v}) — verify-then-merge",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # Tooling package at any non-major — Group A.
    if is_tooling_package(pkg):
        return Classification(
            pr_number=number,
            group="A",
            reason=f"tooling/test package {pkg} {update_type} — auto-merge eligible",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # Lockfile-only — Group A.
    if is_lockfile_only(files):
        return Classification(
            pr_number=number,
            group="A",
            reason="lockfile-only diff — auto-merge eligible",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # github-actions patch/minor — Group A (unless it's a major, caught above).
    if ecosystem == "github-actions" and update_type in ("patch", "minor"):
        return Classification(
            pr_number=number,
            group="A",
            reason=f"github-actions {update_type} bump — auto-merge eligible",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # Patch on non-critical NuGet/npm — Group A.
    if update_type == "patch":
        return Classification(
            pr_number=number,
            group="A",
            reason=f"patch bump on non-critical path ({pkg} {from_v} -> {to_v}) — auto-merge eligible",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # Minor bump on a non-tooling, non-auth-critical package — Group B.
    # User-visible runtime change deserves a local test pass.
    if update_type == "minor":
        return Classification(
            pr_number=number,
            group="B",
            reason=f"minor bump ({pkg} {from_v} -> {to_v}) — verify-then-merge for runtime change",
            ecosystem=ecosystem,
            update_type=update_type,
            package=pkg,
            from_version=from_v,
            to_version=to_v,
        )

    # Couldn't parse version — safe default is Group C with reason.
    return Classification(
        pr_number=number,
        group="C",
        reason=f"could not classify (title='{title}', ecosystem={ecosystem}, update_type={update_type}) — manual review",
        ecosystem=ecosystem,
        update_type=update_type,
        package=pkg,
        from_version=from_v,
        to_version=to_v,
    )


def _cli() -> int:
    """CLI entry point. Reads a JSON array of PRs on stdin, writes classifications on stdout."""
    try:
        data = json.load(sys.stdin)
    except json.JSONDecodeError as e:
        print(f"error: invalid JSON on stdin: {e}", file=sys.stderr)
        return 1

    if isinstance(data, dict):
        data = [data]

    results = [classify_pr(pr).to_dict() for pr in data]
    json.dump(results, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(_cli())
