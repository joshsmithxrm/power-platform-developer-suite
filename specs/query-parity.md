# Query Parity

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-15
**Code:** [src/PPDS.Cli/Services/Query/](../src/PPDS.Cli/Services/Query/), [src/PPDS.Cli/Commands/Serve/Handlers/](../src/PPDS.Cli/Commands/Serve/Handlers/), [src/PPDS.Extension/src/](../src/PPDS.Extension/src/)

---

## Overview

The daemon's RPC `query/sql` handler bypasses `SqlQueryService` entirely — it inlines its own transpile-and-execute pipeline. This violates Constitution A2 ("Application Services are the single code path") and causes silent feature disparity between TUI/CLI and the VS Code extension. This spec aligns all interfaces to use the shared `SqlQueryService`, integrates the orphaned `QueryHintParser`, surfaces environment colors in VS Code, and adds cross-environment query source attribution.

### Goals

- **Single code path**: Daemon uses `SqlQueryService.ExecuteAsync()` like TUI and CLI — no bespoke query pipeline
- **Query hints**: Integrate `QueryHintParser` into `SqlQueryService.PrepareExecutionAsync()` so all 8 hints work across all interfaces
- **Cross-environment queries in VS Code**: Wire `RemoteExecutorFactory` on the daemon's service instance so `[LABEL].entity` syntax works
- **Environment color rendering**: Surface configured environment colors as a toolbar accent in VS Code webview panels
- **Cross-environment attribution**: Show which environments contributed data when a query touches multiple sources

### Non-Goals

- VS Code UI controls (buttons/toggles) for individual query hints — hints work via SQL comment syntax; UI discoverability is deferred
- New hint types beyond the 8 already defined in `QueryHintParser`
- Changes to the TUI environment color rendering (already works)
- IntelliSense/completions for `-- ppds:` hint prefix (tracked separately)
- Documentation on ppds-docs site (tracked via separate GitHub issue)

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
| `FetchXmlGenerator` | Emits `no-lock="true"` when NoLock hint is active |
| `IQueryExecutor` | Accepts execution options, sets OrganizationRequest headers |

### Dependencies

- Depends on: [query.md](./query.md), [per-panel-environment-scoping.md](./per-panel-environment-scoping.md), [connection-pooling.md](./connection-pooling.md)
- Aligns with: [CONSTITUTION.md](./CONSTITUTION.md) A1, A2

---

## Specification

### Phase 1: Daemon Uses SqlQueryService

#### Core Requirements

1. `RpcMethodHandler.QuerySqlAsync()` delegates ALL query execution to `SqlQueryService.ExecuteAsync()` — no inline transpilation or direct executor calls
2. The daemon wires `RemoteExecutorFactory`, `ProfileResolver`, `EnvironmentSafetySettings`, and `EnvironmentProtectionLevel` on the `SqlQueryService` instance, using the same pattern as `InteractiveSession.GetSqlQueryServiceAsync()` (lines 321-355)
3. The daemon's inline DML safety pre-check (lines 954-1001) is removed — `SqlQueryService.PrepareExecutionAsync()` handles DML safety
4. The private `TranspileSqlToFetchXml()` helper in `RpcMethodHandler` is removed
5. The `QueryResultResponse` RPC DTO contract is unchanged — `SqlQueryResult` maps to the existing fields
6. TDS routing is controlled via `SqlQueryRequest.UseTdsEndpoint` — the service handles TDS internally

#### Primary Flow

**query/sql RPC call:**

1. **Validate**: Check `request.Sql` is non-empty
2. **Resolve environment**: Call `WithProfileAndEnvironmentAsync(request.EnvironmentUrl, ...)` to get service provider
3. **Get service**: Get `ISqlQueryService` from provider, cast to `SqlQueryService`
4. **Wire cross-env**: Load `EnvironmentConfigStore` configs, create `ProfileResolutionService`, set `RemoteExecutorFactory` (label → resolve config → get provider → get `IQueryExecutor`)
5. **Wire safety**: Set `ProfileResolver`, `EnvironmentSafetySettings`, `EnvironmentProtectionLevel` from environment config
6. **Build request**: Map RPC `QuerySqlRequest` fields to `SqlQueryRequest`
7. **Execute**: Call `service.ExecuteAsync(sqlRequest, cancellationToken)`
8. **Map response**: Convert `SqlQueryResult` to `QueryResultResponse` (records, columns, paging, timing, executed FetchXML, query mode)
9. **Save history**: Fire-and-forget history save (unchanged)

#### Constraints

- The RPC response contract (`QueryResultResponse`) must not change — this is a backend-only fix
- The daemon must create `ProfileResolutionService` per request (or cache and invalidate when environment configs change) — environment labels can be reconfigured at any time
- `SqlQueryService` is registered as transient — each call gets a fresh instance, wiring is per-call

### Phase 2: Query Hints Integration

#### Core Requirements

1. `SqlQueryService.PrepareExecutionAsync()` calls `QueryHintParser.Parse(fragment)` after parsing SQL, before building the execution plan
2. Plan-level hints (`USE_TDS`, `MAX_ROWS`, `MAXDOP`, `HASH_GROUP`) override corresponding fields in `QueryPlanOptions`
3. FetchXML-level hints (`NOLOCK`) are passed to the FetchXML generation layer, resulting in `no-lock="true"` on the `<fetch>` element
4. Execution-level hints (`BYPASS_PLUGINS`, `BYPASS_FLOWS`) are threaded to `IQueryExecutor` and applied as OrganizationRequest headers (`BypassCustomPluginExecution`, `SuppressCallbackRegistrationExpanderJob`)
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

2. **`QueryPlanOptions`**: Add `ForceClientAggregation` (bool, default false). Add `NoLock` (bool, default false). Other plan-level hints map to existing fields.

3. **`QueryExecutionOptions`** (new type): `BypassPlugins` (bool), `BypassFlows` (bool). Passed to plan executor and applied on `IQueryExecutor` calls.

4. **`FetchXmlGenerator.Generate()`** or **`FetchXmlScanNode`**: When `NoLock` is true, emit `no-lock="true"` on the `<fetch>` element.

5. **`IQueryExecutor.ExecuteFetchXmlAsync()`**: Accept optional `QueryExecutionOptions`. When `BypassPlugins` is true, set `BypassCustomPluginExecution` header. When `BypassFlows` is true, set `SuppressCallbackRegistrationExpanderJob` header.

6. **`ExecutionPlanBuilder.PlanSelect()`**: When `ForceClientAggregation` is true and query has aggregates, route to client-side hash group plan regardless of record count.

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

---

## Follow-Up Work (Out of Scope)

- GitHub issue in `ppds-docs` for query hints documentation page
- IntelliSense completions for `-- ppds:` prefix in SQL editor
- VS Code UI controls (buttons/toggles) for hint discoverability
- Environment color in QuickPick environment picker
