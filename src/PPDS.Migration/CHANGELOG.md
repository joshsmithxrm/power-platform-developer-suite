# Changelog - PPDS.Migration

All notable changes to PPDS.Migration will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **Boolean values now export as True/False** - CMT format uses `True`/`False` for boolean values; PPDS was incorrectly exporting `1`/`0`. This change ensures CMT import compatibility. ([#181](https://github.com/joshsmithxrm/ppds-sdk/issues/181))
- **Schema export includes relationships section** - The `<relationships>` section from input schemas is now preserved in exported `data_schema.xml` files. ([#182](https://github.com/joshsmithxrm/ppds-sdk/issues/182))

## [1.0.0-beta.3] - 2026-01-04

### Changed

- **PluginStepManager and UserMappingGenerator refactored to use early-bound entities** - Replaced magic string attribute access with strongly-typed `PPDS.Dataverse.Generated` classes (`SdkMessageProcessingStep`, `SdkMessageFilter`, `SystemUser`). Provides compile-time type safety and IntelliSense for entity operations. ([#56](https://github.com/joshsmithxrm/ppds-sdk/issues/56))
- **Progress reporters output to stderr** - `ConsoleProgressReporter` and `JsonProgressReporter` now write to `Console.Error` instead of `Console.Out`, enabling clean piping of JSON output ([#76](https://github.com/joshsmithxrm/ppds-sdk/issues/76))

## [1.0.0-beta.2] - 2026-01-02

### Changed

- **TieredImporter refactored for Single Responsibility Principle** - Reduced from 1,085 to 646 lines by extracting: ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
  - `SchemaValidator` / `ISchemaValidator` - Metadata loading and schema mismatch detection
  - `DeferredFieldProcessor` - Self-referential lookup field updates
  - `RelationshipProcessor` - M2M relationship association processing
  - `ImportContext` - Shared context passed between import phases
  - `IImportPhaseProcessor` / `PhaseResult` - Phase processor abstraction

### Added

- `FieldMetadataCollection` wrapper for convenient field metadata lookup ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- `FieldValidity` record struct for tracking field create/update validity ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- `SchemaMismatchResult` result type with error message builder ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- Input validation for `CmtSchemaReader` entity and field name attributes ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- Validation for `ImportOptions.MaxParallelEntities` property ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))

## [1.0.0-beta.1] - 2025-12-29

### Added

- Parallel export with configurable degree of parallelism
- Tiered import with automatic dependency resolution using Tarjan's algorithm
- Circular reference detection with deferred field processing
- CMT format compatibility (schema.xml and data.zip)
- Schema generation from Dataverse metadata
- Metadata-driven field filtering (include custom fields, exclude system fields)
- User mapping generation for cross-environment migrations (match by AAD Object ID or domain)
- Progress reporting with console and JSON output formats
- Security-first design: connection string redaction, no PII in logs
- DI integration via `AddDataverseMigration()` extension method
- Targets: `net8.0`, `net10.0`

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.3...HEAD
[1.0.0-beta.3]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.2...Migration-v1.0.0-beta.3
[1.0.0-beta.2]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.1...Migration-v1.0.0-beta.2
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Migration-v1.0.0-beta.1
