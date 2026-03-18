# Query Engine Roadmap

> Roadmap extracted from specs/query-engine-v2.md during spec governance restructuring.
> Phase 0 (Foundation) has been absorbed into specs/query.md as core architecture.
> This file retains the unimplemented phases (1-7) as a roadmap for future development.

**Clean Room:** All implementations derive from Microsoft FetchXML documentation, T-SQL language specification (ANSI SQL:2016), and Dataverse SDK documentation. No third-party query engine code is referenced or adapted.

**Tech Stack:** C# (.NET 8+), Terminal.Gui 1.19+, Microsoft.Data.SqlClient (Phase 3.5), xUnit

---

## Phase Dependency Graph

```
Phase 0 (Foundation) — IMPLEMENTED, see specs/query.md
  ├── Phase 1 (Core Gaps) ← requires expression evaluator, plan layer
  │     ├── Phase 2 (Composition) ← requires planner rewrite rules
  │     └── Phase 3 (Functions) ← requires expression evaluator + function registry
  │           └── Phase 3.5 (TDS) ← requires plan layer for routing
  ├── Phase 4 (Parallel) ← requires plan layer + pool integration
  ├── Phase 5 (DML) ← requires expression evaluator + plan layer
  ├── Phase 6 (Metadata) ← requires plan layer for MetadataScanNode
  └── Phase 7 (Advanced) ← requires all of the above
```

Phases 1, 2, 3 can proceed in parallel after Phase 0.
Phases 4, 5, 6 can proceed in parallel after Phase 1.
Phase 3.5 can proceed after Phase 3 (or in parallel with reduced scope).
Phase 7 is the final phase.

---

## Phase 1: Core SQL Gaps

**Goal:** HAVING, CASE/IIF, basic computed column expressions, COUNT(*) optimization.

### HAVING Clause

**Parser:** Add `SqlTokenType.Having`. After GROUP BY parsing, look for `HAVING` keyword. Parse condition into `ISqlCondition` stored in `SqlSelectStatement.Having`.

**Planning:** FetchXML does NOT support filtering on aggregate results, so HAVING always produces a `ClientFilterNode` after the `FetchXmlScanNode`.

```
FetchXmlScanNode (with aggregate=true) → ClientFilterNode(having condition) → ProjectNode
```

**ClientFilterNode:** Iterates input rows, yields only those where `evaluator.EvaluateCondition(condition, row)` is true.

### CASE / IIF Expressions

**Parser:** Add `CASE`, `WHEN`, `THEN`, `ELSE`, `END`, `IIF` keywords.

Both produce `ISqlExpression` nodes (`SqlCaseExpression`, `SqlIifExpression`). They appear in SELECT column list, WHERE/HAVING conditions, and ORDER BY.

**Planning:** CASE/IIF in SELECT creates a `ProjectNode` with computed output columns. In WHERE, pushable parts go server-side; CASE comparison uses `ClientFilterNode`.

### Computed Column Expressions

`revenue * 0.1 AS tax` → `SqlComputedColumn(SqlBinaryExpression(revenue, Multiply, 0.1), "tax")`

The `FetchXmlScanNode` requests the base columns needed by the expressions; `ProjectNode` evaluates them using the expression evaluator.

### COUNT(*) Optimization

For unfiltered `SELECT COUNT(*) FROM entity` (no WHERE, no JOIN, no GROUP BY), use `RetrieveTotalRecordCountRequest` (near-instant metadata read) via `CountOptimizedNode`. Fall back to aggregate FetchXML if unsupported.

### Condition Expressions (WHERE with expressions)

Extend WHERE to allow `ISqlExpression` on both sides of comparisons. Column-to-column and computed expressions use `ClientFilterNode`; literal comparisons push to FetchXML.

### Deliverables

| Item | Tests |
|------|-------|
| HAVING parsing + AST | Parser tests for HAVING with aggregates |
| ClientFilterNode | Filter node with mock data |
| CASE/IIF parsing + AST | Parser tests for nested CASE, IIF |
| CASE/IIF evaluation | Expression evaluator tests |
| Computed columns parsing | `revenue * 0.1 AS tax` parses correctly |
| Computed column projection | ProjectNode evaluates arithmetic |
| COUNT(*) optimization | CountOptimizedNode with mock service |
| Expression conditions | `WHERE revenue > cost` parsed and planned |

---

## Phase 2: Query Composition

**Goal:** IN/EXISTS subqueries, UNION/UNION ALL.

### IN Subquery -> JOIN Rewrite

```sql
SELECT name FROM account
WHERE accountid IN (SELECT parentaccountid FROM opportunity WHERE revenue > 1000000)
```

The planner rewrites to an INNER JOIN at the AST level before transpiling to FetchXML, so Dataverse handles the join server-side. When rewrite isn't possible (correlated subqueries), fall back to two-phase execution or `ClientFilterNode` with hash set lookup.

### EXISTS Subquery

EXISTS with correlated reference -> rewrite to INNER JOIN. NOT EXISTS -> LEFT JOIN + IS NULL. Both produce FetchXML `<link-entity>`.

### UNION / UNION ALL

**AST:** `SqlUnionStatement` with list of `SqlSelectStatement` queries and `IsUnionAll` flags per boundary.

**Planning:** `ConcatenateNode` yields rows from each child. `DistinctNode` deduplicates for UNION (without ALL) using `HashSet<CompositeKey>`.

### Deliverables

| Item | Tests |
|------|-------|
| IN subquery parsing | `WHERE id IN (SELECT ...)` parses |
| IN -> JOIN rewrite | Rewrite produces correct FetchXML |
| IN fallback (large results) | Hash set client filter works |
| EXISTS parsing | `WHERE EXISTS (SELECT ...)` parses |
| EXISTS -> JOIN rewrite | Correlated EXISTS becomes INNER JOIN |
| NOT EXISTS -> LEFT JOIN | Produces IS NULL filter |
| UNION/UNION ALL parsing | Multiple SELECTs parse into SqlUnionStatement |
| ConcatenateNode | Yields rows from both children |
| DistinctNode | Deduplicates on UNION |
| Column count validation | Mismatched UNION column counts error |

---

## Phase 3: Functions

**Goal:** String functions, date functions, CAST/CONVERT. Client-side evaluated with server-side pushdown where FetchXML supports it.

### String Functions

| Function | Signature | Notes |
|----------|-----------|-------|
| `UPPER(expr)` | -> string | Client-side |
| `LOWER(expr)` | -> string | Client-side |
| `LEN(expr)` | -> int | Client-side |
| `LEFT(expr, n)` | -> string | Client-side |
| `RIGHT(expr, n)` | -> string | Client-side |
| `SUBSTRING(expr, start, length)` | -> string | 1-based start per T-SQL |
| `TRIM(expr)` / `LTRIM` / `RTRIM` | -> string | Client-side |
| `REPLACE(expr, find, replace)` | -> string | Client-side |
| `CHARINDEX(find, expr [, start])` | -> int | 1-based result, 0 = not found |
| `CONCAT(expr, expr, ...)` | -> string | Variadic, NULL-safe |
| `STUFF(expr, start, length, replace)` | -> string | Client-side |
| `REVERSE(expr)` | -> string | Client-side |

Functions are registered in a `FunctionRegistry` keyed by name. The expression evaluator dispatches `SqlFunctionExpression` to the registry.

### Date Functions

| Function | Signature | Notes |
|----------|-----------|-------|
| `GETDATE()` / `GETUTCDATE()` | -> datetime | Current UTC time |
| `YEAR(expr)` / `MONTH(expr)` / `DAY(expr)` | -> int | FetchXML-native in GROUP BY |
| `DATEADD(part, n, expr)` | -> datetime | Client-side |
| `DATEDIFF(part, start, end)` | -> int | Client-side |
| `DATEPART(part, expr)` | -> int | Client-side |
| `DATETRUNC(part, expr)` | -> datetime | Client-side |

**Server pushdown for GROUP BY:** `YEAR/MONTH/DAY(column)` in GROUP BY maps to native FetchXML date grouping (`dategrouping="year"`).

### CAST / CONVERT

Supported target types: `int`, `bigint`, `decimal(p,s)`, `float`, `nvarchar(n)`, `varchar(n)`, `datetime`, `date`, `bit`, `uniqueidentifier`, `money`.

---

## Phase 3.5: TDS Endpoint Acceleration

**Goal:** Optional acceleration path for read queries using the Dataverse TDS Endpoint (SQL Server wire protocol against a read-only replica).

> **Note:** TDS integration (DI wiring, UI toggle, error handling) is tracked in the query parity project plan. This phase covers the query engine routing and compatibility checking aspects.

### TDS Compatibility Check

A query is TDS-compatible when:
- It is a SELECT (no DML)
- All referenced entities support TDS (no elastic tables, no virtual entities)
- No PPDS-specific features used (virtual *name columns)
- The SQL is expressible in standard T-SQL

### TdsScanNode

Executes SQL directly against TDS endpoint via `SqlConnection`, yields rows via `SqlDataReader`.

### Auth Integration

No new auth flow needed. TDS endpoint accepts the same OAuth bearer token via `SqlConnection.AccessToken`.

---

## Phase 4: Parallel Execution Intelligence

**Goal:** Pool-aware parallel aggregate partitioning, parallel page fetching, EXPLAIN command.

### Parallel Aggregate Partitioning

When an aggregate query fails with the 50K limit error, retry with partitioned strategy:
1. Estimate record count via `RetrieveTotalRecordCountRequest`
2. Calculate partition count: `ceil(estimatedCount / 40000)`
3. Generate N date-range partitions with non-overlapping `createdon` filters
4. Execute ALL partitions in parallel across the connection pool
5. Merge-aggregate partial results (COUNT->sum, SUM->sum, AVG->sum/count, MIN->min, MAX->max)

```
ParallelPartitionNode
├── FetchXmlScanNode (partition 1, aggregate)
├── FetchXmlScanNode (partition 2, aggregate)
└── MergeAggregateNode (combines partial aggregates)
```

With pool capacity of 48, all partitions execute simultaneously — 48x faster than single-connection tools.

### Parallel Page Prefetch

`PrefetchScanNode` wraps `FetchXmlScanNode`, using a bounded `Channel<QueryRow>` to fetch pages ahead while the consumer processes current results.

### EXPLAIN Command

```
EXPLAIN SELECT COUNT(*) FROM account GROUP BY ownerid
```

Output: Plan tree with estimated rows, partition info, pool capacity, effective parallelism.

**CLI:** `ppds query sql "..." --explain` or `ppds query explain "..."`
**TUI:** Ctrl+Shift+E in SQL query screen

---

## Phase 5: DML via SQL

**Goal:** INSERT, UPDATE, DELETE via SQL syntax, leveraging existing `IBulkOperationExecutor` infrastructure.

### Safety Model

- `DELETE FROM entity` (no WHERE) BLOCKED at parse time
- `UPDATE entity SET ...` (no WHERE) BLOCKED at parse time
- Before execution: show estimated row count, require confirmation
- CLI: `--confirm` flag or interactive prompt; `--dry-run` shows plan without executing
- Default row cap: 10,000. Override with `--no-limit`
- All DML reports progress via `IProgressReporter`

### INSERT

```sql
INSERT INTO account (name, revenue) VALUES ('Contoso', 1000000)
INSERT INTO account (name, revenue) SELECT name, revenue FROM account WHERE statecode = 0
```

Pipelines source rows through `IBulkOperationExecutor.CreateMultipleAsync()`.

### UPDATE

```sql
UPDATE account SET revenue = revenue * 1.1 WHERE statecode = 0
```

Two-phase: Retrieve target records via FetchXML, evaluate SET expressions per row, feed to `UpdateMultipleAsync()`.

### DELETE

```sql
DELETE FROM opportunity WHERE statecode = 2 AND actualclosedate < '2020-01-01'
```

Retrieve target IDs via FetchXML, feed to `DeleteMultipleAsync()`.

---

## Phase 6: Metadata & Streaming

**Goal:** Query Dataverse metadata via SQL, progressive result streaming in TUI.

### Metadata Schema

```sql
SELECT logicalname, displayname FROM metadata.entity WHERE iscustomentity = 1
```

Virtual tables: `metadata.entity`, `metadata.attribute`, `metadata.relationship_1_n`, `metadata.relationship_n_n`, `metadata.optionset`, `metadata.optionsetvalue`.

`MetadataScanNode` executes appropriate metadata requests and yields rows.

### Progressive Result Streaming

TUI consumes `IAsyncEnumerable<QueryRow>` incrementally, refreshing UI every 100 rows. Combined with `PrefetchScanNode` for zero-wait pagination.

---

## Phase 7: Advanced

**Goal:** Window functions, variables, flow control. Power-user features with lower priority.

### Window Functions

```sql
SELECT name, revenue,
  ROW_NUMBER() OVER (ORDER BY revenue DESC) AS rank,
  SUM(revenue) OVER (PARTITION BY industrycode) AS industry_total
FROM account
```

Always client-side (FetchXML has no window function support). `ClientWindowNode` materializes all input rows, sorts/partitions, computes function values.

### Variables

```sql
DECLARE @threshold MONEY = 1000000
SELECT name, revenue FROM account WHERE revenue > @threshold
```

Variables in WHERE clauses resolved at plan time (substituted as literals into FetchXML).

### Flow Control

IF/ELSE, WHILE, BEGIN/END blocks. Creates `ScriptExecutionNode` — a mini interpreter. Lowest priority; variables without flow control cover 80% of use cases.

---

## Cross-Cutting Concerns

### Error Handling

New error codes: `QUERY_AGGREGATE_LIMIT`, `QUERY_DML_BLOCKED`, `QUERY_DML_ROW_CAP`, `QUERY_TDS_INCOMPATIBLE`, `QUERY_PLAN_TIMEOUT`, `QUERY_TYPE_MISMATCH`.

### Cancellation

All plan nodes accept `CancellationToken` via `QueryPlanContext`. TUI Ctrl+C sets the token.

### Progress Reporting

All plan nodes >1 second accept `IProgressReporter` via `QueryPlanContext`.

### Memory Bounds

Client-side nodes that materialize data (DistinctNode, ClientWindowNode, HashJoinNode) have configurable memory limits. Default: 500MB.

---

## Testing Strategy

### Unit Test Categories

| Category | Scope | Infrastructure |
|----------|-------|----------------|
| `TuiUnit` | AST, parser, transpiler, expression evaluator | No mocks needed |
| `PlanUnit` | Plan construction, node logic | Mock IQueryExecutor, mock data |
| `IntegrationQuery` | End-to-end against live Dataverse | Real connection pool |
| `IntegrationTds` | TDS endpoint queries | Real TDS connection |

### Regression Suite

SQL conformance test suite: JSON file mapping SQL input to expected FetchXML output and expected result shapes. Run on every commit.
