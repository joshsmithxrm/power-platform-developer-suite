# MinVer Versioning Implementation Spec

## Overview

This document specifies the implementation of automated versioning using [MinVer](https://github.com/adamralph/minver) for the PPDS SDK monorepo, enabling independent per-package releases with git tag-driven version management.

---

## Current State

| Package | Current Version | Location |
|---------|-----------------|----------|
| PPDS.Plugins | 1.1.0 | `src/PPDS.Plugins/PPDS.Plugins.csproj` |
| PPDS.Dataverse | 1.0.0-alpha1 | `src/PPDS.Dataverse/PPDS.Dataverse.csproj` |
| PPDS.Migration | 1.0.0-alpha1 | `src/PPDS.Migration/PPDS.Migration.csproj` |
| PPDS.Migration.Cli | 1.0.0-alpha1 | `src/PPDS.Migration.Cli/PPDS.Migration.Cli.csproj` |

**Problems with current approach:**
1. Versions hardcoded in `.csproj` - easy to forget updates
2. Single release publishes ALL packages regardless of changes
3. No automated pre-release versioning
4. Tag version and package version can diverge

---

## Package Dependency Tree

```
PPDS.Plugins (independent)

PPDS.Migration.Cli
├── PPDS.Migration
│   └── PPDS.Dataverse
└── PPDS.Dataverse
```

**Key Insight:** PPDS.Migration and PPDS.Migration.Cli are tightly coupled and should share versions. They will be released together as a unit.

---

## Proposed Versioning Strategy

### Package Groups

| Group | Packages | Tag Prefix | Rationale |
|-------|----------|------------|-----------|
| **Plugins** | PPDS.Plugins | `Plugins-v` | Stable, independent, rarely changes |
| **Dataverse** | PPDS.Dataverse | `Dataverse-v` | Core library, independent release cycle |
| **Migration** | PPDS.Migration, PPDS.Migration.Cli | `Migration-v` | Tightly coupled, release together |

### Tag Format

```
{PackageGroup}-v{Major}.{Minor}.{Patch}[-{PreRelease}]
```

**Examples:**
```
Plugins-v1.1.0          → PPDS.Plugins 1.1.0
Dataverse-v1.2.0        → PPDS.Dataverse 1.2.0
Dataverse-v1.2.0-alpha  → PPDS.Dataverse 1.2.0-alpha
Migration-v1.0.0        → PPDS.Migration 1.0.0 + PPDS.Migration.Cli 1.0.0
Migration-v1.0.0-beta.1 → PPDS.Migration 1.0.0-beta.1 + PPDS.Migration.Cli 1.0.0-beta.1
```

### Pre-Release Versioning

MinVer automatically generates pre-release versions based on commits since the last tag:

| Scenario | Result |
|----------|--------|
| Tagged `Dataverse-v1.2.0` | 1.2.0 |
| 3 commits after tag | 1.2.1-alpha.0.3 (auto-generated) |
| Tagged `Dataverse-v1.3.0-beta.1` | 1.3.0-beta.1 |

**Explicit pre-release tags** should follow this convention:
- Alpha: `Dataverse-v1.2.0-alpha.1`
- Beta: `Dataverse-v1.2.0-beta.1`
- Release Candidate: `Dataverse-v1.2.0-rc.1`

---

## Implementation Changes

### 1. Add MinVer Package References

**Create `Directory.Build.props` in repo root:**

```xml
<Project>
  <PropertyGroup>
    <!-- Shared settings for all projects -->
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

**Add MinVer to each publishable project:**

```xml
<!-- PPDS.Plugins.csproj -->
<ItemGroup>
  <PackageReference Include="MinVer" Version="6.0.0" PrivateAssets="all" />
</ItemGroup>
<PropertyGroup>
  <MinVerTagPrefix>Plugins-v</MinVerTagPrefix>
  <MinVerDefaultPreReleaseIdentifiers>alpha.0</MinVerDefaultPreReleaseIdentifiers>
</PropertyGroup>
```

```xml
<!-- PPDS.Dataverse.csproj -->
<ItemGroup>
  <PackageReference Include="MinVer" Version="6.0.0" PrivateAssets="all" />
</ItemGroup>
<PropertyGroup>
  <MinVerTagPrefix>Dataverse-v</MinVerTagPrefix>
  <MinVerDefaultPreReleaseIdentifiers>alpha.0</MinVerDefaultPreReleaseIdentifiers>
</PropertyGroup>
```

```xml
<!-- PPDS.Migration.csproj AND PPDS.Migration.Cli.csproj -->
<ItemGroup>
  <PackageReference Include="MinVer" Version="6.0.0" PrivateAssets="all" />
</ItemGroup>
<PropertyGroup>
  <MinVerTagPrefix>Migration-v</MinVerTagPrefix>
  <MinVerDefaultPreReleaseIdentifiers>alpha.0</MinVerDefaultPreReleaseIdentifiers>
</PropertyGroup>
```

### 2. Remove Hardcoded Versions

**Remove from each `.csproj`:**
```xml
<!-- DELETE THIS LINE -->
<Version>1.0.0-alpha1</Version>
```

MinVer will set `Version`, `PackageVersion`, `AssemblyVersion`, and `FileVersion` automatically.

### 3. Update CI/CD Workflow

**Replace `publish-nuget.yml` with per-package release support:**

```yaml
name: Publish to NuGet

on:
  push:
    tags:
      - 'Plugins-v*'
      - 'Dataverse-v*'
      - 'Migration-v*'

jobs:
  publish:
    runs-on: windows-latest

    steps:
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0  # Required for MinVer to read git history

    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: |
          8.0.x
          10.0.x

    - name: Determine package to publish
      id: package
      shell: bash
      run: |
        TAG=${GITHUB_REF#refs/tags/}
        echo "tag=$TAG" >> $GITHUB_OUTPUT

        if [[ $TAG == Plugins-v* ]]; then
          echo "package=PPDS.Plugins" >> $GITHUB_OUTPUT
          echo "projects=src/PPDS.Plugins/PPDS.Plugins.csproj" >> $GITHUB_OUTPUT
        elif [[ $TAG == Dataverse-v* ]]; then
          echo "package=PPDS.Dataverse" >> $GITHUB_OUTPUT
          echo "projects=src/PPDS.Dataverse/PPDS.Dataverse.csproj" >> $GITHUB_OUTPUT
        elif [[ $TAG == Migration-v* ]]; then
          echo "package=PPDS.Migration" >> $GITHUB_OUTPUT
          echo "projects=src/PPDS.Migration/PPDS.Migration.csproj src/PPDS.Migration.Cli/PPDS.Migration.Cli.csproj" >> $GITHUB_OUTPUT
        else
          echo "Unknown tag format: $TAG"
          exit 1
        fi

    - name: Show version info
      shell: bash
      run: |
        echo "Tag: ${{ steps.package.outputs.tag }}"
        echo "Package: ${{ steps.package.outputs.package }}"
        echo "Projects: ${{ steps.package.outputs.projects }}"

    - name: Restore dependencies
      run: dotnet restore

    - name: Build
      run: dotnet build --configuration Release --no-restore

    - name: Pack specific projects
      shell: bash
      run: |
        mkdir -p ./nupkgs
        for project in ${{ steps.package.outputs.projects }}; do
          echo "Packing $project"
          dotnet pack "$project" --configuration Release --no-build --output ./nupkgs
        done

    - name: List packages
      shell: bash
      run: ls -la ./nupkgs/

    - name: Push packages to NuGet
      shell: bash
      env:
        NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
      run: |
        for package in ./nupkgs/*.nupkg; do
          echo "Pushing $package"
          dotnet nuget push "$package" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate
        done

    - name: Push symbols to NuGet
      shell: bash
      env:
        NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
      run: |
        for package in ./nupkgs/*.snupkg; do
          echo "Pushing $package"
          dotnet nuget push "$package" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate
        done
      continue-on-error: true
```

---

## Release Process

### Creating a Release

**Step 1: Create and push the tag**
```bash
# For PPDS.Plugins
git tag Plugins-v1.2.0
git push origin Plugins-v1.2.0

# For PPDS.Dataverse
git tag Dataverse-v1.3.0
git push origin Dataverse-v1.3.0

# For PPDS.Migration (includes CLI)
git tag Migration-v1.1.0
git push origin Migration-v1.1.0

# For pre-release
git tag Dataverse-v1.3.0-beta.1
git push origin Dataverse-v1.3.0-beta.1
```

**Step 2: CI/CD automatically:**
1. Detects the tag pattern
2. Builds the specific package(s)
3. MinVer reads the tag and sets the version
4. Packs only the relevant package(s)
5. Pushes to NuGet

**Step 3: Create GitHub Release (optional but recommended)**
- Go to GitHub → Releases → "Draft a new release"
- Select the tag you just pushed
- Add release notes (see Release Notes section below)
- Publish

### Pre-Release Workflow

```bash
# Start alpha phase
git tag Migration-v1.1.0-alpha.1
git push origin Migration-v1.1.0-alpha.1

# Iterate on alpha
git tag Migration-v1.1.0-alpha.2
git push origin Migration-v1.1.0-alpha.2

# Move to beta
git tag Migration-v1.1.0-beta.1
git push origin Migration-v1.1.0-beta.1

# Release candidate
git tag Migration-v1.1.0-rc.1
git push origin Migration-v1.1.0-rc.1

# Final release
git tag Migration-v1.1.0
git push origin Migration-v1.1.0
```

---

## Release Notes Strategy

### Single CHANGELOG.md (Recommended)

Keep the current `CHANGELOG.md` but organize by package:

```markdown
# Changelog

## [Unreleased]

### PPDS.Dataverse
- Added feature X

### PPDS.Migration
- Fixed bug Y

## PPDS.Plugins v1.2.0 - 2025-01-15

### Added
- New feature A

## PPDS.Dataverse v1.3.0 - 2025-01-10

### Added
- New feature B

### Fixed
- Bug fix C

## PPDS.Migration v1.1.0 - 2025-01-10

### Added
- CLI command Z
```

### GitHub Release Notes

When creating a GitHub Release, copy the relevant section from CHANGELOG.md:

**Example: `Dataverse-v1.3.0` Release**
```markdown
## What's New

### Added
- Bulk operations with parallel batch processing
- Connection pooling with throttle-aware routing
- TVP race condition retry (SQL 3732)
- SQL deadlock retry (SQL 1205)

### Fixed
- Connection pool leak on disposal

## Installation

```bash
dotnet add package PPDS.Dataverse --version 1.3.0
```

## Full Changelog
See [CHANGELOG.md](https://github.com/joshsmithxrm/ppds-sdk/blob/main/CHANGELOG.md)
```

---

## Version Compatibility

### Major Version Sync Rule

Per existing CLAUDE.md guidance, **major versions stay in sync across ecosystem**:

| Package | Valid Versions | Notes |
|---------|----------------|-------|
| PPDS.Plugins | 1.x.x | Stable |
| PPDS.Dataverse | 1.x.x | Must match major with Migration |
| PPDS.Migration | 1.x.x | Must match major with Dataverse |

**When to bump major version:**
- Breaking API changes in any package
- Coordinate major bumps across all packages

### Dependency Version Constraints

When PPDS.Migration references PPDS.Dataverse, use **minimum version**:

```xml
<!-- PPDS.Migration.csproj - for NuGet package reference (not ProjectReference) -->
<PackageReference Include="PPDS.Dataverse" Version="1.0.0" />
```

This means "1.0.0 or higher within major version 1.x".

---

## Migration Plan

### Phase 1: Prepare (This PR)
1. Add MinVer to all publishable projects
2. Remove hardcoded `<Version>` elements
3. Update CI/CD workflow
4. Test locally with `dotnet pack` to verify versions

### Phase 2: Initial Tags
After merging, create initial tags to establish version baseline:
```bash
git tag Plugins-v1.1.0      # Match current version
git tag Dataverse-v1.0.0    # Start fresh for stable release
git tag Migration-v1.0.0    # Start fresh for stable release
```

### Phase 3: Verify
1. Push one tag
2. Verify CI/CD triggers correctly
3. Verify NuGet package has correct version
4. Repeat for other packages

---

## Local Development

### Checking Current Version

```bash
# MinVer shows version during build
dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj -v minimal

# Or explicitly
dotnet msbuild src/PPDS.Dataverse/PPDS.Dataverse.csproj -t:MinVer
```

### Simulating a Release Locally

```bash
# Create a local tag (don't push)
git tag Dataverse-v1.5.0

# Build and pack
dotnet pack src/PPDS.Dataverse/PPDS.Dataverse.csproj -o ./nupkgs

# Check the version
ls ./nupkgs/
# Should show: PPDS.Dataverse.1.5.0.nupkg

# Delete the local tag
git tag -d Dataverse-v1.5.0
```

---

## FAQ

### Q: What if I forget to tag before releasing?
MinVer uses the last tag it can find. If no tag exists for that package prefix, it defaults to `0.0.0-alpha.0.{height}` where height is commit count. Always tag before releasing.

### Q: Can I release multiple packages at once?
Yes, push multiple tags:
```bash
git tag Dataverse-v1.3.0
git tag Migration-v1.1.0
git push origin Dataverse-v1.3.0 Migration-v1.1.0
```
Each tag triggers a separate workflow run.

### Q: What happens to existing v1.1.0 tag?
The old `v1.1.0` tag format won't match any `MinVerTagPrefix`, so it will be ignored. Leave it for historical reference or delete it.

### Q: How do I see what version will be built?
```bash
dotnet build -v minimal 2>&1 | grep "MinVer"
```

### Q: Can I override the version manually?
Yes, for emergencies:
```bash
dotnet pack -p:Version=1.2.3-hotfix
```
But prefer tagging for traceability.

---

## Changelog Strategy

### Per-Package CHANGELOG Files

Each publishable package group gets its own CHANGELOG:

```
src/
├── PPDS.Plugins/
│   └── CHANGELOG.md
├── PPDS.Dataverse/
│   └── CHANGELOG.md
└── PPDS.Migration/
    └── CHANGELOG.md      # Covers library + CLI (same release)
```

### CHANGELOG Format

Each per-package CHANGELOG follows Keep a Changelog format:

```markdown
# Changelog - PPDS.Dataverse

All notable changes to PPDS.Dataverse will be documented in this file.

## [Unreleased]

## [1.0.0] - 2025-01-XX

### Added
- Bulk operations with parallel batch processing
- Connection pooling with throttle-aware routing
- TVP race condition retry (SQL 3732)
- SQL deadlock retry (SQL 1205)

### Fixed
- Connection pool leak on disposal

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Dataverse-v1.0.0...HEAD
[1.0.0]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Dataverse-v1.0.0
```

### Root CHANGELOG.md

Convert to an index/overview file:

```markdown
# PPDS SDK Changelog Index

This repository contains multiple packages. See per-package changelogs:

- [PPDS.Plugins](src/PPDS.Plugins/CHANGELOG.md) - Plugin attributes for Dataverse
- [PPDS.Dataverse](src/PPDS.Dataverse/CHANGELOG.md) - High-performance Dataverse connectivity
- [PPDS.Migration](src/PPDS.Migration/CHANGELOG.md) - Migration library and CLI tool

For GitHub Releases with full release notes, see:
https://github.com/joshsmithxrm/ppds-sdk/releases
```

---

## Documentation Updates

### sdk/CLAUDE.md Changes

#### Update Project Structure Section

```markdown
## 📁 Project Structure

```
ppds-sdk/
├── src/
│   ├── PPDS.Plugins/
│   │   ├── CHANGELOG.md          # Package changelog
│   │   ├── Attributes/
│   │   ├── Enums/
│   │   ├── PPDS.Plugins.csproj
│   │   └── PPDS.Plugins.snk
│   ├── PPDS.Dataverse/
│   │   ├── CHANGELOG.md          # Package changelog
│   │   ├── BulkOperations/
│   │   ├── Pooling/
│   │   ├── Resilience/
│   │   └── PPDS.Dataverse.csproj
│   ├── PPDS.Migration/
│   │   ├── CHANGELOG.md          # Package changelog (covers CLI too)
│   │   └── PPDS.Migration.csproj
│   └── PPDS.Migration.Cli/
│       └── PPDS.Migration.Cli.csproj
├── tests/
├── docs/
│   ├── adr/
│   ├── architecture/
│   └── specs/
├── .github/workflows/
│   ├── build.yml
│   ├── test.yml
│   └── publish-nuget.yml
├── PPDS.Sdk.sln
└── CHANGELOG.md                   # Index pointing to per-package changelogs
```
```

#### Replace Version Management Section

```markdown
## 📦 Version Management

### MinVer (Automated Versioning)

Versions are determined automatically from git tags using [MinVer](https://github.com/adamralph/minver).

| Package Group | Tag Prefix | Example Tag |
|---------------|------------|-------------|
| PPDS.Plugins | `Plugins-v` | `Plugins-v1.2.0` |
| PPDS.Dataverse | `Dataverse-v` | `Dataverse-v1.0.0` |
| PPDS.Migration + CLI | `Migration-v` | `Migration-v1.0.0` |

**Pre-release versions:**
```bash
Dataverse-v1.0.0-alpha.1    # Alpha
Dataverse-v1.0.0-beta.1     # Beta
Dataverse-v1.0.0-rc.1       # Release candidate
Dataverse-v1.0.0            # Stable release
```

**Between tags:** MinVer auto-generates versions like `1.0.1-alpha.0.3` (3 commits after 1.0.0).

### Major Version Sync

Major versions stay in sync across ecosystem for compatibility:
- PPDS.Plugins 1.x, PPDS.Dataverse 1.x, PPDS.Migration 1.x = compatible
- Major bump in any package = coordinate across all packages
```

#### Replace Release Process Section

```markdown
## 🚀 Release Process

### Per-Package Release

1. **Update package CHANGELOG** (`src/PPDS.{Package}/CHANGELOG.md`)
2. **Merge to main**
3. **Create and push tag:**
   ```bash
   git tag Dataverse-v1.0.0
   git push origin Dataverse-v1.0.0
   ```
4. **Create GitHub Release:**
   - Go to Releases → "Draft new release"
   - Select the tag
   - Copy release notes from package CHANGELOG
   - Publish → CI publishes to NuGet

### Pre-Release Workflow

```bash
git tag Dataverse-v1.0.0-alpha.1 && git push origin Dataverse-v1.0.0-alpha.1
git tag Dataverse-v1.0.0-beta.1 && git push origin Dataverse-v1.0.0-beta.1
git tag Dataverse-v1.0.0-rc.1 && git push origin Dataverse-v1.0.0-rc.1
git tag Dataverse-v1.0.0 && git push origin Dataverse-v1.0.0
```

### Multi-Package Release

To release multiple packages:
```bash
git tag Dataverse-v1.0.0
git tag Migration-v1.0.0
git push origin Dataverse-v1.0.0 Migration-v1.0.0
```
Create separate GitHub Releases for each tag.

**Required Secret:** `NUGET_API_KEY`
```

#### Update Development Workflow Section

```markdown
## 🔄 Development Workflow

### Making Changes

1. Create feature branch from `main`
2. Make changes
3. Run `dotnet build` and `dotnet test`
4. Update the relevant package CHANGELOG (`src/PPDS.{Package}/CHANGELOG.md`)
5. Create PR to `main`
```

#### Update Key Files Section

```markdown
## 📋 Key Files

| File | Purpose |
|------|---------|
| `src/PPDS.*/CHANGELOG.md` | Per-package release notes |
| `CHANGELOG.md` | Index pointing to per-package changelogs |
| `PPDS.Plugins.snk` | Strong name key (DO NOT regenerate) |
| `.editorconfig` | Code style settings |
| `docs/specs/MINVER_VERSIONING.md` | Versioning implementation spec |
```

#### Update NEVER Section

Add to the NEVER table:

```markdown
| Manually set `<Version>` in csproj | MinVer manages versions via git tags |
```

#### Update ALWAYS Section

Update the CHANGELOG rule:

```markdown
| Update package CHANGELOG with changes | Per-package release notes in `src/PPDS.{Package}/CHANGELOG.md` |
```

### Root CLAUDE.md (ppds/) Changes

#### Update Versioning Section

```markdown
## 📦 Versioning

- All repos use SemVer
- Major versions stay in sync across ecosystem for compatibility
- Each repo has independent minor/patch versions
- **ppds-sdk uses MinVer** with per-package tags (e.g., `Plugins-v1.0.0`, `Dataverse-v1.0.0`)

### SDK Package Tags

| Package | Tag Format | Example |
|---------|------------|---------|
| PPDS.Plugins | `Plugins-v{version}` | `Plugins-v1.1.0` |
| PPDS.Dataverse | `Dataverse-v{version}` | `Dataverse-v1.0.0` |
| PPDS.Migration | `Migration-v{version}` | `Migration-v1.0.0` |
```

#### Update Coordinated Release Process

```markdown
## 🚀 Coordinated Release Process

When releasing a new major version across the ecosystem:

1. **ppds-sdk** - Create per-package tags and GitHub Releases:
   - `Plugins-v2.0.0` (if changed)
   - `Dataverse-v2.0.0`
   - `Migration-v2.0.0`
2. **ppds-tools** - Update and tag (PowerShell Gallery must publish)
3. **ppds-alm** - Tag (templates reference specific versions)
4. **ppds-demo** - Update to use new versions
5. **extension** - Update if needed
```

---

## Summary of Changes

| File | Change |
|------|--------|
| `src/PPDS.Plugins/PPDS.Plugins.csproj` | Add MinVer, remove `<Version>`, add `MinVerTagPrefix` |
| `src/PPDS.Dataverse/PPDS.Dataverse.csproj` | Add MinVer, remove `<Version>`, add `MinVerTagPrefix` |
| `src/PPDS.Migration/PPDS.Migration.csproj` | Add MinVer, remove `<Version>`, add `MinVerTagPrefix` |
| `src/PPDS.Migration.Cli/PPDS.Migration.Cli.csproj` | Add MinVer, remove `<Version>`, add `MinVerTagPrefix` |
| `.github/workflows/publish-nuget.yml` | Update to filter by tag pattern |
| `CHANGELOG.md` | Convert to index pointing to per-package changelogs |
| `src/PPDS.Plugins/CHANGELOG.md` | **NEW** - Per-package changelog |
| `src/PPDS.Dataverse/CHANGELOG.md` | **NEW** - Per-package changelog |
| `src/PPDS.Migration/CHANGELOG.md` | **NEW** - Per-package changelog (covers CLI) |
| `CLAUDE.md` (sdk) | Update Version Management, Release Process, Project Structure, Key Files, NEVER/ALWAYS rules |
| `CLAUDE.md` (root ppds) | Update Versioning section with SDK tag formats |

---

## Migration Checklist

### Pre-Implementation
- [ ] Review and approve this spec

### Implementation (Single PR)
- [ ] Add MinVer to all publishable .csproj files
- [ ] Remove hardcoded `<Version>` elements
- [ ] Update `.github/workflows/publish-nuget.yml`
- [ ] Create per-package CHANGELOG.md files (migrate content from root)
- [ ] Convert root CHANGELOG.md to index
- [ ] Update sdk/CLAUDE.md
- [ ] Update root ppds/CLAUDE.md
- [ ] Test locally: `dotnet pack` shows correct versions

### Post-Merge
- [ ] Create new tags at appropriate commits:
  - `Plugins-v1.1.0` at same commit as old `v1.1.0`
  - `Dataverse-v1.0.0` at HEAD
  - `Migration-v1.0.0` at HEAD
- [ ] Delete old `v1.1.0` tag
- [ ] Create GitHub Releases for each tag
- [ ] Verify NuGet packages published correctly

---

## References

- [MinVer GitHub](https://github.com/adamralph/minver)
- [MinVer Documentation](https://github.com/adamralph/minver#readme)
- [Semantic Versioning](https://semver.org/)
- [Keep a Changelog](https://keepachangelog.com/)
