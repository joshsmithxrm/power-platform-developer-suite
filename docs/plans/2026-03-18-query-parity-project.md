# Query Parity Project Plan

> Project plan extracted from specs/query-parity.md during spec governance restructuring.
> Permanent domain behavior (hints, cross-env, Extension surface) has been absorbed into specs/query.md.
> This file retains project-tracking content: implementation phases, dead code cleanup, daemon migration steps, and error mapping.

---

## Implementation Phases

| Phase | Description | Dependencies |
|-------|-------------|--------------|
| 1 | Daemon uses `SqlQueryService` | None |
| 2 | Query hints integration in `SqlQueryService` | Phase 1 (daemon benefits immediately) |
| 3 | VS Code environment colors | None (can parallel with Phase 2) |
| 4 | Cross-environment query attribution | Phase 1 (needs data source collection from plan) |
| 5 | TDS Endpoint wiring (DI, reporting, UI, error handling) | Phase 1 (needs `SqlQueryService` integration) |

---

## Phase 1: Daemon Uses SqlQueryService

### Core Requirements

1. `RpcMethodHandler.QuerySqlAsync()` delegates ALL query execution to `SqlQueryService.ExecuteAsync()` — no inline transpilation or direct executor calls
2. `RpcMethodHandler.QueryExplainAsync()` delegates to `SqlQueryService.ExplainAsync()` — no inline parser/planner/generator instantiation
3. `RpcMethodHandler.QueryExportAsync()` has two input paths:
   - **SQL input** (`request.Sql`): Delegates transpilation to `SqlQueryService.TranspileSql()` and execution to `SqlQueryService.ExecuteAsync()`
   - **FetchXML input** (`request.FetchXml`): Stays as-is — raw FetchXML is already the final format
4. The daemon wires `RemoteExecutorFactory`, `ProfileResolver`, `EnvironmentSafetySettings`, and `EnvironmentProtectionLevel` on the `SqlQueryService` instance, using the same pattern as `InteractiveSession.GetSqlQueryServiceAsync()` (lines 321-355)
5. The daemon's inline DML safety pre-check (lines 954-1001) is removed — `SqlQueryService.PrepareExecutionAsync()` handles DML safety
6. All bespoke helper methods are deleted (see Dead Code Cleanup section)
7. The `QueryResultResponse` RPC DTO contract is unchanged — `SqlQueryResult` maps to the existing fields
8. TDS routing is controlled via `SqlQueryRequest.UseTdsEndpoint` — the service handles TDS internally

### Primary Flow

**query/sql RPC call (execute mode):**

1. **Validate**: Check `request.Sql` is non-empty
2. **Resolve environment**: Call `WithProfileAndEnvironmentAsync(request.EnvironmentUrl, ...)` to get service provider
3. **Get service**: Get `ISqlQueryService` from provider, cast to `SqlQueryService`
4. **Wire cross-env**: Load `EnvironmentConfigStore` configs, create `ProfileResolutionService`, set `RemoteExecutorFactory` (label -> resolve config -> get provider -> get `IQueryExecutor`)
5. **Wire safety**: Set `ProfileResolver`, `EnvironmentSafetySettings`, `EnvironmentProtectionLevel` from environment config
6. **Build request**: Map RPC `QuerySqlRequest` fields to `SqlQueryRequest`
7. **Execute**: Call `service.ExecuteAsync(sqlRequest, cancellationToken)`
8. **Map response**: Convert `SqlQueryResult` to `QueryResultResponse`
9. **Save history**: Fire-and-forget history save (unchanged)

**query/sql RPC call (showFetchXml mode):**

When `request.ShowFetchXml` is true, use `service.TranspileSql(request.Sql, request.Top)` instead of `ExecuteAsync()` and return the FetchXML string in the response. No plan building, no execution, no wiring needed.

### Error Mapping

| Service Exception | RPC Error Code | Mapping |
|-------------------|---------------|---------|
| `QueryParseException` | `ErrorCodes.Query.ParseError` | Wrap in `RpcException` with parse error details |
| `PpdsException` with `DmlBlocked` ErrorCode | `ErrorCodes.Query.DmlBlocked` | Wrap in `RpcException` with `DmlSafetyErrorData { DmlBlocked = true }` |
| `PpdsException` with `DmlConfirmationRequired` ErrorCode | `ErrorCodes.Query.DmlConfirmationRequired` | Wrap in `RpcException` with `DmlSafetyErrorData { DmlConfirmationRequired = true }` |
| `PpdsException` with `TdsIncompatible` ErrorCode | `ErrorCodes.Query.TdsIncompatible` | Wrap in `RpcException` with error message (Phase 5) |
| `PpdsException` with `TdsConnectionFailed` ErrorCode | `ErrorCodes.Query.TdsConnectionFailed` | Wrap in `RpcException` with error message (Phase 5) |
| `PpdsException` (other) | `ErrorCodes.Query.ExecutionFailed` | Wrap in `RpcException` with error message |
| Other exceptions | Standard StreamJsonRpc error | Let StreamJsonRpc handle (500-level equivalent) |

### Dead Code Cleanup

After switching to `SqlQueryService`, the following code in `RpcMethodHandler.cs` must be deleted:

| Code | Lines | Reason |
|------|-------|--------|
| `TranspileSqlToFetchXml()` private method | 1339-1370 | Replaced by `SqlQueryService.TranspileSql()` |
| Inline DML safety check block | 954-1002 | Handled by `SqlQueryService.PrepareExecutionAsync()` |
| `InjectTopAttribute()` private method | 1456-1473 | Retained — still used by `query/fetch` handler for raw FetchXML TOP injection |
| Inline `QueryParser`/`ExecutionPlanBuilder`/`FetchXmlGeneratorService` in `QueryExplainAsync` | 1261-1266 | Replaced by `SqlQueryService.ExplainAsync()` |
| TDS executor inline construction in `QuerySqlAsync` | 1012-1024 | Handled by `SqlQueryService` TDS routing |
| `FormatExportContent()` private method | 1372-1427 | Move to shared `QueryExportFormatter` utility |
| `ExtractDisplayValue()` private method | 1429-1440 | Moves with `FormatExportContent` |
| `CsvEscape()` private method | 1442-1449 | Moves with `FormatExportContent` |
| `using Microsoft.SqlServer.TransactSql.ScriptDom` import | Line 24 | No longer needed in handler |
| `using PPDS.Query.Transpilation` import | Line 29 | No longer needed in handler |

**Keep:** `MapToResponse()` (refactor to accept `SqlQueryResult`), `MapQueryValue()`, `FireAndForgetHistorySave()`.

### Constraints

- The RPC response contract (`QueryResultResponse`) must not change
- The daemon must create `ProfileResolutionService` per request (or cache and invalidate when environment configs change)
- `SqlQueryService` is registered as transient — each call gets a fresh instance, wiring is per-call

---

## Phase 2: Query Hints Integration

### Changes Per Layer

1. **`SqlQueryService.PrepareExecutionAsync()`**: After `QueryParser.Parse()`, call `QueryHintParser.Parse(fragment)`. Merge overrides into `QueryPlanOptions`. Create `QueryExecutionOptions` from execution-level hints.
2. **`QueryPlanOptions`**: Add `ForceClientAggregation` (bool) and `NoLock` (bool).
3. **`QueryExecutionOptions`** (new type): `BypassPlugins` (bool), `BypassFlows` (bool). Stored on `QueryPlanContext`.
4. **`QueryPlanContext`**: Add `QueryExecutionOptions? ExecutionOptions` property.
5. **`FetchXmlScanNode`**: When `NoLock` is true, inject `no-lock="true"` attribute into FetchXML string before execution.
6. **`IQueryExecutor`**: Add new overload with default interface implementation (backward-compatible).
7. **`QueryExecutor`** (concrete): Override new overload, apply `BypassPlugins`/`BypassFlows` as OrganizationRequest headers.
8. **`FetchXmlScanNode`**: Call new overload, passing `context.ExecutionOptions`.
9. **`ExecutionPlanBuilder.PlanSelect()`**: When `ForceClientAggregation` is true, route to client-side hash group plan.

---

## Phase 3: VS Code Environment Colors

### Changes

1. **Extension host** (`QueryPanel.ts`, `SolutionsPanel.ts`): Include `resolvedColor` from `daemon.envConfigGet()` in the `updateEnvironment` webview message.
2. **Message types** (`message-types.ts`): Add `envColor?: string` to `updateEnvironment` message.
3. **Webview** (`query-panel.ts`, `solutions-panel.ts`): On `updateEnvironment`, set `data-env-color` attribute on toolbar. CSS applies left border color.
4. **CSS**: Map terminal color names to CSS custom properties (13 colors: Red through BrightBlue).

---

## Phase 4: Cross-Environment Query Attribution

### Data Source Collection

After `_planBuilder.Plan()` returns, walk `QueryPlanResult.RootNode` tree to collect `RemoteScanNode.RemoteLabel` values. Local environment is always present.

**Local environment label resolution**: From environment config label, falling back to display name, then URL.

### VS Code Banner

When `dataSources` has 2+ entries, render above results grid: "Data from: PPDS Dev (local) / QA (remote)" with environment colors.

---

## Phase 5: TDS Endpoint Wiring

### DI Registration

#### Daemon Path (`DaemonConnectionPoolManager.CreateProviderFromSources`)

Pass `AuthProfile` and `environmentUrl` as parameters. Create `IPowerPlatformTokenProvider` from profile, wrap as `Func<CancellationToken, Task<string>>`, register `ITdsQueryExecutor` as singleton per-provider.

#### CLI Path (`ServiceRegistration.AddCliApplicationServices`)

Resolve `ResolvedConnectionInfo` from DI, create token provider, register `ITdsQueryExecutor` as transient factory delegate.

### Execution Mode Reporting

- `SqlQueryResult.ExecutionMode` set by walking plan tree (any `TdsScanNode` -> Tds, else Dataverse)
- `SqlQueryStreamChunk.ExecutionMode` set on final chunk for TUI streaming
- `RpcMethodHandler` maps to `queryMode` RPC response field

### TDS Failure Behavior

- **Incompatible query**: `SqlQueryService.PrepareExecutionAsync()` pre-checks before planning. Throws `PpdsException(TdsIncompatible)`.
- **Connection failure**: `TdsScanNode` lets `SqlException` propagate. `SqlQueryService` wraps in `PpdsException(TdsConnectionFailed)`.
- **Design principle**: Never silently substitute Dataverse for TDS.

### VS Code Extension Changes

- Un-comment TDS Read Replica menu item in `query-panel.ts:511-512`
- TDS errors display via existing error banner (no special UI needed)

### TUI Changes

- Status label after execution reflects actual mode: "Returned N rows in Xms via TDS/Dataverse"
- TDS errors handled by existing `ErrorService.ReportError()`

---

## Acceptance Criteria

| ID | Criterion | Status |
|----|-----------|--------|
| AC-01 | Daemon `query/sql` executes via `SqlQueryService.ExecuteAsync()` | Pending |
| AC-02 | Daemon query results include virtual column expansion | Pending |
| AC-03 | `[LABEL].entity` syntax works in daemon | Pending |
| AC-04 | `-- ppds:USE_TDS` routes through TDS endpoint | Pending |
| AC-05 | `-- ppds:NOLOCK` produces `<fetch no-lock="true">` | Pending |
| AC-06 | `-- ppds:BYPASS_PLUGINS` sets header | Pending |
| AC-07 | `-- ppds:BYPASS_FLOWS` sets header | Pending |
| AC-08-13 | Remaining hint ACs (MAX_ROWS, MAXDOP, HASH_GROUP, BATCH_SIZE, precedence, malformed) | Pending |
| AC-14-15 | VS Code environment colors | Pending |
| AC-16-18 | Data source attribution | Pending |
| AC-19 | Hints work identically across all interfaces | Pending |
| AC-20 | Daemon no longer contains inline SQL transpilation | Pending |
| AC-21-29 | Remaining Phase 1-4 ACs | Pending |
| AC-30-48 | Phase 5 TDS wiring ACs | Pending |

---

## Follow-Up Work (Out of Scope)

- GitHub issue in `ppds-docs` for query hints documentation page
- IntelliSense completions for `-- ppds:` prefix in SQL editor
- TDS toggle re-enabled in Phase 5 (pre-existing menu item)
- Environment color in QuickPick environment picker
- `IProgressReporter` for `SqlQueryService` (separate enhancement)
- `query/fetchxml` handler virtual column expansion (tracked separately)
