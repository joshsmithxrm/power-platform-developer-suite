# Query

**Status:** Implemented
**Last Updated:** 2026-11-17
**Surfaces:** CLI, TUI, Extension, MCP
**Code:** [src/PPDS.Dataverse/Query/](../src/PPDS.Dataverse/Query/), [src/PPDS.Dataverse/Sql/](../src/PPDS.Dataverse/Sql/), [src/PPDS.Cli/Services/Query/](../src/PPDS.Cli/Services/Query/), [src/PPDS.Cli/Services/History/](../src/PPDS.Cli/Services/History/), [src/PPDS.Cli/Commands/Serve/Handlers/](../src/PPDS.Cli/Commands/Serve/Handlers/), [src/PPDS.Extension/src/](../src/PPDS.Extension/src/)

---

## Overview

The query system enables SQL-like queries against Dataverse through automatic transpilation to FetchXML. It provides a full query pipeline: parsing SQL into an AST, transpiling to FetchXML with virtual column support, executing against Dataverse via the connection pool, and expanding results with formatted values. Query history tracks executed queries per environment for recall and re-execution.

### Goals

- **SQL Familiarity**: Query Dataverse using standard SQL syntax instead of FetchXML
- **Virtual Column Transparency**: Automatically handle Dataverse naming conventions (owneridname, statuscodename)
- **Formatted Values**: Preserve and expose display values for lookups, option sets, and booleans
- **Query History**: Track and recall queries per environment for iterative exploration

### Non-Goals

- Full SQL compatibility (subqueries, UNION, CASE, functions not yet supported — see Unsupported SQL Features and Roadmap sections below)
- Query optimization beyond plan-layer routing (FetchXML pushdown is maximized; client-side operators are fallbacks)
- OData query generation (FetchXML is the target format)

---

## Architecture

All interfaces (CLI, TUI, VS Code Extension via daemon, MCP) use `SqlQueryService` as the single code path (Constitution A2). The daemon's RPC handlers are thin wrappers that map requests/responses — no bespoke query pipelines.

```
CLI ─────┐
TUI ─────┤
Daemon ──┤
MCP ─────┘
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
    │    └─ Standard → FetchXmlScanNode → ProjectNode
    ├─ Execute plan (with hint-influenced options)
    └─ Expand results (virtual columns via SqlQueryResultExpander)
    │
    ▼
SqlQueryResult (with DataSources metadata, ExecutionMode)

                ┌──────────────────────────────┐
                │   IQueryHistoryService        │
                │  ~/.ppds/history/{hash}.json  │
                │  Max 200 entries/env          │
                └──────────────────────────────┘
```

The plan-based pipeline (introduced by the v2 execution plan layer) replaces the original straight-line `parse → transpile → execute → expand` flow. The `QueryPlanner` builds a tree of `IQueryPlanNode` operators that are executed lazily (Volcano/iterator model via `IAsyncEnumerable<QueryRow>`). Phase 0 nodes (`FetchXmlScanNode`, `ProjectNode`) reproduce the original behavior; subsequent nodes add client-side evaluation, parallel execution, and DML support.

### Components

| Component | Responsibility |
|-----------|----------------|
| `SqlLexer` | Tokenizes SQL input, preserves comments |
| `SqlParser` | Recursive descent parser producing `ISqlStatement` AST hierarchy |
| `SqlToFetchXmlTranspiler` | Converts AST to FetchXML, detects virtual columns |
| `ExecutionPlanBuilder` | Builds `IQueryPlanNode` tree from parsed AST and plan options |
| `FetchXmlScanNode` | Plan node: executes FetchXML via `IQueryExecutor`, yields rows page-by-page |
| `ProjectNode` | Plan node: column selection, renaming, expression evaluation |
| `ExpressionEvaluator` | Evaluates `ISqlExpression` trees against `QueryRow` data |
| `QueryExecutor` | Executes FetchXML via connection pool, maps SDK entities to `QueryValue` |
| `SqlQueryService` | Orchestrates parse → hints → safety → plan → execute → expand |
| `SqlQueryResultExpander` | Adds formatted value columns to results |
| `QueryHistoryService` | Persists and retrieves query history per environment |
| `QueryHintParser` | Extracts `-- ppds:*` comments and `OPTION()` hints from parsed AST |
| `DmlSafetyGuard` | Blocks unsafe DML (no-WHERE DELETE/UPDATE), enforces row caps |
| `RemoteExecutorFactory` | Creates `IQueryExecutor` instances for cross-environment `[LABEL].entity` queries |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for `IDataverseConnectionPool`
- Depends on: [per-panel-environment-scoping.md](./per-panel-environment-scoping.md) for per-panel environment model (Extension surface)
- Uses patterns from: [architecture.md](./architecture.md) for Application Services
- Consumed by: [mcp.md](./mcp.md) for AI assistant query tools

---

## Specification

### Core Requirements

1. **SQL must transpile to valid FetchXML**: Parser validates syntax; transpiler produces well-formed FetchXML
2. **Virtual columns are transparent**: Queries for `owneridname` automatically query `ownerid` and extract formatted value
3. **Paging is stateless**: Results include paging cookie for next page retrieval; no server-side cursor
4. **History deduplicates by normalized SQL**: Same query with different whitespace shares history entry

### Supported SQL Features

| Feature | SQL Syntax | FetchXML Mapping |
|---------|------------|------------------|
| Select columns | `SELECT name, revenue` | `<attribute name="..."/>` |
| Select all | `SELECT *` | `<all-attributes/>` |
| Aliases | `SELECT name AS n` | `alias="n"` |
| TOP/LIMIT | `SELECT TOP 10`, `LIMIT 10` | `<fetch top="10">` |
| DISTINCT | `SELECT DISTINCT` | `<fetch distinct="true">` |
| WHERE | `WHERE status = 1` | `<filter><condition.../></filter>` |
| AND/OR | `WHERE a = 1 AND b = 2` | `<filter type="and/or">` |
| IN | `WHERE status IN (1, 2)` | Multiple `<condition operator="in">` |
| LIKE | `WHERE name LIKE '%acme%'` | `<condition operator="like">` |
| IS NULL | `WHERE parent IS NULL` | `<condition operator="null">` |
| ORDER BY | `ORDER BY name DESC` | `<order attribute="name" descending="true"/>` |
| JOIN | `INNER JOIN contact ON...` | `<link-entity link-type="inner">` |
| COUNT/SUM/AVG/MIN/MAX | `COUNT(*)`, `SUM(revenue)` | `aggregate="count"`, `aggregate="sum"` |
| GROUP BY | `GROUP BY statecode` | `<attribute groupby="true"/>` |

### Unsupported SQL Features

- Subqueries (`SELECT * FROM (SELECT...)`, `WHERE id IN (SELECT ...)`, `EXISTS`) — planned
- UNION/INTERSECT/EXCEPT — planned
- Complex expressions (`revenue * 1.1`, `CASE WHEN`) — planned
- Functions (`CONCAT()`, `UPPER()`) — planned

> **Note:** HAVING is now supported via client-side filtering (`ClientFilterNode` after aggregate `FetchXmlScanNode`). Cross-environment queries are supported via `RemoteExecutorFactory` and `[LABEL].entity` syntax.

### Primary Flows

**SQL Query Execution (plan-based pipeline):**

1. **Parse**: `SqlLexer` tokenizes input → `SqlParser` builds `ISqlStatement` AST
2. **Extract hints**: `QueryHintParser.Parse(fragment)` extracts `-- ppds:*` hints
3. **DML safety**: `DmlSafetyGuard.Check()` blocks unsafe DML (no-WHERE DELETE/UPDATE) and requires confirmation for execution. A request that carries no `DmlSafety` options is guarded with the defaults (not confirmed, not dry-run, default row cap) rather than skipping the guard, so every surface fails closed; callers that mean to run DML must pass explicit options. `--dry-run` is the side-effect-free preview path and never requires execution confirmation, while hard blocks (including cross-environment read-only policy) remain enforced.
4. **Plan**: `ExecutionPlanBuilder` builds `IQueryPlanNode` tree (FetchXmlScanNode → ProjectNode for standard queries; RemoteScanNode for cross-env; TdsScanNode for TDS)
5. **Execute plan**: For dry-run, return the plan without dispatching any executor; otherwise walk the plan tree and dispatch to the appropriate executors
6. **Expand**: `SqlQueryResultExpander` adds `*name` columns from formatted values
7. **Return**: `SqlQueryResult` with original SQL, transpiled FetchXML, expanded `QueryResult`, `DataSources` metadata, and `ExecutionMode`

**Query History:**

1. **Execute Query**: After successful execution, SQL is normalized and stored
2. **Deduplicate**: Same normalized SQL updates existing entry timestamp/metadata
3. **Persist**: Atomic write to `~/.ppds/history/{environment-hash}.json`
4. **Recall**: History entries retrievable by ID or searchable by pattern

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| SQL | Non-empty, valid syntax | `SqlParseException` with line/column |
| FROM entity | Must be valid Dataverse entity | Dataverse returns entity not found |
| Column names | Must exist on entity | Dataverse returns attribute not found |
| TOP value | Positive integer | `SqlParseException` |

### Query Hints

The query system supports 8 inline hints via SQL comments (`-- ppds:HINT_NAME [value]`) or `OPTION()` clause. Hints allow per-query control of execution behavior across all interfaces.

**Supported Hints:**

| Hint | Category | Effect |
|------|----------|--------|
| `USE_TDS` | Plan-level | Routes query through TDS Endpoint (SQL Server wire protocol against read replica) |
| `MAX_ROWS` | Plan-level | Overrides caller-provided row limit for SELECT. A request containing INSERT, UPDATE or DELETE that carries it is refused (see "Row limits and DML") |
| `MAXDOP` | Plan-level | Caps parallelism (cannot exceed pool capacity) |
| `HASH_GROUP` | Plan-level | Forces aggregate queries to client-side hash grouping |
| `NOLOCK` | FetchXML-level | Injects `<fetch no-lock="true">` on root element |
| `BYPASS_PLUGINS` | Execution-level | Sets `BypassCustomPluginExecution = true` on `OrganizationRequest` |
| `BYPASS_FLOWS` | Execution-level | Sets `SuppressCallbackRegistrationExpanderJob = true` on `OrganizationRequest` |
| `BATCH_SIZE` | DML-level | Parsed but not applied yet (the bulk executor takes no per-query batch size); never reported in `appliedHints` |

**Hint syntax:**
```sql
-- ppds:NOLOCK
-- ppds:BYPASS_PLUGINS
-- ppds:MAX_ROWS 100
SELECT * FROM account
```

**Parsing:** `QueryHintParser.Parse(fragment)` extracts hints after SQL parsing. Hints produce `QueryHintOverrides` (nullable bag — null means "use default"), which are merged into `QueryPlanOptions` (plan-level) and `QueryExecutionOptions` (execution-level).

**Precedence:** Inline hints (from SQL comments) override caller-provided settings. If the RPC sends `useTds=false` but the SQL contains `-- ppds:USE_TDS`, the hint wins. Rationale: the query text is the user's explicit intent for that specific query.

**Error handling:** Malformed hint values (e.g., `-- ppds:BATCH_SIZE abc`) and unrecognized hint names are silently ignored — the hint is skipped, the query proceeds normally. This matches SQL Server behavior for unrecognized hints.

**Applied hints:** `appliedHints` lists a hint only when it changed the plan or execution. `MAX_ROWS` is listed only when it limited the plan (not when the statement has its own `TOP`, not for multi-statement scripts, whose statements are planned without it); `BATCH_SIZE` is never listed.

### Cross-Environment Queries

Queries can reference tables in other configured environments using `[LABEL].entity` syntax:

```sql
SELECT * FROM [QA].account WHERE name LIKE '%Contoso%'
```

**Components:**

| Component | Responsibility |
|-----------|----------------|
| `RemoteExecutorFactory` | Creates `IQueryExecutor` instances for remote environments, resolved by label |
| `ProfileResolutionService` | Resolves environment labels to connection profiles via `EnvironmentConfigStore` |
| `RemoteScanNode` | Plan node that executes FetchXML against a remote environment's executor |

**Label resolution:** Labels are configured via `ppds env config` and stored in `EnvironmentConfigStore`. The `ProfileResolutionService` maps a label string to a resolved environment config, from which a remote `IQueryExecutor` is created. The special label `dbo` is treated as standard SQL schema and executes locally.

**Writing to another environment is not supported.** The DML planner sends every write to the connected environment, so a statement whose write target is another environment (`[LABEL].entity`, `[LABEL].dbo.entity`, or an alias bound to one in `FROM`) is refused with `Query.DmlBlocked` before anything runs, before confirmation is asked for, and also for `--dry-run`. A target whose `cross_env_dml_policy` is read-only gets the read-only message; every other target gets "Writing to another environment ([LABEL]) is not supported ... Nothing was executed. Connect to that environment and run the statement there". Reading from another environment while writing to the connected one (`INSERT INTO account (...) SELECT ... FROM [QA].account`) is supported.

**Data source attribution:** `SqlQueryResult` includes a `DataSources` property — a list of `QueryDataSource` entries identifying which environments contributed data. After plan building, the plan tree is walked to collect `RemoteScanNode.RemoteLabel` values. The local environment is always present. Remote labels are collected from any `RemoteScanNode` in the plan.

### Extension Surface

The VS Code extension surfaces query capabilities through webview panels served by the daemon's RPC handlers.

**Environment colors:** The webview toolbar renders a 4px left border in the environment's configured color. Colors map terminal color names (Red, Green, Yellow, Cyan, Blue, Gray, Brown, White, Bright* variants) to CSS values. When no explicit color is configured, type-based defaults apply (Production=Red, Sandbox=Brown, Development=Green, Test=Yellow, Trial=Cyan, Unknown=Gray).

**Data source banner:** When a query touches 2+ environments, a banner appears above results showing each source label styled with its environment color (e.g., "Data from: PPDS Dev (local) / QA (remote)"). Single-environment queries show no banner.

**TDS Read Replica toggle:** The query panel menu includes a TDS Read Replica toggle. When enabled, queries route through the TDS Endpoint. The status text reflects the actual execution mode ("via TDS" or "via Dataverse") based on `SqlQueryResult.ExecutionMode`, not the toggle state.

**DML dry-run response:** A `query/sql` request with `dmlSafety.isDryRun=true` returns the side-effect-free preview through the standard response contract. The response sets `dryRun`, `plan`, `executedFetchXml`, `rowCap`, and `requiresConfirmationForExecution`; no executor is dispatched, so `queryMode` is omitted. Compound scripts recursively expose each control-flow branch and planned data statement, with per-statement FetchXML. The dry-run fields are omitted for ordinary query responses. Service-level streaming previews expose the same plan and DML safety metadata on their single completion chunk.

---

## Core Types

### IQueryExecutor

Entry point for FetchXML execution with automatic paging support.

```csharp
public interface IQueryExecutor
{
    Task<QueryResult> ExecuteFetchXmlAsync(
        string fetchXml,
        int? pageNumber = null,
        string? pagingCookie = null,
        bool includeCount = false,
        CancellationToken cancellationToken = default);

    Task<QueryResult> ExecuteFetchXmlAllPagesAsync(
        string fetchXml,
        int maxRecords = 5000,
        CancellationToken cancellationToken = default);
}
```

The implementation ([`QueryExecutor.cs:37-122`](../src/PPDS.Dataverse/Query/QueryExecutor.cs#L37-L122)) parses FetchXML to extract column metadata, applies paging attributes, executes via `IDataverseConnectionPool`, and maps SDK entities to `QueryValue` dictionaries with formatted values preserved.

### QueryResult

Structured result containing records, column metadata, and paging information.

```csharp
public sealed class QueryResult
{
    public string EntityLogicalName { get; }
    public IReadOnlyList<QueryColumn> Columns { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, QueryValue>> Records { get; }
    public int Count { get; }
    public bool MoreRecords { get; }
    public string? PagingCookie { get; }
    public int PageNumber { get; }
    public int? TotalCount { get; }
    public long ExecutionTimeMs { get; }
    public string? ExecutedFetchXml { get; }  // Transpiled FetchXML for SQL queries
    public bool IsAggregate { get; }
}
```

The `QueryColumn` type ([`QueryColumn.cs:1-63`](../src/PPDS.Dataverse/Query/QueryColumn.cs#L1-L63)) captures attribute name, alias, data type, aggregate function, and linked entity context.

### QueryValue

Wrapper preserving both raw value and formatted display text.

```csharp
public sealed class QueryValue
{
    public object? Value { get; }
    public string? FormattedValue { get; }
    public string? LookupEntityType { get; }
    public Guid? LookupEntityId { get; }

    public static QueryValue Lookup(Guid id, string type, string? name);
    public static QueryValue WithFormatting(object? value, string? formatted);
}
```

The implementation ([`QueryValue.cs:1-97`](../src/PPDS.Dataverse/Query/QueryValue.cs#L1-L97)) handles SDK value type conversions including `EntityReference`, `OptionSetValue`, `Money`, and `AliasedValue` for aggregates.

### SqlSelectStatement

Immutable AST representing a parsed SQL SELECT statement.

```csharp
public sealed class SqlSelectStatement
{
    public IReadOnlyList<ISqlSelectColumn> Columns { get; }
    public SqlTableRef From { get; }
    public IReadOnlyList<SqlJoin> Joins { get; }
    public ISqlCondition? Where { get; }
    public IReadOnlyList<SqlOrderByItem> OrderBy { get; }
    public int? Top { get; }
    public bool Distinct { get; }
}
```

The AST includes helper methods ([`SqlSelectStatement.cs:136-234`](../src/PPDS.Dataverse/Sql/SqlSelectStatement.cs#L136-L234)) for detecting aggregates, extracting table names, and replacing virtual columns.

### ISqlQueryService

Application service orchestrating the full SQL query pipeline.

```csharp
public interface ISqlQueryService
{
    string TranspileSql(string sql, int? topOverride = null);

    Task<SqlQueryResult> ExecuteAsync(
        SqlQueryRequest request,
        CancellationToken cancellationToken = default);
}
```

The implementation ([`SqlQueryService.cs:42-80`](../src/PPDS.Cli/Services/Query/SqlQueryService.cs#L42-L80)) orchestrates parsing, hint extraction, safety checks, plan building, execution, and result expansion.

### QueryExecutionOptions

Execution-level options threaded from hint parsing to `IQueryExecutor`. Separate from `QueryPlanOptions` because these affect HOW the query is sent to Dataverse, not how the plan is built.

```csharp
public sealed record QueryExecutionOptions
{
    public bool BypassPlugins { get; init; }
    public bool BypassFlows { get; init; }
}
```

### QueryDataSource

Identifies an environment that contributed data to a query result.

```csharp
public sealed record QueryDataSource
{
    public required string Label { get; init; }
    public bool IsRemote { get; init; }
}
```

### QueryExecutionMode

Identifies the actual execution path used for a query. Set by `SqlQueryService` after plan execution.

```csharp
public enum QueryExecutionMode
{
    Dataverse,  // FetchXML against Dataverse Web API
    Tds         // TDS Endpoint (SQL Server wire protocol)
}
```

### IQueryHistoryService

Manages per-environment query history with deduplication.

**Replaying history (`ppds query history execute <id>`).** History can contain DML (the daemon and TUI record every executed statement), so replay is guarded exactly like `ppds query sql`: the request always carries `DmlSafety`, built from the same `--confirm`, `--dry-run` and `--no-limit` options (defined once in `DmlSafetyOptionSet` and shared by both commands), and the same environment safety settings are applied through `SqlCommand.ConfigureDmlSafetyAsync`. Every DML statement needs `--confirm` at every protection level (exit code 11 otherwise); once confirmed, the protection level adds no further gate. The no-WHERE block and the 10,000-row cap apply, and `--dry-run` previews the plan without executing. The statement text is echoed to stderr before anything runs or is refused, with control characters escaped (`TerminalText.Sanitize`) so the terminal cannot show something different from what runs. SELECT replay is unchanged. `--dry-run` rejects CSV output, as in `query sql`.

**Row limits and DML.** A DML statement affects exactly the rows its own text says, or nothing. Every argument that limits *returned* rows is kept away from INSERT/UPDATE/DELETE:

- An explicit CLI `--top` (`query sql`, `query history execute`) with a statement that is, or a script that contains, DML is refused before anything executes, including with `--dry-run`: invalid arguments, exit code 3, message "--top limits returned rows only and does not apply to INSERT, UPDATE or DELETE ... Put the limit in the statement's WHERE clause instead" (`SqlQueryRequest.TopIsUserSpecified`). A default `top` the caller applies to every query (the extension's default top over RPC) is ignored for DML.
- An explicit CLI `--page` or `--paging-cookie` with DML is refused the same way (exit 3, `SqlQueryRequest.PagingIsUserSpecified`). Over RPC and in the TUI, a first-page value is ignored for DML (every match is affected, subject to the row cap), and a request for a further page (`page` > 1 or a paging cookie: load-more) never runs DML, even confirmed: `Query.DmlBlocked`, "This is a request for a further page of results, which never runs INSERT, UPDATE or DELETE".
- A `-- ppds:MAX_ROWS n` hint on a request containing DML is refused before anything runs, on every surface and also for `--dry-run`: invalid arguments (exit 3), message "The -- ppds:MAX_ROWS hint limits returned rows only and does not apply to INSERT, UPDATE or DELETE, so nothing was executed. Remove the hint, and put the limit in the statement's WHERE clause instead." It is never reported as applied.

**Clauses the engine cannot honour.** These parse but would be dropped, so they are refused with `Query.DmlBlocked` before anything runs (in the guard, so before confirmation and for `--dry-run`, and again in the planner): `TOP` on UPDATE or DELETE, `INSERT TOP (n)`, `OUTPUT`/`OUTPUT INTO`, `WITH` (CTE) before DML, `SET col += ...` and other compound assignments, `SET @variable = ...` in UPDATE, INSERT without a column list, a VALUES row or SELECT whose column count differs from the INSERT column list, `INSERT ... SELECT *`, `INSERT ... EXEC`, `INSERT ... DEFAULT VALUES`, INSERT from UNION/INTERSECT/EXCEPT, `MERGE` (the engine counted rows but wrote nothing), and `EXECUTE AS`/`REVERT` (impersonation was never applied). Each message names the clause, says "Nothing was executed" and what to write instead. `INSERT ... SELECT TOP (n)` keeps working (the TOP belongs to the SELECT); `UPDATE ... FROM ... JOIN` is honoured.

**Other statement kinds.** Only INSERT, UPDATE and DELETE change Dataverse data. `EXEC <message>` (including `sp_executesql` and `EXEC('...')`) never sends a request: `ExecuteMessageNode` throws "not yet supported" and dynamic SQL is rejected at plan time. `TRUNCATE`, `ALTER`, and `CREATE TABLE` of a non-temporary table are unsupported statements. `CREATE TABLE #t`/`DROP TABLE #t` act on session temp tables only (they need a session, which `SqlQueryService` does not have) and never create or drop a Dataverse table. Cursors need a session too. DECLARE/SET/PRINT/THROW/RAISERROR/IF/WHILE/TRY act on script state only.

**Guard refusals in TRY/CATCH.** A safety refusal raised while a script runs (the row cap; the planner's unsupported-clause refusal) is not catchable by script code: it passes through `BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH`, ends the run with its own error code, and nothing after it runs. Ordinary errors (THROW, RAISERROR, data errors) are still caught.

**Fail-closed guard.** A `SqlQueryRequest` whose `DmlSafety` is null is checked with the default options (not confirmed, not dry-run, default row cap), so DML without explicit options is refused with `Query.DmlConfirmationRequired`. RPC `query/sql` without `dmlSafety` therefore refuses DML with error code `Query.DmlConfirmationRequired` and error data `{ code, message, dmlConfirmationRequired: true, dmlTargets }`. The VS Code notebook sends no `dmlSafety`, so notebook cells cannot run DML (they get this refusal; the notebook has no confirmation UI). TUI load-more never runs DML (refused as a further page).

**Confirmation targets.** `Query.DmlConfirmationRequired` lists every environment the request would write to (`PpdsDmlConfirmationRequiredException.Targets`); over RPC the error data adds `dmlTargets: [{ label, url, type, protection }]` (`label` and `type` are null when the environment has no configuration; `protection` is the level the guard applied). Writes to another environment are refused before confirmation is asked for, so the list is the connected environment. Every surface configures the guard through `SqlQueryService.ConfigureEnvironment(url, config)`.

**Refusal wording per surface.** Service messages name no CLI flag. The confirmation message is "This request changes data (INSERT, UPDATE or DELETE) and must be confirmed before it runs. Nothing was executed."; the CLI replaces it with "DML operations require --confirm to execute. Use --dry-run to preview the operation." The row-cap message ends "Narrow the WHERE clause to affect fewer rows."; the CLI adds "To change every matching row, re-run with --no-limit.", and the TUI adds that the cap cannot be removed in the TUI and to use `ppds query sql --no-limit`. RPC returns the neutral message with structured data. `--top`, `--page` and `--paging-cookie` refusals name CLI flags because only the CLI can set them.

**Row cap.** A DML statement that would affect more rows than the cap (10,000 by default; removed by `--no-limit` on the CLI or `dmlSafety.noLimit` over RPC; the TUI cannot remove it) is refused before it changes anything, with error code `Query.DmlRowCapExceeded` (`PpdsDmlRowCapExceededException`, carrying the cap, operation and entity). The message states the cap, that at least cap + 1 rows match (INSERT VALUES states its exact row count), that the statement changed nothing, and to narrow the WHERE clause; numbers use invariant digit grouping. Rows the statement would skip (no id) do not count toward the cap. A statement affecting exactly the cap rows runs. Applies to UPDATE, DELETE, INSERT...SELECT and INSERT VALUES. A non-positive `rowCap` is refused as invalid. CLI exit code 2 (like `Query.DmlBlocked`: confirmation cannot fix it, unlike exit 11). RPC error data is `{ code, message, dmlRowCapExceeded: true, rowCap, operation, entity }`, plus `dmlCommitted` when an earlier statement of the script already ran. `--dry-run` does not execute, so it cannot count rows: it reports the active `rowCap` and `ExceedsRowCap` stays false.

**Interrupted runs.** When a run stops after DML already changed rows (an error, a refusal, or a cancellation), every surface reports what already ran and was not rolled back, and names the statement that was running when it stopped as "may be partly applied", with the counts the bulk executor had reported (a lower bound; batches run in parallel), including for a single statement. Nothing claims a rollback. The service wraps a non-PPDS failure after DML ran in a `PpdsException` (keeping a query error code; an authentication failure then does not ask to re-authenticate and retry, which would run committed statements again); cancellation stays an `OperationCanceledException` with the outcome attached. The CLI prints the note to stderr and, in JSON mode, also puts it in the error envelope as `error.dmlCommitted`; RPC returns it as `dmlCommitted` and repeats it in `message` (a cancelled run returns `Operation.Cancelled` with `dmlCommitted`); the TUI shows it in the error text, including "Query cancelled." plus the note when the user cancels.

```csharp
public interface IQueryHistoryService
{
    Task<IReadOnlyList<QueryHistoryEntry>> GetHistoryAsync(
        string environmentUrl, int count = 50, CancellationToken ct = default);

    Task<QueryHistoryEntry> AddQueryAsync(
        string environmentUrl, string sql, int? rowCount = null,
        long? executionTimeMs = null, CancellationToken ct = default);

    Task<IReadOnlyList<QueryHistoryEntry>> SearchHistoryAsync(
        string environmentUrl, string pattern, int count = 50, CancellationToken ct = default);

    Task<QueryHistoryEntry?> GetEntryByIdAsync(
        string environmentUrl, string entryId, CancellationToken ct = default);

    Task<bool> DeleteEntryAsync(
        string environmentUrl, string entryId, CancellationToken ct = default);

    Task ClearHistoryAsync(
        string environmentUrl, CancellationToken ct = default);
}
```

The implementation ([`QueryHistoryService.cs:52-98`](../src/PPDS.Cli/Services/History/QueryHistoryService.cs#L52-L98)) normalizes SQL for deduplication, stores up to 200 entries per environment, and performs atomic writes via temp file rename.

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `SqlParseException` | Invalid SQL syntax | Display line/column with context snippet |
| `FaultException` | Dataverse execution error | Map to `PpdsException`, show entity/attribute |
| `ThrottleException` | Service protection limits | Automatic retry via connection pool |
| `FileNotFoundException` | History file missing | Return empty list |
| `PpdsException(DmlBlocked)` | DML without WHERE clause | Block execution, tell user to add WHERE or use `ppds truncate` |
| `PpdsException(DmlBlocked)` | Unsupported DML clause or statement (TOP on UPDATE/DELETE, OUTPUT, MERGE, EXECUTE AS, ...), a write to another environment, or a request for a further page that contains DML | Refused before anything runs, with the clause named and what to write instead (CLI exit 2, RPC `dmlBlocked`) |
| `PpdsException(DmlConfirmationRequired)` | Actual DML was requested without confirmation | Require `--confirm` for execution (CLI exit code 11); `--dry-run` previews without confirmation; RPC `dmlConfirmationRequired` with `dmlTargets` |
| `PpdsException(DmlRowCapExceeded)` | A DML statement would affect more rows than the row cap | Refused before it changes anything; narrow the WHERE clause or remove the cap (`--no-limit`, RPC `noLimit`). CLI exit 2, RPC `dmlRowCapExceeded` with `rowCap`, `operation`, `entity` |
| `PpdsValidationException` (`top`, `page`, `MAX_ROWS`, `rowCap`) | Explicit `--top`/`--page`/`--paging-cookie` or a `MAX_ROWS` hint with DML, or a non-positive row cap | Refused before anything runs; CLI exit 3 |
| DML rows failed (`SqlQueryResult.DmlOutcome.HasFailures`) | Any row of any executed INSERT/UPDATE/DELETE statement failed (scripts included, not only the last statement) | CLI exit 1 (some rows succeeded, error code `Operation.PartialFailure`) or 2 (none did, `Query.ExecutionFailed`); the JSON envelope has `success=false`, the unchanged `data`, a top-level `error` and a `results[]` entry; text/CSV print `Error: ...` to stderr. The message gives counts, and for scripts per-statement counts with a representative error tied to its statement (batches run in parallel, so the error is not necessarily the first row's). RPC `query/sql` returns `success=false` plus additive `affectedRows`/`failedRows`/`dmlError`; the TUI status line and `query history execute` report the same outcome; the streaming final chunk carries `DmlOutcome` |
| `PpdsException(TdsIncompatible)` | TDS requested but query can't use TDS | Fail with reason (DML, incompatible entity, unsupported feature) |
| `PpdsException(TdsConnectionFailed)` | TDS endpoint disabled or unreachable | Fail with clear error, suggest disabling TDS mode |
| Unknown environment label | `[LABEL].entity` where label not in `EnvironmentConfigStore` | Error: "No environment found matching label '{label}'" |

### SqlParseException Details

The parser provides rich error context ([`SqlParseException.cs:1-147`](../src/PPDS.Dataverse/Sql/Parsing/SqlParseException.cs#L1-L147)):

```csharp
public sealed class SqlParseException : Exception
{
    public int Position { get; }
    public int Line { get; }
    public int Column { get; }
    public string ContextSnippet { get; }

    // Example output:
    // Unexpected token 'WHEE' (Identifier) at line 2, column 15
    // Context: ...WHERE id = 5 WHEE...
}
```

### Recovery Strategies

- **Parse errors**: Display error with line/column context; user corrects SQL
- **Execution errors**: Map Dataverse fault codes to actionable messages
- **Throttle errors**: Transparent retry with exponential backoff (handled by pool)
- **History errors**: Log warning, continue without history tracking

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty SQL | `SqlParseException` with position 0 |
| SELECT * on large entity | Warn about performance, execute normally |
| Virtual column + base column both requested | Return both, no duplicate expansion |
| History file corruption | Log error, start fresh history |
| Query with 0 results | Return empty Records, empty Columns |

---

## Design Decisions

### Why SQL Instead of FetchXML Direct?

**Context:** FetchXML is verbose and requires knowledge of Dataverse-specific syntax. SQL is universally known.

**Decision:** Provide SQL as the primary query interface with FetchXML as the execution target.

**Test results:**
| Metric | SQL | FetchXML |
|--------|-----|----------|
| Characters for simple query | 45 | 180 |
| Learning curve | Minimal | Significant |
| Autocomplete potential | High | Low |

**Alternatives considered:**
- OData: Rejected - less powerful than FetchXML for Dataverse features
- Raw FetchXML only: Rejected - poor developer experience
- Custom DSL: Rejected - unnecessary learning curve

**Consequences:**
- Positive: Familiar syntax, reduced learning curve
- Negative: SQL features must be explicitly supported

### Why Recursive Descent Parser?

**Context:** Need a maintainable parser that produces a clean AST for transpilation.

**Decision:** Hand-written recursive descent parser with explicit token handling.

**Alternatives considered:**
- Parser generator (ANTLR): Rejected - adds complexity, harder to debug
- Regex-based parsing: Rejected - can't handle SQL grammar
- Third-party SQL parser: Rejected - no control over Dataverse-specific features

**Consequences:**
- Positive: Full control, excellent error messages, comment preservation
- Negative: Manual maintenance for new SQL features

### Why Virtual Column Detection?

**Context:** Dataverse stores lookups as GUIDs but users often want display names. Querying `owneridname` directly fails.

**Decision:** Detect virtual columns (ending in `name`) at transpilation, query base column, populate from formatted values.

**Implementation** ([`SqlToFetchXmlTranspiler.cs:113-188`](../src/PPDS.Dataverse/Sql/Transpilation/SqlToFetchXmlTranspiler.cs#L113-L188)):
- Patterns: `*idname`, `*codename`, `*typename`
- Explicit patterns: `statecodename`, `statuscodename`, `is*name`, `do*name`, `has*name`

**Consequences:**
- Positive: Users can query naturally (`owneridname`), results include display values
- Negative: Additional processing overhead, potential for pattern false positives

### Why Per-Environment History?

**Context:** Users work with multiple Dataverse environments. Mixing history would cause confusion.

**Decision:** Store history per environment using URL hash for filename isolation.

**Storage:** `~/.ppds/history/{sha256(url)[:16]}.json`

**Consequences:**
- Positive: Clean separation, no cross-environment confusion
- Negative: Users must reconnect to see environment-specific history

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| History max entries | int | No | 200 | Maximum entries per environment before trimming |
| History location | path | No | `~/.ppds/history/` | Directory for history JSON files |
| Default TOP | int | No | None | Default row limit if not specified |

---

## Testing

### Acceptance Criteria

- [ ] SQL queries transpile to valid FetchXML
- [ ] Virtual columns (`owneridname`) resolve to formatted values
- [ ] Paging works across multiple pages with cookies
- [ ] History deduplicates on normalized SQL
- [ ] Parse errors include line/column information
- [ ] All interfaces (CLI, TUI, Extension, MCP) use `SqlQueryService` — no bespoke query pipelines
- [ ] `-- ppds:NOLOCK` hint produces `<fetch no-lock="true">` in executed FetchXML
- [ ] `-- ppds:BYPASS_PLUGINS` sets `BypassCustomPluginExecution` header
- [ ] `-- ppds:BYPASS_FLOWS` sets `SuppressCallbackRegistrationExpanderJob` header
- [ ] `-- ppds:USE_TDS` routes query through TDS endpoint
- [ ] Inline hints override caller-provided settings
- [ ] `[LABEL].entity` syntax works for cross-environment queries
- [ ] VS Code webview toolbar shows environment color as 4px left border
- [ ] Cross-environment query results include `DataSources` metadata
- [ ] TDS requested + incompatible query fails with clear error, no silent fallback
- [ ] DML `--dry-run` succeeds without `--confirm`, executes no mutations, and leaves hard DML safety blocks intact
- [ ] Daemon `query/sql` DML dry-run responses include the preview plan, FetchXML, row cap, and execution-confirmation requirement
- [ ] Streaming DML dry-run completion chunks include the preview plan and DML safety metadata without dispatching an executor
- [ ] `query history execute` of DML is refused without `--confirm` (exit 11), runs with it, `--dry-run` executes nothing, and environment protection, the no-WHERE block and the row cap match `query sql` (`HistoryExecuteCommandTests.Replay_*`, `BothCommands_*`)
- [ ] The replayed statement is echoed to stderr before any refusal or execution with control characters escaped (`HistoryExecuteCommandTests.Replay_EscapesControlCharactersInTheEchoedStatement`, `TerminalTextTests`)
- [ ] A request with no `DmlSafety` refuses DML, including via RPC `query/sql` and streaming (so notebook cells cannot run DML); SELECT is unaffected (`DmlGuardFailClosedTests`)
- [ ] An explicit CLI `--top` with DML (single, script or `--dry-run`) is refused before executing with exit code 3 and changes nothing; a default top over RPC is ignored for DML; SELECT with `--top` works (`DmlRowCapTests.ExplicitTop_*`, `DefaultTop_*`, `DmlTopOverrideTests`, `HistoryExecuteCommandTests.Replay_DmlWithExplicitTop_*`, `SqlCommandTests.BuildRequest_*`)
- [ ] A DML statement over the row cap is refused before changing anything with `Query.DmlRowCapExceeded` (CLI exit 2, RPC `dmlRowCapExceeded`), a statement at exactly the cap runs, `--no-limit` removes the cap, earlier script statements stay committed and are reported (`DmlRowCapTests.OverTheCap_*`, `ExactlyAtTheCap_*`, `NoLimit_*`, `Script_EarlierStatementsStayCommitted_*`, `DmlInterruptedRunTests.Rpc_RowCapRefusal_*`, `HistoryExecuteCommandTests.Replay_DmlOverTheDefaultRowCap_*`)
- [ ] Any failed row of any executed DML statement is not a success: CLI exit 1 (partial, `Operation.PartialFailure`) or 2 (none succeeded, `Query.ExecutionFailed`), JSON `success=false` with unchanged `data`, a top-level `error` and a `results[]` entry, stderr `Error:` for text/CSV (`SqlCommandTests.WriteResult_*`, `DmlOutcomeTests`)
- [ ] DML outcomes cover every statement of a script with its real position (`#3`, `#2.1` inside a block), name the operation and entity, include statements that matched no rows, and tie the representative error to its statement (`DmlScriptReportingTests`, `DmlOutcomeTests`)
- [ ] RPC `query/sql` returns `success=false` with additive `affectedRows`, `failedRows` and `dmlError` when DML rows failed; the TUI status line and the streaming final chunk (`DmlOutcome`) report the same outcome (`RpcMethodHandlerTests.MapToResponse_Dml*`, `SqlQueryServiceTests.ExecuteStreamingAsync_DmlRowsFail_*`, `SqlQueryPresenterTests.FormatCompletionStatus_*`)
- [ ] A `MAX_ROWS` hint on a request containing DML is refused before anything runs (exit 3), also for `--dry-run` and before confirmation; for SELECT it applies and is reported; it is not reported when the statement has its own TOP; `BATCH_SIZE` is never reported as applied (`DmlStatementFidelityTests.MaxRowsHint_*`, `BatchSizeHint_*`)
- [ ] Clauses the engine cannot honour (TOP on UPDATE/DELETE, INSERT TOP, OUTPUT, WITH, compound SET, SET @variable, missing or mismatched INSERT columns, SELECT *, INSERT EXEC/DEFAULT VALUES/UNION, MERGE) are refused with `Query.DmlBlocked` before anything changes, confirmed, unconfirmed and for dry-run; INSERT ... SELECT TOP and UPDATE ... FROM JOIN keep working (`DmlStatementFidelityTests.UnsupportedClause_*`, `InsertSelectTop_*`, `UpdateWithJoinedFrom_*`, `DmlSafetyGuardTests.Check_Merge_*`)
- [ ] EXEC, dynamic SQL, TRUNCATE, DROP/CREATE/ALTER TABLE and cursors never change data (and DROP/CREATE TABLE no longer overflow the stack); EXECUTE AS/REVERT are refused (`DmlStatementFidelityTests.OtherStatements_NeverChangeData`, `Impersonation_*`)
- [ ] A write to another environment is refused before anything runs (also for dry-run and before confirmation); reading from another environment while writing locally works (`DmlStatementFidelityTests.WriteToAnotherEnvironment_*`, `ReadFromAnotherEnvironment_*`, `DmlRefusalWordingTests.ReadOnlyCrossEnvironmentWrite_*`, `SqlQueryServiceTests.ExecuteAsync_CrossEnvDml_*`)
- [ ] Explicit CLI paging with DML is refused (exit 3); a caller's first-page value never narrows DML; a request for a further page never runs DML, even confirmed; SELECT paging is unchanged (`DmlStatementFidelityTests.ExplicitPaging_*`, `FirstPageArgument_*`, `LoadMoreRequest_*`, `Paging_WithSelect_*`, `DmlRowCapTests.Tui_LoadMoreRequest_*`, `SqlCommandTests.BuildRequest_MarksExplicitPagingAsUserSpecified`)
- [ ] A row-cap refusal inside BEGIN TRY ends the script with `Query.DmlRowCapExceeded` and nothing after it runs; ordinary errors are still caught (`DmlStatementFidelityTests.RowCapRefusal_InsideTry_*`, `OrdinaryErrors_InsideTry_*`)
- [ ] An interrupted run (error or cancellation) reports what already ran and the in-flight statement as "may be partly applied" with the reported counts, including a single statement, on CLI (stderr and JSON `error.dmlCommitted`), RPC (`dmlCommitted`, `Operation.Cancelled` for a cancel) and TUI (including cancel); the service wraps non-PPDS failures after DML ran (`DmlInterruptedRunTests`, `SqlQueryPresenterTests.ExecuteAsync_CancelledAfterDmlRan_*`, `HistoryExecuteCommandTests.Replay_ScriptInterrupted*`, `DmlScriptReportingTests.Script_Interrupted*`)
- [ ] RPC error data for guard refusals is produced by the handler's real mapping (`TryMapQuerySqlFailure`): confirmation (`dmlConfirmationRequired`, `dmlTargets`), blocked (`dmlBlocked`), row cap (`dmlRowCapExceeded`, `rowCap`, `operation`, `entity`, `dmlCommitted`) (`DmlInterruptedRunTests.Rpc_*`, `DmlRefusalWordingTests.ConfirmationRequired_Rpc_*`, `RowCap_Rpc_*`)
- [ ] Service refusal messages name no CLI flag; the CLI words confirmation and row cap with `--confirm`/`--dry-run`/`--no-limit`, the TUI says the cap cannot be removed there (`DmlRefusalWordingTests`, `DmlRowCapTests.OverTheCap_*`, `SqlQueryPresenterTests.ExecuteAsync_RowCapRefusal_*`)
- [ ] Row-cap edges: id-less rows do not count, INSERT VALUES states its exact count, invariant digit grouping, a non-positive row cap is refused (`DmlRowCapTests.AStatementWithExactlyTheCapUsableRows_*`, `RowCapMessage_UsesInvariantDigitGrouping`, `DmlRefusalWordingTests.NonPositiveRowCap_*`)
- [ ] History replay is exactly as hard as `query sql` at Production and Test protection, with exact exit codes, and dry-run reports the row cap with and without `--no-limit` (`HistoryExecuteCommandTests.Replay_ProtectedEnvironment_IsExactlyAsHardAsSql`, `Replay_DryRun_PrintsPlan_WithTheRowCap_ExecutesNothing`, `Replay_DmlWithoutWhere_*`)
- [ ] Echoed and printed history text escapes control, bidirectional and zero-width characters; `history get` keeps redirected SQL exact and sanitises the stored error (`TerminalTextTests`, `HistoryExecuteCommandTests.HistoryGet_TextOutput_*`)

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty SELECT | `SELECT FROM account` | Parse error at column 8 |
| Unknown operator | `SELECT * FROM account WHERE name ~ 'test'` | Parse error at `~` |
| Large result set | SELECT on 50k record entity | Paging with 5000-record pages |
| Comment preservation | `-- get accounts\nSELECT *` | Comment in FetchXML output |
| Unknown environment label | `SELECT * FROM [UNKNOWN].account` | Error: "No environment found matching label 'UNKNOWN'" |
| "dbo" as label | `SELECT * FROM [dbo].account` | Treated as standard SQL schema, executes locally |
| Multiple hints on one query | `-- ppds:NOLOCK` + `-- ppds:BYPASS_PLUGINS` | Both hints applied |
| Hint on TDS query | `-- ppds:NOLOCK` + `-- ppds:USE_TDS` | TDS wins; NOLOCK irrelevant for TDS |
| BATCH_SIZE on SELECT | `-- ppds:BATCH_SIZE 500` on SELECT | Hint has no effect, no error |
| Malformed hint value | `-- ppds:BATCH_SIZE abc` | Hint silently ignored, query proceeds |
| TDS + DML | `DELETE FROM account` with `useTds=true` | Error: DML not supported via TDS |
| Empty environment color | No color configured, type is Sandbox | Toolbar shows Brown (Sandbox default) |

### Test Coverage

**Unit Tests (200+ test facts):**
- `SqlLexerTests.cs`: Tokenization, comments, operators
- `SqlParserTests.cs`: AST construction, error handling
- `SqlToFetchXmlTranspilerTests.cs`: FetchXML generation, virtual columns
- `SqlQueryServiceTests.cs`: Service orchestration
- `QueryHistoryServiceTests.cs`: Persistence, deduplication

**Integration Tests:**
- `QueryExecutionTests.cs`: Paging, filtering with FakeXrmEasy
- `AggregateQueryTests.cs`: COUNT, SUM, AVG, MIN, MAX

### Test Example

```csharp
[Fact]
public void Transpile_SelectWithJoin_GeneratesLinkEntity()
{
    var sql = "SELECT a.name, c.fullname " +
              "FROM account a " +
              "INNER JOIN contact c ON c.parentcustomerid = a.accountid";

    var result = _transpiler.Transpile(_parser.Parse(sql));

    result.FetchXml.Should().Contain("<link-entity");
    result.FetchXml.Should().Contain("link-type=\"inner\"");
    result.FetchXml.Should().Contain("from=\"parentcustomerid\"");
}
```

---

## Related Specs

- [connection-pooling.md](./connection-pooling.md) - Query execution uses pooled connections
- [per-panel-environment-scoping.md](./per-panel-environment-scoping.md) - Per-panel environment model extended with color rendering
- [architecture.md](./architecture.md) - `ISqlQueryService` follows Application Services pattern
- [mcp.md](./mcp.md) - MCP tools expose query capabilities to AI assistants
- [cli.md](./cli.md) - CLI commands for `ppds query sql|fetch|history`
- [CONSTITUTION.md](./CONSTITUTION.md) - A2 (Application Services single code path) motivates shared `SqlQueryService`

---

## Roadmap

- **Query builder TUI**: Interactive query construction with autocomplete
- **Explain plan**: Show how FetchXML will be executed (`ExplainAsync()` infrastructure exists)
- **Query templates**: Parameterized saved queries
- **Advanced SQL features**: Subqueries, UNION/INTERSECT/EXCEPT, CASE/IIF, scalar functions (string, date, CAST), window functions, variables

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-18 | Absorbed query-parity.md (daemon alignment, hints, cross-env, Extension surface) and query-engine-v2.md Phase 0 (plan layer) per SL1/SL2 |
| 2026-01-23 | Initial spec |
