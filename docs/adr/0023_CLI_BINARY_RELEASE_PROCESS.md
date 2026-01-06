# ADR-0023: CLI Binary Release Process

## Status

Accepted

## Context

The PPDS CLI is distributed as:
1. A .NET tool via NuGet (`dotnet tool install ppds`)
2. Self-contained binaries via GitHub releases (for users without .NET SDK)

The VS Code extension will consume these binaries to run the CLI in daemon mode, making the binary naming convention a **contract** between repositories.

GitHub introduced immutable releases in late 2024. Once a release is published, assets cannot be added or modified. This broke our original workflow where `/release` created published releases and the workflow uploaded binaries afterward.

## Decision

### Release Flow

CLI releases use a **draft-first** flow:

1. `/release` command creates a **draft** release with changelog notes
2. Tag push triggers `release-cli.yml` workflow
3. Workflow builds self-contained binaries for all platforms
4. Workflow uploads binaries to the draft release
5. Workflow publishes the release (removes draft status)

Other packages (Plugins, Dataverse, Migration, Auth) continue using direct publish since they have no binary assets.

### Binary Naming Convention (Contract)

| Platform | Architecture | Asset Name |
|----------|--------------|------------|
| Windows | x64 | `ppds-win-x64.exe` |
| Windows | ARM64 | `ppds-win-arm64.exe` |
| macOS | x64 | `ppds-osx-x64` |
| macOS | ARM64 | `ppds-osx-arm64` |
| Linux | x64 | `ppds-linux-x64` |

Pattern: `ppds-{os}-{arch}[.exe]`

**This naming convention is a contract.** The VS Code extension depends on these exact names to download the correct binary. Do not change without coordinating with the extension repository.

### Checksums

Each release includes `checksums.sha256` containing SHA-256 hashes of all binaries.

## Consequences

### Positive

- Binaries are always attached before release is visible to users
- Changelog notes are preserved (not auto-generated)
- Extension can reliably download binaries by predictable name
- Fallback path handles manual tag pushes

### Negative

- CLI release process differs from other packages
- Draft releases are visible in GitHub UI until published
- Requires workflow to complete before release is public

## References

- [GitHub immutable releases announcement](https://github.blog/changelog/2024-10-15-immutable-releases/)
- VS Code extension daemon mode (future - extension repo)
