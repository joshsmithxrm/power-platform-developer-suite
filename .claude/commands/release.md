# Release Command

Manage the release process for PPDS packages. This command handles changelog updates and GitHub release creation.

## Packages

| Package | Tag Format | Changelog |
|---------|------------|-----------|
| PPDS.Plugins | `Plugins-v{version}` | `src/PPDS.Plugins/CHANGELOG.md` |
| PPDS.Dataverse | `Dataverse-v{version}` | `src/PPDS.Dataverse/CHANGELOG.md` |
| PPDS.Migration | `Migration-v{version}` | `src/PPDS.Migration/CHANGELOG.md` |
| PPDS.Auth | `Auth-v{version}` | `src/PPDS.Auth/CHANGELOG.md` |
| PPDS.Cli | `Cli-v{version}` | `src/PPDS.Cli/CHANGELOG.md` |

## Workflow

Execute the following steps in order:

### Step 1: Detect State

1. **Check current branch** - Note if on `main` or a feature branch
2. **Scan changelogs** - For each package, check if `[Unreleased]` section has content
3. **Check existing tags** - Run `git tag --list "{Package}-v*"` for each package to find current versions
4. **Find untagged versions** - Check if any changelog has a version header without a corresponding git tag

### Step 2: Report Findings

Present a summary table to the user:

```
## Release Status

| Package | Current Tag | Unreleased Changes | Action Needed |
|---------|-------------|-------------------|---------------|
| Auth | v1.0.0-beta.3 | Yes | Prep changelog |
| Cli | v1.0.0-beta.5 | Yes | Prep changelog |
| Plugins | v1.0.0 | No | None |
| ... | ... | ... | ... |
```

### Step 3: Changelog Prep (if unreleased changes exist)

For each package with unreleased changes:

1. **Determine current version** from latest git tag (e.g., `Auth-v1.0.0-beta.3`)
2. **Recommend next version:**
   - If prerelease (alpha/beta/rc): increment prerelease number (beta.3 → beta.4)
   - If stable: analyze changelog content:
     - Has "BREAKING" → major bump (1.0.0 → 2.0.0)
     - Has "Added" features → minor bump (1.0.0 → 1.1.0)
     - Only "Fixed" → patch bump (1.0.0 → 1.0.1)
3. **Present recommendation and ask user to confirm or specify different version**
4. **Update changelog:**
   - Insert `## [X.Y.Z] - YYYY-MM-DD` header after `## [Unreleased]`
   - Keep `## [Unreleased]` section (now empty) at top
5. **Commit changes** with message: `docs: update changelogs for {versions} release`

Example changelog update:
```markdown
## [Unreleased]

## [1.0.0-beta.4] - 2026-01-03

### Changed
- ... (moved from Unreleased)
```

### Step 4: Create GitHub Releases (if on main with prepped changelogs)

**Prerequisites:**
- Must be on `main` branch
- Must have changelog versions without corresponding git tags
- Changelogs must be committed and pushed

For each package with untagged versions:

1. **Extract release notes** from changelog (content between version header and next version header)
2. **Create GitHub release:**

   **For CLI package** (creates draft - see [ADR-0023](../../docs/adr/0023_CLI_BINARY_RELEASE_PROCESS.md)):
   ```bash
   gh release create Cli-v{version} \
     --title "PPDS CLI v{version}" \
     --notes "{changelog_content}" \
     --prerelease \  # only if version contains -alpha, -beta, or -rc
     --draft
   ```
   Report: "Draft release created. The release-cli.yml workflow will add binaries and publish."

   **For all other packages** (published immediately):
   ```bash
   gh release create {Package}-v{version} \
     --title "PPDS.{Package} v{version}" \
     --notes "{changelog_content}" \
     --prerelease  # if version contains -alpha, -beta, or -rc
   ```
3. **Report success** with link to release

### Step 5: Post-Release Summary

Show final summary:
- Releases created with links
- Next steps (if any)
- Reminder about NuGet publishing (automated via workflow on tag push)

## Edge Cases

1. **On feature branch after prep**: Skip create phase, remind user to merge then run again
2. **No changes detected**: Report "No packages have unreleased changes"
3. **Mixed state (some prepped, some not)**: Handle each package independently
4. **User specifies packages**: Only process those packages (e.g., if user says "just auth and cli")

## CLI Release Special Handling

The CLI package requires special handling because GitHub releases become immutable after publishing. See [ADR-0023](../../docs/adr/0023_CLI_BINARY_RELEASE_PROCESS.md) for details.

When the `Cli-v*` tag is pushed:

1. `publish-nuget.yml` publishes the CLI as a .NET tool to NuGet.org
2. `release-cli.yml` builds self-contained binaries and handles the release

**Draft-first flow (recommended via `/release`):**
1. `/release` creates a **draft** release with changelog notes
2. Tag push triggers `release-cli.yml`
3. Workflow uploads binaries to the draft release
4. Workflow publishes the release (removes draft status)

**Fallback flow (manual tag push):**
If a tag is pushed without a pre-existing draft release, the workflow creates the release with auto-generated notes and attaches binaries.

**Important:** Do NOT publish CLI releases manually via GitHub UI. The workflow handles publishing after binaries are attached.

## Example Session

```
User: /release

Claude: Scanning packages for release status...

## Release Status

| Package | Current Tag | Unreleased Changes | Action |
|---------|-------------|-------------------|--------|
| Auth | v1.0.0-beta.3 | Yes | Prep |
| Cli | v1.0.0-beta.5 | Yes | Prep |
| Dataverse | v1.0.0-beta.2 | No | - |
| Migration | v1.0.0-beta.1 | No | - |
| Plugins | v1.0.0 | No | - |

**2 packages have unreleased changes.**

### Recommended Versions

| Package | Current | Recommended | Reason |
|---------|---------|-------------|--------|
| Auth | 1.0.0-beta.3 | 1.0.0-beta.4 | Prerelease increment |
| Cli | 1.0.0-beta.5 | 1.0.0-beta.6 | Prerelease increment |

Do you want to proceed with these versions, or specify different ones?

User: looks good, proceed

Claude: Updating changelogs...
- Updated src/PPDS.Auth/CHANGELOG.md: [1.0.0-beta.4] - 2026-01-03
- Updated src/PPDS.Cli/CHANGELOG.md: [1.0.0-beta.6] - 2026-01-03

Committing changes...
[docs/feature-branch abc1234] docs: update changelogs for Auth-v1.0.0-beta.4, Cli-v1.0.0-beta.6 release

✅ Changelogs updated. Next steps:
1. Push this branch and merge to main
2. Run `/release` again on main to create GitHub releases
```

**After merge, on main:**

```
User: /release

Claude: Scanning packages for release status...

## Release Status

| Package | Changelog Version | Git Tag Exists | Action |
|---------|-------------------|----------------|--------|
| Auth | 1.0.0-beta.4 | No | Create release |
| Cli | 1.0.0-beta.6 | No | Create release |

**2 packages ready for release.**

Create GitHub releases for:
- PPDS.Auth v1.0.0-beta.4 (prerelease)
- PPDS.Cli v1.0.0-beta.6 (prerelease)

Proceed?

User: yes

Claude: Creating releases...

✅ Created: PPDS.Auth v1.0.0-beta.4
   https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Auth-v1.0.0-beta.4

✅ Created: PPDS.Cli v1.0.0-beta.6
   https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Cli-v1.0.0-beta.6

## Summary
- 2 releases created
- NuGet packages will be published automatically via GitHub Actions
- CLI binaries will be built and attached to the Cli release
```