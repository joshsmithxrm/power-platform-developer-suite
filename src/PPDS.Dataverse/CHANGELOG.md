# Changelog - PPDS.Dataverse

All notable changes to PPDS.Dataverse will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html), under the rules in [docs/VERSIONING.md](https://github.com/joshsmithxrm/power-platform-developer-suite/blob/main/docs/VERSIONING.md).

## [Unreleased]

## [1.5.0] - Unreleased

1.4.0 was tagged but never published to NuGet. 1.5.0 carries the 1.4.0 changes listed below plus the additions in this section. Upgrading from 1.3.2 gets both.

### Added

- `DmlValueAdmission`, `DmlLiteral`, `DmlLiteralKind`, `DmlTargetKind`, `DmlValueUse` and `DmlAdmission`: the admission of DML values, one function per target kind (text, whole number, decimal, currency, floating point, user-local date and time, date only, yes/no, choice, row id). A literal is admitted only when its text matches the kind's exact grammar and the value that will be sent loses nothing (#1698).

- `QueryErrorCode.DmlUnsupportedConstruct`, `DmlValueStoredDifferently` and `DmlMetadataUnavailable`: a code for each DML refusal class (the maintainer's "A code per class"); `DmlBlocked` keeps the safety guard's own refusals (#1698).

- The AppContext switch `PPDS.BlockInteractiveSignIn`, for test hosts: when a host turns it on, a connection string that the Dataverse SDK reads as OAuth (`AuthType=OAuth` or `AuthType=5`) is refused before it reaches the SDK, which could otherwise show a sign-in prompt. The refusal is an `InvalidOperationException`; `ConnectionStringSource` passes it on as the inner exception of a `DataverseConnectionException`. It is off by default and changes nothing unless set. It is described under "Blocking Interactive Sign-In (Test Hosts)" in `src/PPDS.Dataverse/README.md` in the repository, and in the PPDS.Auth package README; the PPDS.Dataverse package ships the repository's main README, which does not describe it (#1757).
- `OptionSetNotFoundException` (`PPDS.Dataverse.Metadata`): the exception `IMetadataQueryService.GetOptionSetAsync` throws for a global choice that exists in no spelling (#1759).
- `DeleteGlobalChoiceRequest.ExpectedMetadataId` (default null), a request field for the CLI/MCP metadata authoring service. When it is set, that service refuses the delete unless its pre-delete check finds exactly that object under the requested name. This library does not act on it itself, and existing callers are unaffected (#1535).
- A `MetadataValidationException(string errorCode, string message, string? field, Exception? innerException)` constructor that keeps the inner exception (#1535).
- `MetadataErrorCodes.WriteNotConfirmed` (`WRITE_NOT_CONFIRMED`): a write request returned success but a follow-up confirmation, such as the stored option colour, failed or disagreed (#1604).
- Keep the public int OptionDefinition.Value accessors and add a read-only HasExplicitValue presence getter. System.Text.Json preserves omission versus explicit zero across roundtrips; omit Value to request allocation. This is an additive public API change (#1530).

### Changed

- **Behaviour change:** `IMetadataQueryService.GetOptionSetAsync` throws `OptionSetNotFoundException` (inner exception: the original `FaultException<OrganizationServiceFault>`) when Dataverse reports the name not found and the published catalog holds it in no spelling. Before, it rethrew the `FaultException`. Code that caught `FaultException<OrganizationServiceFault>` to detect an unknown name must also catch `OptionSetNotFoundException`, or read `InnerException`. The `FaultException` is still thrown when the catalog lookup fails or several case variants exist (#1759).
- Refreshed MSAL and its cache extensions from 4.90.1 to 4.92.0 (#1513).
- **DML execution (`DmlExecuteNode`, #1698):** a statement over the row cap is refused before anything is written (`DmlRowCapExceededException`, `QueryErrorCode.DmlRowCapExceeded`) instead of writing the first rows; values are not written without column metadata (`DmlSafetyRefusalException`, `QueryErrorCode.DmlMetadataUnavailable`) or for a column missing from it (`QueryErrorCode.DmlUnsupportedConstruct`); every value of a statement passes `DmlValueAdmission.CheckSentValue` while the rows are built, before the first bulk write (text that is empty or has white space at either end, or a value that would lose digits, refuses the statement with `QueryErrorCode.DmlValueStoredDifferently`); `DmlValueCoercer`'s errors no longer quote the value; a bulk write that is cancelled or reports fewer rows than it was given is recorded as interrupted (`DmlStatementOutcome.Interrupted`) instead of complete; per-statement outcomes are collected in `QueryPlanStatistics`; DML hints overlay the executor's configured bulk options (`IBulkOperationExecutor.ConfiguredOptions`, a default interface member, and `BulkOperationOptions.Clone`).
- `CastConverter`: a number converted to `nvarchar(n)`/`nchar(n)` that does not fit is an overflow error (`varchar`/`char` of an integer gives `*`, as in T-SQL), and a value that does not fit `decimal(p, s)` is an overflow error; TRY_CAST/TRY_CONVERT return NULL for both; `decimal(p, s)` wider than 28 digits returns the value when its digits before the point plus the `s` places fit in 28 (as on 1.4.x); a value that does not fit, or a scale above 28, which the engine cannot hold exactly, is a `NotSupportedException`, never NULL (#1698).

### Fixed

- `DmlValueCoercer` sends a date/time literal with `Z` or an offset as that UTC instant (an offset was converted to the machine's local time and stored hours off) and converts numbers to text with the invariant culture; out-of-range numeric conversions fail with a type-mismatch error instead of a raw exception. LEN, TRIM, LTRIM and RTRIM remove and ignore spaces only, as in T-SQL (#1698).
- `DataverseMetadataQueryService.GetOptionSetAsync` finds a global choice whose stored name differs from the requested name only by case. The exact name is always tried first. Only when Dataverse reports that option set as not found does the service list global choices and retry once under a single case-insensitive match. Other faults, no match and several matches rethrow the original error unchanged. Recognition checks the Dataverse fault code `0x80040217` first and falls back to the English server message. That fault code was observed live for a mis-cased and for an unknown global choice name on an English-base and on a French-base organisation, with the message text in English on both (#1535, #1604, #1699). `UpdateOptionValueRequest.Description` is documented as written in the organization's base language (#1699).
- The `MetadataErrorCodes.SdkOperationFailed` documentation no longer implies that nothing was written (#1604).

## [1.4.0] - 2026-11-01 (tagged, not published)

Tagged as `Dataverse-v1.4.0` but not published to NuGet. These changes first ship in 1.5.0; the 1.4.0 tag is not reused.

### Changed

- Refreshed Microsoft.Extensions dependencies to 10.0.14 and System.IdentityModel.Tokens.Jwt to 8.23.0 (#1501, #1502).
- Updated SourceLink build tooling to 10.0.403 (#1498). Connection-pool library source and public APIs are unchanged; captured session/pool behavior in this release belongs to the CLI/MCP application layer.

## [1.3.2] - 2026-10-30

### Security

- Update `System.Security.Cryptography.Pkcs` from 10.0.9 to 10.0.13 for [CVE-2026-84008](https://github.com/advisories/GHSA-846f-f764-76mx) and [CVE-2026-70162](https://github.com/advisories/GHSA-2fmv-rv2c-59x7).
- Update `System.Security.Cryptography.Xml` from 8.0.4 to 8.0.5 for [CVE-2026-96960](https://github.com/advisories/GHSA-7hqp-7c5m-4vq6), [CVE-2026-96959](https://github.com/advisories/GHSA-c399-9853-6jmj), [CVE-2026-96958](https://github.com/advisories/GHSA-r955-8q5j-3h83), and [CVE-2026-96222](https://github.com/advisories/GHSA-vrv5-2jg9-4mmr).

### Changed

- Refresh the Dataverse SDK to 1.2.27, MSAL and its cache extensions to 4.90.1, and Microsoft.Extensions dependencies to 10.0.13. SourceLink build tooling is updated to 10.0.401; the library API is unchanged.

## [1.3.1] - 2026-09-12

### Security

- **Patched XML cryptography dependency** — `System.Security.Cryptography.Xml` is updated from 8.0.3 to 8.0.4, the first non-vulnerable 8.x servicing release for the July 2026 high-severity advisories ([#1402](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/1402)).

## [1.3.0] - 2026-07-15

### Added

- **Enriched attribute metadata on `AttributeMetadataDto`** — added `DeprecatedVersion`, `IsAuditEnabled`, `IsCustomizable`, `IsRenameable`, `IsValidForAdvancedFind`, `ExternalName`, `ColumnNumber`, `CreatedOn`, and `ModifiedOn`, mapped from the SDK `AttributeMetadata`. Combined with the already-captured `AttributeOf`, this gives consumers the full attribute picture (including the deterministic auxiliary-attribute marker) rather than a curated subset ([#1369](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1369), [#1368](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1368)).

## [1.2.0] - 2026-06-17

### Added

- **Raw Web API passthrough + draft-app sitemap resolution** — new public `IDataverseClient.GetRawWebApiAsync(queryString, ct)` returns the raw Web API JSON for a relative query, and model-driven-app sitemap resolution now handles draft (unpublished) apps ([#1242](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1242)).
- **Metadata-authoring support for status reasons and local option sets** — `AddStatusReasonRequest`/`UpdateStatusReasonRequest`/`RemoveStatusReasonRequest`/`StatusReasonInfo` for entity status-reason management, and `AddOptionValueRequest`/`UpdateOptionValueRequest`/`DeleteOptionValueRequest` plus the shared `OptionValueDeriver` (publisher-prefix-based option value derivation) for column-scoped (local) Choice option sets ([#1167](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1167), [#1159](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1159), [#1160](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1160), [#1161](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1161)).
- **`DeleteOptionValueRequest` targets by value or label** — `Value` is now nullable and a `Label` selector was added (exactly one required); the service resolves the target against unpublished metadata and throws `OPTION_NOT_FOUND` when unresolved ([#1169](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1169)).
- **`UpdateOptionValueRequest` aligned with `UpdateColumnOptionRequest`** — `Value`/`Label` are now the (exactly-one) target selectors, `NewLabel` carries the updated label (current label preserved when omitted), and `Color` is now forwarded to the SDK ([#1170](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1170)).
- **`Publish` on `CreateTableRequest`/`UpdateTableRequest`/`UpdateColumnRequest`** — opt-in publish of the affected entity after the change, matching `CreateColumnRequest` ([#1171](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1171)).
- **`DryRun` on `AddOptionValueRequest`/`UpdateOptionValueRequest`/`DeleteOptionValueRequest`** — validation-only early exit for global option-value mutations; update/delete still resolve (and therefore validate) the target option before exiting ([#1172](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1172)).
- **Entity icon assignment on `UpdateTableRequest`** — `IconSmallName`/`IconMediumName`/`IconVectorName` carry web-resource logical names that are applied through the SDK `UpdateEntityRequest` on `EntityMetadata` via the new `IDataverseClient.UpdateEntity`, because the Web API `EntityDefinitions` PATCH silently rejects icon updates ([#1244](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1244)).

### Fixed

- **Actionable environment-lock errors on publish** — `PooledClientExtensions` re-throws in-progress import/publish faults as an `InvalidOperationException` with remediation guidance instead of surfacing the opaque platform fault ([#1245](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1245)).
- **M:N relationship create** — populate `IntersectEntitySchemaName` on the create request (defaulting to the relationship schema name) so many-to-many relationship creation no longer fails with `Required field 'IntersectEntitySchemaName' is missing` ([#1018](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1018)).

## [1.1.0] - 2026-04-26

Coordinated `v1.1` release rollover ([#991](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/991)). No functional changes to `PPDS.Dataverse` since `1.0.0` — the package version was advanced for release-train parity.

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
- **SQL parser and FetchXML transpiler (legacy)** — Built-in SQL-to-FetchXML transpilation; superseded by the standalone PPDS.Query engine; entry points retained for backwards compatibility ([#52](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/52)).
- **`ListResult<T>`** — Structured return type for all service `List*` methods: `Items`, `TotalCount`, `WasTruncated`, `FiltersApplied` per Constitution I4 (no silent truncation) ([#651](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/651)).
- **`CallerId` impersonation** — Bulk-operation pipeline accepts `DataverseClientOptions` end-to-end so callers can execute as mapped owners.
- **`ComponentNameResolver`** — Resolves solution component types via `IMetadataQueryService` with per-environment caching; names for Roles, Forms, SiteMaps, ConnectionRoles, and entity-typed components.
- **Early-bound entity classes** — `PluginTracelog`, `ServiceEndpoint`, `CustomAPI`, `CustomAPIRequestParameter`, `CustomAPIResponseProperty`, `EntityDataProvider`, `WebResource`, `Workflow`, `ConnectionReference`, and supporting types. Generated classes replace magic-string attribute access ([#56](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/56), [#149](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/149), [#440](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/440)).
- **Field-level error context** — `BulkOperationError` includes `FieldName` (extracted from error messages) and sanitized `FieldValueDescription` for `EntityReference` lookups.
- **Full `appsettings.json` configuration** for all pool options.
- **`DmlValueCoercer`** — SQL DML values are now coerced to Dataverse SDK types (`EntityReference`, `OptionSetValue`, `Money`, etc.) using cached attribute metadata, fixing `CreateMultiple`/`UpdateMultiple` failures on lookup, choice, money, and boolean columns.
- **DML failure count propagation** — DML results now surface both `affected_rows` and `failed_rows`, enabling callers to detect partial failures and return non-zero exit codes.
- **Default `AcquireTimeout` of 120 s** — Connection acquisition from the pool waits up to 120 s, accommodating queuing on the DOP semaphore during large imports.
- **Pool-managed concurrency** — Batch parallelism is capped at pool capacity via pool-queue blocking at `GetClientAsync()`, preventing oversubscription during throttling.

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Dataverse-v1.4.0...HEAD
[1.4.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Dataverse-v1.3.2...Dataverse-v1.4.0
[1.3.2]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Dataverse-v1.3.1...Dataverse-v1.3.2
[1.3.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Dataverse-v1.3.0...Dataverse-v1.3.1
[1.3.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Dataverse-v1.2.0...Dataverse-v1.3.0
[1.2.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Dataverse-v1.1.0...Dataverse-v1.2.0
[1.1.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Dataverse-v1.0.0...Dataverse-v1.1.0
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Dataverse-v1.0.0
