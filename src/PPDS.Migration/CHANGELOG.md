# Changelog - PPDS.Migration

All notable changes to PPDS.Migration will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Warnings array in summary.json** - Import warnings (column skipped, bulk not supported, schema mismatch, etc.) are now captured in `ImportResult.Warnings` and serialized to the `warnings` array in summary.json. Enables programmatic consumers to detect non-fatal issues. ([#271](https://github.com/joshsmithxrm/ppds-sdk/issues/271))
- **Pool statistics in summary.json** - `ImportResult.PoolStatistics` captures throttle/retry metrics (`requestsServed`, `throttleEvents`, `totalBackoffTime`, `retriesAttempted`, `retriesSucceeded`) and serializes to the `poolStatistics` object in summary.json. ([#273](https://github.com/joshsmithxrm/ppds-sdk/issues/273))
- **IWarningCollector interface** - Thread-safe warning collection during parallel import operations. Used by `TieredImporter` to aggregate warnings from concurrent entity imports. ([#271](https://github.com/joshsmithxrm/ppds-sdk/issues/271))
- **ImportWarning and ImportWarningCodes** - Structured warning type with standard codes: `BULK_NOT_SUPPORTED`, `COLUMN_SKIPPED`, `SCHEMA_MISMATCH`, `USER_MAPPING_FALLBACK`, `PLUGIN_REENABLE_FAILED`. ([#271](https://github.com/joshsmithxrm/ppds-sdk/issues/271))

## [1.0.0-beta.5] - 2026-01-06

### Added

- **Error report v1.1 with execution context** - Import error reports now include `executionContext` object with CLI/SDK versions, runtime, platform, import mode, and option flags. Enables reproducing and troubleshooting imports after the fact. Version bumped from "1.0" to "1.1". See [ADR-0022](../../docs/adr/0022_IMPORT_DIAGNOSTICS_ARCHITECTURE.md).
- **Bulk operation probe-once optimization** - When detecting if an entity supports bulk operations (UpsertMultiple), now probes with 1 record first instead of sending the full batch. Reduces wasted records from N to 1 for unsupported entities (e.g., `team` entity). Cache is per-import-session. See [ADR-0022](../../docs/adr/0022_IMPORT_DIAGNOSTICS_ARCHITECTURE.md).

### Changed

- **M2M relationship import parallelized** - M2M associations now process in parallel using the connection pool's DOP (previously sequential). Progress reporting now shows actual `Current/Total` counts instead of `0/0`. Expected 4-8x performance improvement depending on DOP. ([#196](https://github.com/joshsmithxrm/ppds-sdk/issues/196))
- **M2M import is now idempotent** - Duplicate association errors ("Cannot insert duplicate key") are treated as success since the desired state is achieved. Enables re-running imports without failing on existing associations.
- **Deferred field updates use bulk APIs** - Self-referencing lookup field updates now use `UpdateMultiple` bulk API with probe-once fallback pattern. Expected ~60x performance improvement (~8 rec/s → ~500 rec/s). ([#196](https://github.com/joshsmithxrm/ppds-sdk/issues/196))

### Fixed

- **CMT import compatibility: Added `number` type alias** - CMT exports integer fields with `type="number"`, but PPDS only recognized `int`/`integer`. Unrecognized types were treated as strings, causing import failures. ([#187](https://github.com/joshsmithxrm/ppds-sdk/issues/187))
- **CMT import compatibility: Added `bigint` type support** - 64-bit integer fields now parse correctly instead of falling back to string. ([#187](https://github.com/joshsmithxrm/ppds-sdk/issues/187))
- **CMT import compatibility: Added `partylist` type handling** - Email participant fields now parse as EntityReference like other lookup types. ([#187](https://github.com/joshsmithxrm/ppds-sdk/issues/187))
- **CMT import compatibility: Infer lookup type from `lookupentity` attribute** - Fields with `lookupentity` attribute but no `type` attribute are now correctly parsed as EntityReference instead of string. ([#187](https://github.com/joshsmithxrm/ppds-sdk/issues/187))

## [1.0.0-beta.4] - 2026-01-05

### Fixed

- **Boolean values now export as True/False** - CMT format uses `True`/`False` for boolean values; PPDS was incorrectly exporting `1`/`0`. This change ensures CMT import compatibility. ([#181](https://github.com/joshsmithxrm/ppds-sdk/issues/181))
- **Schema export includes relationships section** - The `<relationships>` section from input schemas is now preserved in exported `data_schema.xml` files. ([#182](https://github.com/joshsmithxrm/ppds-sdk/issues/182))
- **M2M relationship export now shows progress counts** - Previously displayed `0/0` for M2M export progress; now shows actual association counts with relationship names (e.g., `[Export] team M2M teamroles: 112/112`). Starting messages no longer trigger entity progress display. ([#184](https://github.com/joshsmithxrm/ppds-sdk/issues/184))

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

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.5...HEAD
[1.0.0-beta.5]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.4...Migration-v1.0.0-beta.5
[1.0.0-beta.4]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.3...Migration-v1.0.0-beta.4
[1.0.0-beta.3]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.2...Migration-v1.0.0-beta.3
[1.0.0-beta.2]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.1...Migration-v1.0.0-beta.2
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Migration-v1.0.0-beta.1
