# Query Parity

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-15
**Code:** [src/PPDS.Cli/Services/Query/](../src/PPDS.Cli/Services/Query/), [src/PPDS.Cli/Commands/Serve/Handlers/](../src/PPDS.Cli/Commands/Serve/Handlers/), [src/PPDS.Extension/src/](../src/PPDS.Extension/src/)

---

## Overview

The daemon's RPC `query/sql` handler bypasses `SqlQueryService` entirely — it inlines its own transpile-and-execute pipeline. This violates Constitution A2 ("Application Services are the single code path") and causes silent feature disparity between TUI/CLI and the VS Code extension. This spec aligns all interfaces to use the shared `SqlQueryService`, integrates the orphaned `QueryHintParser`, surfaces environment colors in VS Code, and adds cross-environment query source attribution.

### Goals

- **Single code path**: All daemon query RPC handlers (`query/sql`, `query/explain`, `query/export`) use `SqlQueryService` — no bespoke query pipelines
- **Query hints**: Integrate `QueryHintParser` into `SqlQueryService.PrepareExecutionAsync()` so all 8 hints work across all interfaces
- **Cross-environment queries in VS Code**: Wire `RemoteExecutorFactory` on the daemon's service instance so `[LABEL].entity` syntax works
- **Environment color rendering**: Surface configured environment colors as a toolbar accent in VS Code webview panels
- **Cross-environment attribution**: Show which environments contributed data when a query touches multiple sources

### Non-Goals

- VS Code UI controls (buttons/toggles) for individual query hints — hints work via SQL comment syntax; UI discoverability is deferred. **Exception:** TDS Read Replica toggle is re-enabled in Phase 5 (it's a pre-existing menu item, not a new UI control)
- New hint types beyond the 8 already defined in `QueryHintParser`
- Changes to the TUI environment color rendering (already works)
- IntelliSense/completions for `-- ppds:` hint prefix (tracked separately)
- Documentation on ppds-docs site (tracked via separate GitHub issue)
- `IProgressReporter` for `SqlQueryService` — query execution does not currently accept a progress reporter; adding one for long-running queries (large aggregates, cross-env joins) is a separate enhancement
- `query/fetchxml` handler alignment — executes raw FetchXML directly via `IQueryExecutor`, which is appropriate since FetchXML is already the final format (no transpilation/planning needed). The only missing feature is virtual column expansion; tracked separately

---

## Architecture

### Current State (Broken)

```
CLI/TUI                              Daemon (VS Code)
  │                                    │
  ▼                                    ▼
SqlQueryService.ExecuteAsync()       TranspileSqlToFetchXml() ← private helper
  │                                    │
  ├─ Parse (QueryParser)               ├─ Parse (QueryParser)
  ├─ DML Safety (DmlSafetyGuard)       ├─ DML Safety (inline, duplicated)
  ├─ Plan (ExecutionPlanBuilder)       │  (no planning step)
  ├─ Execute (plan nodes)              ├─ Execute (IQueryExecutor directly)
  └─ Expand (virtual columns)         │  (no expansion step)
                                       │
  Features: cross-env, aggregates,     Features: basic single-env FetchXML
  HAVING, prefetch, virtual cols       only
```

### Target State

```
CLI ─────┐
TUI ─────┤
Daemon ──┘
    │
    ▼
SqlQueryService.PrepareExecutionAsync()
    │
    ├─ Parse (QueryParser)
    ├─ Extract hints (QueryHintParser)
    ├─ DML Safety (DmlSafetyGuard)
    ├─ Build plan (ExecutionPlanBuilder)
    │    ├─ Cross-env → RemoteScanNode
    │    ├─ TDS → TdsScanNode
    │    ├─ Aggregates → partitioned plan
    │    └─ Standard → FetchXmlScanNode
    ├─ Execute plan (with hint-influenced options)
    └─ Expand results (virtual columns)
    │
    ▼
SqlQueryResult (with DataSources metadata)
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `RpcMethodHandler.QuerySqlAsync()` | Thin wrapper: build `SqlQueryRequest`, call `SqlQueryService`, map to RPC response |
| `SqlQueryService.PrepareExecutionAsync()` | Parse → extract hints → safety check → plan → execute (shared by all interfaces) |
| `QueryHintParser` | Extract `-- ppds:*` comments and `OPTION()` hints from parsed AST |
| `QueryHintOverrides` | Nullable override bag — null means "use default" |
| `QueryExecutionOptions` | Execution-level options (BypassPlugins, BypassFlows, NoLock) threaded to executor |
| `QueryPlanOptions` | Plan-level options (UseTds, ForceClientAggregation, MaxRows, etc.) |
| `FetchXmlScanNode` | Injects `no-lock="true"` into FetchXML string; passes `QueryExecutionOptions` to executor overload |
| `QueryPlanContext` | Carries `QueryExecutionOptions` through plan execution |
| `IQueryExecutor` | New default-implementation overload accepts `QueryExecutionOptions?`; existing overload unchanged |
| `QueryExecutor` | Concrete override applies BypassPlugins/BypassFlows as OrganizationRequest headers |

### Dependencies

- Depends on: [query.md](./query.md), [per-panel-environment-scoping.md](./per-panel-environment-scoping.md), [connection-pooling.md](./connection-pooling.md)
- Aligns with: [CONSTITUTION.md](./CONSTITUTION.md) A1, A2

---

## Specification

### Phase 1: Daemon Uses SqlQueryService

#### Core Requirements

1. `RpcMethodHandler.QuerySqlAsync()` delegates ALL query execution to `SqlQueryService.ExecuteAsync()` — no inline transpilation or direct executor calls
2. `RpcMethodHandler.QueryExplainAsync()` delegates to `SqlQueryService.ExplainAsync()` — no inline parser/planner/generator instantiation
3. `RpcMethodHandler.QueryExportAsync()` has two input paths:
   - **SQL input** (`request.Sql`): Delegates transpilation to `SqlQueryService.TranspileSql()` and execution to `SqlQueryService.ExecuteAsync()` — replaces the current `TranspileSqlToFetchXml()` call
   - **FetchXML input** (`request.FetchXml`): Stays as-is — raw FetchXML is already the final format, no transpilation or planning needed. Same rationale as `query/fetchxml` (see Non-Goals). The direct `IQueryExecutor.ExecuteFetchXmlAsync()` call with manual paging is appropriate here.
4. The daemon wires `RemoteExecutorFactory`, `ProfileResolver`, `EnvironmentSafetySettings`, and `EnvironmentProtectionLevel` on the `SqlQueryService` instance, using the same pattern as `InteractiveSession.GetSqlQueryServiceAsync()` (lines 321-355)
5. The daemon's inline DML safety pre-check (lines 954-1001) is removed — `SqlQueryService.PrepareExecutionAsync()` handles DML safety
6. All bespoke helper methods are deleted (see Dead Code Cleanup section)
7. The `QueryResultResponse` RPC DTO contract is unchanged — `SqlQueryResult` maps to the existing fields
8. TDS routing is controlled via `SqlQueryRequest.UseTdsEndpoint` — the service handles TDS internally

#### Primary Flow

**query/sql RPC call (execute mode):**

1. **Validate**: Check `request.Sql` is non-empty
2. **Resolve environment**: Call `WithProfileAndEnvironmentAsync(request.EnvironmentUrl, ...)` to get service provider
3. **Get service**: Get `ISqlQueryService` from provider, cast to `SqlQueryService`
4. **Wire cross-env**: Load `EnvironmentConfigStore` configs, create `ProfileResolutionService`, set `RemoteExecutorFactory` (label → resolve config → get provider → get `IQueryExecutor`)
5. **Wire safety**: Set `ProfileResolver`, `EnvironmentSafetySettings`, `EnvironmentProtectionLevel` from environment config
6. **Build request**: Map RPC `QuerySqlRequest` fields to `SqlQueryRequest`
7. **Execute**: Call `service.ExecuteAsync(sqlRequest, cancellationToken)`
8. **Map response**: Convert `SqlQueryResult` to `QueryResultResponse` — map `sqlQueryResult.Result` (records, columns, paging, timing) and `sqlQueryResult.TranspiledFetchXml` (executed FetchXML). The source object type changes from `QueryResult` to `SqlQueryResult` wrapper, but the RPC response fields remain identical.
9. **Save history**: Fire-and-forget history save (unchanged)

**query/sql RPC call (showFetchXml mode):**

When `request.ShowFetchXml` is true, the caller wants only the transpiled FetchXML without execution. This code path uses `service.TranspileSql(request.Sql, request.Top)` instead of `ExecuteAsync()` and returns the FetchXML string in the response. No plan building, no execution, no wiring needed.

#### Error Mapping

When `SqlQueryService` throws exceptions, the daemon must catch and remap to existing RPC error codes to preserve the extension's error handling:

| Service Exception | RPC Error Code | Mapping |
|-------------------|---------------|---------|
| `QueryParseException` | `ErrorCodes.Query.ParseError` | Wrap in `RpcException` with parse error details |
| `PpdsException` with `DmlBlocked` ErrorCode | `ErrorCodes.Query.DmlBlocked` | Wrap in `RpcException` with `DmlSafetyErrorData { DmlBlocked = true }` |
| `PpdsException` with `DmlConfirmationRequired` ErrorCode | `ErrorCodes.Query.DmlConfirmationRequired` | Wrap in `RpcException` with `DmlSafetyErrorData { DmlConfirmationRequired = true }` |
| `PpdsException` (other) | `ErrorCodes.Query.ExecutionFailed` | Wrap in `RpcException` with error message |
| Other exceptions | Standard StreamJsonRpc error | Let StreamJsonRpc handle (500-level equivalent) |

The daemon's `QuerySqlAsync` method wraps the `service.ExecuteAsync()` call in a try/catch that performs this mapping. This replaces the current inline DML safety checks that throw `RpcException` directly.

#### Dead Code Cleanup

After switching to `SqlQueryService`, the following code in `RpcMethodHandler.cs` must be deleted:

| Code | Lines | Reason |
|------|-------|--------|
| `TranspileSqlToFetchXml()` private method | 1339-1370 | Replaced by `SqlQueryService.TranspileSql()` |
| Inline DML safety check block | 954-1002 | Handled by `SqlQueryService.PrepareExecutionAsync()` |
| `InjectTopAttribute()` private method | 1456-1473 | Retained — still used by `query/fetch` handler for raw FetchXML TOP injection (outside this spec's scope) |
| Inline `QueryParser`/`ExecutionPlanBuilder`/`FetchXmlGeneratorService` in `QueryExplainAsync` | 1261-1266 | Replaced by `SqlQueryService.ExplainAsync()` |
| TDS executor inline construction in `QuerySqlAsync` | 1012-1024 | Handled by `SqlQueryService` TDS routing |
| `FormatExportContent()` private method | 1372-1427 | Move to a shared `QueryExportFormatter` utility or keep in handler if only used there — but decouple from execution |
| `ExtractDisplayValue()` private method | 1429-1440 | Moves with `FormatExportContent` |
| `CsvEscape()` private method | 1442-1449 | Moves with `FormatExportContent` |
| `using Microsoft.SqlServer.TransactSql.ScriptDom` import | Line 24 | No longer needed in handler |
| `using PPDS.Query.Transpilation` import | Line 29 | No longer needed in handler |

**`MapToResponse()` (lines 1475-1502)**: Refactor, not delete. Change to accept `SqlQueryResult` instead of `QueryResult` — unwrap via `sqlQueryResult.Result` and `sqlQueryResult.TranspiledFetchXml`. Still needed for `query/fetchxml` handler which returns `QueryResult` directly.

**`MapQueryValue()` (lines 1504-1532)**: Keep — pure transformation logic for `QueryValue` → RPC response objects.

**`FireAndForgetHistorySave()` (lines 1299-1338)**: Keep — orthogonal to execution, still needed for all query endpoints.

#### Constraints

- The RPC response contract (`QueryResultResponse`) must not change — this is a backend-only fix
- The daemon must create `ProfileResolutionService` per request (or cache and invalidate when environment configs change) — environment labels can be reconfigured at any time
- `SqlQueryService` is registered as transient — each call gets a fresh instance, wiring is per-call

### Phase 2: Query Hints Integration

#### Core Requirements

1. Both `SqlQueryService.PrepareExecutionAsync()` and `SqlQueryService.ExplainAsync()` call `QueryHintParser.Parse(fragment)` after parsing SQL, before building the execution plan — so `EXPLAIN` output reflects hint-influenced plans
2. Plan-level hints (`USE_TDS`, `MAX_ROWS`, `MAXDOP`, `HASH_GROUP`) override corresponding fields in `QueryPlanOptions`
3. FetchXML-level hints (`NOLOCK`) are applied in `FetchXmlScanNode` by injecting `no-lock="true"` into the FetchXML string before execution. `NoLock` is a new property on `QueryPlanOptions` (does not exist today), threaded through to `FetchXmlScanNode` at plan construction.
4. Execution-level hints (`BYPASS_PLUGINS`, `BYPASS_FLOWS`) are carried via `QueryExecutionOptions` on `QueryPlanContext` so plan nodes have them available. `FetchXmlScanNode` passes them to a new non-breaking default-implementation overload on `IQueryExecutor` (see Changes Per Layer item 6 below). Existing implementations are unaffected because the default delegates to the existing method.
5. DML-level hints (`BATCH_SIZE`) override the batch size for bulk DML operations
6. Inline hints override caller-provided settings — the query text is the user's explicit intent

#### Hint Flow

**Plan-level hints → QueryPlanOptions:**

| Hint | Target Field | Behavior |
|------|-------------|----------|
| `USE_TDS` | `UseTdsEndpoint` | `true` overrides to TDS routing |
| `MAX_ROWS` | `MaxRows` | Overrides caller-provided TopOverride |
| `MAXDOP` | `PoolCapacity` | Caps parallelism (cannot exceed pool capacity) |
| `HASH_GROUP` | `ForceClientAggregation` (new) | Forces aggregate queries to use client-side hash grouping |

**FetchXML-level hints → FetchXmlGenerator:**

| Hint | FetchXML Effect |
|------|----------------|
| `NOLOCK` | `<fetch no-lock="true" ...>` attribute on root element |

**Execution-level hints → QueryExecutionOptions (new type):**

| Hint | OrganizationRequest Header |
|------|---------------------------|
| `BYPASS_PLUGINS` | `BypassCustomPluginExecution = true` |
| `BYPASS_FLOWS` | `SuppressCallbackRegistrationExpanderJob = true` |

**DML-level hints → DML execution:**

| Hint | Effect |
|------|--------|
| `BATCH_SIZE` | Overrides default batch size for bulk CreateMultiple/UpdateMultiple/DeleteMultiple |

#### Changes Per Layer

1. **`SqlQueryService.PrepareExecutionAsync()`**: After `QueryParser.Parse()`, call `QueryHintParser.Parse(fragment)`. Merge overrides into `QueryPlanOptions`. Create `QueryExecutionOptions` from execution-level hints. Thread both through to execution.

2. **`QueryPlanOptions`**: Add two new properties (neither exists today):
   - `ForceClientAggregation` (bool, default false) — forces aggregate queries to client-side hash grouping
   - `NoLock` (bool, default false) — signals `FetchXmlScanNode` to inject `no-lock="true"`
   Other plan-level hints map to existing fields (`UseTdsEndpoint`, `MaxRows`, `PoolCapacity`).

3. **`QueryExecutionOptions`** (new type): `BypassPlugins` (bool), `BypassFlows` (bool). Stored on `QueryPlanContext` so plan nodes can read them at execution time.

4. **`QueryPlanContext`**: Add `QueryExecutionOptions? ExecutionOptions` property. Set by `SqlQueryService` before plan execution.

5. **`FetchXmlScanNode`**: When `NoLock` is true (from `QueryPlanOptions` at construction), inject `no-lock="true"` attribute into the FetchXML string before calling `IQueryExecutor.ExecuteFetchXmlAsync()`. This is string manipulation on the already-generated FetchXML — no `IQueryExecutor` interface change needed.

6. **`IQueryExecutor`**: Add a new overload with a default interface implementation (requires .NET 8+, which all non-plugin code targets):
   ```csharp
   Task<QueryResult> ExecuteFetchXmlAsync(
       string fetchXml, int? pageNumber, string? pagingCookie,
       bool includeCount, QueryExecutionOptions? executionOptions,
       CancellationToken cancellationToken = default)
   {
       // Default: ignore options, call existing method
       return ExecuteFetchXmlAsync(fetchXml, pageNumber, pagingCookie,
           includeCount, cancellationToken);
   }
   ```
   This is backward-compatible: existing implementations (including test fakes and `RemoteScanNode`'s executor) don't break because the default implementation delegates to the existing method. Only `QueryExecutor` (the concrete Dataverse implementation) overrides it to apply headers.

7. **`QueryExecutor` (concrete implementation)**: Override the new overload. When `executionOptions?.BypassPlugins` is true, set `BypassCustomPluginExecution = true` on the `OrganizationRequest`. Same for `BypassFlows` → `SuppressCallbackRegistrationExpanderJob`.

8. **`FetchXmlScanNode`**: Call the new overload, passing `context.ExecutionOptions` from `QueryPlanContext`. Existing callers that don't use hints pass `null` (or use the original overload).

9. **`ExecutionPlanBuilder.PlanSelect()`**: When `ForceClientAggregation` is true and query has aggregates, route to client-side hash group plan regardless of record count.

#### Precedence Rule

Inline hints (from SQL comments) override caller-provided settings. If the RPC sends `useTds=false` but the SQL contains `-- ppds:USE_TDS`, the hint wins. Rationale: the query text is the user's explicit intent for that specific query.

#### Error Handling

- Malformed hint values (e.g., `-- ppds:BATCH_SIZE abc`) are silently ignored — the hint is skipped, query proceeds normally
- Unrecognized hint names after `-- ppds:` prefix are silently ignored
- Valid hint with invalid context (e.g., `BATCH_SIZE` on a SELECT) has no effect — no error

### Phase 3: VS Code Environment Colors

#### Core Requirements

1. The `updateEnvironment` webview message includes `envColor` — the resolved color string from environment config
2. The webview toolbar renders a 4px left border in the environment's configured color
3. Colors map terminal color names (Red, Green, Yellow, Cyan, Blue, Gray, Brown, White, Bright* variants) to CSS values
4. Single-environment queries use the toolbar color. The color is always visible when an environment is selected.
5. When no explicit color is configured, the type-based default applies (Production→Red, Sandbox→Brown, Development→Green, Test→Yellow, Trial→Cyan, Unknown→Gray)

#### Changes

1. **Extension host** (`QueryPanel.ts`, `SolutionsPanel.ts`): When resolving environment, include `resolvedColor` from `daemon.envConfigGet()` in the `updateEnvironment` message to webview.

2. **Message types** (`message-types.ts`): Add `envColor?: string` to the `updateEnvironment` host-to-webview message type.

3. **Webview** (`query-panel.ts`, `solutions-panel.ts`): On `updateEnvironment` message, set `data-env-color` attribute on toolbar element. CSS applies left border color from attribute.

4. **CSS**: Map terminal color names to CSS custom properties:

| Terminal Color | CSS Variable | Value |
|---------------|-------------|-------|
| Red | `--env-color-red` | `#e74c3c` |
| Green | `--env-color-green` | `#2ecc71` |
| Yellow | `--env-color-yellow` | `#f1c40f` |
| Cyan | `--env-color-cyan` | `#00bcd4` |
| Blue | `--env-color-blue` | `#3498db` |
| Gray | `--env-color-gray` | `#95a5a6` |
| Brown | `--env-color-brown` | `#8d6e63` |
| White | `--env-color-white` | `#ecf0f1` |
| BrightRed | `--env-color-brightred` | `#ff6b6b` |
| BrightGreen | `--env-color-brightgreen` | `#69db7c` |
| BrightYellow | `--env-color-brightyellow` | `#ffe066` |
| BrightCyan | `--env-color-brightcyan` | `#66d9ef` |
| BrightBlue | `--env-color-brightblue` | `#74b9ff` |

### Phase 4: Cross-Environment Query Attribution

#### Core Requirements

1. `SqlQueryResult` includes a `DataSources` property — list of `QueryDataSource` entries identifying which environments contributed data
2. `QueryResultResponse` RPC DTO includes `dataSources` field serializing the above
3. VS Code webview renders a banner above results when 2+ data sources are present, showing each source label styled with its environment color
4. TUI renders a status indicator when 2+ data sources are present
5. Single-environment queries have one data source entry and show no banner

#### QueryDataSource Type

```csharp
public sealed record QueryDataSource
{
    public required string Label { get; init; }
    public bool IsRemote { get; init; }
}
```

#### Data Source Collection

After `_planBuilder.Plan()` returns, walk the `QueryPlanResult.RootNode` tree to collect `RemoteScanNode.RemoteLabel` values. The local environment is always present. Remote labels are collected from any `RemoteScanNode` in the plan.

**Local environment label resolution**: The local environment's label comes from the environment config (if a label is configured), falling back to the environment's display name (from discovery), then the environment URL as a last resort. This mirrors how the panel toolbar already resolves the environment name for display.

#### VS Code Banner

When `dataSources` has 2+ entries, render above the results grid:

```
Data from: PPDS Dev (local) · QA (remote)
```

Each label is styled with its environment color (using the same CSS custom properties from Phase 3). The banner does not appear for single-environment queries.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Daemon `query/sql` executes via `SqlQueryService.ExecuteAsync()`, not inline transpile-and-execute | `RpcMethodHandlerTests.QuerySql_UsesSharedService` | 🔲 |
| AC-02 | Daemon query results include virtual column expansion (e.g., `owneridname`) | `RpcMethodHandlerTests.QuerySql_ExpandsVirtualColumns` | 🔲 |
| AC-03 | `[LABEL].entity` syntax works in daemon when environment label is configured | `RpcMethodHandlerTests.QuerySql_CrossEnvironment_ResolvesLabel` | 🔲 |
| AC-04 | `-- ppds:USE_TDS` hint routes query through TDS endpoint | `SqlQueryServiceTests.Hints_UseTds_RoutesToTdsEndpoint` | 🔲 |
| AC-05 | `-- ppds:NOLOCK` hint produces `<fetch no-lock="true">` in executed FetchXML | `SqlQueryServiceTests.Hints_NoLock_SetsFetchXmlAttribute` | 🔲 |
| AC-06 | `-- ppds:BYPASS_PLUGINS` hint sets `BypassCustomPluginExecution` header on OrganizationRequest | `QueryExecutorTests.ExecuteWithOptions_BypassPlugins_SetsHeader` | 🔲 |
| AC-07 | `-- ppds:BYPASS_FLOWS` hint sets `SuppressCallbackRegistrationExpanderJob` header | `QueryExecutorTests.ExecuteWithOptions_BypassFlows_SetsHeader` | 🔲 |
| AC-08 | `-- ppds:MAX_ROWS 100` limits result rows to 100 | `SqlQueryServiceTests.Hints_MaxRows_LimitsResults` | 🔲 |
| AC-09 | `-- ppds:MAXDOP 2` caps parallelism to 2 | `SqlQueryServiceTests.Hints_Maxdop_CapsParallelism` | 🔲 |
| AC-10 | `-- ppds:HASH_GROUP` forces client-side aggregation | `ExecutionPlanBuilderTests.Hints_HashGroup_ForcesClientAggregation` | 🔲 |
| AC-11 | `-- ppds:BATCH_SIZE 500` overrides DML batch size | `SqlQueryServiceTests.Hints_BatchSize_OverridesDmlBatch` | 🔲 |
| AC-12 | Inline hints override caller-provided settings (e.g., `useTds=false` + `-- ppds:USE_TDS` → TDS wins) | `SqlQueryServiceTests.Hints_OverrideCallerSettings` | 🔲 |
| AC-13 | Malformed hint values are silently ignored, query proceeds | `SqlQueryServiceTests.Hints_MalformedValue_Ignored` | 🔲 |
| AC-14 | VS Code webview toolbar shows 4px left border in environment color | Visual verification via `@webview-cdp` | 🔲 |
| AC-15 | Environment color defaults to type-based color when no explicit color configured | Visual verification via `@webview-cdp` | 🔲 |
| AC-16 | Cross-environment query result includes `dataSources` with local + remote entries | `SqlQueryServiceTests.CrossEnv_DataSources_IncludesAllEnvironments` | 🔲 |
| AC-17 | VS Code webview shows data source banner when query touches 2+ environments | Visual verification via `@webview-cdp` | 🔲 |
| AC-18 | Single-environment query does not show data source banner | Visual verification via `@webview-cdp` | 🔲 |
| AC-19 | Hints work identically in TUI, CLI, and VS Code (all use `SqlQueryService`) | Manual verification against PPDS Dev | 🔲 |
| AC-20 | Daemon no longer contains inline SQL transpilation or direct FetchXML execution code | Code review — `TranspileSqlToFetchXml` method deleted | 🔲 |
| AC-21 | `ExplainAsync()` reflects hint-influenced plans (e.g., `-- ppds:USE_TDS` changes explain output to show TDS routing) | `SqlQueryServiceTests.Explain_ReflectsHints` | 🔲 |
| AC-22 | `showFetchXml` mode uses `service.TranspileSql()` instead of inline transpilation | `RpcMethodHandlerTests.QuerySql_ShowFetchXml_UsesService` | 🔲 |
| AC-23 | Daemon `query/explain` executes via `SqlQueryService.ExplainAsync()`, not inline parser/planner | `RpcMethodHandlerTests.QueryExplain_UsesSharedService` | 🔲 |
| AC-24 | Daemon `query/export` uses `SqlQueryService` for transpilation and execution | `RpcMethodHandlerTests.QueryExport_UsesSharedService` | 🔲 |
| AC-25 | `SqlQueryService` exceptions map to correct RPC error codes (`ParseError`, `DmlBlocked`, `DmlConfirmationRequired`) | `RpcMethodHandlerTests.QuerySql_ErrorMapping_PreservesRpcCodes` | 🔲 |
| AC-26 | `TranspileSqlToFetchXml()` private method deleted from `RpcMethodHandler` | Code review — method no longer exists | 🔲 |
| AC-27 | `InjectTopAttribute()` private method deleted from `RpcMethodHandler` | Code review — method no longer exists | 🔲 |
| AC-28 | Inline DML safety check block deleted from `QuerySqlAsync` | Code review — block no longer exists | 🔲 |
| AC-29 | Inline `QueryParser`/`ExecutionPlanBuilder`/`FetchXmlGeneratorService` deleted from `QueryExplainAsync` | Code review — instantiations no longer exist | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Unknown environment label | `SELECT * FROM [UNKNOWN].account` | Error: "No environment found matching label 'UNKNOWN'" |
| "dbo" as label | `SELECT * FROM [dbo].account` | Treated as standard SQL schema, executes locally |
| Multiple hints on one query | `-- ppds:NOLOCK`<br>`-- ppds:BYPASS_PLUGINS`<br>`SELECT * FROM account` | Both hints applied |
| Hint on TDS query | `-- ppds:NOLOCK`<br>`-- ppds:USE_TDS`<br>`SELECT * FROM account` | TDS wins; NOLOCK is irrelevant for TDS (SQL Server handles locking) |
| BATCH_SIZE on SELECT | `-- ppds:BATCH_SIZE 500`<br>`SELECT * FROM account` | Hint has no effect, no error |
| Hint with extra whitespace | `--  ppds:NOLOCK` | Parsed correctly (flexible whitespace) |
| Empty environment color | No color configured, type is Sandbox | Toolbar shows Brown (Sandbox default) |
| BYPASS_PLUGINS on cross-env DML | `-- ppds:BYPASS_PLUGINS`<br>`DELETE FROM [QA].account WHERE ...` | Hint applies to the remote executor's OrganizationRequest — `FetchXmlScanNode` reads from `QueryPlanContext.ExecutionOptions` regardless of whether the executor is local or remote |
| HASH_GROUP + USE_TDS | `-- ppds:HASH_GROUP`<br>`-- ppds:USE_TDS`<br>`SELECT region, COUNT(*) FROM account GROUP BY region` | TDS wins — query goes to TDS endpoint, HASH_GROUP is irrelevant (SQL Server handles aggregation) |
| Multi-statement script | `-- ppds:NOLOCK`<br>`SELECT * FROM account; SELECT * FROM contact` | Hint applies to all statements in the batch — `QueryHintParser` walks the entire token stream |
| Local environment label in DataSources | Single-env query on PPDS Dev | `DataSources: [{ Label: "PPDS Dev", IsRemote: false }]` — label resolved from environment config label, falling back to environment display name, then URL |

---

## Core Types

### QueryExecutionOptions (new)

Execution-level options threaded from hint parsing to `IQueryExecutor`. Separate from `QueryPlanOptions` because these affect HOW the query is sent to Dataverse, not how the plan is built.

```csharp
public sealed record QueryExecutionOptions
{
    public bool BypassPlugins { get; init; }
    public bool BypassFlows { get; init; }
}
```

### QueryDataSource (new)

Identifies an environment that contributed data to a query result.

```csharp
public sealed record QueryDataSource
{
    public required string Label { get; init; }
    public bool IsRemote { get; init; }
}
```

### QueryHintOverrides (existing, unchanged)

Already defined in `src/PPDS.Query/Planning/QueryHintParser.cs:124-142`. All nullable properties — null means "use default."

---

## API/Contracts

### RPC Changes

**`query/sql` request** — no changes to the request DTO. Hints come from the SQL text.

**`query/sql` response** — additions (backward compatible):

| Field | Type | Description |
|-------|------|-------------|
| `dataSources` | `QueryDataSourceDto[]?` | Environments that contributed data (null for single-env) |
| `appliedHints` | `string[]?` | List of hint names that were applied (e.g., `["NOLOCK", "BYPASS_PLUGINS"]`) — useful for debugging |

**`updateEnvironment` webview message** — addition:

| Field | Type | Description |
|-------|------|-------------|
| `envColor` | `string?` | Resolved environment color name (e.g., "Red", "Green") |

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Unknown environment label | `[LABEL].entity` where label not in `EnvironmentConfigStore` | Error message: "No environment found matching label '{label}'. Configure a label via `ppds env config`." |
| Remote environment auth failure | Label resolves but connection fails | Standard auth error from connection pool |
| Malformed hint value | `-- ppds:BATCH_SIZE abc` | Hint silently ignored, query proceeds |

### Recovery Strategies

- **Unknown label**: User-actionable error message with guidance to configure label via CLI or VS Code command
- **Auth failure**: Standard connection error handling — profile's credentials used for all environments
- **Malformed hints**: Silent skip — matches SQL Server behavior for unrecognized hints

---

## Design Decisions

### Why Replace the Daemon's Query Pipeline Instead of Patching It?

**Context:** The daemon's `QuerySqlAsync` inlines its own transpile-and-execute logic, missing features the shared `SqlQueryService` provides.

**Decision:** Delete the bespoke code path, use `SqlQueryService`.

**Alternatives considered:**
- Patch the daemon with individual feature support (cross-env wiring, hint parsing, etc.): Rejected — perpetuates the A2 violation, requires duplicating every future `SqlQueryService` enhancement in the daemon
- Create a shared helper that both TUI and daemon call: Rejected — YAGNI, only two callers, the shared service already exists

**Consequences:**
- Positive: All current and future `SqlQueryService` features automatically available in VS Code
- Positive: Less code to maintain — daemon's query path becomes a thin mapping layer
- Negative: One-time migration effort to ensure response mapping is correct

### Why Inline Hints Override Caller Settings?

**Context:** When `useTds=false` is sent as an RPC parameter but the SQL contains `-- ppds:USE_TDS`, which wins?

**Decision:** Inline hints win.

**Alternatives considered:**
- RPC parameters win: Rejected — the query text is what the user is looking at and editing. If they typed a hint, that's their intent for this query.
- Error on conflict: Rejected — too strict, hints should feel lightweight

**Consequences:**
- Positive: Query text is self-contained and portable — copy a query between interfaces and hints travel with it
- Negative: UI controls (like a TDS toggle) could be confusing if a hint in the SQL overrides them — mitigated by returning `appliedHints` in the response so the UI can indicate which hints are active

### Why a 4px Left Border for Environment Color?

**Context:** Environment colors are configured but not rendered in VS Code.

**Decision:** 4px left border on the panel toolbar.

**Alternatives considered:**
- Full toolbar background tint: Rejected — interferes with text readability, clashes with VS Code theming
- Status bar item: Rejected — per-panel environment scoping spec removed status bar items in favor of per-panel pickers
- Colored dot next to environment name: Viable alternative but less visible at a glance

**Consequences:**
- Positive: Always visible, non-intrusive, works with all 13 colors
- Positive: Same visual language as VS Code's remote indicator (colored bar)
- Negative: Subtle — users must know to look for it. Mitigated by the environment name also being visible in the toolbar.

---

## Related Specs

- [query.md](./query.md) — Core query pipeline this spec aligns the daemon to use
- [per-panel-environment-scoping.md](./per-panel-environment-scoping.md) — Per-panel environment model this spec extends with color rendering
- [connection-pooling.md](./connection-pooling.md) — Connection pool used by `RemoteExecutorFactory` for cross-env queries
- [CONSTITUTION.md](./CONSTITUTION.md) — A2 violation that motivates Phase 1

---

## Implementation Phases

| Phase | Description | Dependencies |
|-------|-------------|--------------|
| 1 | Daemon uses `SqlQueryService` | None |
| 2 | Query hints integration in `SqlQueryService` | Phase 1 (daemon benefits immediately) |
| 3 | VS Code environment colors | None (can parallel with Phase 2) |
| 4 | Cross-environment query attribution | Phase 1 (needs data source collection from plan) |
| 5 | TDS Endpoint wiring (DI, reporting, UI, fallback) | Phase 1 (needs `SqlQueryService` integration) |

---

## Follow-Up Work (Out of Scope)

- GitHub issue in `ppds-docs` for query hints documentation page
- IntelliSense completions for `-- ppds:` prefix in SQL editor
- ~~VS Code UI controls (buttons/toggles) for hint discoverability~~ TDS toggle re-enabled in Phase 5
- Environment color in QuickPick environment picker
- Update `query.md` Non-Goals/Unsupported Features to reflect current reality (HAVING and cross-env are now supported)

---

## Phase 5: TDS Endpoint Wiring

### Overview

The TDS (Tabular Data Stream) endpoint implementation is 95% complete — `TdsQueryExecutor`, `TdsScanNode`, `TdsCompatibilityChecker`, and `ExecutionPlanBuilder` routing logic all exist. But `ITdsQueryExecutor` is never registered in DI, so it resolves to `null` everywhere, TDS routing silently falls through to Dataverse, and all UIs hardcode `queryMode = "dataverse"`.

This phase completes the last mile: DI registration, honest execution mode reporting, UI toggle re-enablement, and graceful fallback behavior.

### Goals

- **TDS works end-to-end**: Queries actually execute via TDS Endpoint when the user requests it
- **Honest reporting**: UI shows the actual execution path (TDS, Dataverse, or fallback) — never lies
- **Graceful degradation**: TDS incompatibility or connection failure falls back to Dataverse with clear user notification

### Non-Goals

- TDS write support (TDS Endpoint is read-only by design)
- Auto-detection of TDS availability per environment (user opts in explicitly)
- TDS connection pooling (single connection per query is sufficient for read-only workloads)

### Core Requirements

1. `ITdsQueryExecutor` is registered in DI for both the daemon path (`DaemonConnectionPoolManager.CreateProviderFromSources()`) and CLI path (`ServiceRegistration.AddCliApplicationServices()`)
2. `TdsQueryExecutor` is per-environment — when the daemon switches environments, the TDS executor must target the new environment's org URL and auth tokens
3. `SqlQueryResult` carries a `QueryExecutionMode` property set by the execution pipeline, not guessed by the RPC handler
4. `RpcMethodHandler` maps `SqlQueryResult.ExecutionMode` to the existing `queryMode` RPC response field instead of hardcoding `"dataverse"`
5. When TDS is requested but the query is incompatible, execution falls back to Dataverse with an honest `queryMode` and fallback reason
6. When TDS connection fails (endpoint disabled on environment), execution falls back to Dataverse with an honest `queryMode` and failure reason
7. The VS Code extension's TDS Read Replica menu item is re-enabled (currently commented out in `query-panel.ts:511-512`)
8. The TUI status label reflects the actual execution mode from query results, not the toggle state

### DI Registration

#### Token Provider Pattern

`TdsQueryExecutor` needs `Func<CancellationToken, Task<string>>` — a function returning a Dataverse-scoped access token. Both registration sites create this by wrapping `IPowerPlatformTokenProvider.GetTokenForResourceAsync()`:

```csharp
// Create token provider from profile (same pattern as ConnectionService registration)
var tokenProvider = PowerPlatformTokenProvider.FromProfile(profile);
// or FromProfileWithSecret(profile, secret) for SPN auth

// Adapt to TdsQueryExecutor's Func signature
Func<CancellationToken, Task<string>> tdsTokenFunc = async ct =>
{
    var token = await tokenProvider.GetTokenForResourceAsync(orgUrl, ct)
        .ConfigureAwait(false);
    return token.AccessToken;
};
```

#### Daemon Path (`DaemonConnectionPoolManager.CreateProviderFromSources`)

The daemon creates a `ServiceProvider` per environment in `CreateProviderFromSources()`. The `CachedPoolEntry` carries `EnvironmentUrl` and `CredentialStore`, and the `ProfileConnectionSource[]` sources carry the `AuthProfile`. Registration:

1. Extract the `AuthProfile` from the first `ProfileConnectionSource` (via the `IConnectionSource[]` sources — these are `ProfileConnectionSourceAdapter` wrappers)
2. Create an `IPowerPlatformTokenProvider` from the profile (same pattern as `ServiceRegistration.cs:104-133`)
3. Create the `Func<CancellationToken, Task<string>>` wrapper
4. Register `ITdsQueryExecutor` as singleton per-provider (the provider is already per-environment)

**Key constraint:** `ProfileConnectionSourceAdapter` wraps `ProfileConnectionSource` but does not expose the `Profile` property. Two options:
- **Option A**: Add a `Profile` property to `ProfileConnectionSourceAdapter` (expose the wrapped source's profile)
- **Option B**: Pass the `AuthProfile` and `environmentUrl` as parameters to `CreateProviderFromSources()`

Option B is cleaner — the caller (`CreatePoolEntryAsync`) already has the profile and environment URL.

#### CLI Path (`ServiceRegistration.AddCliApplicationServices`)

The CLI path has `ResolvedConnectionInfo` in DI (containing `Profile` and `EnvironmentUrl`). Registration follows the same pattern as the existing `IConnectionService` factory (lines 88-140):

1. Resolve `ResolvedConnectionInfo` from DI
2. Create `IPowerPlatformTokenProvider` from profile
3. Register `ITdsQueryExecutor` as transient factory delegate

### Execution Mode Reporting

#### QueryExecutionMode Enum (new)

```csharp
/// <summary>
/// The actual execution path used for a query.
/// </summary>
public enum QueryExecutionMode
{
    /// <summary>Query executed via FetchXML against Dataverse Web API.</summary>
    Dataverse,

    /// <summary>Query executed via TDS Endpoint (SQL Server wire protocol).</summary>
    Tds,

    /// <summary>
    /// TDS was requested but query fell back to Dataverse.
    /// Check FallbackReason for details.
    /// </summary>
    DataverseFallback
}
```

**Location:** `src/PPDS.Cli/Services/Query/QueryExecutionMode.cs`

#### SqlQueryResult Changes

Add two properties:

```csharp
/// <summary>
/// The actual execution path used. Null for transpile-only or dry-run results.
/// </summary>
public QueryExecutionMode? ExecutionMode { get; init; }

/// <summary>
/// When ExecutionMode is DataverseFallback, explains why TDS could not be used.
/// Null when TDS was not requested or was used successfully.
/// </summary>
public string? TdsFallbackReason { get; init; }
```

#### Setting ExecutionMode in SqlQueryService

After plan execution in `SqlQueryService.ExecuteAsync()` and the streaming path:

1. Walk `planResult.RootNode` tree
2. If any node is `TdsScanNode` → `QueryExecutionMode.Tds`
3. Otherwise → `QueryExecutionMode.Dataverse`

For fallback cases (TDS requested but incompatible), the `ExecutionPlanBuilder` already falls through from the TDS routing block (lines 174-185) to FetchXML. To detect this, `SqlQueryService.PrepareExecutionAsync()` checks: if `request.UseTdsEndpoint == true` but the plan contains no `TdsScanNode`, it's a fallback. The compatibility reason comes from calling `TdsCompatibilityChecker.CheckCompatibility()` on the original SQL.

#### RPC Response Mapping

`RpcMethodHandler` maps the new properties:

```csharp
// Replace hardcoded "dataverse"
mapped.QueryMode = result.ExecutionMode switch
{
    QueryExecutionMode.Tds => "tds",
    QueryExecutionMode.Dataverse => "dataverse",
    QueryExecutionMode.DataverseFallback => "dataverse",
    _ => "dataverse"
};

// New optional field for fallback messaging
mapped.TdsFallbackReason = result.TdsFallbackReason;
```

### TDS Fallback Behavior

#### Incompatible Query

When `UseTdsEndpoint = true` and `TdsCompatibilityChecker` returns non-Compatible:

1. `ExecutionPlanBuilder` skips TDS routing (existing behavior — lines 174-185)
2. Query proceeds via FetchXML (no user-visible error)
3. `SqlQueryService` detects the fallback and sets:
   - `ExecutionMode = QueryExecutionMode.DataverseFallback`
   - `TdsFallbackReason = "Query is incompatible with TDS Endpoint: {reason}"` where reason is `IncompatibleDml`, `IncompatibleEntity`, or `IncompatibleFeature`
4. Logs: `"TDS requested but query is incompatible ({reason}), falling back to Dataverse"`

#### Connection Failure

When `TdsScanNode` executes but `SqlConnection.OpenAsync` throws:

1. `TdsQueryExecutor.ExecuteSqlAsync()` currently throws `InvalidOperationException` for incompatible queries, and lets `SqlException` propagate for connection failures
2. **Change:** `TdsScanNode.ExecuteAsync()` catches `SqlException` / `InvalidOperationException` from the TDS executor
3. Falls back to Dataverse: re-plan without TDS and execute via FetchXML
4. Sets `ExecutionMode = DataverseFallback` and `TdsFallbackReason = "TDS connection failed: {message}"`
5. Logs the failure at Warning level

**Implementation note:** The fallback re-plan happens in `TdsScanNode.ExecuteAsync()` or in `SqlQueryService` with a retry. The cleaner approach is in `SqlQueryService`: wrap `_planExecutor.ExecuteAsync()` in a try/catch, and on TDS failure, re-plan with `UseTdsEndpoint = false` and re-execute.

### VS Code Extension Changes

#### Re-enable TDS Menu Item (`query-panel.ts`)

Un-comment lines 511-512:

```typescript
{ label: '', action: 'separator' },
{ label: 'TDS Read Replica', action: 'toggleTds', checked: useTds },
```

Remove the TODO comment on line 510.

The existing `toggleTds` case handler (line 544-545) and `useTds` state variable (line 115) are already correct.

#### Show Fallback Reason

When `queryMode === 'dataverse'` and `tdsFallbackReason` is present in the response, the status text should show:

```
5 rows in 42ms via Dataverse (TDS incompatible)
```

Instead of just:

```
5 rows in 42ms via Dataverse
```

This requires:
1. Adding `tdsFallbackReason?: string` to the `QueryResult` TypeScript type (`types.ts:114`)
2. Updating `query-panel.ts:878` mode text logic to include fallback reason
3. Updating `notebookResultRenderer.ts:44` mode label logic similarly

#### RPC Response DTO

Add to `QueryResultResponse`:

```csharp
[JsonPropertyName("tdsFallbackReason")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? TdsFallbackReason { get; set; }
```

### TUI Changes

#### Status Label After Execution (`SqlQueryScreen.cs`)

The current `ToggleTdsEndpoint()` method (lines 812-818) sets the status label based on toggle state. After query execution, update the label to reflect the actual result:

```csharp
// After query execution, update status to reflect actual mode
var modeText = result.ExecutionMode switch
{
    QueryExecutionMode.Tds => "Mode: TDS Read Replica (active)",
    QueryExecutionMode.DataverseFallback =>
        $"Mode: Dataverse (TDS fallback: {result.TdsFallbackReason ?? "unknown"})",
    _ => "Mode: Dataverse (real-time)"
};
_statusLabel.Text = modeText;
```

This requires `SqlQueryResult` to be accessible in the query result handler. The `ExecuteQueryAsync` method in `SqlQueryScreen.cs` already has access to the result.

### Constraints

- `TdsQueryExecutor` is per-environment — must not be registered as a global singleton in the daemon
- The `Func<CancellationToken, Task<string>>` token provider must use the same MSAL token cache as the connection pool to avoid extra auth prompts
- `QueryExecutionMode` flows through `SqlQueryResult` only — it is not a property of `QueryResult` (which is the raw Dataverse result type)
- The existing `queryMode` RPC field type (`'tds' | 'dataverse' | null`) is unchanged — `DataverseFallback` maps to `"dataverse"` in the RPC response; the fallback detail comes via the separate `tdsFallbackReason` field

---

## Phase 5 Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-30 | `ITdsQueryExecutor` resolves to a non-null instance in daemon `ServiceProvider` when environment is connected | `DaemonConnectionPoolManagerTests.CreateProvider_RegistersTdsQueryExecutor` | 🔲 |
| AC-31 | `ITdsQueryExecutor` resolves to a non-null instance in CLI `ServiceProvider` when profile has environment | `ServiceRegistrationTests.AddCliServices_RegistersTdsQueryExecutor` | 🔲 |
| AC-32 | TDS executor targets the correct org URL (hostname extracted from environment URL) | `TdsQueryExecutorTests.Constructor_NormalizesOrgUrl` | ✅ |
| AC-33 | TDS executor uses fresh tokens (calls token provider per execution, not cached) | `TdsQueryExecutorTests.ExecuteSqlAsync_CallsTokenProvider` | 🔲 |
| AC-34 | `SqlQueryResult.ExecutionMode` is `Tds` when plan contains `TdsScanNode` | `SqlQueryServiceTests.Execute_TdsPlan_SetsExecutionModeTds` | 🔲 |
| AC-35 | `SqlQueryResult.ExecutionMode` is `Dataverse` when plan contains `FetchXmlScanNode` | `SqlQueryServiceTests.Execute_FetchXmlPlan_SetsExecutionModeDataverse` | 🔲 |
| AC-36 | `SqlQueryResult.ExecutionMode` is `DataverseFallback` when TDS requested but query is incompatible | `SqlQueryServiceTests.Execute_TdsIncompatible_SetsDataverseFallback` | 🔲 |
| AC-37 | `SqlQueryResult.TdsFallbackReason` contains compatibility reason when TDS falls back | `SqlQueryServiceTests.Execute_TdsIncompatible_SetsFallbackReason` | 🔲 |
| AC-38 | `RpcMethodHandler` maps `ExecutionMode.Tds` to `queryMode = "tds"` | `RpcMethodHandlerTests.QuerySql_TdsMode_ReportsQueryModeTds` | 🔲 |
| AC-39 | `RpcMethodHandler` maps `ExecutionMode.DataverseFallback` to `queryMode = "dataverse"` with `tdsFallbackReason` | `RpcMethodHandlerTests.QuerySql_TdsFallback_ReportsDataverseWithReason` | 🔲 |
| AC-40 | VS Code extension TDS Read Replica menu item is visible and functional | Visual verification via `@webview-cdp` | 🔲 |
| AC-41 | VS Code status text shows "via TDS" when TDS executes successfully | Visual verification via `@webview-cdp` | 🔲 |
| AC-42 | VS Code status text shows "via Dataverse (TDS incompatible)" on TDS fallback | Visual verification via `@webview-cdp` | 🔲 |
| AC-43 | TUI status label reflects actual execution mode after query, not toggle state | Manual verification | 🔲 |
| AC-44 | TDS connection failure falls back to Dataverse with logged warning and honest `queryMode` | `SqlQueryServiceTests.Execute_TdsConnectionFails_FallsBackToDataverse` | 🔲 |
| AC-45 | Daemon TDS executor updates when environment switches (per-environment isolation) | `DaemonConnectionPoolManagerTests.SwitchEnvironment_CreatesFreshTdsExecutor` | 🔲 |
| AC-46 | `tdsFallbackReason` field present in RPC response DTO | `RpcMethodHandlerTests.QuerySql_Response_IncludesTdsFallbackReason` | 🔲 |
| AC-47 | Notebook renderer shows "via TDS" when `queryMode` is `"tds"` | `notebookResultRenderer.test.ts` (existing, already passes) | ✅ |

### Phase 5 Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| TDS requested, compatible query, endpoint enabled | `SELECT TOP 5 name FROM account` with `useTds=true` | Executes via TDS, `queryMode="tds"`, results returned |
| TDS requested, DML query | `DELETE FROM account WHERE ...` with `useTds=true` | Falls back to Dataverse, `queryMode="dataverse"`, `tdsFallbackReason="Query is incompatible with TDS Endpoint: IncompatibleDml"` |
| TDS requested, incompatible entity | `SELECT * FROM activityparty` with `useTds=true` | Falls back to Dataverse with `IncompatibleEntity` reason |
| TDS requested, endpoint disabled on env | `SELECT TOP 5 name FROM account` with `useTds=true`, TDS port 5558 not listening | Falls back to Dataverse, `tdsFallbackReason="TDS connection failed: ..."` |
| TDS not requested | Any query with `useTds=false` | Standard Dataverse execution, `queryMode="dataverse"`, no fallback reason |
| TDS via hint | `-- ppds:USE_TDS\nSELECT TOP 5 name FROM account` | TDS routing activated by hint (Phase 2 integration), `queryMode="tds"` |
| TDS toggle + environment switch | Toggle TDS on, switch environment | New environment gets fresh TDS executor with correct org URL and tokens |
| SPN auth profile | ClientSecret auth with `useTds=true` | TDS token acquired via client credentials flow, query succeeds |

---

## Phase 5 Core Types

### QueryExecutionMode (new)

Identifies the actual execution path used for a query. Set by `SqlQueryService` after plan execution, consumed by `RpcMethodHandler` for `queryMode` response mapping and by TUI for status label display.

```csharp
public enum QueryExecutionMode
{
    Dataverse,
    Tds,
    DataverseFallback
}
```

**Location:** `src/PPDS.Cli/Services/Query/QueryExecutionMode.cs`

---

## Phase 5 Design Decisions

### Why Fallback in SqlQueryService, Not TdsScanNode?

**Context:** When TDS connection fails at execution time, where should the fallback logic live?

**Decision:** `SqlQueryService` wraps plan execution in a try/catch. On TDS failure, it re-plans with `UseTdsEndpoint = false` and re-executes.

**Alternatives considered:**
- `TdsScanNode.ExecuteAsync()` catches and falls back internally: Rejected — a plan node shouldn't re-plan. Nodes execute; the service orchestrates.
- `PlanExecutor` handles fallback: Rejected — executor is a generic tree walker, shouldn't know about TDS specifics.

**Consequences:**
- Positive: Clean separation — planning is idempotent, retry is explicit
- Positive: Fallback reason can be set on `SqlQueryResult` naturally
- Negative: The query is parsed and planned twice on failure — acceptable because TDS failures are exceptional, not the hot path

### Why DataverseFallback Instead of Just Dataverse?

**Context:** When TDS falls back, should `ExecutionMode` be `Dataverse` or a distinct `DataverseFallback`?

**Decision:** Distinct `DataverseFallback` value.

**Alternatives considered:**
- Use `Dataverse` with a separate `TdsFallbackReason` property only: Rejected — callers would need to check both fields to know if fallback happened. An enum value makes pattern matching clean.
- Use a string like `"dataverse-fallback"` in the RPC response: Rejected — extension TypeScript type is `'tds' | 'dataverse' | null`. Adding a new value breaks existing code. The RPC maps `DataverseFallback` → `"dataverse"` and carries the reason in a separate field.

**Consequences:**
- Positive: C# callers can switch on enum cleanly
- Positive: RPC contract backward compatible (existing `queryMode` values unchanged)
- Positive: Fallback reason is surfaced separately for UI display

### Why Pass Profile and EnvironmentUrl to CreateProviderFromSources?

**Context:** The daemon's `CreateProviderFromSources()` needs profile info for TDS token acquisition, but `ProfileConnectionSourceAdapter` doesn't expose the wrapped profile.

**Decision:** Pass `AuthProfile` and `environmentUrl` as parameters to `CreateProviderFromSources()`.

**Alternatives considered:**
- Expose `Profile` on `ProfileConnectionSourceAdapter`: Rejected — adapter is a thin wrapper following the `IConnectionSource` interface. Adding profile-specific properties breaks the abstraction.
- Cast `IConnectionSource` to `ProfileConnectionSourceAdapter` and access internals: Rejected — fragile, assumes implementation type.

**Consequences:**
- Positive: Explicit data flow, no casting or abstraction leaks
- Negative: Method signature grows — acceptable for a private method
