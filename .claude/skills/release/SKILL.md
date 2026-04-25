---
name: release
description: Cut a PPDS release — CHANGELOG refresh, version bump, tag push sequence, CI monitoring, post-publish verification. Use when preparing a new prerelease or stable release across CLI, TUI, MCP, Extension, and NuGet libraries.
---

# Release

End-to-end release ceremony for PPDS. Produces CHANGELOGs, version bumps, tags, and triggers the CI publishing workflows. Use for prereleases (`*-beta.N`) and stable releases (`1.0.0`, `1.1.0`, etc.) alike — the mechanics are identical, version suffixes differ.

## When to Use

- "Let's cut a release"
- "Time for a new prerelease"
- "Ship v1.0.0"
- "Bump all the packages"
- Scheduled release cadence

Do NOT use this skill for:
- Hot-fix a single package — use `/pr` and tag that package only
- Documentation-only updates — those don't need a release
- Pre-public-release key rotation — that's a separate operation

## Usage

- `/release` — default to prerelease flow
- `/release prerelease` — explicit prerelease
- `/release stable v1.0.0` — stable release, version specified

## Prerequisites

Before starting:

1. **All in-flight PRs are merged or explicitly deferred.** Running a release with open feature PRs means either you forget to include them or you rush-merge. Decide first.
2. **No broken CI on `main`.** Check `gh run list --branch main --limit 5`. If CI is red, fix first.
3. **Security review done for stable releases (enforced).** Prereleases can skip; stable (`vX.Y.0`) **must** have a completed `/security-review` artifact at `docs/qa/security-review-*.md` covering the diff since the last stable release. The pre-merge verification step (Step 6) checks for this file — the release **cannot proceed** without it. Patches (`vX.Y.Z` where Z > 0) do not require a security review.
4. **Known the last release commit and/or tags.** The CHANGELOG enumeration is "since when?". Grab the previous release commit hash:
   ```bash
   git log --grep='chore(release)' --oneline -5
   ```

## Process

### 1. Register Phase and Create Release Worktree

```bash
python scripts/workflow-state.py set phase release
```

> **Note:** `release` is a custom phase for this skill. Existing workflow hooks (`verify-workflow.py`, `session-stop-workflow.py`) do not recognize it and fall through as non-enforcing. The phase registration is purely for audit-trail and retro mining. If you need PR-gate enforcement during the release PR, use `set phase pr` at Step 7 instead.

Create a dated release worktree (following the PPDS `release/*` branch convention):

```bash
# From main repo root
git worktree add .worktrees/release-YYYY-MM-DD -b release/prerelease-YYYY-MM-DD origin/main
# For stable: git worktree add .worktrees/release-v1.0.0 -b release/v1.0.0 origin/main
```

Name convention:
- Prerelease: `release/prerelease-YYYY-MM-DD`
- Stable: `release/vMAJOR.MINOR.PATCH`

### 2. Enumerate Changes Per Package

Each package has its own lineage — find the last release tag for each and diff from there:

```bash
# For each package, get the latest release tag
for prefix in Auth Cli Dataverse Extension Mcp Migration Plugins Query; do
  last_tag=$(git describe --tags --match "${prefix}-v*" --abbrev=0 2>/dev/null)
  echo "$prefix: $last_tag"
done
```

Then enumerate commits per package since its own last tag:

```bash
# Example: commits in PPDS.Auth since Auth-v1.0.0-beta.7
git log Auth-v1.0.0-beta.7..HEAD --oneline -- src/PPDS.Auth/
```

Repeat for all 8 packages (7 NuGet + 1 Extension):
- `src/PPDS.Auth/`
- `src/PPDS.Cli/`
- `src/PPDS.Dataverse/`
- `src/PPDS.Extension/`
- `src/PPDS.Mcp/`
- `src/PPDS.Migration/`
- `src/PPDS.Plugins/`
- `src/PPDS.Query/`

### 3. Draft CHANGELOGs (Parallel Agents)

All 8 packages have a `CHANGELOG.md` — you update each one. The version-bump mechanics in Step 4 differ (7 via git tags, 1 via `package.json`), but CHANGELOG work is uniform across 8 packages.

The previous release session dispatched **8 parallel research agents** (one per package) to draft CHANGELOG entries from `git log`. Follow the same pattern:

Dispatch one agent per package with this prompt template:

> Draft a CHANGELOG entry for `PPDS.<Package>` covering commits from `<last-release-commit>..HEAD` against `src/PPDS.<Package>/`. Group by Keep-a-Changelog sections (Added / Changed / Fixed / Deprecated / Removed / Security). Reference PR numbers where relevant (`#NNN`). Be user-facing — describe what changed, not which files moved. Output a single markdown block suitable for inserting below the `[Unreleased]` header in `src/PPDS.<Package>/CHANGELOG.md`.

After agents return, curate their drafts and insert into each CHANGELOG:

- **Prerelease:** new entry under `[Unreleased]` as `[<version>-beta.N] - YYYY-MM-DD`
- **Stable:** consolidate accumulated `[*-beta.*]` entries into a single `[<version>] - YYYY-MM-DD`
- **Always leave `[Unreleased]` header empty at top** after the release entry (Keep-a-Changelog convention — the next prerelease/release writes into that empty header)
- Update the bottom link references (`[<version>]: https://github.com/.../compare/<prev>..<new>`)

### 4. Version Bumps

#### NuGet packages (7): NO csproj edits

**PPDS.Auth, PPDS.Cli, PPDS.Dataverse, PPDS.Mcp, PPDS.Migration, PPDS.Plugins, PPDS.Query** are versioned via **MinVer from git tags** — you do NOT edit `<Version>` in any csproj. Version becomes whatever tag you push at step 7.

#### Extension: bump `package.json` version manually

Only the Extension version lives in a tracked file:

```
src/PPDS.Extension/package.json — "version": "X.Y.Z"
src/PPDS.Extension/package-lock.json — must match
```

Use the **odd/even minor convention**:
- **Odd minor** (0.5, 0.7, 1.1, 1.3) = pre-release channel on VS Marketplace
- **Even minor** (0.4, 0.6, 1.0, 1.2) = stable channel

Bump `package.json` version AND sync the lock file:

```bash
( cd src/PPDS.Extension && npm install )
```

**Why odd/even split?** VS Code Marketplace publishes both channels from the
same codebase; version gating happens at package time. Odd minors map to the
pre-release channel; even minors map to the stable channel. A single git tag
push does NOT automatically update the marketplace listing for both — see
`extension-publish.yml` for the channel-specific publish flow.

### 5. Package Lineage — discover at release time, do not hardcode

Each package has its own lineage. Embedding the current version table here would go stale on every release. Instead, query the actual state when you need it:

```bash
# Latest published tag per package (sorted by semver, prerelease-aware)
for prefix in Auth Cli Dataverse Extension Mcp Migration Plugins Query; do
  last=$(git tag --list "${prefix}-v*" --sort=-v:refname | head -1)
  echo "$prefix: $last"
done
```

Principles to apply when picking the next version:

- **Most NuGet packages** (Auth, Cli, Dataverse, Mcp, Migration, Query) share a unified `1.x` lineage — the prerelease-to-stable cadence keeps them in lockstep at major/minor.
- **PPDS.Plugins has its own lineage** that pre-dates the unified lineage (first released as `1.0.0` in Jan 2026 before the other packages). It progresses independently — don't try to reconcile its version with the others. Whenever you would regress its major number, you've made a mistake.
- **Strong-name rotation is a SemVer breaking change** (PublicKeyToken changes the assembly identity). Bump major on any package you re-sign with a new key — do not treat a key rotation as a minor or patch bump.
- **PPDS.Extension** is not MinVer; version lives in `src/PPDS.Extension/package.json` and follows the odd/even-minor convention (odd = pre-release channel, even = stable).

### 6. Pre-Merge Verification

Run the gates before opening the PR:

```bash
# Restore (--source is a restore/pack flag, not build)
dotnet restore PPDS.sln --source https://api.nuget.org/v3/index.json

# Build against restored packages
dotnet build PPDS.sln --no-restore -v q

# Full unit suite across TFMs
dotnet test PPDS.sln --no-build --filter "Category!=Integration" -v minimal

# Extension tests
( cd src/PPDS.Extension && npm test && npm run typecheck )

# TUI snapshots
npm run tui:test

# Dependency audit
dotnet list package --vulnerable
( cd src/PPDS.Extension && npm audit --production --audit-level=high )
```

**All must be green. No exceptions.**

#### Security Review Gate (stable releases only)

For stable releases (`vX.Y.0`), verify a completed security review artifact exists:

```bash
# Must find at least one matching file — if empty, STOP and run /security-review first
ls docs/qa/security-review-*.md
```

The security review must cover the delta since the last stable release. If no artifact exists, run `/security-review` before proceeding. **This gate is not enforced for patches or prereleases.**

Spot-check:
- Per-package CHANGELOGs: scan for fabricated PR numbers (`gh pr view NNN` should work for each cited PR)
- Extension `package.json` and `package-lock.json` version match
- For stable releases: confirm the security review artifact exists at `docs/qa/security-review-*.md` and covers the current release delta
- For stable releases: confirm any "What's new" doc (e.g., `docs/whats-new-v<major>.md` if present) reflects the final feature list

### 7. Open Release PR

```bash
gh pr create \
  --base main \
  --head release/prerelease-YYYY-MM-DD \
  --title "chore(release): prerelease bump for all packages — YYYY-MM-DD" \
  --body-file release-pr-body.md
```

Include in the PR body:
- **Summary** of the release window (dates, commit range)
- **Versions table** — previous → new for each package
- **Highlights** — 1-line per package of user-facing changes
- **Release flow after merge** — the exact tag-push sequence (see section 8)
- **Test plan** — what to verify before and after merge

Template: use PR #785 as reference (`gh pr view 785 --json body`).

### 8. Post-Merge: Push Tags

After the PR merges, pull main and push tags **individually** (see Gotcha 6):

```bash
git fetch origin
git checkout main && git pull

# One tag per NuGet package (7) — versions are per-package; do NOT assume a single shared version.
# PPDS.Plugins in particular is on a 2.x lineage (see Step 5).
git tag Auth-v<auth-version>
git tag Cli-v<cli-version>
git tag Dataverse-v<dataverse-version>
git tag Mcp-v<mcp-version>
git tag Migration-v<migration-version>
git tag Query-v<query-version>
git tag Plugins-v<plugins-version>

# Extension (requires manual workflow dispatch with --ref Extension-v<version> —
# push the tag for source-of-truth and as the --ref target)
git tag Extension-v<extension-version>

# Verify tag prefixes BEFORE push — catches MinVer-prefix bugs early
git tag -l 'Auth-v*' | tail -3
git tag -l 'Cli-v*' | tail -3
# ...etc for each prefix

# Push tags individually (batch --tags does NOT trigger GitHub Actions — see Gotcha 6)
for tag in Auth-v<auth-version> Cli-v<cli-version> Dataverse-v<dataverse-version> Mcp-v<mcp-version> Migration-v<migration-version> Query-v<query-version> Plugins-v<plugins-version> Extension-v<extension-version>; do
  git push origin "refs/tags/$tag"
  sleep 3
done
```

**Worked example** — verbatim from PR #785 (2026-04-17 prerelease):
```bash
git tag Auth-v1.0.0-beta.8
git tag Cli-v1.0.0-beta.14
git tag Dataverse-v1.0.0-beta.7
git tag Mcp-v1.0.0-beta.2
git tag Migration-v1.0.0-beta.8
git tag Query-v1.0.0-beta.2
git tag Plugins-v2.1.0-beta.1
git tag Extension-v0.7.0
for tag in Auth-v1.0.0-beta.8 Cli-v1.0.0-beta.14 Dataverse-v1.0.0-beta.7 Mcp-v1.0.0-beta.2 Migration-v1.0.0-beta.8 Query-v1.0.0-beta.2 Plugins-v2.1.0-beta.1 Extension-v0.7.0; do
  git push origin "refs/tags/$tag"
  sleep 3
done
```

**Tag prefixes must match MinVer config** in each csproj's `<MinVerTagPrefix>`. Deviations will produce wrong versions.

#### Unified v* Tag (minor/stable releases only)

After pushing all per-package tags, push a **unified version tag** to trigger docs generation:

```bash
# For minor/stable releases: push a unified tag that triggers docs-release.yml
git tag v<version>   # e.g. v1.1.0
git push origin "refs/tags/v<version>"
```

**When to push a unified `v*` tag:**

| Release type | Unified tag? | Example | Triggers |
|---|---|---|---|
| Minor (coordinated) | **Yes** — `vX.Y.0` | `v1.1.0` | `docs-release.yml` → regenerate reference docs, open ppds-docs PR |
| Stable (coordinated) | **Yes** — `vX.Y.0` | `v1.0.0` | Same as minor |
| Prerelease (coordinated) | **Optional** — `vX.Y.0-beta.N` | `v1.1.0-beta.3` | `docs-release.yml` dry-run preview only (if desired) |
| Patch (single-package) | **No** | — | Per-package tags only; docs don't regenerate for patches |

The unified tag is **not used for versioning** — it is purely a trigger for `docs-release.yml`. Per-package tags (`Auth-v*`, `Cli-v*`, etc.) remain the source of truth for package versions via MinVer.

**Relationship between tag types:**

- **Per-package tags** (e.g., `Auth-v1.1.0`, `Cli-v1.1.0`): trigger publishing workflows (`publish-nuget.yml`, `release-cli.yml`, `extension-publish.yml`). One per package, versioned independently.
- **Unified tag** (e.g., `v1.1.0`): triggers `docs-release.yml` which regenerates reference documentation and opens a PR in ppds-docs. One per coordinated release.

### 9. Monitor CI Workflow Runs

Tags trigger different workflows:

| Tag pattern | Workflow | Output |
|---|---|---|
| `Auth-v*`, `Dataverse-v*`, `Migration-v*`, `Query-v*`, `Mcp-v*`, `Plugins-v*` | `publish-nuget.yml` | NuGet.org packages |
| `Cli-v*` | `publish-nuget.yml` + `release-cli.yml` | NuGet tool package + multi-platform binaries (win-x64/arm64, osx-x64/arm64, linux-x64) + draft GitHub release published |
| `Extension-v*` | `extension-publish.yml` (auto-dispatches on tag push; channel inferred from odd/even minor) | VS Code Marketplace — matrix of 4 targets (win32-x64, linux-x64, darwin-x64, darwin-arm64). Manual override: `gh workflow run extension-publish.yml --ref Extension-v<version> -f dry_run=false -f channel=stable`. |

**Release-cli draft flow.** `release-cli.yml` prefers an existing **draft release** created ahead of time; if none exists, it falls back to creating one. For the cleanest path, create a draft release with notes pulled from the CLI CHANGELOG before pushing the `Cli-v*` tag:

```bash
gh release create Cli-v<cli-version> --draft --title "PPDS CLI v<cli-version>" --notes-file <(sed -n '/## \[<cli-version>\]/,/## \[/p' src/PPDS.Cli/CHANGELOG.md | head -n -1)
```

This avoids the "already has an immutable release for this tag" failure mode (Gotcha 1) in most cases. If you skip this, the workflow creates the draft itself — fine unless something else has created a draft already.

Watch the runs:

```bash
gh run list --limit 20 --json status,conclusion,createdAt,displayTitle,workflowName
```

Expected sequence (typical timing):
1. 7 `publish-nuget.yml` runs start within seconds of tag push — ~3–5 min each
2. `release-cli.yml` runs in parallel — builds binaries on 3 OSes — ~8–15 min
3. `extension-publish.yml` auto-dispatches on `Extension-v*` tag push — ~10–15 min. Channel (pre-release vs stable) is inferred from the tag version (odd minor = pre-release, even minor = stable). Manual override available via `gh workflow run extension-publish.yml --ref Extension-v<version> -f channel=<stable|pre-release>`.
4. `docs-release.yml` fires on the unified `v*` tag push (if pushed) — opens paired PRs in ppds-docs and this repo.

### 10. Verify Publishes

#### NuGet.org
For each package:
```bash
# Check listing exists at expected version.
# IMPORTANT: the v3-flatcontainer path segment MUST be lowercase (e.g. ppds.auth, ppds.cli),
# even though the canonical PackageId is PPDS.<Name>. Mixed case returns 404.
curl -s https://api.nuget.org/v3-flatcontainer/<package-id-lowercase>/index.json | jq '.versions' | tail -5
```
Or visit `https://www.nuget.org/packages/PPDS.<Package>/`.

#### GitHub Release
```bash
gh release view Cli-v<cli-version>
```
- Confirm 5 binaries attached (win-x64.exe, win-arm64.exe, osx-x64, osx-arm64, linux-x64)
- Confirm release notes pulled from CHANGELOG

#### VS Code Marketplace
Visit `https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite`
- Confirm latest version matches the tag
- Confirm channel is correct (pre-release vs stable)
- Confirm changelog tab shows the new entry

#### Smoke test installs
```bash
# Fresh CLI tool install
dotnet tool install -g PPDS.Cli --version <cli-version>
ppds --version
dotnet tool uninstall -g PPDS.Cli

# Fresh Extension install
code --install-extension JoshSmithXRM.power-platform-developer-suite --pre-release  # prerelease channel
# or:
code --install-extension JoshSmithXRM.power-platform-developer-suite  # stable
# Then open VS Code and verify the sidebar loads, the version matches, basic commands work
```

## Patch Release Procedure

For a single-package patch (the common case — one bug fix or security fix in one package),
the full ceremony above is unnecessary. Use this abbreviated single-package flow instead. See
`specs/release-cycle.md` for the policy that decides when a `release:patch` label warrants a
patch release.

**When to use:**

- Exactly one package is affected (e.g., a bug in `PPDS.Query` only)
- The change is a bug fix or security fix — not a feature
- The merged PR is labeled `release:patch`

For multi-package patches, or any patch that touches the Extension, fall back to the full
ceremony above.

**Steps (abbreviated):**

1. Identify the affected package from the merged PR'"'"'s changed paths (the
   `post-merge-release-check.yml` workflow does this automatically and opens an issue).
2. Update **only that package'"'"'s CHANGELOG** — add a new `[X.Y.Z] - YYYY-MM-DD` entry under
   `[Unreleased]`. Do not touch the other 7 CHANGELOGs.
3. Open a tiny CHANGELOG-only PR, merge it, then pull main.
4. Push **one tag** for the affected package only:
   ```bash
   git tag <Prefix>-v<X.Y.Z>
   git push origin "refs/tags/<Prefix>-v<X.Y.Z>"
   ```
5. Monitor the **single** `publish-nuget.yml` workflow run that fires for that tag.
6. Verify the publish on NuGet.org (see Section 10 above for the verification commands).

**No release PR is required for single-package patches** — the original fix PR is the audit
trail. The CHANGELOG-only PR in step 3 provides the version-bump record.

**Cross-reference:** for multi-package patches (rare) or any patch touching the Extension,
follow the full ceremony in Sections 1–10 above. The abbreviated flow only applies when the
blast radius is a single NuGet package.

## Stabilization Branch

Stabilization branches (`release/X.Y`) are an **escape hatch** — used only when active
development for the next minor (`X.(Y+1)`) has started on `main` before `X.Y` has been
verified and shipped. For a solo maintainer this should be the rare exception, not the
default. The default is to tag from `main`.

**When to create one:**

- A milestone (`vX.Y.0`) is feature-complete on `main` but you need to keep merging
  unrelated work for `vX.(Y+1).0` before the `vX.Y.0` release is verified
- A stable release is soaking and you want to land bug fixes for it without freezing `main`

**When NOT to create one:**

- The common case: tag directly from `main`. No branch, no merge-back overhead.
- Patches: use the abbreviated patch flow above instead.

**How to create one:**

```bash
git checkout main && git pull
git checkout -b release/X.Y main
git push -u origin release/X.Y
```

Create the branch at the commit where the milestone is feature-complete — typically the
merge commit of the last issue/PR in the milestone.

**What goes on it:**

- **Cherry-pick bug fixes only.** No new features, no refactors. Each cherry-pick should
  reference the original commit on `main`:
  ```bash
  git checkout release/X.Y
  git cherry-pick -x <commit-sha-from-main>
  git push origin release/X.Y
  ```
- The `-x` flag preserves the original commit hash in the message — critical for the
  merge-back step below.

**How to tag from it:**

Tags push from the stabilization branch, **not** from `main`:

```bash
git checkout release/X.Y
git pull
git tag <Prefix>-v<X.Y.Z>
git push origin "refs/tags/<Prefix>-v<X.Y.Z>"
```

The CI publish workflows fire from the tag — they don'"'"'t care which branch it points to.

**How to merge back to main:**

After the release publishes, merge the stabilization branch back into `main` so any
fixes that were cherry-picked don'"'"'t drift:

```bash
git checkout main && git pull
git merge --no-ff release/X.Y -m "merge: release/X.Y back into main after vX.Y.Z"
# Resolve conflicts manually — favor main'"'"'s version for files that diverged
git push origin main
```

If every commit on `release/X.Y` was cherry-picked from `main`, the merge is empty and you
can simply delete the branch. If divergence happened (rare — usually only happens when a
bug was fixed on the branch first and never on main), resolve conflicts case-by-case.

**Cleanup:**

```bash
# Once main has all the fixes:
git branch -d release/X.Y
git push origin :release/X.Y
```

Do not keep stabilization branches around indefinitely — they'"'"'re intentionally short-lived.

## Known Gotchas

### Gotcha 1: `release-cli.yml` fails with HTTP 422 "tag_name was used by an immutable release"

**Symptom:** `release-cli.yml` workflow fails at the "Publish draft release" step with:
```
HTTP 422: Validation Failed
tag_name was used by an immutable release
```

**Cause:** The workflow creates a draft release, then tries `gh release edit --draft=false` to publish it. If a draft release for that tag already exists (e.g., from a previous failed run, or a manually-created draft), the publish step fails because GitHub has marked it immutable after the draft was saved.

**Fix:** Before re-running, check `gh release list --limit 10` for a pre-existing draft for that tag. Delete it with `gh release delete <tag>` and re-run the workflow via `gh run rerun <run-id>`.

**Prevention:** Don't manually create GitHub releases. Let `release-cli.yml` own that. If a previous release for this tag partially succeeded, delete the draft before re-pushing.

### Gotcha 2: Extension publish — auto-dispatch on tag push

**Current behavior:** `extension-publish.yml` triggers automatically when an `Extension-v*` tag is pushed. The workflow infers the release channel from the tag version (odd minor = pre-release, even minor = stable).

**Manual override:** If you need to override the inferred channel or do a dry run, use `workflow_dispatch` with `--ref`:
```bash
gh workflow run extension-publish.yml --ref Extension-v<version> -f dry_run=false -f channel=stable
# For prerelease: -f channel=pre-release
```

**Note:** Omitting `--ref` defaults to `refs/heads/main`, which will fail the ref-check guard. Always pass `--ref Extension-v<version>` for manual dispatch.

### Gotcha 3: NuGet central package management (NU1507)

**Symptom:** `dotnet build` fails with `NU1507: There are 2 package sources defined...`

**Cause:** Local machine has both `nuget.org` and an internal feed (`hitachi-github`) configured without source mapping. MSBuild treats this as error.

**Fix:** Pass `--source https://api.nuget.org/v3/index.json` to isolate to public NuGet. Alternative permanent fix: commit a `NuGet.config` in the repo root that pins sources, but only if you need local builds to work without `--source`.

### Gotcha 4: MinVer produces wrong version if tag prefix is off

**Symptom:** Published package has wrong version (often stale).

**Cause:** MinVer reads `<MinVerTagPrefix>` from csproj and looks for tags matching that prefix. If you push `Cli-v1.0.0` but csproj says `MinVerTagPrefix: CLI-v`, MinVer falls back to older tags.

**Fix:** Check each csproj's `<MinVerTagPrefix>` matches exactly (case-sensitive on Linux runners). Tag prefixes used:
- `Auth-v`, `Cli-v`, `Dataverse-v`, `Mcp-v`, `Migration-v`, `Plugins-v`, `Query-v`

Run `git log --oneline --grep='MinVerTagPrefix'` to see any recent changes.

### Gotcha 5: Hook or build-configuration changes needed mid-release

Previous releases have needed inline fixes:
- `fix(build): set NuGetAuditMode=direct to unblock CI` — CI audit mode broke the build
- `.claude/hooks/protect-main-branch.py` reading `file_path` at top level instead of under `tool_input`

**Prevention:** Run full CI on the release branch BEFORE opening the release PR. Catch these on the release branch, not in the merge.

### Gotcha 6: `git push --tags` does not trigger GitHub Actions workflows

**Symptom:** After `git push origin --tags`, no `publish-nuget.yml` or `release-cli.yml` workflow runs appear.

**Cause:** GitHub Actions does not fire `on: push: tags:` triggers for tags pushed in a single batch via `--tags`. This is a known GitHub platform limitation.

**Fix:** Push tags individually with a small delay between each:
```bash
for tag in Auth-v<ver> Cli-v<ver> Dataverse-v<ver> Mcp-v<ver> Migration-v<ver> Query-v<ver> Plugins-v<ver> Extension-v<ver>; do
  git push origin "refs/tags/$tag"
  sleep 3
done
```

### Gotcha 7: MSYS2 path conversion strips `/p:` MSBuild properties

**Symptom:** `dotnet pack` for Plugins fails with MSB1008 when run in a `shell: bash` step on `windows-latest`.

**Cause:** Git Bash (MSYS2) converts `/p:PropertyName=value` to a Windows path like `C:/msys64/p:PropertyName=value`. This breaks MSBuild property syntax.

**Fix:** Use `-p:` instead of `/p:` for MSBuild properties in bash shell steps. The `-p:` syntax is equivalent and not subject to MSYS2 path conversion. Fixed in PR #884.

## Rollback / Recovery

### A tag published a broken package
- **NuGet:** Unlist the version via the NuGet.org web UI (`https://www.nuget.org/packages/PPDS.<Package>/<version>/Delete`) OR via CLI:
  ```bash
  dotnet nuget delete PPDS.<Package> <version> \
    --api-key "$NUGET_API_KEY" \
    --source https://api.nuget.org/v3/index.json
  ```
  Requires the same `NUGET_API_KEY` secret CI uses. NuGet allows unlisting; hard-deletion requires support.
- **GitHub release:** `gh release delete <tag>` and `git tag -d <tag> && git push origin :<tag>`.
- **Extension:** VS Marketplace supports unlisting via the management portal. You cannot hard-delete.

### Fix-forward
Bump the patch version (e.g., `1.0.0` → `1.0.1`), prepare a new CHANGELOG entry, and re-run this skill for the single affected package.

### Never
- Force-push or rewrite tags that have been published. Consumers have already pulled them.
- Re-use a version number for different content. SemVer consumers treat the tag + version as immutable identity.

## Post-Release Cleanup

After all publishes verify:

1. **Announce** — release notes on GitHub, optionally social/blog.
2. **Record the release in workflow state** for audit/retro mining:
   ```bash
   python scripts/workflow-state.py set release.pr "https://github.com/.../pull/NNN"
   python scripts/workflow-state.py set release.tags "Auth-v...,Cli-v...,Dataverse-v...,Mcp-v...,Migration-v...,Query-v...,Plugins-v...,Extension-v..."
   python scripts/workflow-state.py set release.completed_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
   ```
3. **Clean up the release worktree:**
   ```bash
   git worktree remove .worktrees/release-YYYY-MM-DD
   git branch -d release/prerelease-YYYY-MM-DD
   ```
4. **File any follow-up issues** surfaced during verification (e.g., "marketplace listing missing icon"). Label `v<next>` milestone.
5. **Update `.plans/v*-release-plan.md`** (if present) with actual vs planned dates and any learnings.
6. **Patch this skill** if any step was wrong, missing, or ambiguous. The skill is the canonical release runbook — keeping it accurate across releases is how it stays useful.

## Rules

1. **Version bumps only via tags for NuGet packages.** Never edit `<Version>` in a `.csproj`.
2. **Extension version lives in `package.json` only.** Must match `package-lock.json`.
3. **Tags push individually with delay.** `git push --tags` does not trigger GitHub Actions workflows (see Gotcha 6). Push each tag separately with a small delay.
4. **Monitor all workflow runs.** Don't assume success — verify each publish separately.
5. **Stable releases require full verification.** Prereleases tolerate gotchas being caught post-publish; stable releases should not.
6. **Package lineage is immutable.** PPDS.Plugins started at 1.0.0 stable in Jan 2026; never regress to an earlier major.
7. **MinVer tag prefixes are source of truth.** Don't invent new prefixes without updating the matching csproj.
8. **Stable releases require a security review artifact.** A `docs/qa/security-review-*.md` file covering the current release delta must exist before tagging `vX.Y.0`. Patches and prereleases are exempt.

## References

- Previous prerelease: PR #785 (2026-04-17), commit `5bb4d5b90`
- CI workflows:
  - `.github/workflows/publish-nuget.yml`
  - `.github/workflows/release-cli.yml`
  - `.github/workflows/extension-publish.yml`
- Per-package CHANGELOG format: Keep a Changelog 1.1.0 (https://keepachangelog.com/en/1.1.0/)
- Semantic versioning: https://semver.org/spec/v2.0.0.html
