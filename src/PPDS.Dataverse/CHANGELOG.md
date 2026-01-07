# Changelog - PPDS.Dataverse

All notable changes to PPDS.Dataverse will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Eager pool initialization** - `IDataverseConnectionPool.EnsureInitializedAsync()` allows triggering authentication during startup, avoiding surprise browser popups on first query. Idempotent - subsequent calls are no-ops. ([#292](https://github.com/joshsmithxrm/ppds-sdk/issues/292))
- **Seed initialization result tracking** - `IDataverseConnectionPool.InitializationResults` exposes per-source seed initialization status with failure reason classification (auth, network, service, connection not ready). Enables accurate pool health reporting. ([#287](https://github.com/joshsmithxrm/ppds-sdk/issues/287))
- **Throttle backoff time tracking** - `IThrottleTracker.TotalBackoffTime` accumulates total backoff duration across all throttle events for observability. ([#273](https://github.com/joshsmithxrm/ppds-sdk/issues/273))
- **Retry statistics in PoolStatistics** - `PoolStatistics` now includes `TotalBackoffTime`, `RetriesAttempted`, and `RetriesSucceeded` counters for throttle/retry visibility. ([#273](https://github.com/joshsmithxrm/ppds-sdk/issues/273))

### Changed

- **Reduced seed failure log noise** - Per-attempt seed creation failures now log at DEBUG level instead of WARNING. Only the final consolidated error logs at ERROR level with classified failure reason. ([#287](https://github.com/joshsmithxrm/ppds-sdk/issues/287))
- **Accurate pool initialization status** - Pool now logs "initialized with N degraded source(s)" or "initialization failed" based on actual seed results, instead of always claiming success. ([#287](https://github.com/joshsmithxrm/ppds-sdk/issues/287))

## [1.0.0-beta.4] - 2026-01-06

### Added

- **`IFlowService`** - Service for cloud flow operations ([#142](https://github.com/joshsmithxrm/ppds-sdk/issues/142)):
  - `ListAsync` - List flows with optional solution and state filters
  - `GetAsync` - Get flow by unique name
  - `GetByIdAsync` - Get flow by ID
  - Parses flow `clientdata` JSON to extract connection reference logical names
- **`IConnectionReferenceService`** - Service for connection reference operations with orphan detection ([#143](https://github.com/joshsmithxrm/ppds-sdk/issues/143)):
  - `ListAsync` - List connection references with solution and orphan filtering
  - `GetAsync` - Get connection reference by logical name
  - `GetFlowsUsingAsync` - Get flows that use a specific connection reference
  - `AnalyzeAsync` - Full relationship analysis with orphan detection (flows referencing missing CRs, CRs not used by any flow)
- **`IDeploymentSettingsService`** - Service for PAC-compatible deployment settings files ([#145](https://github.com/joshsmithxrm/ppds-sdk/issues/145)):
  - `GenerateAsync` - Generate settings from current environment (captures current values)
  - `SyncAsync` - Sync existing file with solution (preserves values, adds new entries, removes stale)
  - `ValidateAsync` - Validate settings against solution (missing entries, stale entries, unbound CRs)
- **`FlowClientDataParser`** - Utility for extracting connection reference logical names from flow clientdata JSON
- **`Workflow` early-bound entity** - Entity class for Power Automate flows (classic workflows). Supports flow management operations. ([#149](https://github.com/joshsmithxrm/ppds-sdk/issues/149))
- **`ConnectionReference` early-bound entity** - Entity class for connection references used by flows and canvas apps. Fixed naming from pac modelbuilder's inconsistent lowercase output. ([#149](https://github.com/joshsmithxrm/ppds-sdk/issues/149))
- **Field-level error context in bulk operation errors** - `BulkOperationError` now includes `FieldName` (extracted from error messages) and `FieldValueDescription` (sanitized value info for EntityReferences). Makes debugging lookup failures and required field errors easier. See [ADR-0022](../../docs/adr/0022_IMPORT_DIAGNOSTICS_ARCHITECTURE.md).

### Changed

- **Increased default AcquireTimeout from 30s to 120s** - With ADR-0019 pool-managed concurrency, tasks queue on the semaphore and need longer timeouts for large imports with many batches. Previously tasks would timeout during normal queuing.
- **Reduced pool exhaustion retry attempts from 3 to 1** - With proper pool queuing, exhaustion is rare and typically indicates a real capacity issue rather than transient contention.

### Fixed

- **Pool exhaustion under concurrent bulk operations** - Multiple consumers (e.g., entities importing in parallel) each assumed they could use full pool capacity, causing N×DOP tasks to compete for DOP semaphore slots. Replaced adaptive parallelism calculation with pool-managed blocking where tasks naturally queue on `GetClientAsync()`. See [ADR-0019](../../docs/adr/0019_POOL_MANAGED_CONCURRENCY.md).
- **Pool exhaustion during throttling** - Capped batch parallelism at pool capacity to prevent over-subscription when throttling reduces effective throughput. On high-core machines, `ProcessorCount * 4` (e.g., 96 tasks on 24-core) far exceeded pool capacity (~20 slots), causing timeout storms when throttled connections held semaphore slots during Retry-After waits.

## [1.0.0-beta.3] - 2026-01-04

### Added

- **SQL parser and FetchXML transpiler** - Full SQL-to-FetchXML translation for querying Dataverse ([#52](https://github.com/joshsmithxrm/ppds-sdk/issues/52)):
  - `SqlLexer` - SQL tokenizer with keyword/operator/literal recognition
  - `SqlParser` - Recursive descent parser producing typed AST
  - `SqlToFetchXmlTranspiler` - AST to FetchXML conversion
  - Supports: SELECT (columns, *, aliases), WHERE (=, <>, !=, <, >, <=, >=, LIKE, IS NULL, IN, AND, OR, parentheses), JOIN (INNER, LEFT, RIGHT), ORDER BY, GROUP BY, TOP, DISTINCT, aggregates (COUNT, SUM, AVG, MIN, MAX, COUNT DISTINCT)
  - Comment preservation for round-trip scenarios
  - Position-aware error messages with context snippets
- **Query execution service** - New `IQueryExecutor` interface and `QueryExecutor` implementation:
  - Execute FetchXML queries via SDK's `RetrieveMultiple`
  - Proper XML parsing with `System.Xml.Linq` (replaces regex-based extraction)
  - Paging support with cookies
  - Total record count via `returntotalrecordcount` preference header
  - Result mapping to typed `QueryResult` with column metadata
- **Metadata service for entity browsing** - New `IMetadataService` interface and `DataverseMetadataService` implementation providing:
  - `GetEntitiesAsync()` - List all entities with optional filtering
  - `GetEntityAsync()` - Get full entity metadata including attributes, relationships, keys, and privileges
  - `GetAttributesAsync()` - List entity attributes with type filtering
  - `GetRelationshipsAsync()` - List 1:N, N:1, and N:N relationships
  - `GetKeysAsync()` - List alternate keys for an entity
  - `GetGlobalOptionSetsAsync()` - List global option sets
  - `GetOptionSetAsync()` - Get option set details with values
  ([#51](https://github.com/joshsmithxrm/ppds-sdk/issues/51))
- **Comprehensive metadata DTOs** - All metadata DTOs now include complete properties for extension Metadata Browser support:
  - `AttributeMetadataDto`: Added `metadataId`, `sourceType`, `isSecured`, `formulaDefinition`, `autoNumberFormat`, form/grid validity, security capabilities, and advanced properties
  - `EntityMetadataDto`: Added `metadataId`, `pluralName`, `hasNotes`, `hasActivities`, `isValidForAdvancedFind`
  - `RelationshipMetadataDto`: Added `metadataId`, `isHierarchical`, `securityTypes`
  - `ManyToManyRelationshipDto`: Added `metadataId`, `securityTypes`
  - `EntityKeyDto`, `OptionSetSummary`, `OptionSetMetadataDto`: Added `metadataId`
  - `OptionValueDto`: Added `isManaged`
  ([#51](https://github.com/joshsmithxrm/ppds-sdk/issues/51))

### Changed

- **Replaced Newtonsoft.Json with System.Text.Json** - Removed external dependency; uses built-in JSON serialization with case-insensitive property matching ([#72](https://github.com/joshsmithxrm/ppds-sdk/issues/72))

## [1.0.0-beta.2] - 2026-01-02

### Fixed

- **Double-checked locking in ConnectionStringSource** - Added `volatile` modifier to `_client` field for correct multi-threaded behavior ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))

### Changed

- Extracted throttle detection logic into `ThrottleDetector` class for cleaner separation of concerns in `PooledClient` ([#82](https://github.com/joshsmithxrm/ppds-sdk/pull/82))
- Improved input validation for `DataverseConnection` name parameter ([#82](https://github.com/joshsmithxrm/ppds-sdk/pull/82))

## [1.0.0-beta.1] - 2025-12-29

### Added

- Multi-connection pool supporting multiple Application Users for load distribution
- DOP-based parallelism using server's `RecommendedDegreesOfParallelism` (`x-ms-dop-hint` header)
- Connection selection strategies: RoundRobin, LeastConnections, ThrottleAware
- Throttle tracking with automatic routing away from throttled connections
- `IConnectionSource` abstraction for custom authentication methods (ADR-0006)
- `ServiceClientSource` for integrating pre-authenticated ServiceClient instances
- `CredentialProviderSource` for integration with PPDS.Auth credential providers
- Bulk operation wrappers: CreateMultiple, UpdateMultiple, UpsertMultiple, DeleteMultiple
- `IProgress<ProgressSnapshot>` support for real-time progress reporting
- Full `appsettings.json` configuration support for all options
- DI integration via `AddDataverseConnectionPool()` extension method
- Affinity cookie disabled by default for improved throughput (ADR-0001)
- TVP race condition retry (SQL error 3732/2812)
- SQL deadlock retry (SQL error 1205)
- Connection validation with background health checks
- Security-first design: connection string redaction, sensitive data attributes
- Targets: `net8.0`, `net10.0`

### Changed

- Removed rate control presets (`Conservative`, `Balanced`, `Aggressive`) in favor of DOP-based parallelism
- Removed adaptive rate control in favor of server-recommended limits

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Dataverse-v1.0.0-beta.4...HEAD
[1.0.0-beta.4]: https://github.com/joshsmithxrm/ppds-sdk/compare/Dataverse-v1.0.0-beta.3...Dataverse-v1.0.0-beta.4
[1.0.0-beta.3]: https://github.com/joshsmithxrm/ppds-sdk/compare/Dataverse-v1.0.0-beta.2...Dataverse-v1.0.0-beta.3
[1.0.0-beta.2]: https://github.com/joshsmithxrm/ppds-sdk/compare/Dataverse-v1.0.0-beta.1...Dataverse-v1.0.0-beta.2
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Dataverse-v1.0.0-beta.1
