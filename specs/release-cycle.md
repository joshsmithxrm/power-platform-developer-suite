# Release Cycle

**Status:** Draft
**Last Updated:** 2026-09-12
**Code:** [.claude/skills/release/](../.claude/skills/release/), [.github/workflows/](../.github/workflows/), [scripts/ci/](../scripts/ci/), [tests/ci/](../tests/ci/), [tests/test_release_skill_content.py](../tests/test_release_skill_content.py), [tests/ci/test_extension_publish_workflow.py](../tests/ci/test_extension_publish_workflow.py)
**Surfaces:** All

---

## Overview

Policy layer governing *when* PPDS releases happen, *how* work is grouped into milestones, and *what* automation surfaces release readiness. The existing `/release` skill owns the ceremony (CHANGELOGs, version bumps, tag pushes, CI monitoring); this spec owns the decisions above it — triggering, targeting, branching, and enforcement.

### Goals

- **Predictable release cadence**: patches ship fast, minors ship when ready, nothing drifts silently
- **Automated detection**: the system tells the maintainer when a release is warranted — not the other way around
- **Manual ceremony**: irreversible actions (tag push, NuGet/Marketplace publish) remain human-initiated via `/release`
- **Explained scope**: direct changes, downstream deliverables, and MinVer tag prerequisites are reported separately

### Non-Goals

- Replacing the `/release` skill ceremony — this spec extends it, not replaces it
- Auto-publishing to NuGet or Marketplace on merge — too risky for a multi-surface product
- Release trains or calendar-driven minor releases — wrong for a solo maintainer
- Cross-repo docs generation automation — covered by `specs/docs-generation.md`

---

## Architecture

```
PR merges to main
       │
       ├── label: release:patch ──▶ post-merge-release-check.yml
       │                                  │
       │                                  ▼
       │                           Opens "Patch release needed"
       │                           issue with package + diff
       │
       ├── milestone 100% closed ──▶ milestone-release-check.yml
       │                                  │
       │                                  ▼
       │                           Opens "Milestone vX.Y.0 ready"
       │                           issue with summary
       │
       └── (accumulates) ──────────▶ release-cadence-check.yml (weekly cron)
                                          │
                                          ▼
                                   Opens "Release check-in" issue
                                   if >8 weeks + unreleased commits

All three paths lead to:
       │
       ▼
  Maintainer runs /release (manual)
       │
       ▼
  Tag push → CI publish workflows
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `post-merge-release-check.yml` | Detects `release:patch` label on merged PRs, opens patch release issue |
| `milestone-release-check.yml` | Detects milestone completion, opens minor release readiness issue |
| `release-cadence-check.yml` | Weekly cron, checks days since last release tag, opens check-in issue |
| `/release` skill (existing) | Release ceremony — CHANGELOGs, version bumps, tag push, CI monitoring |
| `verify_public_release.py` | Read-only post-publish verification through nuget.org, GitHub Releases, and the VS Code Marketplace |
| GitHub Milestones | Group PRs into minor release targets (v1.1.0, v1.2.0, ...) |

### Dependencies

- Extends: [/release skill](../.claude/skills/release/SKILL.md) (ceremony)
- Complements: [MERGE-POLICY.md](../docs/MERGE-POLICY.md) (how PRs merge)

---

## Specification

### Release Types

| Type | Version | Trigger | Scope | Example |
|------|---------|---------|-------|---------|
| Patch | `X.Y.Z` (Z > 0) | Bug fix or security fix merged with `release:patch` label | Direct product changes, downstream deliverables, and required same-commit MinVer dependency tags | `Dataverse-v1.0.1`, `Query-v1.0.1` |
| Minor | `X.Y.0` (Y > 0) | GitHub Milestone reaches 100% closed | All packages — coordinated release via `/release` | `Auth-v1.1.0`, `Cli-v1.1.0`, ... |
| Major | `X.0.0` (X > 1) | Breaking change (API, strong-name rotation, etc.) | All packages — coordinated release via `/release` | Future |

### Release Triggering Model

Releases are **event-driven with a cadence floor**:

1. **Patch releases** ship when ready. Any merged PR labeled `release:patch` triggers automated detection. The maintainer decides whether to release immediately or batch with other pending fixes.

2. **Minor releases** ship when complete. GitHub Milestones define scope. When all issues/PRs in a milestone are closed, automated detection surfaces readiness. The maintainer runs `/release`.

3. **Cadence floor**: if no release (patch or minor) has shipped in 8 weeks and main has unreleased commits, a scheduled workflow opens a check-in issue. This prevents drift where accumulated changes never ship because nothing feels "ready enough."

### Milestone Targeting Strategy

**GitHub Milestones** are the targeting mechanism:

- Each planned minor release has a milestone: `v1.1.0`, `v1.2.0`, etc.
- PRs are assigned to milestones when merged (or when the scope is known).
- Patch releases do not need milestones — they are reactive and small.
- A milestone can be deferred by moving its open issues to the next milestone and closing it.

**Version progression:**

- `1.0.0` → bug fix → `1.0.1` (patch, per-package)
- `1.0.x` → feature batch → `1.1.0` (minor, all packages)
- The Plugins package follows its own lineage (currently `3.0.0`) but its minor/patch increments follow the same policy.
- Extension follows odd/even minor convention (odd = pre-release channel, even = stable).

### Branching and Merge Policy

**Trunk-based development with optional stabilization branches:**

1. **Default path** (used for all patch releases and most minor releases):
   - Feature/fix branches → squash-merge to main → tag from main → CI publishes
   - No release branch needed. This is the current model and it works.

2. **Stabilization branch** (rare, manual procedure — documented in `/release` skill per AC-07):
   - Create `release/X.Y` from main at the point where the milestone is feature-complete
   - Only cherry-pick bug fixes onto the stabilization branch
   - Tag from the stabilization branch, not main
   - Merge the branch back to main after release (or delete if all fixes were already on main)
   - **When to use**: only if active development for X.(Y+1) has started on main before X.Y is verified and shipped. For a solo maintainer, this should be the exception.

3. **Patch releases from main**:
   - Bug fix merges to main as a normal PR
   - Tag reviewed release targets and MinVer prerequisites from the merge commit on main
   - No branch or full release PR is needed for a focused patch
   - Multi-package patches (rare) follow the standard `/release` ceremony

### Patch Release Procedure

For focused patches (the common case):

1. Fix merges to main via normal PR process
2. Maintainer runs the read-only scope advisory and reviews its three categories:
   - Direct product changes
   - Downstream deliverables that consume those changes
   - Same-commit stable MinVer tag prerequisites
3. Maintainer runs abbreviated `/release` for the reviewed scope:
   - Update each release target's CHANGELOG
   - Push each reviewed target/prerequisite tag individually
   - Monitor every triggered publish workflow
   - Verify publish
4. No full release PR needed — the fix and focused CHANGELOG PRs are the audit trail

For multi-package patches or patches that touch the Extension:

1. Follow the full `/release` ceremony (release PR, all CHANGELOGs, coordinated tags)

### Severity-Based Patch Trigger

Human policy — the `release:patch` label is applied by the maintainer based on judgment. It may be present when the PR merges or added to an already-merged PR. The criteria below are guidelines, not automated enforcement.

A merged PR warrants the `release:patch` label when any of these apply:

- User-facing bug that causes wrong behavior, data loss, or crash
- Security vulnerability (any severity)
- Regression from a recent release

A merged PR does NOT warrant `release:patch`:

- Cosmetic or UX polish issues
- Performance improvements (unless severe degradation)
- Internal refactoring
- Documentation-only changes
- Test-only changes

### Primary Flows

**Flow 1 — Patch release (label-triggered):**

1. **PR merges to main** with `release:patch`, or the label is added to the merged PR later
2. **`post-merge-release-check.yml` fires**: uses the release model to analyze the exact merge diff and opens an advisory issue only when product impact exists
3. **Issue body includes**: a stable per-PR marker, explained direct changes, internal build changes, downstream deliverables, delivery/MinVer tag prerequisites, strict-SemVer latest tags, ignored non-product changes, diagnostics, and a link to the public release procedure
4. **Maintainer reviews issue**, runs `/release` for the affected package(s)
5. **Maintainer closes issue** after publish verification

Patch records are permanent audit records. Reruns and label toggles search both
open and closed `release:patch` issues for the per-PR marker. Only a labeled,
workflow-owned record can deduplicate a run; an unlabeled issue containing a
copied marker has no authority. An existing labeled record is never duplicated
or reopened; a different merged PR receives a different marker and record.

**Flow 2 — Minor release (milestone-driven):**

1. **PRs merge to main** with milestone `vX.Y.0` assigned
2. **Last issue/PR in milestone closes** → milestone reaches 100%
3. **`milestone-release-check.yml` fires**: opens a GitHub issue titled "Milestone vX.Y.0 complete — ready for release"
4. **Issue body includes**: milestone summary, PR list, any deferred items
5. **Maintainer reviews**, runs full `/release` ceremony
6. **Maintainer closes milestone and issue** after publish verification

**Flow 3 — Cadence floor (scheduled):**

1. **Weekly cron** (`release-cadence-check.yml`) runs on Monday
2. **Checks**: last release tag date vs. today, commit count since last tag
3. **If >8 weeks and >0 unreleased commits**: opens issue titled "Release check-in: {N} commits unreleased, {W} weeks since last release"
4. **Maintainer triages**: release now, defer with reason, or close as not-needed

### Tag Convention

PPDS uses **two layers of git tags** with distinct purposes:

| Tag type | Pattern | Purpose | Trigger |
|----------|---------|---------|---------|
| Per-package | `{Package}-v{version}` | Source of truth for package versions (MinVer); triggers publishing workflows | `publish-nuget.yml`, `release-cli.yml`, `extension-publish.yml` |
| Unified | `v{version}` | Trigger for docs generation; marks the coordinated release point | `docs-release.yml` |

**Per-package tags** are pushed for reviewed release targets, any same-commit
delivery prerequisites, and any stable same-commit MinVer prerequisites.
An Extension tag requires a CLI tag on the same commit because the Extension
publisher resolves its bundled CLI from that exact tag. ProjectReference dependencies are discovered
from MSBuild XML; they are not maintained as a hard-coded closure. These tags
drive MinVer version resolution and trigger the appropriate CI publishing
workflows.

**Unified tags** are pushed only for coordinated releases (minor/stable). They trigger `docs-release.yml` which regenerates reference documentation and opens a paired PR in ppds-docs. Patches do not push unified tags because docs don't regenerate for single-package fixes.

**Examples:**

```
# Minor release — all packages + unified tag:
Auth-v1.1.0  Cli-v1.1.0  Dataverse-v1.1.0  ...  v1.1.0

# Stable Query patch — Dataverse prerequisite + Query target, no unified tag:
Dataverse-v1.0.1  Query-v1.0.1

# Prerelease — all packages, optional unified tag:
Auth-v1.1.0-beta.3  Cli-v1.1.0-beta.3  ...  (optionally: v1.1.0-beta.3)
```

### Constraints

- Tag push is irreversible — never auto-tag or auto-publish
- Patch scope must explain direct changes, downstream deliverables, delivery prerequisites, and MinVer prerequisite-only tags separately; a prerequisite tag is not misreported as a product change
- Release-scope automation is advisory and must never create/push tags, publish packages, or dispatch release workflows
- Workflow-owned release labels come from one checked-in manifest and are created or reconciled idempotently before use
- Untrusted event text is rendered by tested Python helpers and issue bodies are passed to GitHub through files, never interpolated into shell commands
- Merged fork PRs use a least-privilege `pull_request_target` path: strict merged/base/action/label gates apply, executable repository automation comes only from `github.workflow_sha`, checkout credentials are not persisted, and the historical merge checkout is data-only
- Latest tag selection must use strict SemVer 2.0 precedence with ASCII digits only, never git refname sorting
- Extension publish auto-dispatches on `Extension-v*` tag push (channel inferred from odd/even minor convention); manual dispatch remains available for override
- All release types must produce CHANGELOG entries before tagging
- Stable releases (`vX.Y.0`) require a completed `/security-review` artifact before tagging — enforced in the `/release` skill's pre-merge verification step
- Publish workflows poll public availability every two minutes, with a bounded 30-attempt limit, then validate installability, versions, target coverage, and checksums as applicable
- A public verification failure stops and escalates; automation never unpublishes, deletes, deprecates, replaces, or rolls back published artifacts
- Downloaded public executables run only in fresh follow-on jobs with read-only repository permissions, non-persisted checkout credentials, and no publishing secrets

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `post-merge-release-check.yml` opens a GitHub issue when a PR with `release:patch` label merges to main and the advisory finds product impact | `tests/ci/test_post_merge_release_check.py::TestOpensIssueOnPatchLabel` | ✅ |
| AC-02 | The patch release issue body explains direct product changes, downstream deliverables, and MinVer prerequisites | `tests/ci/test_post_merge_release_check.py::TestUnknownPackageWarning::test_workflow_uses_explained_release_model` | ✅ |
| AC-03 | `milestone-release-check.yml` opens a GitHub issue when a milestone reaches 100% closed with merged PRs | `tests/ci/test_milestone_release_check.py::test_opens_issue_on_milestone_complete` | ✅ |
| AC-04 | `release-cadence-check.yml` opens a check-in issue if >8 weeks since last release tag and >0 unreleased commits on main | `tests/ci/test_release_cadence_check.py::test_opens_issue_when_overdue` | ✅ |
| AC-05 | `release-cadence-check.yml` does NOT open an issue if a release was cut within the last 8 weeks | `tests/ci/test_release_cadence_check.py::test_no_issue_when_recent_release` | ✅ |
| AC-06 | `/release` skill contains a "Patch Release Procedure" section documenting the single-package abbreviated flow | `tests/test_release_skill_content.py::test_patch_procedure_documented` | ✅ |
| AC-07 | `/release` skill contains a "Stabilization Branch" section documenting when to create one and how to merge back | `tests/test_release_skill_content.py::test_stabilization_branch_documented` | ✅ |
| AC-08 | `release-cadence-check.yml` does NOT open a duplicate issue if one is already open | `tests/ci/test_release_cadence_check.py::test_no_duplicate_issue` | ✅ |
| AC-09 | `milestone-release-check.yml` does NOT open a release issue when a milestone is closed with 0 merged PRs | `tests/ci/test_milestone_release_check.py::test_no_issue_on_empty_milestone` | ✅ |
| AC-10 | `post-merge-release-check.yml` does not open a release issue when the explained advisory finds only deterministic non-product changes | `tests/ci/test_post_merge_release_check.py::TestUnknownPackageWarning::test_workflow_skips_issue_when_model_finds_no_product_impact` | ✅ |
| AC-11 | `release-cadence-check.yml` does NOT open an issue if >8 weeks since last release but 0 unreleased commits on main | `tests/ci/test_release_cadence_check.py::test_no_issue_when_no_unreleased_commits` | ✅ |
| AC-12 | The release model identifies and explains multiple direct/downstream surfaces when a patch spans the product graph | `tests/ci/test_release_model.py::TestImpactAnalysis::test_pr_1402_fixture_yields_seven_surfaces_excluding_plugins` | ✅ |
| AC-13 | `/release` skill enforces security review gate for stable releases — `docs/qa/security-review-*.md` must exist before tagging `vX.Y.0`; patches and prereleases are exempt | `tests/test_release_skill_content.py::test_security_review_gate_documented` | ✅ |
| AC-14 | `extension-publish.yml` auto-dispatches on `Extension-v*` tag push with channel inferred from odd/even minor convention | `tests/ci/test_extension_publish_workflow.py::test_tag_push_trigger` | ✅ |
| AC-15 | `docs-release.yml` uses `actions/create-github-app-token@v2` with documented manual setup steps for GitHub App provisioning | Manual verification — secrets require repo admin | ✅ |
| AC-16 | Unified `v*` tag convention documented alongside per-package tags in `/release` skill and `specs/release-cycle.md` | `tests/test_release_skill_content.py::test_unified_tag_convention_documented` | ✅ |
| AC-17 | NuGet publication is polled at a bounded two-minute cadence, restored/installed from a clean nuget.org-only configuration, and public CLI/MCP tool releases execute with the exact tagged version | `test_polling_retries_at_two_minute_intervals_then_succeeds`, `test_nuget_library_restore_uses_only_clean_public_feed`, `test_public_cli_is_installed_to_temp_and_version_checked`, `test_public_mcp_launcher_exits_zero_and_reports_exact_version` | ✅ |
| AC-18 | Public CLI GitHub Releases contain all five binaries and a complete checksum manifest; downloaded checksums and CLI version must match | `test_checksum_parser_requires_exact_binary_coverage`, `test_github_release_checksum_mismatch_fails_before_execution` | ✅ |
| AC-19 | After the Marketplace publish matrix completes, all four public target VSIXs match the Extension version, target RID, and bundled CLI release | `test_marketplace_downloads_and_validates_every_target`, `test_marketplace_version_and_target_mismatches_fail`, `test_marketplace_verification_waits_for_full_publish_matrix` | ✅ |
| AC-20 | Verification failures stop and escalate without any automated unpublish, delete, deprecate, replacement, or rollback action; downloaded executables run only in read-only follow-on jobs without publishing credentials | `test_failure_exits_with_escalation_and_no_rollback`, `test_publish_workflows_run_verification_in_follow_on_jobs` | ✅ |
| AC-21 | One shared strict SemVer implementation uses ASCII digits, orders stable/prerelease and numeric prerelease identifiers correctly, ignores build metadata for precedence, and reports malformed release tags | `tests/ci/test_release_model.py::TestStrictSemVer` | ✅ |
| AC-22 | The release graph discovers publishable and build-only MSBuild projects, all `ProjectReference` edges, declarative packed inputs, and non-MSBuild delivery edges while keeping internal nodes out of release targets | `tests/ci/test_release_model.py::TestProjectGraphDiscovery` | ✅ |
| AC-23 | Release advisories separate explained direct changes, internal build changes, downstream deliverables, same-commit delivery prerequisites, and MinVer prerequisites; packed assets override documentation suppression, NuGet IDs match case-insensitively, deterministic non-product changes produce no impact, and uncertain source/build changes remain conservative | `tests/ci/test_release_model.py::TestImpactAnalysis` | ✅ |
| AC-24 | A production-shaped PR #1402 fixture yields seven affected surfaces excluding Plugins; stable Query/Migration plans require Dataverse; coordinated minors plan all surfaces | `tests/ci/test_release_model.py::TestImpactAnalysis` | ✅ |
| AC-25 | Patch detection handles both merge-time labels and `release:patch` added to an already-merged PR, including the production-shaped PR #1399 event and merged PRs originating from forks | `tests/ci/test_patch_release_issue.py::TestEventEligibility` | ✅ |
| AC-26 | Each patch record contains a stable PR marker; only labeled workflow records deduplicate, reruns, label removal/re-addition, and closed records do not duplicate or reopen it, and different PRs remain independent | `tests/ci/test_patch_release_issue.py::TestPatchRecordIdempotency` | ✅ |
| AC-27 | Tested Python builds patch titles and bodies, hostile PR titles remain inert, GitHub receives the body through `--body-file`, and the public release runbook is linked | `tests/ci/test_patch_release_issue.py::TestSafeIssueRendering` | ✅ |
| AC-28 | One authoritative manifest defines all four workflow-owned release labels and synchronization creates or updates them idempotently with precise failure diagnostics | `tests/ci/test_release_labels.py` | ✅ |
| AC-29 | Milestone helper failures stop issue creation instead of being swallowed | `tests/ci/test_milestone_release_check.py::TestWorkflowFailureHandling` | ✅ |
| AC-30 | Fork-originated merged PRs use the base repository token without executing historical PR content: executable automation is pinned to `github.workflow_sha`, checkout credentials are not persisted, and strict event gates remain | `tests/ci/test_patch_release_issue.py::TestWorkflowTrustBoundary` | ✅ |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| PR has `release:patch` but contains only deterministic non-product changes | Advisory reports no impact and the workflow does not open a release issue |
| `release:patch` is added after merge | The labeled event analyzes the original merge commit and creates the same per-PR record the close event would have created |
| A patch workflow is rerun or its label is removed and re-added | Search open and closed records for the stable marker; do not create or reopen anything when one exists |
| An unlabeled issue copies a valid patch-record marker | Ignore it; only an issue carrying the workflow-owned `release:patch` label can deduplicate a record |
| A merged PR originated from a fork | Use the trusted base workflow and issue-write token; inspect the merged revision only as data and never execute repository content from that checkout |
| A PR title contains shell or Markdown metacharacters and newlines | Render it as escaped text in Python and pass the issue body by file so it cannot affect command execution or issue structure |
| A workflow-owned label is missing or has drifted | Create or reconcile it from the checked-in manifest; stop with the exact label and command error if synchronization fails |
| Source change cannot be classified with certainty | Include its owning release surface conservatively and explain why |
| Repository-wide .NET build input changes | Include every .NET surface and downstream bundled deliverables |
| Central package version and another central-management setting change together | Map the version delta to consumers and conservatively include every .NET surface for the residual semantic change |
| Packed README or icon changes | Include every package whose declarative MSBuild `Pack` item consumes that asset |
| Build-only analyzer dependency changes | Explain the internal node and include its downstream CLI/MCP/Extension deliverables without making the analyzer a release target |
| Central PackageId differs only by case | Match it to consumers using NuGet's case-insensitive identity rules |
| Extension-only patch | List CLI as a same-commit delivery tag prerequisite because the publisher requires an exact `Cli-v*` tag before bundling |
| Release tag has malformed SemVer | Ignore it for latest-version selection and emit a diagnostic for maintainer review |
| Stable Query or Migration patch has no Dataverse tag on the target commit | List Dataverse separately as a same-commit MinVer prerequisite |
| Milestone closed with 0 PRs (deferred all) | No release issue opened — workflow checks PR count |
| Two `release:patch` PRs merge in quick succession | Two separate issues opened — maintainer can batch into one patch release |
| Cadence check runs but an open check-in issue already exists | No duplicate issue — workflow checks for existing open issues with the label |
| Stabilization branch diverges from main | Maintainer merges back to main after release; conflicts resolved manually |
| Public feed propagation is delayed | Retry every two minutes for at most 30 attempts, then fail and escalate |
| One public target, checksum, or version differs | Fail immediately and preserve every published artifact for investigation |

---

## Design Decisions

### Why event-driven over calendar-driven?

**Context:** Solo maintainer needs a release model that doesn't create artificial pressure but also doesn't let releases drift indefinitely.

**Decision:** Event-driven (patches on severity, minors on milestone completion) with an 8-week cadence floor as a safety net.

**Alternatives considered:**
- **Calendar-driven (every N weeks)**: Creates pressure to ship half-baked features. Forces arbitrary scope cuts. Wrong for solo maintainer with variable bandwidth.
- **Auto-release on merge**: Works for single-package libraries but dangerous for a multi-surface product where CLI + Extension + NuGet must be coherent. One bad merge auto-publishes to NuGet with no review.
- **Pure ad-hoc ("ship when it feels right")**: Current model. Works but causes drift — the 5-7 week gaps between prereleases were accidental, not intentional.

**Consequences:**
- Positive: Patches ship fast, minors ship complete, cadence floor prevents drift
- Negative: Requires discipline to label PRs and assign milestones (mitigated by automation surfacing gaps)

### Why trunk-based over GitFlow?

**Context:** Need a branching model that supports both patches and minors without coordination overhead.

**Decision:** Trunk-based (main-only) with optional stabilization branches for rare cases.

**Alternatives considered:**
- **GitFlow (long-lived develop + release branches)**: Massive overhead for solo maintainer. Designed for teams of 5+ with parallel release tracks.
- **Release branches per minor**: Creates merge-back burden. Solo maintainer ends up maintaining two branches for no benefit most of the time.
- **Pure trunk (no stabilization escape hatch)**: Would work 90% of the time but leaves no option when 1.2 development starts before 1.1 is verified.

**Consequences:**
- Positive: Simple default path (just merge and tag), escape hatch exists if needed
- Negative: Stabilization branches, when used, require cherry-pick discipline

### Why automate detection but not ceremony?

**Context:** Want to reduce cognitive load ("do I need to release?") without losing control over irreversible actions.

**Decision:** Three detection workflows (patch label, milestone completion, cadence floor) that open issues. The `/release` skill ceremony remains human-initiated.

**Alternatives considered:**
- **Full auto-release**: Tag push on merge. Too risky — NuGet publishes are permanent (unlisting is possible but the version is consumed).
- **Manual everything**: Current model. Works but relies on the maintainer remembering to check if a release is needed.

**Consequences:**
- Positive: Maintainer never misses a release trigger, never accidentally publishes
- Negative: Three new workflows to maintain (mitigated by simplicity — each is ~50 lines)

### Why per-package patching?

**Context:** A bug in PPDS.Query shouldn't force re-releasing PPDS.Auth, PPDS.Plugins, and 5 other packages.

**Decision:** Patch releases target direct changes and actual downstream
deliverables. Stable library tags also include any same-commit MinVer
prerequisites required by their MSBuild project dependencies, clearly labeled as
version-consistency tags rather than product changes.

**Alternatives considered:**
- **Always release all packages together**: Simpler mental model but wastes CI time and creates noise on NuGet (7 packages with identical content, just bumped version).
- **Path-only package mapping**: misses shared central dependencies and hides the reason a downstream deliverable contains changed code.
- **Always cascade every package**: avoids dependency reasoning but creates unrelated releases and noise.

**Consequences:**
- Positive: Minimal, explainable releases without broken stable MinVer dependency graphs.
- Negative: Some unchanged dependencies need consistency tags; the advisory keeps those distinct so CHANGELOGs do not claim product changes.

---

## Related Specs

- [docs-generation.md](./docs-generation.md) - Release tags trigger docs generation via `docs-release.yml`

---

## Changelog

| Date | Change |
|------|--------|
| 2026-09-12 | Make patch release records late-label and fork aware, label-authenticated and permanently idempotent per PR, safe for untrusted event text, and backed by one reconciled release-label manifest; keep historical merge content data-only and stop swallowing milestone helper failures (AC-25 through AC-30) |
| 2026-09-12 | Add bounded, read-only public artifact verification after NuGet, CLI GitHub Release, and four-target Marketplace publication (AC-17 through AC-20) |
| 2026-09-12 | Add strict ASCII SemVer and explained MSBuild-derived release impact planning, including packed package assets, build-only dependency nodes, repository-wide build inputs, and central-package semantics (AC-21–AC-24) |
| 2026-04-25 | Add security review gate (AC-13), extension auto-dispatch (AC-14), docs PR GitHub App setup (AC-15), unified tag convention (AC-16) |
| 2026-04-24 | Initial spec |
