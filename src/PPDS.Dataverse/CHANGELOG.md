# Changelog - PPDS.Dataverse

All notable changes to PPDS.Dataverse will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Breaking

- 12 domain services have been moved from `PPDS.Dataverse` to `PPDS.Cli` as part of constitution A1 compliance and the shakedown-guard v1 landing. The services are no longer registered by `RegisterDataverseServices` — consumers must now reference `PPDS.Cli.Services` for these types:
  - `IPluginTraceService` / `PluginTraceService` → `PPDS.Cli.Services.PluginTraces`
  - `IWebResourceService` / `WebResourceService` → `PPDS.Cli.Services.WebResources`
  - `IEnvironmentVariableService` / `EnvironmentVariableService` → `PPDS.Cli.Services.EnvironmentVariables`
  - `ISolutionService` / `SolutionService` → `PPDS.Cli.Services.Solutions`
  - `IImportJobService` / `ImportJobService` → `PPDS.Cli.Services.ImportJobs`
  - `IMetadataAuthoringService` / `MetadataAuthoringService` → `PPDS.Cli.Services.Metadata.Authoring`
  - `IUserService` / `UserService` → `PPDS.Cli.Services.Users`
  - `IRoleService` / `RoleService` → `PPDS.Cli.Services.Roles`
  - `IFlowService` / `FlowService` → `PPDS.Cli.Services.Flows`
  - `IConnectionReferenceService` / `ConnectionReferenceService` → `PPDS.Cli.Services.ConnectionReferences`
  - `IDeploymentSettingsService` / `DeploymentSettingsService` → `PPDS.Cli.Services.DeploymentSettings`
  - `IComponentNameResolver` / `ComponentNameResolver` → `PPDS.Cli.Services.SolutionComponents`

  Consumers depending on these services or on `RegisterDataverseServices` registering them will see compile/runtime errors on upgrade. Update `using` directives to the new namespaces and register them via `AddCliApplicationServices`. Method signatures are preserved.

## [1.0.0] - 2026-04-18

First stable release. Consolidates features developed across the `1.0.0-beta.1` through `1.0.0-beta.7` series. Targets `net8.0`, `net9.0`, `net10.0`.

### Added

- **Connection pool (`IDataverseConnectionPool`)** — Multi-connection pool supporting multiple Application Users for load distribution. DOP-based parallelism using server's `RecommendedDegreesOfParallelism` (`x-ms-dop-hint`). Connection strategies: `RoundRobin`, `LeastConnections`, `ThrottleAware`. Affinity cookie disabled by default for throughput. DI integration via `AddDataverseConnectionPool()`.
- **`IConnectionSource` abstraction** — `ServiceClientSource` for pre-authenticated clients and `CredentialProviderSource` for PPDS.Auth integration; supports custom authentication methods.
- **Bulk operation wrappers** — `CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`, `DeleteMultiple` with `IProgress<ProgressSnapshot>` progress reporting.
- **Throttle tracking and retry** — Automatic routing away from throttled connections, TVP race-condition retry (SQL 3732/2812), SQL deadlock retry (SQL 1205). `IThrottleTracker.TotalBackoffTime` accumulates across events; `PoolStatistics` exposes `TotalBackoffTime`, `RetriesAttempted`, `RetriesSucceeded` ([#273](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/273)).
- **Pool lifecycle** — `EnsureInitializedAsync()` triggers eager authentication during startup (idempotent) ([#292](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/292)); `InitializationResults` exposes per-source status with failure classification (auth, network, service, connection not ready) ([#287](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/287)). Background health checks validate connections.
- **Query execution (`IQueryExecutor`)** — FetchXML execution via `RetrieveMultiple`, proper `System.Xml.Linq` parsing (no regex extraction), paging cookies, total record count via `returntotalrecordcount` preference header, typed `QueryResult` with column metadata.
- **Query Engine v2 integration** — Execution plan layer with Volcano iterator model for streaming results; parallel partitioned aggregates (accurate `COUNT(*)` beyond Dataverse 50K limit via date-range partitioning); adaptive retry with binary splitting; prefetch scan node for page-ahead buffering; child-record paging boundary detection; adaptive thread management during 429 backoff; auto-paging for `RemoteScanNode` cross-environment results; DML safety guard.
- **Query hints in execution pipeline** — `ppds:` hint set (`USE_TDS`, `MAX_ROWS`, `MAXDOP`, `NOLOCK`, `HASH_GROUP`, `BYPASS_PLUGINS`, `BYPASS_FLOWS`) integrated into routing and execution.
- **TDS endpoint routing** — Automatic routing of compatible queries to the SQL endpoint with types and routing logic.
- **Metadata query service (`IMetadataQueryService`)** — `GetEntitiesAsync`, `GetEntityAsync`, `GetAttributesAsync`, `GetRelationshipsAsync`, `GetKeysAsync`, `GetGlobalOptionSetsAsync`, `GetOptionSetAsync`. Comprehensive DTOs (`AttributeMetadataDto`, `EntityMetadataDto`, `RelationshipMetadataDto`, `ManyToManyRelationshipDto`, `EntityKeyDto`, `OptionSetSummary`, `OptionSetMetadataDto`, `OptionValueDto`) with full coverage for extension Metadata Browser ([#51](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/51)).
- **Metadata authoring service (`IMetadataAuthoringService`)** — Schema CRUD for tables, columns, relationships, choices, and alternate keys with validation and dry-run support ([#764](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/764), [#766](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/766)).
- **`IWebResourceService`** — Web resource querying, content access, and publishing operations ([#618](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/618)).
- **`IFlowService`, `IConnectionReferenceService`, `IDeploymentSettingsService`** — Cloud-flow operations, connection-reference analysis (orphan detection), and PAC-compatible deployment settings (`Generate`, `Sync`, `Validate`) ([#142](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/142)–[#145](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/145)).
- **`IPluginTraceService`** — Trace log operations with 15+ filter options: `ListAsync`, `GetAsync`, `GetRelatedAsync`, `GetTimelineAsync`, `GetSettingsAsync`/`SetSettingsAsync`, `DeleteAsync`/`DeleteByFilterAsync`/`DeleteByAgeAsync`, `CountAsync`. `TimelineHierarchyBuilder` for execution-tree visualization ([#152](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/152)–[#158](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/158)).
- **SQL parser and FetchXML transpiler (legacy)** — Shipped in beta.3 and superseded by PPDS.Query in later betas; retained entry points for backwards compatibility during transition ([#52](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/52)).
- **`ListResult<T>`** — Structured return type for all service `List*` methods: `Items`, `TotalCount`, `WasTruncated`, `FiltersApplied` per Constitution I4 (no silent truncation) ([#651](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/651)).
- **`CallerId` impersonation** — Bulk-operation pipeline accepts `DataverseClientOptions` end-to-end so callers can execute as mapped owners.
- **`ComponentNameResolver`** — Resolves solution component types via `IMetadataQueryService` with per-environment caching; names for Roles, Forms, SiteMaps, ConnectionRoles, and entity-typed components.
- **Early-bound entity classes** — `PluginTracelog`, `ServiceEndpoint`, `CustomAPI`, `CustomAPIRequestParameter`, `CustomAPIResponseProperty`, `EntityDataProvider`, `WebResource`, `Workflow`, `ConnectionReference`, and supporting types. Generated classes replace magic-string attribute access ([#56](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/56), [#149](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/149), [#440](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/440)).
- **Field-level error context** — `BulkOperationError` includes `FieldName` (extracted from error messages) and sanitized `FieldValueDescription` for `EntityReference` lookups.
- **Full `appsettings.json` configuration** for all pool options.

### Changed

- **BREAKING — `IMetadataService` renamed to `IMetadataQueryService`** — Read-only semantics made explicit ahead of `IMetadataAuthoringService`; consumers must update references ([#766](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/766)).
- **BREAKING — Service `List*` return type** — Changed from `IReadOnlyList<T>` to `ListResult<T>` ([#651](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/651)).
- **BREAKING — Replaced Newtonsoft.Json with System.Text.Json** — Removes external dependency; case-insensitive property matching ([#72](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/72)).
- **Default `AcquireTimeout` raised from 30 s to 120 s** — Accommodates queuing on the DOP semaphore during large imports.
- **Pool-managed concurrency** — Replaced adaptive parallelism calculation with pool-queue blocking at `GetClientAsync()`; batch parallelism capped at pool capacity to prevent oversubscription during throttling. Exhaustion retry reduced from 3 to 1 (rare under proper queuing).
- **Removed rate-control presets and adaptive rate control** — Replaced by DOP-based parallelism driven by `RecommendedDegreesOfParallelism`.
- **Reduced seed-failure log noise** — Per-attempt seed failures log at DEBUG; consolidated final errors log at ERROR with classified reason ([#287](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/287)).
- **Pool initialization status accuracy** — Logs "initialized with N degraded source(s)" or "initialization failed" based on actual seed results.

### Fixed

- **Double-checked locking in `ConnectionStringSource`** — Added `volatile` to `_client` for correct multi-threaded behavior ([#81](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/81)).
- **Pool exhaustion under concurrent bulk operations** — Multiple consumers no longer oversubscribe the DOP semaphore.
- **Pool exhaustion during throttling** — Batch parallelism capped at pool capacity on high-core machines.
- **Floating-point equality comparisons** — Use proper SQL semantics for `float` comparisons.
- **Throttle detection extraction** — `ThrottleDetector` separated from `PooledClient` for cleaner separation of concerns ([#82](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/82)).

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Dataverse-v1.0.0...HEAD
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Dataverse-v1.0.0
