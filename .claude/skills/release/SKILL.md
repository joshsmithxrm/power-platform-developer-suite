---
name: release
description: Cut a PPDS release - CHANGELOG refresh, version bump, tag push sequence, CI monitoring, post-publish verification. Use when preparing a new prerelease or stable release across CLI, TUI, MCP, Extension, and NuGet libraries.
---

# Release

Coordinated multi-surface release: NuGet libraries, CLI/TUI binaries, VS Code extension, MCP server. The pipeline tags + GitHub Actions does the actual publishing.

## Phase Registration

```bash
python scripts/workflow-state.py set phase release
```

## Process

### 1. Pre-flight

Read REFERENCE.md §1 "Channel layout" before deciding stable vs prerelease vs preview.
Read REFERENCE.md §3 "Version-bump matrix" before choosing the new version.

```bash
git status                              # working tree clean
git pull --ff-only origin main          # caught up
gh pr list --state open --label release  # any in-flight release PRs?
gh run list --workflow=ci.yml --limit 5  # recent CI status
```

### 2. CHANGELOG Refresh

Update `CHANGELOG.md` for the new version. Sections: Added, Changed, Fixed, Deprecated, Removed, Security.

Read REFERENCE.md §4 "CHANGELOG format" for the empty-section rule and entry style.

```bash
# Generate the comparison range
git log v<previous>..HEAD --oneline --no-merges
```

For each commit, decide which section it belongs in. Drop empty sections from the published changelog.

### 3. Version Bump

Files to update:

- `Directory.Build.props` (Version property for all .NET projects)
- `src/PPDS.Extension/package.json` (version field)
- Any other manifest files containing the version

```bash
# .NET projects
sed -i 's|<Version>.*</Version>|<Version>X.Y.Z</Version>|' Directory.Build.props

# Extension
cd src/PPDS.Extension && npm version X.Y.Z --no-git-tag-version && cd -
```

### 4. Commit Version Bump

```bash
git add CHANGELOG.md Directory.Build.props src/PPDS.Extension/package.json
git commit -m "release: X.Y.Z"
```

The commit message must start with `release:` - the publish workflow keys off this.

### 5. Tag

Read REFERENCE.md §2 "Signing matrix" if any signing inputs need preparation.

```bash
git tag v<X.Y.Z> -m "release: X.Y.Z"
git push origin main
git push origin v<X.Y.Z>
```

The tag push triggers `.github/workflows/publish.yml`. CI signs, packs, and uploads to NuGet, the VS Code marketplace, and the GitHub releases page.

### 6. Monitor CI

```bash
gh run watch
```

Pass: green publish workflow.
Fail: investigate per REFERENCE.md §5 "Platform-specific notes" (signing certs, marketplace token, etc.) - do NOT manually re-tag.

### 7. Post-publish Verification

```bash
# NuGet packages live
gh api "repos/joshsmithxrm/ppdsw-ppds/releases/latest" --jq '.assets[].name'

# IMPORTANT: the v3-flatcontainer path segment MUST be lowercase (e.g. ppds.auth, ppds.cli),
# regardless of how the package is cased on NuGet. Bug if the URL contains uppercase. <!-- enforcement: T3 -->
curl -s https://api.nuget.org/v3-flatcontainer/ppds.auth/index.json | jq '.versions | last'

# Extension marketplace
# (manual: visit marketplace listing, confirm new version)

# CLI install
ppds --version  # should match the new tag
```

### 8. Workflow State

```bash
python scripts/workflow-state.py set release.published now
python scripts/workflow-state.py set release.version <X.Y.Z>
```

### 9. Announce

Update GitHub release notes with CHANGELOG content. Optional: short post-mortem in the release commit if anything was tricky (file under `.retros/release-X.Y.Z.md`).

## Continue with

- `/cleanup` to prune stale worktrees on shipped releases.
- `/retro` (interactive) to capture release-process learnings.

## References

- `.claude/skills/release/REFERENCE.md` - channel layout, signing matrix, version-bump matrix, CHANGELOG format, platform-specific notes, NEVER list. <!-- enforcement: T3 -->
- `.github/workflows/publish.yml` - the publish workflow keyed off release: prefix.
- `Directory.Build.props` - canonical version source for .NET projects.
