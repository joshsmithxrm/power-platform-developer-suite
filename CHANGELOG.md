# Changelog

All notable changes to the PPDS SDK packages will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **PPDS.Migration** - New library for high-performance Dataverse data migration
  - Parallel export with configurable degree of parallelism
  - Tiered import with automatic dependency resolution using Tarjan's algorithm
  - Circular reference detection with deferred field processing
  - CMT format compatibility (schema.xml and data.zip)
  - Progress reporting with console and JSON output formats
  - Security-first design: connection string redaction, no PII in logs
  - DI integration via `AddDataverseMigration()` extension method
  - Targets: `net8.0`, `net10.0`

- **PPDS.Migration.Cli** - New CLI tool for high-performance Dataverse data migration
  - Commands: `export`, `import`, `analyze`, `migrate`
  - JSON progress output for tool integration (`--json` flag)
  - Support for multiple Application Users and bypass options
  - Packaged as .NET global tool (`ppds-migrate`)
  - Comprehensive unit test suite (98 tests)
  - Targets: `net8.0`, `net10.0`

- **PPDS.Dataverse** - New package for high-performance Dataverse connectivity
  - Multi-connection pool supporting multiple Application Users for load distribution
  - Connection selection strategies: RoundRobin, LeastConnections, ThrottleAware
  - Throttle tracking with automatic routing away from throttled connections
  - Bulk operation wrappers: CreateMultiple, UpdateMultiple, UpsertMultiple, DeleteMultiple
  - `IProgress<ProgressSnapshot>` support for real-time progress reporting during bulk operations
  - DI integration via `AddDataverseConnectionPool()` extension method
  - Affinity cookie disabled by default for improved throughput
  - Targets: `net8.0`, `net10.0`

### Documentation

- Added UpsertMultiple pitfalls section to `BULK_OPERATIONS_PATTERNS.md` - documents the duplicate key error when setting alternate key columns in both `KeyAttributes` and `Attributes`

### Changed

- Updated publish workflow to support multiple packages and extract version from git tag
- Updated target frameworks for PPDS.Plugins: dropped `net6.0` (out of support), added `net10.0` (current LTS)
  - PPDS.Plugins now targets: `net462`, `net8.0`, `net10.0`

## [1.1.0] - 2025-12-16

### Added

- Added `SecureConfiguration` property to `PluginStepAttribute` for secure plugin settings

### Changed

- Updated GitHub Actions dependencies (checkout v6, setup-dotnet v5, upload-artifact v6)

## [1.0.0] - 2025-12-15

### Added

- `PluginStepAttribute` for declarative plugin step registration
  - `Message`, `EntityLogicalName`, `Stage` (required)
  - `Mode`, `FilteringAttributes`, `ExecutionOrder` (optional)
  - `UnsecureConfiguration` for plugin settings
  - `StepId` for multi-step plugins
- `PluginImageAttribute` for defining pre/post images
  - `ImageType`, `Name` (required)
  - `Attributes`, `EntityAlias`, `StepId` (optional)
- `PluginStage` enum (`PreValidation`, `PreOperation`, `PostOperation`)
- `PluginMode` enum (`Synchronous`, `Asynchronous`)
- `PluginImageType` enum (`PreImage`, `PostImage`, `Both`)
- Multi-targeting: `net462`, `net6.0`, `net8.0`
- Strong name signing for Dataverse compatibility
- Full XML documentation
- GitHub Actions workflows for build and NuGet publishing
- Comprehensive unit test suite

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/joshsmithxrm/ppds-sdk/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/v1.0.0
