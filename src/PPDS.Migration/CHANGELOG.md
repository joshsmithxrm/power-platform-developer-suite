# Changelog - PPDS.Migration

All notable changes to PPDS.Migration will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Migration-v1.0.0...HEAD
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Migration-v1.0.0
