# Changelog - PPDS.Migration

All notable changes to PPDS.Migration will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html), under the rules in [docs/VERSIONING.md](https://github.com/joshsmithxrm/power-platform-developer-suite/blob/main/docs/VERSIONING.md).

## [Unreleased]

## [1.3.0] - Unreleased

### Added

- Add nullable `ExportOptions.MaxRecordsPerEntity` for opt-in bounded primary-row exports. The positive limit is checked before output, never silently truncates and requires paging cookies. A bounded export throws before exporting when `IncludeFileData` is set, when any entity in the schema has relationships or file, image or binary fields, or when an entity's FetchXML filter contains anything other than `filter`, `condition` and `value` elements. The default remains unlimited (#1575).

### Changed

- Aligned with PPDS.Dataverse 1.5.0 in the coordinated release and refreshed direct Microsoft.Extensions dependencies to 10.0.14 (#1501, #1502). Refreshed the MSAL cache extension from 4.90.1 to 4.92.0 (#1513).
- Updated SourceLink build tooling to 10.0.403 (#1498). The bounded-export property is an additive public API change; dependency servicing and version alignment are also included.

### Fixed

- **Breaking: import mode `Skip` for a whole import is refused.** With `ImportOptions.Mode = ImportMode.Skip` and no per-table mode, every record was sent with `UpsertMultiple` (or `UpsertRequest`): missing records were created, existing ones overwritten with the file's values, and all of them reported as imported. `TieredImporter.ImportAsync` now throws `ImportRefusedException` (reason `Import.SkipIsPerTable`) before it reads the data file, connects or writes anything. `Skip` keeps its documented meaning, "do not import this table", as the per-table schema attribute `importMode="skip"`; such a table writes nothing and is now listed in `ImportResult.EntityResults` with its records counted as skipped. No mode falls through to Upsert any more. The maintainer's decision (2026-11-25), asked whether a global Skip should be refused rather than read as a new "create missing only" mode: "Yes, refuse it" (#1914).
- **Breaking: a per-table `importMode` that is not a mode is refused.** `CmtSchemaReader` ignored a value other than `create`, `update`, `upsert` or `skip` (a typo such as `skp`, CMT's `skipupdate`, a number, an empty value), and an attribute spelled `importmode`; the table then took the import's mode, Upsert by default, so a table meant to be skipped had its existing records overwritten. The attribute name is now read in any case and the value in any case with surrounding spaces ignored; any other value, or two such attributes on one table, throws `InvalidDataException` naming the table, before anything is imported, as the specification already said (#1914).
- **Breaking: only records proven written receive the later phases' writes.** The importer mapped every record of the file to the target, written or not, and the deferred-field, state, many-to-many and file phases follow those mappings. So in Create mode a record that already existed in the target failed to be created and then had its deferred lookups updated, its state changed, its associations added and its files uploaded; and records a record filter left out (system, integration and support users, the activity-pointer base type), reported as skipped, still had their deferred lookups written. Now a record is mapped only when the write result proves it was written: every failure names its record id and the counts add up. Failed and filtered records, and every record of a table with `importMode="skip"`, get no mapping and no state change. When a result cannot say which records were written, none of that table's records is mapped, its later phases are not run, its entity result is not a success (`EntityImportResult.LaterPhasesSkipped`) and a `LATER_PHASES_SKIPPED` warning says so. A deferred lookup that points at a record of the file that was not written is no longer set. The state phase also refuses to change the state of a record that has no mapping (before, it used the source id). A skipped table's file data is no longer sent and no longer fails the import (#1914).
- **Breaking: the CMT attributes `skipupdate` and `forcecreate`, and `disableplugins` and `dateMode` values that are not on the list, are refused when the schema is read.** `skipupdate` and `forcecreate` were dropped, so the table was imported with the import's mode (Upsert by default), overwriting what `skipupdate` meant to keep; a table that sets either to anything but false (`false`, `0`, `no`) now throws `InvalidDataException`. `disableplugins` takes `true`, `false`, `1`, `0`, `yes` or `no` (before, any other value, such as `True ` with a space, read as false and left the steps on); `dateMode` takes `absolute`, `relative`, `relativeDaily` or `relativeExact` (before, any other value read as absolute). Values are read in any case with surrounding spaces ignored, and these attribute names in any case (#1914, #1921).
- Import results say what was created, updated, skipped and failed: new `ImportResult.CreatedCount`, `UpdatedCount`, `SkippedCount` and `FailureCount`, `EntityImportResult.SkippedCount` and `MigrationResult.SkippedCount`. Create mode counts every success as created and Update mode as updated (before, both were left empty); an individual-request Upsert counts from each response's `RecordCreated` (before, never counted); records a record filter leaves out are counted as skipped (before, they disappeared from the counts). A total is empty when a table's Upsert responses did not report it. The JSON progress `complete` line and the error report summary carry the new counts (#1914).
- **Breaking: `disableplugins` disables the steps of the right table.** The steps were selected by the object type code in the schema file, which is the source environment's; custom tables have different codes in each environment, so the import disabled the steps of whichever target table had that code and left the intended table's steps running. Each table is now resolved in the target environment by its logical name (entity metadata), and only its steps are disabled. A table that cannot be resolved refuses the import before any step is disabled or any record written (`Import.PluginTableNotResolved`). A step that cannot be disabled refuses the import (`Import.PluginStepNotDisabled`) after re-enabling the steps already disabled; before, the failure was logged and the import ran with that step on. The step query now reads every page (before, only the first). Every step that may have been disabled is re-enabled when the import ends, fails or is cancelled, also when it is cancelled while the steps are being disabled (before, those steps stayed disabled). A step that cannot be re-enabled is named, with its id, in the result's warnings and in the progress output, so a cancelled import names it too. A table marked `disableplugins` without an `etc` in the schema now has its steps disabled (before, it was ignored); a table with `importMode="skip"` no longer does. `IPluginStepManager` changes: `GetActivePluginStepsAsync` takes logical names and returns `PluginStepInfo` (id and name), and `DisablePluginStepsAsync` takes a collection that receives each step before its update is sent (#1921).
- **Breaking: when a plug-in step cannot be re-enabled, the import says so and is not a success.** `ImportResult.PluginStepsLeftDisabled` lists every step the import disabled and could not re-enable, and `ImportResult.Success` is then false (before, a successful import with a step left disabled reported success). A cancelled import returns no result, so it throws `ImportCancelledException`, an `OperationCanceledException` that carries those steps. The refusal for a step that cannot be disabled names that step, and says the other steps were re-enabled only when they were; otherwise it names the steps left disabled, with their ids in `ImportRefusedException.PluginStepsLeftDisabled` (before, it always said they were re-enabled). Before the first step is disabled, the progress output names every step to be disabled, so a run that is killed before it re-enables them leaves a record of what to re-enable. A metadata failure other than a missing table (throttling, sign-in, a missing privilege, an outage) is refused with its own reason, `Import.PluginTableMetadataUnavailable`, instead of "not found". Only enabled, visible, custom steps are selected; steps on "any entity", steps where the table is the secondary entity, and many-to-many steps are not (unchanged, now documented). `MigrationResult.PluginStepsLeftDisabled` is new (#1921).
- **Breaking:** an import mode outside the `ImportMode` members is refused with its own reason, `Import.ModeNotImplemented`, not the Skip reason. Skipped tables now appear in `ImportResult.EntityResults`, and Create and Update imports report `CreatedCount` and `UpdatedCount` (0 rather than empty) in the results and the JSON progress output (#1914).

## [1.2.2] - 2026-10-30

### Security

- Consume PPDS.Dataverse 1.3.2, carrying `System.Security.Cryptography.Pkcs` 10.0.13 and `System.Security.Cryptography.Xml` 8.0.5 through the migration dependency graph.

### Changed

- Refresh Microsoft.Extensions dependencies to 10.0.13, the MSAL cache extension to 4.90.1, and SourceLink build tooling to 10.0.401. Migration formats and public APIs are unchanged.

## [1.2.1] - 2026-09-12

### Security

- **Patched Dataverse security dependency** — now depends on PPDS.Dataverse 1.3.1, which pins `System.Security.Cryptography.Xml` 8.0.4 for the July 2026 high-severity advisories; the obsolete `NU1903` suppression is removed so future high-severity findings remain visible ([#1402](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/1402)).

### Changed

- **Central SQL client pin is authoritative** — removed the redundant local `Microsoft.Data.SqlClient` override so future repository-wide servicing updates cannot silently bypass PPDS.Migration ([#1334](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/1334)).

## [1.2.0] - 2026-06-17

### Added
- **JSON data export format** — `ExportDataFormat.Json` on `ExportOptions` alongside the default CMT XML. `JsonDataWriter` (streaming `Utf8JsonWriter`) emits a single-file PPDS-native payload mirroring the CMT structure (schema metadata, per-entity typed field values, M2M associations), with `AliasedValue` unwrapping for FetchXML-joined attributes. `ParallelExporter` selects the writer from `options.Format`. File-column binaries are not serialized in JSON v1; use CMT format for binary round-trip (#147, #1059).

## [1.1.0] - 2026-04-26

### Added
- `--filter` option for `data schema` command — filter schema generation to specific entities using SQL-like expressions (#502, #948).

## [1.0.0] - 2026-04-18

First stable release. Consolidates features developed across the `1.0.0-beta.1` through `1.0.0-beta.8` series. Targets `net8.0`, `net9.0`, `net10.0`.

### Added

- **Parallel export** — Configurable degree of parallelism with connection-pool backing.
- **Page-level parallelism for single-entity export** — Large entities (default >5000 records) export in parallel via GUID range partitioning. Auto-scales partition count (2–16) by entity size; threshold configurable via `--page-parallel-threshold` ([#503](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/503)).
- **Tiered import** — Automatic dependency resolution using Tarjan's algorithm; circular-reference detection with deferred field processing.
- **M2M relationship import parallelized** — Associations process in parallel using pool DOP; actual `Current/Total` counts instead of `0/0`. Expected 4–8× throughput improvement ([#196](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/196)).
- **M2M import idempotent** — Duplicate association errors treated as success; re-running imports does not fail on existing associations.
- **Deferred field updates use bulk APIs** — Self-referencing lookup updates use `UpdateMultiple` (~60× improvement, ~8 rec/s → ~500 rec/s) ([#196](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/196)).
- **CMT format compatibility** — Produces and consumes `schema.xml` + `data.zip` matching Configuration Migration Tool conventions. Boolean values export as `True`/`False` ([#181](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/181)); schema preserves `<relationships>` section ([#182](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/182)); M2M export shows progress counts with relationship names ([#184](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/184)).
- **CMT parity — handler framework and state transitions** — Entity handler pipeline (10 built-in handlers covering `SystemUser`, `Activity`, `BusinessUnit`, `Opportunity`, `Incident`, `Quote`, `SalesOrder`, `Lead`, `DuplicateRule`, `Product`). Adds state/status transitions (`SetStateRequest`, `WinOpportunityRequest`, etc.), cascading external lookup resolution, and date shifting (absolute, relative, relativeDaily modes) ([#708](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/708)).
- **CMT type aliases** — `number`, `bigint`, `partylist`, and lookup inference from `lookupentity` attribute ([#187](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/187)).
- **Owner impersonation via `CallerId`** — `--impersonate-owners` executes imports as the mapped target owner; unmapped owners fall back to the service principal ([#37](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/37)).
- **File column export and import** — `notes.documentbody` and other file columns export into a `files/` directory inside the ZIP with metadata (filename, MIME type). Import uploads via chunked 4 MB transfers with source→target ID mapping. Controlled by `IncludeFileData` export option (default off) ([#32](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/32)).
- **Filter feedback in export progress** — Progress output lists applied filter conditions and `(filtered)` suffix per entity; warns when a schema contains an empty `<filter>` element ([#501](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/501)).
- **Schema generation** — From Dataverse metadata with metadata-driven field filtering (include custom fields, exclude system fields).
- **User mapping generator** — Cross-environment migrations match by AAD Object ID or domain.
- **Progress reporting** — `IProgressReporter` with console and JSON output; progress output writes to stderr for clean piping ([#76](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/76)).
- **Per-entity import-mode override** — `EntitySchema` supports an `importMode` attribute ([#37](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/37)).
- **Warnings array in `summary.json`** — `ImportResult.Warnings` captures non-fatal issues (column skipped, bulk not supported, schema mismatch, user-mapping fallback, plugin re-enable failed) ([#271](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/271)).
- **Pool statistics in `summary.json`** — `ImportResult.PoolStatistics` captures throttle/retry metrics (`requestsServed`, `throttleEvents`, `totalBackoffTime`, `retriesAttempted`, `retriesSucceeded`) ([#273](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/273)).
- **Error report v1.1 with execution context** — Adds `executionContext` object (CLI/SDK versions, runtime, platform, import mode, option flags) for reproducibility.
- **Bulk operation probe-once optimization** — Detects entity support for bulk operations by probing 1 record first, reducing wasted records for unsupported entities (e.g. `team`). Per-import-session cache.
- **DI integration** — `AddDataverseMigration()` extension method.
- **Security-first design** — Connection string redaction, no PII in logs.

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Migration-v1.3.0...HEAD
[1.3.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Migration-v1.2.2...Migration-v1.3.0
[1.2.2]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Migration-v1.2.1...Migration-v1.2.2
[1.2.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Migration-v1.2.0...Migration-v1.2.1
[1.2.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Migration-v1.1.0...Migration-v1.2.0
[1.1.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Migration-v1.0.0...Migration-v1.1.0
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Migration-v1.0.0
