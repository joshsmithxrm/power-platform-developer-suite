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

The CLI, the TUI and the VS Code Extension (through the daemon) run every SQL statement through `SqlQueryService` as the single code path (Constitution A2); the daemon's RPC handlers are thin wrappers that map requests/responses. The MCP `ppds_query_sql` tool is the exception: it transpiles a SELECT to FetchXML and reads it through `IQueryExecutor`, and never runs INSERT, UPDATE or DELETE (see "Where DML executes").

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
| `DmlSafetyGuard` | Blocks unsafe DML (no-WHERE DELETE/UPDATE), applies the DML allowlist, computes the row cap (`DmlExecuteNode` enforces it) |
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
| Column names | Must exist on entity | SELECT: Dataverse returns attribute not found. INSERT/UPDATE/DELETE: refused before anything runs (`DmlColumnValidator`, `Query.DmlBlocked`) |
| TOP value | Positive integer | `SqlParseException` |

### Query Hints

The query system supports 8 inline hints via SQL comments (`-- ppds:HINT_NAME [value]`) or `OPTION()` clause. Hints allow per-query control of execution behavior across all interfaces.

**Supported Hints:**

| Hint | Category | Effect |
|------|----------|--------|
| `USE_TDS` | Plan-level | Routes query through TDS Endpoint (SQL Server wire protocol against read replica). A request containing DML with TDS selected (the hint, RPC `useTds` or the TUI toggle) is refused with `Query.TdsIncompatible` |
| `MAX_ROWS` | Plan-level | Overrides caller-provided row limit for SELECT. A request containing INSERT, UPDATE or DELETE that carries it is refused (see "Row limits and DML") |
| `MAXDOP` | Plan-level | Caps parallelism (cannot exceed pool capacity). Refused on a request containing INSERT, UPDATE or DELETE |
| `HASH_GROUP` | Plan-level | Forces aggregate queries to client-side hash grouping. Refused on a request containing DML |
| `NOLOCK` | FetchXML-level | Injects `<fetch no-lock="true">` on root element. Refused on a request containing DML (it would select uncommitted rows to change) |
| `BYPASS_PLUGINS` | Execution-level | Sets `BypassCustomPluginExecution = true` on `OrganizationRequest`; on INSERT/UPDATE/DELETE the bulk writes bypass synchronous custom logic (`CustomLogicBypass.Synchronous`) |
| `BYPASS_FLOWS` | Execution-level | Sets `SuppressCallbackRegistrationExpanderJob = true` on `OrganizationRequest`; on INSERT/UPDATE/DELETE the bulk writes bypass Power Automate flows |
| `BATCH_SIZE` | DML-level | Records per bulk request for INSERT/UPDATE/DELETE (1 to 1000; outside that range the request is refused). Not applied, and not reported, for SELECT |

**Hint syntax:**
```sql
-- ppds:NOLOCK
-- ppds:BYPASS_PLUGINS
-- ppds:MAX_ROWS 100
SELECT * FROM account
```

**Parsing:** `QueryHintParser.Parse(fragment)` extracts hints after SQL parsing. Hints produce `QueryHintOverrides` (nullable bag — null means "use default"), which are merged into `QueryPlanOptions` (plan-level) and `QueryExecutionOptions` (execution-level).

**Precedence:** Inline hints (from SQL comments) override caller-provided settings. If the RPC sends `useTds=false` but the SQL contains `-- ppds:USE_TDS`, the hint wins. Rationale: the query text is the user's explicit intent for that specific query.

**Error handling:** On SELECT, malformed hint values (e.g., `-- ppds:BATCH_SIZE abc`), and unrecognized hint names are skipped and the query proceeds normally; a `-- ppds:` line comment keeps the lenient parsing it always had (a flag may be followed by text, a number may carry a sign), and a `/* ppds:... */` block comment is not a hint. On a request containing INSERT, UPDATE or DELETE, any comment in which `ppds:` followed by a letter starts the comment or follows whitespace, and that is not an exact supported hint (`-- ppds:NAME [value]` as a line comment, with a well-formed value: so not `MAX_ROWS 5;`, `MAX_ROWS=5`, `MAX_ROWS 1,000`, a misspelt name, a flag with a value, or a `/* ppds:... */` block comment) is refused before anything runs (invalid arguments, exit 3): a hint on DML is applied exactly or not at all.

**Applied hints:** `appliedHints` lists a hint only when it changed the plan or execution. `MAX_ROWS` is listed only when it limited the plan (not when the statement has its own `TOP`, not for multi-statement scripts, whose statements are planned without it); `BATCH_SIZE` is listed only for DML.

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

**Writing to, or reading into DML from, another environment is not supported.** The DML planner sends every write to the connected environment, so a statement whose write target is another environment (`[LABEL].entity`, `[LABEL].dbo.entity`, or an alias bound to one in `FROM`) is refused with `Query.DmlBlocked` before anything runs, before confirmation is asked for, and also for `--dry-run`. A target whose `cross_env_dml_policy` is read-only gets the read-only message; the connected environment's own label gets "[LABEL] is the connected environment. Name the table without the environment label"; every other target gets "Writing to another environment ([LABEL]) is not supported ... Nothing was executed". Reading another environment inside DML (`INSERT ... SELECT ... FROM [QA].account`, or `UPDATE/DELETE ... FROM` with a remote table) is refused too: the remote read path does not send the WHERE to the other environment (it reads the table and filters on the client, so the rows a statement would change are not chosen by Dataverse), and ids read from another environment must never be applied to the connected one.

**Data source attribution:** `SqlQueryResult` includes a `DataSources` property — a list of `QueryDataSource` entries identifying which environments contributed data. After plan building, the plan tree is walked to collect `RemoteScanNode.RemoteLabel` values. The local environment is always present. Remote labels are collected from any `RemoteScanNode` in the plan.

### Extension Surface

The VS Code extension surfaces query capabilities through webview panels served by the daemon's RPC handlers.

**Environment colors:** The webview toolbar renders a 4px left border in the environment's configured color. Colors map terminal color names (Red, Green, Yellow, Cyan, Blue, Gray, Brown, White, Bright* variants) to CSS values. When no explicit color is configured, type-based defaults apply (Production=Red, Sandbox=Brown, Development=Green, Test=Yellow, Trial=Cyan, Unknown=Gray).

**Data source banner:** When a query touches 2+ environments, a banner appears above results showing each source label styled with its environment color (e.g., "Data from: Contoso Dev (local) / QA (remote)"). Single-environment queries show no banner.

**TDS Read Replica toggle:** The query panel menu includes a TDS Read Replica toggle. When enabled, queries route through the TDS Endpoint. The status text reflects the actual execution mode ("via TDS" or "via Dataverse") based on `SqlQueryResult.ExecutionMode`, not the toggle state.

**DML dry-run response:** A `query/sql` request with `dmlSafety.isDryRun=true` returns the side-effect-free preview through the standard response contract. The response sets `dryRun`, `plan`, `executedFetchXml`, `rowCap`, and `requiresConfirmationForExecution`; no executor is dispatched, so `queryMode` is omitted. Compound scripts recursively expose each control-flow branch and planned data statement, with per-statement FetchXML. The dry-run fields are omitted for ordinary query responses. Service-level streaming previews expose the same plan and DML safety metadata on their single completion chunk.

**DML confirmation (Data Explorer and notebooks):** Every extension `query/sql` request that carries user SQL sends `dmlSafety`. The first attempt, every retry and every load-more page send `{ isConfirmed: false }`; the extension never classifies SQL itself. Only a `Query.DmlConfirmationRequired` answer opens a modal confirmation naming the captured target (profile pinned by identity) and showing the statement (start and end with exact hidden counts when long). "Show Full Statement" is listed first; "Execute Anyway" sends one `{ isConfirmed: true }` request for the same text and target, and a long statement cannot be executed before it has been opened. When the confirmation-required error data carries `dmlTargets`, every write target is listed. Guard refusals (`Query.DmlBlocked`, `Safety.ShakedownActive`, and `Query.DmlRowCapExceeded` from a daemon that raises it) are shown as errors; today the no-WHERE block arrives before the prompt and the shakedown and cross-environment checks refuse the confirmed request. DML results with failed rows show the counts, and the reason when the daemon sends `dmlError`. Acceptance criteria: [data-explorer.md](./data-explorer.md#dml-confirmation-and-failure-display) AC-DML-01 to AC-DML-13.

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
    public int? DmlBatchSize { get; init; } // -- ppds:BATCH_SIZE, DML only
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

**DML allowlist.** INSERT, UPDATE and DELETE run only when every part of the statement is on the allowlist below (`DmlStatementSupport`), which is enforced twice: by the guard, so the refusal comes before confirmation and also applies to `--dry-run`, and by the planner (`ExecutionPlanBuilder.PlanStatement`), so explain, scripts and the ADO.NET provider cannot plan anything else. Anything not on it is refused with `Query.DmlBlocked` (CLI exit 2, RPC `dmlBlocked`), naming the part, saying "Nothing was executed" and what to write instead. In addition the rows an UPDATE/DELETE changes must be selected by a single FetchXML scan of the target; a plan that needs any client-side node is refused, and the planner refuses a row limit, page number or paging cookie for DML (`QueryPlanOptions.MaxRows`, `PageNumber`, `PagingCookie`) from any caller. A target row returned more than once is changed and counted once.

Every shape below passed the live differential run on **2026-11-17** (`tests/PPDS.LiveTests/Query/DmlLiveDifferentialTests.cs`, Category=Integration, against a throwaway table in a development environment with a column of every listed type and a NULL row in each): each statement ran through `SqlQueryService` with confirmation; the rows it changed, inserted or deleted, and the exact values written in every column, were compared with what the same T-SQL does under the organisation's collation; and the affected and failed counts the service reported were compared with what changed in Dataverse (rows whose row version moved, rows removed, rows added). Column types, operators and literal forms are checked against metadata before anything runs (`DmlColumnValidator`); a type, operator or literal form not listed for a part is refused, naming the column and what to write instead.

| Part | Supported (live-verified 2026-11-17) | Unit test |
|------|-----------|------|
| Target | A table of the connected environment named directly (`account` or `dbo.account`; `dbo` is the only schema and is never an environment label), or its alias. Names in any case and in brackets are written as the lower-case logical name | `DmlAllowlistTests.Update_TheTargetOrItsAliasInFrom_*`, `Delete_TheTargetOrItsAliasInFrom_*`, `DmlAllowlistPlannerTests.UpperCase*`, `DmlFaithfulRowsTests.ProvenShape_WritesExactlyTheseValues` |
| UPDATE/DELETE `FROM` | Only the target table itself: aliased (`UPDATE a SET ... FROM account a WHERE a.col = 1`, `DELETE a FROM account a WHERE ...`, also `FROM dbo.account a`) or not (`UPDATE account SET ... FROM account WHERE account.col = 1`, `UPDATE dbo.account SET ... FROM account`, `DELETE account FROM account WHERE ...`); the table is written | `DmlAllowlistPlannerTests.AliasTarget_*`, `DmlAllowlistTests.Update_TheTargetOrItsAliasInFrom_*` |
| No WHERE | UPDATE or DELETE without WHERE only when the environment's safety settings switch off `PreventUpdateWithoutWhere` / `PreventDeleteWithoutWhere` (by default it is refused; for DELETE the message points to `ppds data truncate --entity <table>`) | `DmlGuardFailClosedTests.NullDmlSafety_DmlWithoutWhere_IsBlocked`, `DmlSafetyGuardTests` |
| WHERE structure (UPDATE, DELETE, INSERT ... SELECT) | AND, OR and parentheses, nested to any depth, with SQL precedence, over `column op constant`; the column may be qualified by the table name or its alias. A constant is a quoted string (`'x'` or `N'x'`), a whole or decimal number, or a negative one; a floating-point literal (`2.5E0`) is refused. The operators allowed depend on the column type (next rows). A negated condition (`<>`, `NOT IN`, `NOT LIKE`) excludes rows where the column is NULL in whichever OR branch it sits | `DmlFaithfulRowsTests.Where_ChangesExactlyTheRowsItsTextSelects`, `DmlFaithfulTests.Where_SelectsExactlyTheMatchingRows` |
| WHERE on text and multiline text | `=`, `<>`, `!=`, `LIKE` / `NOT LIKE 'pattern'` (any combination of `%` and `_`, including `'%'` and `''`; no `[ ]` or ESCAPE), `IS [NOT] NULL`; single-line text also `[NOT] IN (...)`. Compared with a quoted string. Matching follows the organisation's collation because WHERE is always evaluated by Dataverse: case- and accent-insensitive; `=`/`IN` ignore trailing spaces, leading spaces count; LIKE does not ignore trailing spaces | `DmlFaithfulRowsTests.Where_*` |
| WHERE on whole number, decimal and currency | `=`, `<>`, `!=`, `<`, `>`, `<=`, `>=`, `[NOT] BETWEEN`, `[NOT] IN` (one value up to 1,000 values), `IS [NOT] NULL`. A whole-number column is compared with a whole number (`5`, not `5.0`) | `DmlFaithfulRowsTests.Where_*`, `DmlAllowlistPlannerTests.InList*` |
| WHERE on floating point | `=`, `<>`, `!=`, `<`, `>`, `<=`, `>=`, `[NOT] BETWEEN`, `IS [NOT] NULL` (no IN) | `DmlFaithfulRowsTests.Where_*` |
| WHERE on yes/no | `=`, `<>`, `!=` with `1` or `0`; a row with no value matches none of them | `DmlFaithfulRowsTests.Where_*` |
| WHERE on choice | `=`, `<>`, `!=`, `[NOT] IN`, `IS [NOT] NULL`, with whole-number values | `DmlFaithfulRowsTests.Where_*` |
| WHERE on status (`statecode`) and status reason (`statuscode`) | `=`, `<>`, `!=`, `[NOT] IN` with whole-number values (verified with rows on both sides) | `DmlFaithfulRowsTests.Where_*` |
| WHERE on user-local date and time | `=`, `<>`, `!=`, `<`, `>`, `<=`, `>=`, `[NOT] BETWEEN`, `IS [NOT] NULL`; the literal must end with `Z` or an offset (`'2024-01-15T09:30:00Z'`, `'2024-01-15T10:30:00+01:00'`) | `DmlFaithfulRowsTests.Where_*`, `DmlFaithfulTests.DateLiteral*` |
| WHERE on date only | `=`, `<>`, `!=`, `<`, `>`, `<=`, `>=`, `[NOT] BETWEEN`, `IS [NOT] NULL`; the literal is `'yyyy-MM-dd'` | `DmlFaithfulRowsTests.Where_*` |
| WHERE on the primary key | `=`, `<>`, `!=`, `[NOT] IN` with quoted GUIDs with dashes, any case, with or without braces. Other unique-identifier columns (`stageid`, `processid`, ...) are refused | `DmlFaithfulRowsTests.Where_*`, `DmlRound1RefusalTests` |
| WHERE on a lookup | `=`, `<>`, `!=`, `[NOT] IN` with quoted GUIDs, `IS [NOT] NULL` | `DmlFaithfulRowsTests.Where_*` |
| WHERE on the owner (`ownerid`) | `=`, `<>`, `!=` with a quoted GUID. Verified only with every row owned by the calling user (all rows match `=`, none `<>`): the throwaway table has no second owner, so a condition that tells two owners apart is not proven | `DmlFaithfulRowsTests.Where_*` |
| SET values, INSERT ... SELECT list | Literals, NULL, columns of text, multiline text, whole number, decimal, currency and floating point; `+ - * / %` between numbers (whole number / whole number truncates toward zero as in SQL; whole-number arithmetic that leaves the int range, and division by zero, are errors and nothing is written), unary minus (also of a parenthesised expression); COALESCE, ISNULL (two arguments), NULLIF and simple CASE of numbers, IIF and searched CASE (nested too) whose conditions are comparisons between numbers or number columns, or IS [NOT] NULL, combined with AND/OR (unknown takes the ELSE branch); CAST/CONVERT to `nvarchar(n)` with n from 1 to 4000 (of text, truncated to n as in SQL; or of a whole number, which is an error when it does not fit, as in SQL), to `int` (of a number) or to `decimal(p, s)` with both given (rounded half away from zero to s places; a value that does not fit is an error), `CAST('<guid>' AS uniqueidentifier)`; TRY_CAST/TRY_CONVERT to `int` or `decimal(p, s)` of a number (NULL where it does not fit); UPPER, LOWER, LTRIM, RTRIM, TRIM and LEN of one text argument; SUBSTRING(text, start, length) with a literal start of 1 or more and a literal length of 0 or more; CONCAT of text and whole numbers; ABS; ROUND(value, places) with a literal number of places of 0 or more, of whole numbers, decimal, currency and floating point. A number written to a whole-number column is truncated toward zero, as in SQL (`SET num = amount`, `SET num = 2.7`). Text that is empty is stored by Dataverse as NULL | `DmlAllowlistTests.SetExpression_*`, `SetNumericExpression_*`, `DmlFaithfulRowsTests.ProvenShape_WritesExactlyTheseValues`, `ValueTsqlWouldRefuse_*`, `TsqlValueSemanticsTests` |
| Written columns (SET, INSERT) | Valid for create (INSERT) or update (UPDATE), and of these kinds: text and multiline text (text, never a number: convert a whole number with CAST(... AS nvarchar(n))); whole number, decimal, currency and floating point (a number, never text); yes/no (`1`, `0` or NULL); choice (one of the column's own values, or NULL); user-local date and time (a quoted literal with `Z` or an offset, or NULL); date only (`'yyyy-MM-dd'` or NULL); a lookup that points at one table (a quoted GUID, `CAST('<guid>' AS uniqueidentifier)`, or NULL); `statecode` and `statuscode` together in one UPDATE, as a pair that belongs together (checked when the metadata states which status a reason belongs to; not in INSERT: a new row starts active) | `DmlFaithfulTests.NotLiveVerified_IsRefused`, `DmlFaithfulRowsTests.ProvenShape_*`, `StatusReasonOfAnotherStatus_IsRefused`, `DmlValueCoercerTests.Coerce_DateTimeWithAnOffset_IsTheSameInstantInUtc` |
| INSERT VALUES | An explicit column list without duplicates; one to 1,000 rows of the same length (SQL Server's own limit); each value a literal (text, also `N'...'`, a whole or decimal number, a negative number), NULL or `CAST('<guid>' AS uniqueidentifier)`, of the kinds above. An expression is refused | `DmlAllowlistTests.InsertValues_*`, `DmlAllowlistPlannerTests.ValuesWithMoreRowsThanSqlServerAllows_IsRefused` |
| INSERT ... SELECT | From the same table (`table`, `dbo.table`, or aliased: `FROM table s ... s.col`); the SELECT list has the INSERT list's length; each item a column or an aliased expression, names unique, and an alias may not be the name of a column the SELECT reads; values are mapped by position; WHERE as above; ORDER BY [ASC/DESC] on one or more whole-number, text, user-local date-and-time or date-only columns (NULLs first, text in collation order), with or without TOP; `TOP n` or `TOP (n)` from 0 to 5,000, only with ORDER BY | `DmlAllowlistTests.InsertSelect_*`, `DmlFaithfulTests.InsertSelect*`, `DmlAllowlistPlannerTests.ProvenShapes_ArePlanned` |
| Row cap | A statement that would change more rows than the cap changes nothing (verified at the cap and one over) | `DmlRowCapTests.*` |
| Hints | `-- ppds:BATCH_SIZE n` (verified live: same rows and values). `-- ppds:BYPASS_PLUGINS` and `-- ppds:BYPASS_FLOWS` were run live and changed exactly the expected rows, but the throwaway table has no plugin or flow, so the bypass itself is not verified; they stay gated: named in the confirmation, one statement only, and refused in a Production-protected environment (and in one whose configuration states neither a type nor a protection level) unless the environment's settings already bypass that logic: `BypassCustomPlugins` `Synchronous` or `All` for BYPASS_PLUGINS (it bypasses synchronous logic), `BypassPowerAutomateFlows` for BYPASS_FLOWS. No other hint, and no `OPTION (...)` clause, is accepted on DML | `DmlStatementFidelityTests.BypassPluginsAndFlowsHints_*`, `BatchSizeHint_*`, `DmlHintGateTests`, `DmlRound1RefusalTests.BypassPlugins_*` |

**Observed Dataverse behaviour (live, 2026-11-17), and what the engine does about it.**

- FetchXML `ne`, `not-in`, `not-like`, `not-begin-with` and `not-end-with` also match rows whose column is NULL; SQL's `<>`, `NOT IN` and `NOT LIKE` never do. The FetchXML generator therefore pairs every such condition with `not-null` on the same column, inside the same branch (SELECT and DML alike). The first live run deleted the NULL row for `DELETE ... WHERE ppds_num <> 2`; after the fix every negated operator on every listed type, also inside OR and nested parentheses, matched exactly the SQL rows. `not-between` already excludes NULL rows.
- A date/time literal without an offset (`'2024-01-01'`) was read in the calling user's time zone in the first live run (it matched a row stored at 2024-01-01T00:00Z for `< '2024-01-01'`), while SQL has no time zone, so DML refuses such a literal on a date-and-time column: "The date/time literal ... has no time zone ... Add Z (UTC) or an offset such as +01:00." Literals with `Z` or an offset matched and were written exactly. Writing `'2024-05-05T10:00:00+02:00'` was first stored hours off (the offset was converted to the machine's local time); it is now sent as the UTC instant.
- A date-only column takes `'yyyy-MM-dd'` and compares and stores the date as written. User-local columns shown as date only (they store a time in UTC), time-zone-independent columns and columns whose behaviour the metadata does not state were not verified and are refused, in WHERE (including `IS [NOT] NULL`), in INSERT ... SELECT ORDER BY and in writes.
- In an earlier live run Dataverse stored a choice value that is not one of the column's choices without an error, so a written choice value is checked against the column's metadata (the live suite now shows only the refusal).
- `statecode` and `statuscode` written together by a plain update changed exactly the matched rows (both directions on the throwaway table). Tables whose status changes need a dedicated message (for example closing a case or an opportunity) were not tested; Dataverse rejects such an update row by row and the failures are reported.
- Text is compared case- and accent-insensitively by Dataverse, ignoring trailing spaces for `=` and `IN`, while the engine's own evaluator (used for SET values) compares ordinally ignoring case. A text comparison inside a value (NULLIF, simple CASE, IIF/CASE conditions) and REPLACE are therefore refused; WHERE never uses the evaluator.
- The first live run also found that INSERT ... SELECT dropped its source WHERE and ORDER BY (the source was re-rendered to text and parsed again); it is now planned from the parsed statement.
- A LIKE pattern with a `%` or `_` inside it (`'alp_h%'`, `'%l_ha'`, `'a%h%'`) is sent whole as FetchXML `like`; begins-with and ends-with are used only when the rest of the pattern is plain text, because whether Dataverse reads a wildcard inside their value is not established.
- An empty string written to a text column is stored as NULL.

**Where DML executes.** Only `SqlQueryService` (`ExecuteAsync` and `ExecuteStreamingAsync`, both through `PrepareExecutionAsync`) passes a bulk executor to the plan, and a DML plan cannot run without one (`DmlExecuteNode`: "BulkOperationExecutor is required"). Every surface that runs DML uses it: `ppds query sql`, `ppds query history execute`, RPC `query/sql` (VS Code extension), and the TUI. In `PrepareExecutionAsync` every INSERT, UPDATE and DELETE anywhere in the request (inside blocks, IF, WHILE and TRY) goes through the guard's allowlist and then `DmlColumnValidator`, before confirmation is asked for and also for `--dry-run`; a service without column metadata (no provider, a provider that fails, or a table with no columns) refuses DML at that point, and `DmlExecuteNode` itself refuses to write values without metadata or for a column missing from it (`DmlRound1RefusalTests.WithoutColumnMetadata_*`, `DmlExecuteNodeCoercionTests.InsertValues_WithoutMetadataProvider_IsRefused_*`). A request with TDS selected never runs DML (`Query.TdsIncompatible`). `ppds query explain` and `--dry-run` plan only. The ADO.NET provider (`PpdsDbCommand`) has no bulk executor and never changes data (`PpdsDbCommandTests.Dml_NeverExecutes_ThroughTheProvider`). The MCP `ppds_query_sql` tool only transpiles to a FetchXML read and sends it; it never writes (a read-only session also refuses non-SELECT text) (`QuerySqlToolDmlTests`); `explain` plans DML without reading or writing (`DmlRound1RefusalTests.Explain_OfDml_NeverReadsOrWrites`).

Refused (each pinned by `DmlAllowlistTests.NotOnTheAllowlist_IsRefusedBeforeAnythingChanges_NamingThePart`, `DmlStatementFidelityTests.UnsupportedClause_*`, `DmlFaithfulTests.NotLiveVerified_IsRefused`, `DateColumnsOfAnUnverifiedBehaviour_AreRefused`, `DmlRound1RefusalTests.IsRefusedBeforeAnythingIsReadOrWritten`, `DmlAllowlistPlannerTests.UnsupportedDml_IsRefusedByThePlanner` or `NotProvenLive_IsRefusedByThePlanner`, and a sample of each category also live): NOT in WHERE, COLLATE, LIKE ESCAPE, ODBC escapes and literals, functions in WHERE, variables, subqueries, EXISTS, column-to-column comparisons, constant-on-the-left comparisons, `= NULL`/`<> NULL`, a NULL inside IN or NOT IN, LIKE patterns with `[ ]`, IN with more than 1,000 values, a floating-point literal (`2.5E0`), a literal of the wrong kind for the column (text for a number, `5.0` or a number outside the int range for a whole number, a number for text, a non-GUID or a GUID without dashes for an id, a date without a zone for a date-and-time column, a date and time for a date-only column), `<`/`>`/BETWEEN on text, yes/no, choice or id columns, IN on floating point, yes/no, date, multiline text or owner columns, a unique-identifier column other than the table's id, any other column type in WHERE (big whole number, multi-table lookups such as customer, file, image, multi-select choice, ...), unverified date kinds (also in `IS [NOT] NULL` and ORDER BY); FROM with joins, derived tables, functions, another environment, or a target not bound in FROM; a schema other than `dbo`; table hints; writes to `#temp` tables; writes to the primary key, the owner, a lookup that can point at more than one table, a calculated or read-only column, a big whole number, `statecode` or `statuscode` alone or in a mismatched pair or in INSERT or set to NULL, a choice value that is not one of the column's choices, text into a number column; reading other column types into a value; text comparisons inside a value; REPLACE, LEFT, RIGHT; GETDATE, GETUTCDATE, SYSDATETIME, SYSUTCDATETIME, SYSDATETIMEOFFSET, CURRENT_TIMESTAMP, NEWID; conversions of text to a number, a decimal, currency or floating-point value turned into text (written to a text column, in CONCAT, CAST/CONVERT to nvarchar) and a number given to a text function, CAST/CONVERT to `nvarchar` without a length, `nvarchar(max)`, `decimal` without both precision and scale, `numeric`, date/time or other types, a CONVERT style, TRY_CAST/TRY_CONVERT to anything but `int` or `decimal(p, s)`; TRIM with LEADING/TRAILING/BOTH/FROM or characters, LTRIM/RTRIM with a second argument, SUBSTRING with a start below 1, a negative length or a non-literal start or length, ROUND with a negative or non-literal number of places or a third argument; `+` with text (use CONCAT); unary plus; NOT inside IIF/CASE; an expression in INSERT VALUES, and more than 1,000 VALUES rows; INSERT ... SELECT from another table or environment, with joins, DISTINCT, GROUP BY/HAVING, OFFSET, TOP PERCENT/WITH TIES, TOP without ORDER BY, TOP above 5,000, `SELECT *`, an unaliased expression, duplicate output names, an alias that is the name of a column the SELECT reads, or ORDER BY on any other column type (choice, lookup, yes/no, decimal, currency, floating point, ...); variables, subqueries and unlisted functions in values; TOP on UPDATE/DELETE, `INSERT TOP (n)`, OUTPUT, WITH (CTE), compound SET (`+=`), duplicate SET columns, `SET @variable`, `WHERE CURRENT OF`, `IDENTITY()`, INSERT without a column list or with mismatched counts, INSERT EXEC, DEFAULT VALUES, INSERT from UNION/INTERSECT/EXCEPT, any `OPTION (...)` clause; the `-- ppds:NOLOCK`, `-- ppds:HASH_GROUP`, `-- ppds:MAXDOP` and `-- ppds:USE_TDS` hints and TDS mode; MERGE (the engine counted rows but wrote nothing) and EXECUTE AS/REVERT (never applied).

**Previously accepted, now refused (with what to write instead).** Every suggested rewrite below is itself on the allowlist (`DmlRefusalRewriteTests`).

| Statement users are likely to write | Why it is refused | Write instead |
|---|---|---|
| `DELETE FROM account WHERE NOT (name = 'x')` | NOT was dropped by the transpiler (this deleted the 'x' rows) | `WHERE name <> 'x'` (also excludes NULL names, as SQL does) |
| `UPDATE account SET ... WHERE name = NULL` | `= NULL` matches nothing in SQL; it was sent as "equals empty" | `WHERE name IS NULL` |
| `UPDATE contact SET ... WHERE createdon < '2024-01-01'` | Dataverse reads a literal without a zone in your time zone | `WHERE createdon < '2024-01-01T00:00:00Z'` (or an offset such as `+01:00`) |
| `UPDATE task SET scheduledend = GETDATE()` | The engine evaluates it per row, SQL once per statement | `SET scheduledend = '2026-11-17T09:30:00Z'` |
| `SET name = LEFT(name, 10)` / `RIGHT(...)` | Not verified | `SUBSTRING(name, 1, 10)` |
| `SET name = REPLACE(name, 'a', 'b')` | Dataverse and the engine match accented text differently | No equivalent; change those rows by id |
| `SET name = name + ' (old)'` | `+` with text | `CONCAT(name, ' (old)')` |
| `SET description = IIF(name = 'x', 'a', 'b')` | Text comparisons inside a value are decided differently by the engine | Two UPDATEs, each with its own WHERE |
| `SET description = CAST(name AS nvarchar)`, `SET amount = CAST(amount AS decimal)` | SQL supplies a length (30) or a precision and scale (18, 0) the engine did not apply | Name them: `CAST(name AS nvarchar(30))`, `CAST(amount AS decimal(18, 0))` |
| `SET name = TRIM(TRAILING FROM name)` | The options were not applied (both sides were trimmed) | `RTRIM(name)` |
| `SET name = SUBSTRING(name, 0, 3)` | SQL's rule for a start below 1 is not reproduced | `SUBSTRING(name, 1, 2)` |
| `INSERT INTO account (name, numberofemployees) VALUES (UPPER('a'), 1 + 2)` | Only literal values were proven in VALUES | `VALUES ('A', 3)` |
| `INSERT INTO account (name) SELECT TOP 10 name FROM account` | Without ORDER BY the text does not say which rows TOP takes | `... SELECT TOP 10 name FROM account ORDER BY name` |
| `INSERT INTO account (name) SELECT fullname FROM contact` | Only copies within one table were proven | Select the rows, then insert them with INSERT ... VALUES |
| `UPDATE TOP (10) account SET ...` | TOP on UPDATE/DELETE was ignored (every match changed) | Narrow the WHERE, for example by `accountid IN (...)` |
| `UPDATE a SET ... FROM account a JOIN contact c ON ...` | Joins in UPDATE/DELETE are not proven | Select the ids first, then `WHERE accountid IN ('...', '...')` |
| `DELETE FROM account WHERE accountid IN (SELECT ...)` | Subqueries in DML WHERE are not proven | Select the ids first, then `IN ('...', '...')` |
| `UPDATE incident SET statecode = 1 WHERE ...` | statecode needs a matching statuscode | `SET statecode = 1, statuscode = 5` (a pair that belongs together) |
| `UPDATE account SET ownerid = '...'` | Assigning moves sharing and cascades, which SQL does not show | Assign the rows in Dataverse |
| `INSERT INTO account (accountid, name) VALUES (NEWID(), 'x')` | Dataverse generates row ids | Leave the id column out |
| `UPDATE contact SET parentcustomerid = '...'` | A customer lookup can point at more than one table | Not supported by SQL DML |
| `UPDATE opportunity SET estimatedclosedate = '2024-01-15'` (user-local date-only column) or a time-zone-independent column | Not verified | Not supported by SQL DML yet |
| `SET revenue = CAST(description AS int)`, `TRY_CAST(name AS int)` | The engine parses text into numbers differently from SQL | Not supported |
| `SET followupby = CAST('2024-01-01' AS datetime)` | Date/time conversions are not verified | The quoted literal with `Z` or an offset |
| `UPDATE account SET customertypecode = 999` | Not one of the column's choices (Dataverse would store it) | One of the column's values (the message lists them) |
| `SET description = revenue`, `CONCAT(name, ' ', revenue)`, `CAST(creditlimit AS nvarchar(20))` | The text SQL makes of a decimal depends on its scale; the engine's differs | Round to a whole number first: `CAST(CAST(revenue AS int) AS nvarchar(20))`, or keep the value in a number column |
| `WHERE numberofemployees = 5.0`, `WHERE name = 5`, `WHERE revenue = 2.5E0` | The literal is not of the column's kind | `= 5`, `= '5'`, `= 2.5` |
| `INSERT INTO account (name) SELECT TOP 5 name FROM account ORDER BY statuscode` (a choice, lookup or yes/no column) | Dataverse sorts those by their label or name, SQL by the stored value | Order by a whole-number, text or date column |
| `-- ppds:MAX_ROWS 10` or CLI `--top 10` with DML | They limit returned rows only and were ignored | Put the limit in the WHERE clause |
| `-- ppds:NOLOCK`, `-- ppds:HASH_GROUP`, `-- ppds:MAXDOP 2` or `OPTION (MAXDOP 2)` with DML | Not proven for DML; NOLOCK selects rows another transaction has not committed | Remove the hint |
| DML with TDS selected (`useTds`, the TUI toggle, `-- ppds:USE_TDS`) behind a leading comment | The TDS check read only the start of the text; the read replica must never choose the rows to change | Turn TDS off |
| A script with `GO` | Batches after the first were dropped | Run each batch as its own request |
| `MERGE ...` | The engine counted rows but wrote nothing | INSERT ... SELECT, UPDATE or DELETE |

**Other statement kinds.** Only INSERT, UPDATE and DELETE change Dataverse data. `EXEC <message>` (including `sp_executesql` and `EXEC('...')`) never sends a request: `ExecuteMessageNode` throws "not yet supported" and dynamic SQL is rejected at plan time. `TRUNCATE`, `ALTER`, and `CREATE TABLE` of a non-temporary table are unsupported statements. `CREATE TABLE #t`/`DROP TABLE #t` act on session temp tables only (they need a session, which `SqlQueryService` does not have, so they fail with "... needs temporary table support"), and `DROP TABLE` of a Dataverse table fails with "is not supported"; neither recurses (`DmlAllowlistTests.TempTableStatements_*`). Cursors need a session too. DECLARE/SET/PRINT/THROW/RAISERROR/IF/WHILE/TRY act on script state only. SQL with more than one batch (separated by `GO`) is refused (invalid arguments, exit 3) instead of running the first batch and dropping the rest (`DmlAllowlistTests.MoreThanOneBatch_*`, `DmlAllowlistPlannerTests.MoreThanOneBatch_*`).

**Guard refusals in TRY/CATCH.** A safety refusal raised while a script runs (the row cap; the planner's allowlist refusal) is not catchable by script code: it passes through `BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH` at any depth (including inside WHILE), ends the run with its own error code, and nothing after it runs. Ordinary errors (THROW, RAISERROR, data errors) are still caught.

**Fail-closed guard.** A `SqlQueryRequest` whose `DmlSafety` is null is checked with the default options (not confirmed, not dry-run, default row cap), so DML without explicit options is refused with `Query.DmlConfirmationRequired`. RPC `query/sql` without `dmlSafety` therefore refuses DML with error code `Query.DmlConfirmationRequired` and error data `{ code, message, dmlConfirmationRequired: true, dmlTargets }`. The VS Code notebook sends no `dmlSafety`, so notebook cells cannot run DML (they get this refusal; the notebook has no confirmation UI). TUI load-more never runs DML (refused as a further page).

**Confirmation targets and binding.** `Query.DmlConfirmationRequired` lists every environment the request would write to (`PpdsDmlConfirmationRequiredException.Targets`); over RPC the error data adds `dmlTargets: [{ label, url, type, protection }]` (`label` and `type` are null when the environment has no configuration; `protection` is the level the guard applied) and, when a bypass hint is present, `dmlBypass: ["BYPASS_PLUGINS" | "BYPASS_FLOWS"]`. Writes to another environment are refused before confirmation is asked for, so the list is the connected environment. Every surface configures the guard through `SqlQueryService.ConfigureEnvironment(url, config)`. A confirmation applies only to what it was given for: in the TUI it is bound to the exact statement text and the screen's environment, is taken at the start of the next execution of any kind (so a cancelled, failed or re-authenticated run, a different statement and a later run of the same statement all ask again), and the dialog names the environment(s), their protection and the statement (`SqlQueryPresenterTests.Confirmation_*`, `ConfirmationPrompt_*`). Over RPC a request with `dmlSafety.isConfirmed` that contains DML must name the environment in `environmentUrl` and runs only against it; otherwise it is refused with `Query.DmlBlocked` ("A confirmed INSERT, UPDATE or DELETE request must name the environment it was confirmed for (environmentUrl) ...") before anything is read (`DmlRound1RefusalTests.Rpc_*`). Both extension surfaces (Data Explorer and notebooks) always send it with a confirmed request. The confirmation is not bound to the statement text over RPC (that needs a protocol change).

**Refusal wording per surface.** Service messages name no CLI flag. The confirmation message is "This request changes data (INSERT, UPDATE or DELETE) and must be confirmed before it runs. Nothing was executed.", followed, when a bypass hint is present, by "It also bypasses synchronous custom plugins and workflows (-- ppds:BYPASS_PLUGINS)." (or Power Automate flows); the CLI replaces the first part with "DML operations require --confirm to execute. Use --dry-run to preview the operation." and keeps the bypass sentence. The row-cap message ends "Narrow the WHERE clause to affect fewer rows." (for INSERT VALUES, which states its exact row count: "Split the INSERT into smaller statements."); the CLI adds "To change every matching row, re-run with --no-limit.", and the TUI adds that the cap cannot be removed in the TUI and to use `ppds query sql --no-limit`. RPC returns the neutral message with structured data. `--top`, `--page` and `--paging-cookie` refusals name CLI flags because only the CLI can set them.

**Row cap.** A DML statement that would affect more rows than the cap (10,000 by default; removed by `--no-limit` on the CLI or `dmlSafety.noLimit` over RPC; the TUI cannot remove it) is refused before it changes anything, with error code `Query.DmlRowCapExceeded` (`PpdsDmlRowCapExceededException`, carrying the cap, operation and entity). The message states the cap, that at least cap + 1 rows match (INSERT VALUES states its exact row count), that the statement changed nothing, and to narrow the WHERE clause; numbers use invariant digit grouping. Rows the statement would skip (no id) do not count toward the cap. A statement affecting exactly the cap rows runs. Applies to UPDATE, DELETE, INSERT...SELECT and INSERT VALUES. A non-positive `rowCap` is refused as invalid. CLI exit code 2 (like `Query.DmlBlocked`: confirmation cannot fix it, unlike exit 11). RPC error data is `{ code, message, dmlRowCapExceeded: true, rowCap, operation, entity }`, plus `dmlCommitted` when an earlier statement of the script already ran. `--dry-run` does not execute, so it cannot count rows: it reports the active `rowCap` and `ExceedsRowCap` stays false.

**Interrupted runs.** When a run stops after DML already changed rows (an error, a refusal, or a cancellation), every surface reports what already ran and was not rolled back (a refusal raised part-way through a script says "This statement and the ones after it did not run" instead of "Nothing was executed", and RPC adds `dmlCommitted` to `dmlBlocked` as it does for the row cap), and names the statement that was running when it stopped as "may be partly applied", with the counts the bulk executor had reported (a lower bound; batches run in parallel), including for a single statement. Nothing claims a rollback. The service wraps a non-PPDS failure after DML ran in a `PpdsException` (keeping a query error code; a sign-in failure after DML ran becomes `Query.DmlInterrupted` (never an `Auth.*` code, which clients answer by re-authenticating and replaying the request, running committed statements again), with `authenticationRequired: true` over RPC; the CLI exits 2 and the TUI shows the error instead of its re-auth dialog; a sign-in failure before any DML ran keeps its normal code); cancellation stays an `OperationCanceledException` with the outcome attached. A bulk write that is cancelled while it runs, on the parallel or the sequential path, or that reports fewer rows than it was given, is recorded as interrupted with the counts reported, never as a completed statement (`DmlExecuteNodeCoercionTests.SequentialBulkWrite_*`, `ParallelBulkWrite_*`, `BulkWrite_ReportingFewerRowsThanSubmitted_*`). The CLI prints the note to stderr and, in JSON mode, also puts it in the error envelope as `error.dmlCommitted`; RPC returns it as `dmlCommitted` and repeats it in `message` (a cancellation the caller requested returns `Operation.Cancelled` with `dmlCommitted`; one it did not request, such as a timeout, returns `Query.DmlInterrupted`). The server sends that error response for a cancelled request; whether the JSON-RPC client delivers its data after cancelling is not verified, and the VS Code Data Explorer currently shows a plain "cancelled" and drops `dmlCommitted` (extension change pending); the TUI shows it in the error text, including "Query cancelled." plus the note when the user cancels. An interrupted statement swallowed by a script's CATCH makes the outcome `Query.DmlInterrupted` (exit 2), worded as interrupted and possibly partly applied.

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
| `PpdsException(DmlBlocked)` | DML without WHERE clause | Block execution, tell user to add WHERE or use `ppds data truncate --entity <table>` |
| `PpdsException(DmlBlocked)` | A DML part not on the allowlist (see "DML allowlist"), MERGE, EXECUTE AS, a write to or read from another environment, or a request for a further page that contains DML; a column type, operator or literal not proven for that column (`DmlColumnValidator`, which also refuses an unknown or display column, a table whose columns cannot be read or that has none, and NULL written to `statecode`/`statuscode`); no column metadata available; the `-- ppds:NOLOCK`, `HASH_GROUP` or `MAXDOP` hint on a request containing DML; a bypass hint on a request with more than one statement, or in a Production-protected environment (or one with no saved type or protection) whose settings do not already bypass that logic; an UPDATE/DELETE target that matches more than one FROM table; a confirmed RPC DML request that names no `environmentUrl` or another environment; a planner refusal raised while a script runs (then the message says the statement and the ones after it did not run, and the outcome of the earlier statements is attached: CLI `error.dmlCommitted`, RPC `dmlCommitted`) | Refused before anything runs, with the part named and what to write instead (CLI exit 2, RPC `dmlBlocked`) |
| `PpdsException(DmlConfirmationRequired)` | Actual DML was requested without confirmation | Require `--confirm` for execution (CLI exit code 11); `--dry-run` previews without confirmation; RPC `dmlConfirmationRequired` with `dmlTargets` |
| `PpdsException(DmlRowCapExceeded)` | A DML statement would affect more rows than the row cap | Refused before it changes anything; narrow the WHERE clause or remove the cap (`--no-limit`, RPC `noLimit`). CLI exit 2, RPC `dmlRowCapExceeded` with `rowCap`, `operation`, `entity` |
| `PpdsException(DmlInterrupted)` | A run stopped by a sign-in failure or a timeout after DML had changed data, or a statement interrupted part-way inside CATCH | Not retryable; the message says what already ran and that the request must not simply be re-run; RPC adds `authenticationRequired` for a sign-in failure; CLI exit 2 |
| `PpdsValidationException` (`top`, `page`, `MAX_ROWS`, `hint`, `BATCH_SIZE`, `rowCap`, `sql`) | Explicit `--top`/`--page`/`--paging-cookie`, a `MAX_ROWS` hint or an inexact `ppds:` comment with DML, an out-of-range BATCH_SIZE or row cap, or more than one batch (GO) | Refused before anything runs; CLI exit 3 |
| DML rows failed (`SqlQueryResult.DmlOutcome.HasFailures`) | Any row of any executed INSERT/UPDATE/DELETE statement failed (scripts included, not only the last statement) | CLI exit 1 (some rows succeeded, error code `Operation.PartialFailure`) or 2 (none did, `Query.ExecutionFailed`); the JSON envelope has `success=false`, the unchanged `data`, a top-level `error` and a `results[]` entry; text/CSV print `Error: ...` to stderr. The message gives counts, and for scripts per-statement counts with a representative error tied to its statement (batches run in parallel, so the error is not necessarily the first row's). RPC `query/sql` returns `success=false` plus additive `affectedRows`/`failedRows`/`dmlError`; the TUI status line and `query history execute` report the same outcome; the streaming final chunk carries `DmlOutcome` |
| `PpdsException(TdsIncompatible)` | TDS requested but query can't use TDS, including any request that contains DML with TDS selected (`useTds`, the TUI toggle or `-- ppds:USE_TDS`) | Fail with reason (DML, incompatible entity, unsupported feature); nothing is executed |
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
- [ ] A `MAX_ROWS` hint on a request containing DML is refused before anything runs (exit 3), also for `--dry-run` and before confirmation; for SELECT it applies and is reported; it is not reported when the statement has its own TOP; `BATCH_SIZE` is reported as applied for DML only (`DmlStatementFidelityTests.MaxRowsHint_*`, `BatchSizeHint_*`)
- [ ] Clauses the engine cannot honour (TOP on UPDATE/DELETE, INSERT TOP, OUTPUT, WITH, compound SET, SET @variable, missing or mismatched INSERT columns, SELECT *, INSERT EXEC/DEFAULT VALUES/UNION, MERGE) are refused with `Query.DmlBlocked` before anything changes, confirmed, unconfirmed and for dry-run; INSERT ... SELECT TOP keeps working and UPDATE ... FROM JOIN is refused until proven (`DmlStatementFidelityTests.UnsupportedClause_*`, `InsertSelectTop_*`, `UpdateWithJoinedFrom_IsRefused_UntilTheJoinIsProven`, `DmlSafetyGuardTests.Check_Merge_*`)
- [ ] DML runs only when every part is on the allowlist; everything else is refused with `Query.DmlBlocked` naming the part, before anything is read or changed, confirmed, unconfirmed and for dry-run; the planner enforces the same allowlist without the guard (`DmlAllowlistTests.NotOnTheAllowlist_*`, `DmlAllowlistPlannerTests.UnsupportedDml_*`)
- [ ] Column types, operators and literal forms are checked against metadata before any DML runs: every allowlisted type/operator/literal changes exactly the rows and writes exactly the values the same T-SQL would against the faithful fake, unverified ones (unverified date kinds also in IS NULL and ORDER BY, unique-identifier columns other than the id, multi-table lookups, owner and id writes, lone or mismatched statecode/statuscode, choice values outside the column's choices, wrong literal kinds, GUIDs without dashes, decimals turned into text) are refused naming the column, wherever the DML sits in the request (`DmlFaithfulRowsTests.Where_ChangesExactlyTheRowsItsTextSelects`, `ProvenShape_WritesExactlyTheseValues`, `DmlRound1RefusalTests.IsRefusedBeforeAnythingIsReadOrWritten`, `DmlFaithfulTests.NotLiveVerified_IsRefused`, `DateColumnsOfAnUnverifiedBehaviour_AreRefused`, `StatusReasonOfAnotherStatus_IsRefused`, `NumbersIntoText_AreRefusedUnlessProven`); without column metadata (no provider, a failing provider, a table with no columns) DML is refused before confirmation and for a dry-run, and `DmlExecuteNode` refuses to write without it (`DmlRound1RefusalTests.WithoutColumnMetadata_*`, `DmlExecuteNodeCoercionTests.InsertValues_WithoutMetadataProvider_*`, `InsertValues_ColumnMissingFromMetadata_IsRefused`); the ADO.NET provider never changes data (`PpdsDbCommandTests.Dml_NeverExecutes_ThroughTheProvider`), nor do the MCP SQL tool (`QuerySqlToolDmlTests`) and explain (`DmlRound1RefusalTests.Explain_OfDml_NeverReadsOrWrites`)
- [ ] Every allowlisted shape changes exactly the rows and values the same T-SQL would, against Dataverse, every column of the table is compared, and the affected/failed counts the service reports equal what changed; every refused category leaves the table and its row versions unchanged (`DmlLiveDifferentialTests.EveryAllowlistedShape_ChangesExactlyTheRowsAndValuesItsTextSays`, Category=Integration, run 2026-11-17). The run is skipped with its reason when `PPDS_DML_LIVE_CONFIRM_URL` is not set; cleanup of an interrupted run is the separate `CleanupOnly_RemovesOnlyThisSuitesLeftovers` operation (`PPDS_DML_LIVE_CLEANUP_ONLY=1`), which never reports the differential run as passed, and both remove only tables marked as this suite's and the solution when its name marks it as this suite's (`DmlLiveConfirmUrlTests`)
- [ ] A SELECT with IN (subquery) or EXISTS inside OR, parentheses or NOT is a clean "not supported" error, not a process crash (`SubqueryPlacementTests`)
- [ ] SET and INSERT ... SELECT expressions read the row's real values for every supported function and node kind, and every column read is fetched (`DmlAllowlistTests.SetExpression_*`, `SetNumericExpression_*`, `SetExpression_RequestsEveryColumnItReads`, `InsertSelect_MapsValuesByPosition_*`, `DmlAllowlistPlannerTests.SetExpression_*`)
- [ ] Every supported WHERE condition selects exactly the rows its text says against a fake that evaluates the FetchXML filter, count/top and aliases like Dataverse, including negated conditions inside OR and nested parentheses (`DmlFaithfulRowsTests.Where_ChangesExactlyTheRowsItsTextSelects`, `Delete_DeletesExactlyTheRowsItsTextSelects`); a row limit that leaked into a DML scan would change fewer rows in the fakes (`DmlTopOverrideTests.TheFake_HonoursTop_SoALeakedLimitWouldBeSeen`) and is refused by the planner (`DmlAllowlistPlannerTests.RowLimitsOrPaging_AreRefusedForDml_InThePlanner`)
- [ ] INSERT ... SELECT maps values by position; unaliased expressions and duplicate names are refused (`DmlAllowlistTests.InsertSelect_*`, `NotOnTheAllowlist_*`)
- [ ] An alias target writes the aliased table; a target row returned more than once is changed and counted once (`DmlAllowlistTests.Update_TheTargetOrItsAliasInFrom_*`, `Delete_TheTargetOrItsAliasInFrom_*`, `DuplicateSourceRows_*`)
- [ ] A refusal inside TRY inside WHILE stops the run once with its code and reports the earlier statement (`DmlAllowlistTests.RowCapRefusal_InsideTryInsideWhile_*`)
- [ ] SQL with more than one batch (GO) is refused (`DmlAllowlistTests.MoreThanOneBatch_*`, `DmlAllowlistPlannerTests.MoreThanOneBatch_*`)
- [ ] On DML, a ppds: comment that is not an exact supported hint is refused; BYPASS_PLUGINS, BYPASS_FLOWS and BATCH_SIZE are applied to the bulk writes (`DmlStatementFidelityTests.HintLikeComment_*`, `BypassPluginsAndFlowsHints_*`, `BatchSizeHint_*`)
- [ ] A sign-in failure after DML ran is `Query.DmlInterrupted` with `authenticationRequired`, never an `Auth.*` code; before DML it keeps its code; an unrequested cancellation after DML ran is an interruption; a swallowed interrupted statement is not "all 0 rows failed" (`DmlInterruptedRunTests.AuthFailureAfterDmlRan_*`, `PpdsAuthFailureAfterDmlRan_*`, `AuthFailureBeforeAnyDml_*`, `DmlReportingEdgeTests`)
- [ ] EXEC, dynamic SQL, TRUNCATE, ALTER and cursors never change data; `CREATE TABLE #t`/`DROP TABLE #t` and `DROP TABLE account` fail with a specific error and never recurse; EXECUTE AS/REVERT and MERGE are refused by the guard and the planner (`DmlStatementFidelityTests.OtherStatements_NeverChangeData`, `Impersonation_*`, `DmlAllowlistTests.TempTableStatements_*`, `DmlAllowlistPlannerTests.ImpersonationIsRefusedByThePlanner_*`, `MergeNodeTests.Plan_Merge*`)
- [ ] A write to another environment, a read of another environment inside DML, and the connected environment's own label are refused before anything runs (also for dry-run and before confirmation) with accurate messages (`DmlStatementFidelityTests.WriteToAnotherEnvironment_*`, `ReadFromAnotherEnvironment_IntoTheConnectedOne_IsRefused`, `DmlAllowlistTests.ConnectedEnvironmentsOwnLabel_*`, `DmlRefusalWordingTests.ReadOnlyCrossEnvironmentWrite_*`, `SqlQueryServiceTests.ExecuteAsync_CrossEnvDml_*`, `ExecuteAsync_LocalTargetWithRemoteFromSource_IsRefused`)
- [ ] Explicit CLI paging with DML is refused (exit 3); a caller's first-page value never narrows DML; a request for a further page never runs DML, even confirmed; SELECT paging is unchanged (`DmlStatementFidelityTests.ExplicitPaging_*`, `FirstPageArgument_*`, `LoadMoreRequest_*`, `Paging_WithSelect_*`, `DmlRowCapTests.Tui_LoadMoreRequest_*`, `SqlCommandTests.BuildRequest_MarksExplicitPagingAsUserSpecified`)
- [ ] A row-cap refusal inside BEGIN TRY ends the script with `Query.DmlRowCapExceeded` and nothing after it runs; ordinary errors are still caught (`DmlStatementFidelityTests.RowCapRefusal_InsideTry_*`, `OrdinaryErrors_InsideTry_*`)
- [ ] An interrupted run (error or cancellation) reports what already ran and the in-flight statement as "may be partly applied" with the reported counts, including a single statement, on CLI (stderr and JSON `error.dmlCommitted`), RPC (`dmlCommitted`, `Operation.Cancelled` for a cancel) and TUI (including cancel); the service wraps non-PPDS failures after DML ran (`DmlInterruptedRunTests`, `SqlQueryPresenterTests.ExecuteAsync_CancelledAfterDmlRan_*`, `HistoryExecuteCommandTests.Replay_ScriptInterrupted*`, `DmlScriptReportingTests.Script_Interrupted*`)
- [ ] RPC error data for guard refusals is produced by the handler's real mapping (`TryMapQuerySqlFailure`): confirmation (`dmlConfirmationRequired`, `dmlTargets`), blocked (`dmlBlocked`), row cap (`dmlRowCapExceeded`, `rowCap`, `operation`, `entity`, `dmlCommitted`) (`DmlInterruptedRunTests.Rpc_*`, `DmlRefusalWordingTests.ConfirmationRequired_Rpc_*`, `RowCap_Rpc_*`)
- [ ] Service refusal messages name no CLI flag; the CLI words confirmation and row cap with `--confirm`/`--dry-run`/`--no-limit`, the TUI says the cap cannot be removed there (`DmlRefusalWordingTests`, `DmlRowCapTests.OverTheCap_*`, `SqlQueryPresenterTests.ExecuteAsync_RowCapRefusal_*`)
- [ ] Row-cap edges: id-less rows do not count, INSERT VALUES states its exact count, invariant digit grouping, a non-positive row cap is refused (`DmlRowCapTests.AStatementWithExactlyTheCapUsableRows_*`, `RowCapMessage_UsesInvariantDigitGrouping`, `DmlRefusalWordingTests.NonPositiveRowCap_*`)
- [ ] History replay is exactly as hard as `query sql` at Production and Test protection, with exact exit codes, and dry-run reports the row cap with and without `--no-limit` (`HistoryExecuteCommandTests.Replay_ProtectedEnvironment_IsExactlyAsHardAsSql`, `Replay_DryRun_PrintsPlan_WithTheRowCap_ExecutesNothing`, `Replay_DmlWithoutWhere_*`)
- [ ] A DML confirmation applies only to the statement and environment it was given for: in the TUI it is taken at the start of the next execution, so a different statement, a later run, a cancelled or failed connection and a declined sign-in all ask again, and the dialog names the environment and the statement; over RPC a confirmed DML request without `environmentUrl`, or for another environment, is refused (`SqlQueryPresenterTests.Confirmation_*`, `DecliningTheConfirmation_*`, `ConfirmationPrompt_*`, `DmlRound1RefusalTests.Rpc_*`)
- [ ] The bypass-hint gate fails closed: an environment with no saved type or protection counts as Production, and BYPASS_PLUGINS needs settings that bypass synchronous plugins (`DmlRound1RefusalTests.BypassPlugins_*`, `DmlHintGateTests`)
- [ ] NOLOCK, HASH_GROUP, MAXDOP and USE_TDS hints, any OPTION clause and TDS mode are refused with DML (`DmlHintGateTests.UnprovenHintOnDml_IsRefused`, `UseTdsHintOnDml_IsRefused`, `DmlRound1RefusalTests.Dml_WithTdsSelected_IsRefused`)
- [ ] Values the engine computes are what T-SQL computes, or an error with nothing written: nvarchar(n) truncation and number overflow, decimal(p, s) rounding and overflow, TRY_ conversions giving NULL, int overflow (`TsqlValueSemanticsTests`, `DmlFaithfulRowsTests.ValueTsqlWouldRefuse_*`)
- [ ] A bulk write cancelled on the sequential or the parallel path, or reporting fewer rows than it was given, is recorded as interrupted (`DmlExecuteNodeCoercionTests.SequentialBulkWrite_*`, `ParallelBulkWrite_*`, `BulkWrite_ReportingFewerRowsThanSubmitted_*`)
- [ ] A refusal raised after earlier statements of a script ran does not say "Nothing was executed" and carries what they committed, on the CLI and over RPC (`DmlRound1RefusalTests.RefusalAfterDmlRan_*`)
- [ ] Every rewrite a refusal suggests is itself on the allowlist (`DmlRefusalRewriteTests`)
- [ ] Echoed and printed history text escapes control, bidirectional and zero-width characters (keeping U+200C/U+200D between non-ASCII letters, marks or emoji); `history get` keeps redirected SQL exact and sanitises the stored error (`TerminalTextTests`, `DmlReportingEdgeTests.Joiners*`, `HistoryExecuteCommandTests.HistoryGet_TextOutput_*`)

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty SELECT | `SELECT FROM account` | Parse error at column 8 |
| Unknown operator | `SELECT * FROM account WHERE name ~ 'test'` | Parse error at `~` |
| Large result set | SELECT on 50k record entity | Paging with 5000-record pages |
| Comment preservation | `-- get accounts\nSELECT *` | Comment in FetchXML output |
| Unknown environment label | `SELECT * FROM [UNKNOWN].account` | Error: "No environment found matching label 'UNKNOWN'" |
| "dbo" as label | `SELECT * FROM [dbo].account` | Treated as standard SQL schema, executes locally |
| Multiple hints on one query | `-- ppds:NOLOCK` + `-- ppds:BYPASS_PLUGINS` | SELECT: both hints applied. DML: refused (NOLOCK is not accepted on DML) |
| Hint on TDS query | `-- ppds:NOLOCK` + `-- ppds:USE_TDS` | TDS wins; NOLOCK irrelevant for TDS |
| BATCH_SIZE on SELECT | `-- ppds:BATCH_SIZE 500` on SELECT | Hint has no effect, no error |
| Malformed hint value | `-- ppds:BATCH_SIZE abc` | SELECT: hint silently ignored, query proceeds; DML: refused (exit 3) |
| TDS + DML | `DELETE FROM account` with `useTds=true`, also behind a leading comment or in a script | Error: DML not supported via TDS (`Query.TdsIncompatible`), nothing executed |
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
