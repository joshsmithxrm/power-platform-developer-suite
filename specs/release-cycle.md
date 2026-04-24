# Release Cycle

**Status:** Draft
**Last Updated:** 2026-04-24
**Code:** [.claude/skills/release/](../.claude/skills/release/), [.github/workflows/](../.github/workflows/), [scripts/ci/](../scripts/ci/), [tests/ci/](../tests/ci/), [tests/test_release_skill_content.py](../tests/test_release_skill_content.py)
**Surfaces:** All

---

## Overview

Policy layer governing *when* PPDS releases happen, *how* work is grouped into milestones, and *what* automation surfaces release readiness. The existing `/release` skill owns the ceremony (CHANGELOGs, version bumps, tag pushes, CI monitoring); this spec owns the decisions above it — triggering, targeting, branching, and enforcement.

### Goals

- **Predictable release cadence**: patches ship fast, minors ship when ready, nothing drifts silently
- **Automated detection**: the system tells the maintainer when a release is warranted — not the other way around
- **Manual ceremony**: irreversible actions (tag push, NuGet/Marketplace publish) remain human-initiated via `/release`

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
| GitHub Milestones | Group PRs into minor release targets (v1.1.0, v1.2.0, ...) |

### Dependencies

- Extends: [/release skill](../.claude/skills/release/SKILL.md) (ceremony)
- Complements: [MERGE-POLICY.md](../docs/MERGE-POLICY.md) (how PRs merge)

---

## Specification

### Release Types

| Type | Version | Trigger | Scope | Example |
|------|---------|---------|-------|---------|
| Patch | `X.Y.Z` (Z > 0) | Bug fix or security fix merged with `release:patch` label | Per-package — only affected package(s) get new tags | `Query-v1.0.1` |
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
   - Tag the affected package(s) from the merge commit on main
   - No branch, no release PR needed for single-package patches
   - Multi-package patches (rare) follow the standard `/release` ceremony

### Patch Release Procedure

For single-package patches (the common case):

1. Fix merges to main via normal PR process
2. Maintainer runs abbreviated `/release` targeting one package:
   - Update that package's CHANGELOG only
   - Push one tag (e.g., `Query-v1.0.1`)
   - Monitor one `publish-nuget.yml` run
   - Verify publish
3. No release PR needed — the fix PR itself is the audit trail

For multi-package patches or patches that touch the Extension:

1. Follow the full `/release` ceremony (release PR, all CHANGELOGs, coordinated tags)

### Severity-Based Patch Trigger

Human policy — the `release:patch` label is applied by the maintainer at PR merge time based on judgment. The criteria below are guidelines, not automated enforcement.

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

1. **PR merges to main** with `release:patch` label
2. **`post-merge-release-check.yml` fires**: identifies affected package(s) from changed file paths, opens a GitHub issue titled "Patch release needed: PPDS.{Package} vX.Y.Z"
3. **Issue body includes**: affected package(s), commit summary, link to merged PR, checklist linking to `/release` steps
4. **Maintainer reviews issue**, runs `/release` for the affected package(s)
5. **Maintainer closes issue** after publish verification

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

### Constraints

- Tag push is irreversible — never auto-tag or auto-publish
- Per-package patching must not require re-releasing unaffected packages
- Extension publish remains manual dispatch (documented workaround for GitHub Actions limitation)
- All release types must produce CHANGELOG entries before tagging

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `post-merge-release-check.yml` opens a GitHub issue when a PR with `release:patch` label merges to main | `tests/ci/test_post_merge_release_check.py::test_opens_issue_on_patch_label` | ✅ |
| AC-02 | The patch release issue body identifies affected package(s) by mapping changed file paths to package prefixes | `tests/ci/test_post_merge_release_check.py::test_maps_paths_to_packages` | ✅ |
| AC-03 | `milestone-release-check.yml` opens a GitHub issue when a milestone reaches 100% closed with merged PRs | `tests/ci/test_milestone_release_check.py::test_opens_issue_on_milestone_complete` | ✅ |
| AC-04 | `release-cadence-check.yml` opens a check-in issue if >8 weeks since last release tag and >0 unreleased commits on main | `tests/ci/test_release_cadence_check.py::test_opens_issue_when_overdue` | ✅ |
| AC-05 | `release-cadence-check.yml` does NOT open an issue if a release was cut within the last 8 weeks | `tests/ci/test_release_cadence_check.py::test_no_issue_when_recent_release` | ✅ |
| AC-06 | `/release` skill contains a "Patch Release Procedure" section documenting the single-package abbreviated flow | `tests/test_release_skill_content.py::test_patch_procedure_documented` | ✅ |
| AC-07 | `/release` skill contains a "Stabilization Branch" section documenting when to create one and how to merge back | `tests/test_release_skill_content.py::test_stabilization_branch_documented` | ✅ |
| AC-08 | `release-cadence-check.yml` does NOT open a duplicate issue if one is already open | `tests/ci/test_release_cadence_check.py::test_no_duplicate_issue` | ✅ |
| AC-09 | `milestone-release-check.yml` does NOT open a release issue when a milestone is closed with 0 merged PRs | `tests/ci/test_milestone_release_check.py::test_no_issue_on_empty_milestone` | ✅ |
| AC-10 | `post-merge-release-check.yml` opens an issue with "unknown package" warning when a `release:patch` PR touches no recognized `src/PPDS.*` paths | `tests/ci/test_post_merge_release_check.py::test_unknown_package_warning` | ✅ |
| AC-11 | `release-cadence-check.yml` does NOT open an issue if >8 weeks since last release but 0 unreleased commits on main | `tests/ci/test_release_cadence_check.py::test_no_issue_when_no_unreleased_commits` | ✅ |
| AC-12 | `post-merge-release-check.yml` identifies multiple affected packages when a `release:patch` PR touches paths in more than one package | `tests/ci/test_post_merge_release_check.py::test_multi_package_detection` | ✅ |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| PR has `release:patch` but touches no `src/PPDS.*` paths | Issue opened with "unknown package" warning — maintainer triages manually |
| Milestone closed with 0 PRs (deferred all) | No release issue opened — workflow checks PR count |
| Two `release:patch` PRs merge in quick succession | Two separate issues opened — maintainer can batch into one patch release |
| Cadence check runs but an open check-in issue already exists | No duplicate issue — workflow checks for existing open issues with the label |
| Stabilization branch diverges from main | Maintainer merges back to main after release; conflicts resolved manually |

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

**Decision:** Patch releases can target individual packages. Only the affected package gets a new tag and publish.

**Alternatives considered:**
- **Always release all packages together**: Simpler mental model but wastes CI time and creates noise on NuGet (7 packages with identical content, just bumped version).
- **Per-package with dependency cascade**: If PPDS.Cli depends on PPDS.Query, bump both. Correct in theory but PPDS packages are independently versioned and consumers pin versions — a Query patch doesn't break Cli consumers.

**Consequences:**
- Positive: Fast, minimal patch releases. No noise on unaffected packages.
- Negative: Requires the maintainer to know which package(s) a fix affects (mitigated by the detection workflow mapping file paths to packages).

---

## Related Specs

- [docs-generation.md](./docs-generation.md) - Release tags trigger docs generation via `docs-release.yml`

---

## Changelog

| Date | Change |
|------|--------|
| 2026-04-24 | Initial spec |
