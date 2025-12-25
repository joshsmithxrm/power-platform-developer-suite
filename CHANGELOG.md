# PPDS SDK Changelog Index

This repository contains multiple packages with independent release cycles.

## Per-Package Changelogs

- [PPDS.Plugins](src/PPDS.Plugins/CHANGELOG.md) - Plugin attributes for Dataverse
- [PPDS.Dataverse](src/PPDS.Dataverse/CHANGELOG.md) - High-performance Dataverse connectivity
- [PPDS.Migration](src/PPDS.Migration/CHANGELOG.md) - Migration library and CLI tool

## GitHub Releases

For full release notes with each version, see:
https://github.com/joshsmithxrm/ppds-sdk/releases

## Versioning

This repository uses [MinVer](https://github.com/adamralph/minver) for automated versioning.
Each package has its own tag prefix:

| Package | Tag Format | Example |
|---------|------------|---------|
| PPDS.Plugins | `Plugins-v{version}` | `Plugins-v1.2.0` |
| PPDS.Dataverse | `Dataverse-v{version}` | `Dataverse-v1.0.0` |
| PPDS.Migration + CLI | `Migration-v{version}` | `Migration-v1.0.0` |

Pre-release versions follow SemVer:
- Alpha: `Dataverse-v1.0.0-alpha.1`
- Beta: `Dataverse-v1.0.0-beta.1`
- Release Candidate: `Dataverse-v1.0.0-rc.1`
