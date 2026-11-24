# Release Runbook

The step-by-step procedure for cutting a PPDS release: CHANGELOGs, version
bumps, the tag-push sequence, CI monitoring and post-publish verification. Use it
for prereleases and stable releases alike; the mechanics are the same and only
the version suffixes differ.

Related documents:

- [docs/RELEASE.md](../RELEASE.md) is the reference for the release model and
  policy this procedure relies on: tags, channels and lineage, scope analysis,
  CLI/Extension co-release, publisher checks, public artifact verification,
  signing and keys, and the native runtime.
- [specs/release-cycle.md](../../specs/release-cycle.md) decides when a release
  is warranted (patch labels, milestones, cadence floor) and the branching policy.
- [docs/runbooks/docs-release.md](docs-release.md) covers the docs-site release
  that a unified `v*` tag triggers.

Every tag push, GitHub release and publish is an irreversible action that the
maintainer performs deliberately. Nothing in this procedure is automatic
approval for the next step.

## When to Use

- Cutting a coordinated prerelease or stable release (`X.Y.0`)
- Shipping a focused patch after a `release:patch` fix has merged (see
  [Patch Release Procedure](#patch-release-procedure))
- Acting on a release issue opened by `post-merge-release-check.yml`,
  `milestone-release-check.yml` or `release-cadence-check.yml`

Not for:

- Implementing a hot-fix: open a normal PR; after it merges, use the
  [Patch Release Procedure](#patch-release-procedure) and its scope advisory
  before choosing tags
- Documentation-only changes, which do not need a release
- Strong-name key rotation, which is an incident procedure
  ([docs/RELEASE.md](../RELEASE.md#manual-strong-name-rotation-incident-response-only))

## Prerequisites

1. **All in-flight PRs are merged or explicitly deferred.** Decide before you
   start; otherwise you either forget them or rush-merge them.
2. **CI on `main` is green.** Check `gh run list --branch main --limit 5`. If it
   is red, fix it first.
3. **Security review for stable releases.** A stable release (`vX.Y.0`)
   cannot proceed without a completed security review artifact at
   `docs/qa/security-review-*.md` covering the diff since the last stable
   release. Step 6 checks it. Patches (`X.Y.Z`, Z > 0) and prereleases are
   exempt. This gate is checked by the maintainer, not by CI.
4. **Know the previous release point.** The CHANGELOG range is "since when?":
   ```bash
   git log --grep='chore(release)' --oneline -5
   ```

## Process

### 1. Create the Release Branch and Worktree

Create a dated release worktree (`.worktrees/` is git-ignored):

```bash
# From the main repository root
git worktree add .worktrees/release-YYYY-MM-DD -b release/prerelease-YYYY-MM-DD origin/main
# Stable: git worktree add .worktrees/release-v1.0.0 -b release/v1.0.0 origin/main
```

Branch names:

- Prerelease: `release/prerelease-YYYY-MM-DD`
- Stable: `release/vMAJOR.MINOR.PATCH`

These are short-lived PR branches. They are not the `release/X.Y`
[stabilization branch](#stabilization-branch), and tags are cut from `main`
after the release PR merges.

### 2. Generate and Review the Release Scope Advisory

Run the read-only release model against the exact release range:

```bash
gh pr list --state merged --base main --label breaking-change --limit 1000 \
  --json number,title,mergeCommit > breaking-prs.json
```

```bash
python scripts/ci/release_plan.py \
  --base <base-commit> \
  --head HEAD \
  --release-kind <patch|minor|major> \
  --channel <stable|prerelease> \
  --breaking-prs-file breaking-prs.json \
  --format markdown
```

If the plan has a **Refused** section (exit 1), stop:

- a `breaking-change` pull request is in the range and the release kind is
  not major, or the package's planned version (its `## [X.Y.Z] - Unreleased`
  CHANGELOG section) is not a new major: plan a major for that package, or
  take the pull request out of the range;
- or the breaking data was not supplied: run the `gh pr list` command above.
  `--breaking-checked-elsewhere "<how>"` records that it was checked another
  way.

`gh pr list --base main` does not list pull requests merged straight into a
`release/X.Y` branch. When releasing from a stabilization branch, also run it
with `--base release/X.Y` and merge the two lists:

```bash
gh pr list --state merged --base release/X.Y --label breaking-change --limit 1000 \
  --json number,title,mergeCommit > breaking-prs-release.json
jq -s 'add' breaking-prs.json breaking-prs-release.json > breaking-prs-all.json
```

Pull requests in the file whose merge commit is not in the range are listed
under "Breaking Pull Requests Outside the Range". On a
[stabilization branch](#stabilization-branch), a fix picked with
`git cherry-pick -x` counts by its `(cherry picked from commit …)` line; a pick
without `-x` is listed as outside the range.

Review all five lists before drafting CHANGELOGs: **direct product changes**,
**internal build changes**, **downstream deliverables**, **same-commit delivery
tag prerequisites** and **same-commit MinVer tag prerequisites**. What each list
means and how the model builds it is in
[Release Scope Analysis](../RELEASE.md#release-scope-analysis).

The command is advisory only. It never creates tags, publishes packages or
dispatches release workflows. Resolve any malformed-tag diagnostics it reports
before choosing versions.

### 3. Draft CHANGELOGs and Contributor Credit

Each of the 8 surfaces has its own CHANGELOG: `src/PPDS.<Surface>/CHANGELOG.md`
for Auth, Cli, Dataverse, Extension, Mcp, Migration, Plugins and Query. Update
every release target from Step 2.

For each package, collect the changes in the range and write user-facing
entries:

```bash
git log --oneline <last-release-commit>..HEAD -- src/PPDS.<Surface>/
```

Format (Keep a Changelog 1.1.0):

- Sections in this order, dropping any that are empty: Added, Changed, Fixed,
  Deprecated, Removed, Security.
- One bullet per logical change, not one per commit. Describe what changed for
  the user, not which files moved.
- Reference the PR or issue as `(#NNN)`. Every cited number must exist
  (`gh pr view NNN`).

Placement:

- **Prerelease:** add `## [<version>-<suffix>] - YYYY-MM-DD` below
  `## [Unreleased]`.
- **Stable:** consolidate the accumulated prerelease entries into one
  `## [<version>] - YYYY-MM-DD`.
- **Always leave the `## [Unreleased]` heading empty at the top** for the next
  release.
- **Open the next version.** In the same release PR, add the next
  `## [X.Y.Z] - Unreleased` section (the next patch version unless the next
  release is already known) below `## [Unreleased]` in every package you
  release. Pull requests add their entries there; until it exists the
  Changelog Check fails every pull request that changes the package, with a
  message that names this step.
- Update the link references at the bottom of the file to the
  `compare/<Prefix>-v<previous>...<Prefix>-v<new>` form the files already use,
  and point `[Unreleased]` at `compare/<Prefix>-v<new>...HEAD`.

Contributor credit: for every included external contribution, follow the
[contributor lifecycle](../CONTRIBUTOR-LIFECYCLE.md). Verify the actual landed
author and trailers, and put the source PR and verified contributor credit in the
release notes now. After publication (Step 11) check the actual published notes.
Keep contributor communication and the original PR's disposition with their named
owners; if sending a reply or closing a PR needs the maintainer's permission,
prepare the exact reply and decision and get that decision rather than leaving
closeout silently unowned or marking it done. Use the policy's explicit
no-contribution record when nothing external is included. None of this grants
merge, publication or communication authority; who may merge is in
[MERGE-POLICY.md](../MERGE-POLICY.md#who-merges).

### 4. Version Bumps

**NuGet packages (7): no csproj edits.** PPDS.Auth, PPDS.Cli, PPDS.Dataverse,
PPDS.Mcp, PPDS.Migration, PPDS.Plugins and PPDS.Query are versioned by MinVer
from git tags. Never edit `<Version>` in a csproj; the version is whatever tag
you push in Step 8.

**Extension: bump `package.json` by hand.** The Extension version is the only
one in a tracked file:

```
src/PPDS.Extension/package.json       "version": "X.Y.Z"
src/PPDS.Extension/package-lock.json  must match
```

Pick the minor by the odd/even channel rule
([Release model](../RELEASE.md#release-model-tags-channels-and-lineage)), then
sync the lock file:

```bash
( cd src/PPDS.Extension && npm install )
```

### 5. Choose Versions

Take the latest valid tag for each surface from the Step 2 advisory, which
orders tags by strict SemVer 2.0 precedence. Do not substitute `git --sort=-v:refname`:
refname sorting can rank `beta.2` above stable `1.0.0` and misorder
multi-digit prerelease identifiers such as `beta.10`. Do not copy a version table
into notes or this runbook; query the current state each time.

Pick the next version per package:

- **Patch** (`X.Y.Z+1`): bug fixes only; no new features and no behavior change
  for working code.
- **Minor** (`X.Y+1.0`): new features and backwards-compatible changes;
  deprecations allowed.
- **Major** (`X+1.0.0`): breaking changes such as removed features, renamed APIs
  or changed defaults that affect existing callers. A major needs a CHANGELOG
  entry under Removed, or a `feat!:`/`fix!:` commit, that explains what broke.
- Heuristic: any `feat:` commit in the range means minor; any `feat!:` or
  `fix!:` means major; otherwise patch.
- Prereleases increment the prerelease number (for example `-beta.N` or
  `-rc.N`) without bumping the base version until it is promoted to stable.

Each package keeps its own lineage, PPDS.Plugins in particular, and a
strong-name key change forces a major. Both rules are in the
[Release model](../RELEASE.md#release-model-tags-channels-and-lineage).

### 6. Pre-Merge Verification

Run every gate below locally; do not skip any. `build.yml` and `test.yml` start
only on pull requests to `main` and pushes to `main`, so pushing the release
branch by itself starts no CI. On the release PR (Step 7), CI selects jobs by
changed paths, and a typical release PR (CHANGELOGs plus the Extension
`package.json`/`package-lock.json`) gets only part of it:

- **Runs:** `build.yml` `build` (.NET restore, vulnerable-package check, Release
  build) and `extension` (typecheck, lint, audit, compile, tests), both because
  `src/PPDS.Extension/**` changed; `test.yml` Python tests and the native/VSIX
  artifact jobs, because `src/**` changed.
- **Does not run:** the `test.yml` .NET `test` job, which needs a `.cs`,
  `.csproj`, `.sln`, props/targets, `global.json`, `NuGet.config` or `test.yml`
  change; and `tui-e2e.yml`, which needs a `src/PPDS.Cli/Tui/**` change.

The local `dotnet test` and `npm run tui:test` below are then the only runs of
the .NET unit tests and TUI snapshots for the release commit.

```bash
# Restore and build (the repository NuGet.config pins nuget.org)
dotnet restore PPDS.sln
dotnet build PPDS.sln --no-restore -v q

# Unit suite across target frameworks
dotnet test PPDS.sln --no-build --filter "Category!=Integration" -v minimal

# Extension checks (the same three the Extension publisher runs)
( cd src/PPDS.Extension && npm run typecheck:all && npm run lint && npm test )

# TUI snapshots
npm run tui:test

# Dependency audit
dotnet list package --vulnerable
( cd src/PPDS.Extension && npm audit --omit=dev --audit-level=high )
```

All must be green. No exceptions.

#### Security Review Gate (stable releases only)

For a stable release (`vX.Y.0`), confirm a completed security review artifact
exists and covers this release:

```bash
ls docs/qa/security-review-*.md
```

The artifact must cover `<last-stable-tag>..HEAD`; an artifact from an earlier
release does not satisfy the gate. If none does, complete a security review of
that diff and record it as a new `docs/qa/security-review-<scope>.md` before
continuing. Patches and prereleases are not subject to this gate.

Spot-check:

- Every PR number cited in the CHANGELOGs exists (`gh pr view NNN`).
- Extension `package.json` and `package-lock.json` versions match.
- Stable releases: the security review artifact exists and covers the current
  delta.
- Stable releases: any "What's new" document (for example `docs/whats-new-v1.md`)
  reflects the final feature list.

### 7. Open the Release PR

Open it as a **draft**, wait for its CI to finish, and mark it ready only when
every check is green. This is where build or configuration problems surface
(see [Build or configuration fixes needed mid-release](#build-or-configuration-fixes-needed-mid-release)).
The PR's CI is path-selected and usually skips the .NET unit tests (Step 6), so
green checks here do not replace the local Step 6 gates.

```bash
gh pr create --draft \
  --base main \
  --head <release branch from Step 1> \
  --title "chore(release): <prerelease|stable> bump for all packages — <version/date>" \
  --body-file release-pr-body.md
```

The PR body contains:

- **Summary** of the release window (dates, commit range)
- **Versions table**: previous to new for each package
- **Highlights**: one line per package of user-facing changes
- **Release flow after merge**: the exact tag-push sequence from Step 8
- **Test plan**: what to verify before and after merge

PR #785 is a worked reference (`gh pr view 785 --json body`).

When CI on the draft is green, mark it ready with `gh pr ready <pr-number>`.

### 8. Post-Merge: Push Tags

Tag only from `main` (or from a `release/X.Y` stabilization branch), never from a
feature branch, so the published commit is on the mainline.

**Co-release rule:** a release train that includes a `Cli-v*` tag also includes
an `Extension-v*` tag on the same commit, because the Extension bundles the CLI
from that exact tag. The rule, how the advisory issue reconciles, and how to opt
out with a durable record are in
[CLI/Extension Co-release Reconciliation](../RELEASE.md#cli-extension-co-release-reconciliation).

After the PR merges, update `main` in your main worktree (git does not let you
check out `main` in the release worktree):

```bash
git fetch origin
git pull --ff-only origin main
```

Create one tag per released package. Versions are per package; do not assume one
shared version:

```bash
git tag Auth-v<auth-version>
git tag Cli-v<cli-version>
git tag Dataverse-v<dataverse-version>
git tag Mcp-v<mcp-version>
git tag Migration-v<migration-version>
git tag Query-v<query-version>
git tag Plugins-v<plugins-version>
git tag Extension-v<extension-version>   # same commit as the Cli tag

# Check the prefixes before pushing; they must match each csproj's <MinVerTagPrefix>
git tag -l 'Auth-v*' | tail -3
git tag -l 'Cli-v*' | tail -3
# ...and so on for each prefix
```

Push **one tag at a time** (see
[Large tag batches suppress GitHub Actions events](#large-tag-batches-suppress-github-actions-events)),
in dependency waves, and wait for each wave's public verification (Step 10)
before starting the next:

1. Auth, Dataverse, Plugins (independent roots; may run concurrently)
2. Query, Migration
3. CLI (NuGet package and GitHub binaries)
4. MCP
5. Extension (same reviewed commit as CLI)

```bash
git push origin "refs/tags/Auth-v<auth-version>"
# Continue through the reviewed version map only after the preceding wave verifies.
```

The Extension tag push publishes to its inferred channel by itself. Do not also
dispatch `extension-publish.yml` for the same version.

Plugins: the Plugins publish path stays blocked until its signing policy is
activated; check
[Isolated Plugins signing preparation](../RELEASE.md#isolated-plugins-signing-preparation)
before planning a `Plugins-v*` tag.

#### First NuGet publish through trusted publishing

Temporary note: remove this subsection after the first successful
trusted-publishing release. `publish-nuget.yml` has used
NuGet trusted publishing (OIDC login, no stored key) since #1564 (2026-11-09), and
no package tag has been published through it yet; the newest package tags
(2026-11-02) predate it. On the first wave of the next release, push a single
tag and watch its `publish` job before pushing any other:

- `Validate trusted publishing identity` and `NuGet login` must succeed. A
  failure there means the token exchange or the nuget.org policy is wrong; no
  package has been uploaded, and there is no stored-key fallback.
- `Push packages to NuGet` and `Push symbols to NuGet` must succeed, and the
  `verify-public-artifact` job must find the version on nuget.org.

If login or upload fails, stop and see
[NuGet trusted publishing](../RELEASE.md#nuget-trusted-publishing) for what is
unverified about the policy before changing anything or rerunning.

**Worked example** from PR #785 (2026-04-17 prerelease):

```bash
git tag Auth-v1.0.0-beta.8
git tag Cli-v1.0.0-beta.14
git tag Dataverse-v1.0.0-beta.7
git tag Mcp-v1.0.0-beta.2
git tag Migration-v1.0.0-beta.8
git tag Query-v1.0.0-beta.2
git tag Plugins-v2.1.0-beta.1
git tag Extension-v0.7.0
```

That release pushed the tags in a loop with a three-second pause. A pause is not
a substitute for the dependency waves above; push each wave only after the
previous one verifies.

#### Unified v* Tag (minor/stable releases only)

After the per-package tags, a coordinated minor or stable release pushes one
unified tag, which triggers `docs-release.yml`:

```bash
git tag v<version>   # e.g. v1.1.0
git push origin "refs/tags/v<version>"
```

| Release type | Unified tag? | Example | Effect |
|---|---|---|---|
| Minor (coordinated) | **Yes**, `vX.Y.0` | `v1.1.0` | `docs-release.yml` regenerates reference docs and opens the ppds-docs PR |
| Stable (coordinated) | **Yes**, `vX.Y.0` | `v1.0.0` | Same as minor |
| Prerelease (coordinated) | **Skip**, or dispatch a dry run | none | Pushing `v*-beta.N` runs the **real** `docs-release.yml` and opens a ppds-docs PR. For a preview use `gh workflow run docs-release.yml -f dry_run=true`. |
| Patch | **No** | none | Per-package tags only; see below for a coordinated patch |

How the unified tag relates to the per-package tags is in the
[Release model](../RELEASE.md#release-model-tags-channels-and-lineage). What the
docs workflow does next, and the order in which to
review and merge its ppds-docs PR and its API-baseline rollover PR, is in
[docs/runbooks/docs-release.md](docs-release.md).

**Docs for a coordinated patch.** Per-product tags such as `Cli-v*` and
`Extension-v*` do not trigger `docs-release.yml`, which listens only for `v*`
tags and manual dispatch. Do not invent a unified version tag to trigger docs.
After the release/docs approval, pick a co-located product tag whose peeled
commit equals the reviewed release commit and dispatch a dry run against it:

```bash
gh workflow run docs-release.yml --ref <reviewed-product-release-tag> -f dry_run=true
```

Review the generated artifacts, then separately authorize the `dry_run=false`
dispatch that opens the paired docs and API-rollover PRs. Check the existing
rollover-PR gate and review both outputs as in Step 9 item 4. Recapture companion skills against the
same final CLI/MCP versions. A successful product publish does not by itself mean
the docs or skills were refreshed.

### 9. Monitor CI Workflow Runs

Which tag starts which workflow is in the
[tag table](../RELEASE.md#release-model-tags-channels-and-lineage). Watch the runs:

```bash
gh run list --limit 20 --json status,conclusion,createdAt,displayTitle,workflowName
```

Expected sequence (timings are estimates; advance only after verification):

1. Each component tag starts its own `publish-nuget.yml` run, about 3-5 minutes.
   Work through the dependency waves from Step 8.
2. The CLI tag also starts `release-cli.yml`, which builds the five native
   binaries on Windows, macOS and Linux runners, about 8-15 minutes. Both runs
   must finish and verify before the next wave.
3. `extension-publish.yml` runs on the `Extension-v*` tag push, about 10-15
   minutes, with the channel inferred from the minor version.
4. `docs-release.yml` runs on the unified `v*` tag if you pushed one. It opens
   the generated-reference PR in ppds-docs, and opens the core API-baseline
   rollover PR only if baseline files change (`scripts/docs-gen/open-rollover-pr.sh`
   skips the PR when there are no Unshipped entries to roll); otherwise record
   the verified no-op. Review any rollover before merging the public docs
   landing, in the order given in [docs-release.md](docs-release.md).

Let `release-cli.yml` own the CLI GitHub release: do not pre-create a draft and
do not use auto-generated notes. Its draft, resume and asset rules are in
[Pre-upload release-input checks](../RELEASE.md#pre-upload-release-input-checks).

Do not assume success: check each publish separately. One surface can succeed
while another fails, and an unverified tag leaves the channels inconsistent.

### 10. Verify Publishes

Every publish workflow already ends with an automated check through the public
channel ([Automated Public Artifact Verification](../RELEASE.md#automated-public-artifact-verification)).
Confirm the results yourself as well.

Stable releases require full verification of every check below before the
release is called done. Prereleases tolerate a gotcha being caught after
publication; stable releases should not.

#### NuGet.org

```bash
# The v3-flatcontainer path segment MUST be lowercase (ppds.auth, ppds.cli),
# even though the PackageId is PPDS.<Name>. Mixed case returns 404.
curl -s https://api.nuget.org/v3-flatcontainer/<package-id-lowercase>/index.json | jq '.versions' | tail -5
```

```
https://api.nuget.org/v3-flatcontainer/ppds.auth/index.json   # correct
https://api.nuget.org/v3-flatcontainer/PPDS.Auth/index.json   # 404
```

Or open `https://www.nuget.org/packages/PPDS.<Package>/`.

#### CLI GitHub Release

```bash
gh release view Cli-v<cli-version>
```

- Five binaries attached (`ppds-win-x64.exe`, `ppds-win-arm64.exe`,
  `ppds-osx-x64`, `ppds-osx-arm64`, `ppds-linux-x64`) plus `checksums.sha256`
- Release notes taken from the CLI CHANGELOG section

#### VS Code Marketplace

Open `https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite`:

- The latest version matches the tag
- The channel is right (pre-release or stable)
- The changelog tab shows the new entry
- The `displayName` and README render correctly; the Marketplace strips some
  Markdown that GitHub renders

#### Smoke test installs

```bash
# CLI tool
dotnet tool install -g PPDS.Cli --version <cli-version>
ppds --version
dotnet tool uninstall -g PPDS.Cli

# A library resolves (in a throwaway project)
dotnet add package PPDS.Auth --version <X.Y.Z>

# Extension
code --install-extension JoshSmithXRM.power-platform-developer-suite --pre-release  # pre-release channel
code --install-extension JoshSmithXRM.power-platform-developer-suite                # stable
# Open VS Code: the sidebar loads, the version matches, basic commands work
```

Also download a CLI binary from the GitHub release, run `./ppds --version` and
confirm it matches the tag.

If a smoke check fails, file a hotfix issue and prepare a patch release. Do not
alter the existing release; publish a new one.

### 11. Create GitHub Releases for the Other Surfaces

Only the CLI gets a GitHub release automatically. Every other released surface
(NuGet libraries and the Extension) needs one created by hand from its CHANGELOG
section. For each released `<Prefix>-vX.Y.Z` **except `Cli`**:

1. Copy the `## [X.Y.Z]` section of `src/PPDS.<Surface>/CHANGELOG.md` (from the
   version heading to the next `## [` heading) into a notes file.
2. Create the release from that file:

```bash
gh release create <Prefix>-vX.Y.Z \
  --title "PPDS.<Surface> X.Y.Z" \
  --notes-file <notes.md> \
  --verify-tag --latest=false
```

Existing releases title the Extension `PPDS Extension X.Y.Z`. `--latest=false`
keeps library and Extension releases from taking the repository's "Latest" badge
from the CLI. Pass the notes with `--notes-file`, not inline: CHANGELOG bodies
contain backticks and command examples that a shell would interpret.

Then check that every pushed tag has a release:

```bash
for t in <every tag you pushed>; do
  gh release view "$t" >/dev/null 2>&1 && echo "$t OK" || echo "$t MISSING"
done
```

Finally, complete the post-publication contributor check from Step 3 against the
published notes.

## Patch Release Procedure

A focused patch (one bug fix or security fix) does not need the full process
above. Use this abbreviated flow when the scope advisory confirms a single-package
target. [specs/release-cycle.md](../../specs/release-cycle.md) is the policy for
when a `release:patch` label warrants a patch release.

**When to use:**

- The advisory's release targets are one NuGet package; prerequisite-only tags
  (for example Dataverse for a Query patch) do not count as extra targets
- The change is a bug fix or security fix, not a feature
- The merged PR is labeled `release:patch`

For a patch with several release targets, or any patch that touches the
Extension, use the full process in Steps 1-11. A `Cli-v*` patch is also subject
to the co-release rule in Step 8.

**Steps (abbreviated):**

1. Run the Step 2 release advisory for the merged PR. Review direct changes,
   downstream deliverables and same-commit MinVer prerequisites separately.
2. Update the CHANGELOG of each release target. A prerequisite-only package may
   say "No user-facing changes; version consistency for stable dependencies."
3. Open a focused CHANGELOG PR, merge it, then rerun the advisory on the final
   commit.
4. Push the reviewed target and prerequisite tags individually. For example, a
   stable Query patch can need both a Dataverse prerequisite tag and a Query tag:
   ```bash
   git tag Dataverse-v<X.Y.Z>
   git push origin "refs/tags/Dataverse-v<X.Y.Z>"
   git tag Query-v<X.Y.Z>
   git push origin "refs/tags/Query-v<X.Y.Z>"
   ```
5. Monitor every workflow run those tags start. **A `Cli-v*` tag also starts
   `release-cli.yml`**; watch both.
6. Verify the publish (Step 10) and create the GitHub release for each non-CLI
   target (Step 11).

**No full release PR is required for a focused patch**: the original fix PR and
the focused CHANGELOG PR are the audit trail.

## Stabilization Branch

Stabilization branches (`release/X.Y`) are an **escape hatch**, used only when
development for the next minor (`X.(Y+1)`) has started on `main` before `X.Y` is
verified and shipped. For a solo maintainer this is the rare exception. The
default is to tag from `main`.

**When to create one:**

- Milestone `vX.Y.0` is feature-complete on `main`, but unrelated work for
  `vX.(Y+1).0` must keep merging before `vX.Y.0` is verified
- A stable release is soaking and needs bug fixes without freezing `main`

**When not to:**

- The common case: tag directly from `main`, with no branch and no merge-back
- Patches: use the [Patch Release Procedure](#patch-release-procedure)

**Create it** at the commit where the milestone is feature-complete, typically the
merge commit of the milestone's last PR:

```bash
git checkout main && git pull
git checkout -b release/X.Y <feature-complete-commit>
git push -u origin release/X.Y
```

**What goes on it:** bug-fix cherry-picks only; no features, no refactors. Use
`-x` so each commit records the original `main` commit, which the merge-back
relies on:

```bash
git checkout release/X.Y
git cherry-pick -x <commit-sha-from-main>
git push origin release/X.Y
```

Pushing to `release/X.Y` starts no CI: `build.yml` and `test.yml` run only for
pushes to `main` and pull requests whose base is `main`. Run the Step 6 gates
locally on the branch before tagging. To get CI as well, open a draft PR from
`release/X.Y` to `main` and do not merge it until the merge-back below; its
checks are path-selected as described in Step 6.

**Tag from it**, not from `main`. The publish workflows run from the tag
regardless of branch:

```bash
git checkout release/X.Y
git pull
git tag <Prefix>-v<X.Y.Z>
git push origin "refs/tags/<Prefix>-v<X.Y.Z>"
```

**Merge back to main** after the release publishes, so cherry-picked fixes do not
drift:

```bash
git checkout main && git pull
git merge --no-ff release/X.Y -m "merge: release/X.Y back into main after vX.Y.Z"
# Resolve conflicts by hand; favor main's version where files diverged
git push origin main
```

If every commit on `release/X.Y` was cherry-picked from `main`, the merge is empty
and you can delete the branch. Divergence (usually a fix made on the branch first
and never on `main`) is resolved case by case.

**Clean up** once `main` has every fix; these branches are deliberately
short-lived:

```bash
git branch -d release/X.Y
git push origin :release/X.Y
```

Afterwards, when working out what shipped, trust the tag rather than the branch
([Tags, not release branches, record what shipped](../RELEASE.md#tags-not-release-branches-record-what-shipped)).

## Known Gotchas

### `release-cli.yml` fails with HTTP 422 "tag_name was used by an immutable release"

**Symptom:** `release-cli.yml` fails while publishing the release with:

```
HTTP 422: Validation Failed
tag_name was used by an immutable release
```

**Diagnosis:** inspect the exact tag, workflow run and public or draft release
state. The input guard rejects already-published versions and drafts whose
source, workflow ownership, notes or assets cannot be verified. A failed read is
not proof that no release exists.

**Recovery:** preserve the existing tags, assets and failed run. Resume a known
owned draft only when the guard verifies it; otherwise stop and obtain a deliberate
recovery decision from the maintainer. Do not delete a draft, move or re-push a tag, or replace assets as a
routine fix. Let the workflow own the release lifecycle.

### Extension publish: manual dispatch needs `--ref`

`extension-publish.yml` runs on every `Extension-v*` tag push and infers the
channel from the minor version. To override the channel or do a dry run, dispatch
it against the tag:

```bash
gh workflow run extension-publish.yml --ref Extension-v<version> -f dry_run=false -f channel=stable
# Pre-release channel: -f channel=pre-release
```

A manual dispatch defaults to `dry_run=true`. Without `--ref` it runs on `main`
and fails the job's `refs/tags/Extension-v*` ref check.

### MinVer produces the wrong version if the tag prefix is off

**Symptom:** a package publishes with the wrong (often stale) version.

**Cause:** MinVer reads `<MinVerTagPrefix>` from the csproj and only considers
tags with that prefix. If you push `Cli-v1.0.0` but the csproj said `CLI-v`,
MinVer falls back to older tags.

**Fix:** tag with exactly the prefixes in the
[Release model](../RELEASE.md#release-model-tags-channels-and-lineage); they are
case-sensitive on Linux runners. Do not invent a new prefix without changing the matching csproj. To see recent prefix
changes: `git log --oneline --grep='MinVerTagPrefix'`.

### Build or configuration fixes needed mid-release

Releases have needed inline build fixes; the 2026-04-17 prerelease (#785) had to
set `NuGetAuditMode=direct` in `Directory.Build.props` to unblock CI.

**Prevention:** run all the Step 6 gates locally, then open the release PR as a
draft and let its CI finish before marking it ready (Step 7), so these surface
before the merge. That CI is path-selected and is not a full run (Step 6).

### Large tag batches suppress GitHub Actions events

**Symptom:** after `git push origin --tags`, no `publish-nuget.yml` or
`release-cli.yml` runs appear.

**Cause:** GitHub does not create push events for tags when more than three tags
are pushed at once
([GitHub's push-event documentation](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#push)).

**Fix:** push tags individually in the dependency order from Step 8. A delay is
not a substitute for waiting for the preceding wave's public verification.

### MSYS2 path conversion breaks `/p:` MSBuild properties

**Symptom:** `dotnet pack` for Plugins fails with MSB1008 in a `shell: bash` step
on `windows-latest`.

**Cause:** Git Bash (MSYS2) rewrites `/p:PropertyName=value` into a Windows path
such as `C:/msys64/p:PropertyName=value`.

**Fix:** use `-p:` instead of `/p:` for MSBuild properties in bash steps; it is
equivalent and not path-converted. Fixed in the workflows by PR #884.

## Rollback and Recovery

### A tag published a broken package

Stop downstream distribution and inspect the exact public artifacts and the
failed phase. Preserve successful immutable versions, tags and evidence. Prepare
a reviewed fix-forward patch. Deleting, unlisting or any other exceptional
recovery needs a separate, explicit decision; it is never a routine release step.

### A publish or signing job failed

Do not retag. Investigate the failure and fix the credential where it lives:

- **NuGet, tag on a commit that includes #1564:** there is no NuGet secret. A
  login or upload failure points at the trusted-publishing policy on nuget.org or
  the workflow's identity
  ([NuGet trusted publishing](../RELEASE.md#nuget-trusted-publishing)).
- **NuGet, tag on an older commit:** a rerun executes that tag's historical
  publisher, which reads the `NUGET_API_KEY` repository secret.
- **Extension:** the `ADO_MARKETPLACE_PAT` repository secret.
- **Plugins signing:** the `PLUGINS_SNK_BASE64` repository secret, once the
  signing policy is active.

Each credential is listed in
[Signing and credentials](../RELEASE.md#signing-and-credentials). Then rerun the
workflow on the existing tag:

```bash
gh run rerun <run-id>
```

Rerun the whole run rather than a single NuGet publisher job, and remember that a
rerun executes the workflow as it was at that tag; both behaviors are explained in
[Publishing pipeline](../RELEASE.md#publishing-pipeline) and
[NuGet trusted publishing](../RELEASE.md#nuget-trusted-publishing).

### Fix-forward

Bump the patch version (for example `1.0.0` to `1.0.1`), write a new CHANGELOG
entry, and run the [Patch Release Procedure](#patch-release-procedure) for the
affected package.

### Never

- Force-push, move or rewrite a published tag. Consumers have already pulled it,
  and package indexes and caches break.
- Reuse a version number for different content. SemVer consumers treat the tag
  and version as immutable identity.
- Skip CI monitoring and public verification.
- Commit a strong-name `.snk` file
  ([Strong-Name Keys](../RELEASE.md#strong-name-keys)).
- Expose a release secret anywhere outside the repository's Actions secrets
  ([Signing and credentials](../RELEASE.md#signing-and-credentials)).

## Post-Release Cleanup

After every publish verifies:

1. **Announce**: GitHub releases (Step 11), optionally social or blog.
2. **Remove the release worktree and branch** (match the names from Step 1):
   ```bash
   # Prerelease: .worktrees/release-YYYY-MM-DD + release/prerelease-YYYY-MM-DD
   # Stable:     .worktrees/release-vX.Y.Z     + release/vX.Y.Z
   git worktree remove .worktrees/release-YYYY-MM-DD
   git branch -d release/prerelease-YYYY-MM-DD
   ```
3. **File follow-up issues** found during verification (for example
   "marketplace listing missing icon") against the `v<next>` milestone.
4. **Update any local release plan** (for example `.plans/v*-release-plan.md`,
   which is git-ignored) with actual versus planned dates and learnings.
5. **Fix this runbook** if any step was wrong, missing or ambiguous.

## References

- Release model and policy: [docs/RELEASE.md](../RELEASE.md)
- Release policy spec: [specs/release-cycle.md](../../specs/release-cycle.md)
- Docs-site release: [docs/runbooks/docs-release.md](docs-release.md)
- Workflows: `.github/workflows/publish-nuget.yml`,
  `.github/workflows/release-cli.yml`, `.github/workflows/extension-publish.yml`,
  `.github/workflows/docs-release.yml`
- Previous prerelease: PR #785 (2026-04-17), commit `5bb4d5b90`
- CHANGELOG format: [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/)
- Versioning: [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html)
