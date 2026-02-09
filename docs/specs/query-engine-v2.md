# Query Engine v2: Comprehensive Specification

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this spec phase-by-phase.

**Goal:** Evolve the PPDS query engine from a SQL-to-FetchXML transpiler into a full query execution engine with an execution plan layer, client-side expression evaluation, DML support, TDS Endpoint acceleration, and pool-aware parallel execution — achieving parity with and exceeding the capabilities of existing Dataverse SQL tools.

**Clean Room:** All implementations derive from Microsoft FetchXML documentation, T-SQL language specification (ANSI SQL:2016), and Dataverse SDK documentation. No third-party query engine code is referenced or adapted.

**Tech Stack:** C# (.NET 8+), Terminal.Gui 1.19+, Microsoft.Data.SqlClient (Phase 3.5), xUnit

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Phase 0: Foundation — Execution Plan Layer & AST Hierarchy](#2-phase-0-foundation)
3. [Phase 1: Core SQL Gaps — HAVING, CASE, Expressions, COUNT(*)](#3-phase-1-core-sql-gaps)
4. [Phase 2: Query Composition — Subqueries & UNION](#4-phase-2-query-composition)
5. [Phase 3: Functions — String, Date, CAST/CONVERT](#5-phase-3-functions)
6. [Phase 3.5: TDS Endpoint Acceleration](#6-phase-35-tds-endpoint)
7. [Phase 4: Parallel Execution Intelligence](#7-phase-4-parallel-execution)
8. [Phase 5: DML via SQL](#8-phase-5-dml)
9. [Phase 6: Metadata Schema & Progressive Streaming](#9-phase-6-metadata--streaming)
10. [Phase 7: Advanced — Window Functions, Variables, Flow Control](#10-phase-7-advanced)
11. [Cross-Cutting Concerns](#11-cross-cutting-concerns)
12. [Testing Strategy](#12-testing-strategy)

---

## 1. Architecture Overview

### Current Pipeline (v1)

```
SQL text → SqlLexer → SqlParser → SqlSelectStatement (AST)
  → SqlToFetchXmlTranspiler → FetchXML string
  → QueryExecutor (IDataverseConnectionPool) → EntityCollection
  → QueryResult → SqlQueryResultExpander → expanded QueryResult
```

All logic is a straight-line transform: parse, transpile, execute, expand. No intermediate representation for optimization, no client-side computation, no execution strategy selection.

### Target Pipeline (v2)

```
SQL text → SqlLexer → SqlParser → SqlStatement (AST hierarchy)
  → QueryPlanner → ExecutionPlan (tree of IQueryPlanNode)
  → PlanOptimizer (optional rewrites)
  → PlanExecutor → walks plan tree, dispatches to:
      ├── FetchXmlScanNode → IQueryExecutor (FetchXML path)
      ├── TdsScanNode → ITdsQueryExecutor (TDS Endpoint path)
      ├── ClientFilterNode → IExpressionEvaluator
      ├── ClientProjectNode → IExpressionEvaluator (computed columns)
      ├── ClientAggregateNode → group/aggregate in memory
      ├── HashJoinNode → client-side join
      ├── ConcatenateNode → UNION
      ├── ClientSortNode → ORDER BY on client-computed columns
      ├── ParallelPartitionNode → fan-out aggregate partitions
      ├── DmlExecuteNode → IBulkOperationExecutor pipeline
      └── MetadataScanNode → IOrganizationService metadata API
  → QueryResult → SqlQueryResultExpander → expanded QueryResult
```

### Key Design Principles

1. **Server pushdown first.** Every operation that FetchXML or TDS can handle natively MUST be pushed to the server. Client-side operators are fallbacks, not defaults.
2. **Plan nodes are iterators.** Each node implements `IAsyncEnumerable<QueryRow>` (Volcano model). Rows flow lazily from leaves to root. This enables streaming and bounded memory.
3. **Pool-aware execution.** The planner knows pool capacity and factors it into parallelism decisions.
4. **Backward compatible.** Every query that works today produces identical results. The plan layer is invisible to callers of `ISqlQueryService`.

### Project Layout

New and modified files (by namespace):

```
PPDS.Dataverse/
  Sql/
    Ast/                         ← existing, extended
      ISqlStatement.cs           ← NEW: statement hierarchy base
      SqlSelectStatement.cs      ← MODIFIED: implements ISqlStatement
      SqlInsertStatement.cs      ← NEW: Phase 5
      SqlUpdateStatement.cs      ← NEW: Phase 5
      SqlDeleteStatement.cs      ← NEW: Phase 5
      SqlUnionStatement.cs       ← NEW: Phase 2
      SqlExpression.cs           ← NEW: Phase 0 (expression AST)
      SqlFunction.cs             ← NEW: Phase 3 (function call AST)
    Parsing/
      SqlParser.cs               ← MODIFIED: multi-statement, expressions
      SqlLexer.cs                ← MODIFIED: new keywords & operators
      SqlTokenType.cs            ← MODIFIED: new token types
    Transpilation/
      SqlToFetchXmlTranspiler.cs ← EXISTING: unchanged core, minor additions
  Query/
    Planning/                    ← NEW: entire directory
      IQueryPlanNode.cs          ← plan node interface
      QueryPlanner.cs            ← AST → plan tree
      PlanOptimizer.cs           ← rewrite rules
      Nodes/                     ← one file per node type
    Execution/
      PlanExecutor.cs            ← NEW: walks plan tree
      IExpressionEvaluator.cs    ← NEW: evaluates expressions on rows
      ExpressionEvaluator.cs     ← NEW: implementation
    QueryExecutor.cs             ← EXISTING: FetchXML execution (unchanged)
    ITdsQueryExecutor.cs         ← NEW: Phase 3.5
    TdsQueryExecutor.cs          ← NEW: Phase 3.5
  Metadata/
    IMetadataQueryExecutor.cs    ← NEW: Phase 6
    MetadataQueryExecutor.cs     ← NEW: Phase 6

PPDS.Cli/
  Services/Query/
    ISqlQueryService.cs          ← MODIFIED: add ExecutePlanAsync, ExplainAsync
    SqlQueryService.cs           ← MODIFIED: route through planner
    DmlSafetyGuard.cs            ← NEW: Phase 5
  Commands/Query/
    SqlCommand.cs                ← MODIFIED: --explain, --dry-run flags
    ExplainCommand.cs            ← NEW: Phase 4
```

---

## 2. Phase 0: Foundation

**Goal:** Introduce the execution plan layer, AST hierarchy, and expression evaluation engine. No new SQL features visible to users — this phase is pure infrastructure that all subsequent phases build on.

### 2.1 AST Statement Hierarchy

Replace the current single `SqlSelectStatement` with a type hierarchy:

```csharp
// Sql/Ast/ISqlStatement.cs
public interface ISqlStatement
{
    /// <summary>Position of the first token for error reporting.</summary>
    int SourcePosition { get; }
}

// SqlSelectStatement implements ISqlStatement (existing class, add interface)
// SqlUnionStatement : ISqlStatement (Phase 2)
// SqlInsertStatement : ISqlStatement (Phase 5)
// SqlUpdateStatement : ISqlStatement (Phase 5)
// SqlDeleteStatement : ISqlStatement (Phase 5)
```

**Parser changes:** `SqlParser.Parse()` return type changes from `SqlSelectStatement` to `ISqlStatement`. For Phase 0, the parser still only produces `SqlSelectStatement` — the hierarchy is forward-looking.

**Backward compatibility:** `ISqlQueryService.TranspileSql()` continues to work — it checks `statement is SqlSelectStatement` and throws `SqlParseException` for unsupported types until those phases land.

### 2.2 Expression AST

Introduce a general expression tree that replaces the current literal-only model. This is the foundation for HAVING, CASE, computed columns, and functions.

```csharp
// Sql/Ast/SqlExpression.cs

/// <summary>Base interface for all SQL expressions.</summary>
public interface ISqlExpression { }

/// <summary>Literal value: 42, 'hello', NULL</summary>
public sealed class SqlLiteralExpression : ISqlExpression
{
    public SqlLiteral Value { get; }
}

/// <summary>Column reference as expression: a.name, revenue</summary>
public sealed class SqlColumnExpression : ISqlExpression
{
    public SqlColumnRef Column { get; }
}

/// <summary>Binary operation: revenue * 0.1, price + tax</summary>
public sealed class SqlBinaryExpression : ISqlExpression
{
    public ISqlExpression Left { get; }
    public SqlBinaryOperator Operator { get; }  // Add, Subtract, Multiply, Divide, Modulo
    public ISqlExpression Right { get; }
}

/// <summary>Unary operation: -amount, NOT flag</summary>
public sealed class SqlUnaryExpression : ISqlExpression
{
    public SqlUnaryOperator Operator { get; }  // Negate, Not
    public ISqlExpression Operand { get; }
}

/// <summary>Function call: UPPER(name), DATEADD(day, 7, createdon)</summary>
public sealed class SqlFunctionExpression : ISqlExpression
{
    public string FunctionName { get; }
    public IReadOnlyList<ISqlExpression> Arguments { get; }
}

/// <summary>CASE WHEN ... THEN ... ELSE ... END</summary>
public sealed class SqlCaseExpression : ISqlExpression
{
    public IReadOnlyList<SqlWhenClause> WhenClauses { get; }
    public ISqlExpression? ElseExpression { get; }
}

public sealed class SqlWhenClause
{
    public ISqlCondition Condition { get; }
    public ISqlExpression Result { get; }
}

/// <summary>IIF(condition, true_value, false_value)</summary>
public sealed class SqlIifExpression : ISqlExpression
{
    public ISqlCondition Condition { get; }
    public ISqlExpression TrueValue { get; }
    public ISqlExpression FalseValue { get; }
}

/// <summary>CAST(expression AS type)</summary>
public sealed class SqlCastExpression : ISqlExpression
{
    public ISqlExpression Expression { get; }
    public string TargetType { get; }  // "int", "nvarchar(100)", "datetime", etc.
}

/// <summary>Aggregate expression: SUM(revenue), COUNT(*)</summary>
public sealed class SqlAggregateExpression : ISqlExpression
{
    public SqlAggregateFunction Function { get; }
    public ISqlExpression? Operand { get; }  // null for COUNT(*)
    public bool IsDistinct { get; }
}

/// <summary>Subquery expression: (SELECT ...)</summary>
public sealed class SqlSubqueryExpression : ISqlExpression
{
    public SqlSelectStatement Subquery { get; }
}
```

**Migration path for existing code:**
- Current `SqlComparisonCondition.Value` (type `SqlLiteral`) stays unchanged in Phase 0
- `ISqlExpression` is used in new AST nodes: `SqlSelectStatement.Having`, computed columns in SELECT
- Existing `ISqlSelectColumn` gains a new implementation: `SqlComputedColumn` that wraps `ISqlExpression`

```csharp
/// <summary>Computed column: revenue * 0.1 AS tax</summary>
public sealed class SqlComputedColumn : ISqlSelectColumn
{
    public ISqlExpression Expression { get; }
    public string? Alias { get; }
    public string? TrailingComment { get; set; }
}
```

### 2.3 Execution Plan Layer

```csharp
// Query/Planning/IQueryPlanNode.cs

/// <summary>
/// A node in the query execution plan tree (Volcano/iterator model).
/// Each node produces rows lazily via IAsyncEnumerable.
/// </summary>
public interface IQueryPlanNode
{
    /// <summary>Human-readable description for EXPLAIN output.</summary>
    string Description { get; }

    /// <summary>Estimated row count (for cost-based decisions). -1 if unknown.</summary>
    long EstimatedRows { get; }

    /// <summary>Child nodes (inputs to this operator).</summary>
    IReadOnlyList<IQueryPlanNode> Children { get; }

    /// <summary>Execute this node, producing rows.</summary>
    IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single row flowing through the plan. Lightweight dictionary wrapper.
/// </summary>
public sealed class QueryRow
{
    // Column name → value. Uses pooled dictionaries for GC pressure reduction.
    public IReadOnlyDictionary<string, QueryValue> Values { get; }
    public string EntityLogicalName { get; }
}

/// <summary>
/// Shared context for plan execution: pool, cancellation, statistics.
/// </summary>
public sealed class QueryPlanContext
{
    public IDataverseConnectionPool ConnectionPool { get; }
    public IExpressionEvaluator ExpressionEvaluator { get; }
    public CancellationToken CancellationToken { get; }
    public QueryPlanStatistics Statistics { get; }  // Mutable: nodes report actual row counts
}
```

### 2.4 Initial Plan Nodes (Phase 0 scope)

Only two nodes are needed in Phase 0 to reproduce existing behavior:

**FetchXmlScanNode** — Executes a FetchXML query, yields rows page-by-page.

```csharp
public sealed class FetchXmlScanNode : IQueryPlanNode
{
    public string FetchXml { get; }
    public string EntityLogicalName { get; }
    public bool AutoPage { get; }  // true = fetch all pages, false = single page
    public int? MaxRows { get; }
    // ExecuteAsync: calls IQueryExecutor, yields QueryRow per record
}
```

**ProjectNode** — Maps input rows to output rows (column selection, renaming, expression evaluation).

> **Note:** Virtual column expansion (e.g., `owneridname` from `FormattedValues`) is handled by `SqlQueryResultExpander` in the service layer, not by `ProjectNode`. This is because virtual column expansion depends on SDK-specific `FormattedValues` metadata from `Entity` objects, which is not available in the generic `QueryRow` format used by plan nodes.

```csharp
public sealed class ProjectNode : IQueryPlanNode
{
    public IQueryPlanNode Input { get; }
    public IReadOnlyList<ProjectColumn> OutputColumns { get; }
    // ProjectColumn: { SourceName, OutputName, Expression? }
    // If Expression is set, evaluate it to produce the output value
}
```

### 2.5 QueryPlanner

```csharp
// Query/Planning/QueryPlanner.cs
public sealed class QueryPlanner
{
    /// <summary>
    /// Builds an execution plan for a parsed SQL statement.
    /// Phase 0: produces FetchXmlScanNode → ProjectNode (equivalent to current pipeline).
    /// Subsequent phases add optimization rules and new node types.
    /// </summary>
    public IQueryPlanNode Plan(ISqlStatement statement, QueryPlanOptions options);
}

public sealed class QueryPlanOptions
{
    public int PoolCapacity { get; init; }        // From pool.GetTotalRecommendedParallelism()
    public bool UseTdsEndpoint { get; init; }     // Phase 3.5
    public bool ExplainOnly { get; init; }        // Don't execute, just return plan
    public int? MaxRows { get; init; }            // Global row limit
}
```

**Phase 0 planning logic** (pseudocode):

```
Plan(SqlSelectStatement stmt):
  1. Transpile stmt → FetchXML via existing SqlToFetchXmlTranspiler
  2. Create FetchXmlScanNode(fetchXml, entityName)
  3. Create ProjectNode wrapping the scan (handles virtual column expansion)
  4. Return ProjectNode as root
```

This is functionally identical to the current pipeline but routed through the plan infrastructure.

### 2.6 Expression Evaluator

```csharp
// Query/Execution/IExpressionEvaluator.cs

/// <summary>
/// Evaluates SQL expressions against a row of data.
/// Used by client-side plan nodes (Filter, Project, Aggregate, Sort).
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>Evaluate an expression, returning the computed value.</summary>
    object? Evaluate(ISqlExpression expression, QueryRow row);

    /// <summary>Evaluate a condition, returning true/false.</summary>
    bool EvaluateCondition(ISqlCondition condition, QueryRow row);
}
```

Phase 0 implementation supports: literals, column references, binary arithmetic (+, -, *, /, %), comparisons, string concatenation, NULL propagation (SQL three-valued logic). Functions and CASE are added in Phase 1.

### 2.7 Service Layer Changes

```csharp
// ISqlQueryService gets new methods (existing ones unchanged)
public interface ISqlQueryService
{
    // Existing
    string TranspileSql(string sql, int? topOverride = null);
    Task<SqlQueryResult> ExecuteAsync(SqlQueryRequest request, CancellationToken ct = default);

    // New
    Task<QueryPlanDescription> ExplainAsync(string sql, CancellationToken ct = default);
}
```

`SqlQueryService.ExecuteAsync` internal implementation changes from:
```
parse → transpile → execute → expand
```
to:
```
parse → plan → execute plan → collect results → expand
```

But the external contract (`SqlQueryRequest` → `SqlQueryResult`) is unchanged. Existing CLI commands, TUI screens, and tests continue to work.

### 2.8 Deliverables

| Item | File(s) | Tests |
|------|---------|-------|
| ISqlStatement interface | `Sql/Ast/ISqlStatement.cs` | Type hierarchy tests |
| Expression AST | `Sql/Ast/SqlExpression.cs` | Expression construction tests |
| SqlComputedColumn | `Sql/Ast/SqlComputedColumn.cs` | — |
| SqlSelectStatement.Having | `Sql/Ast/SqlSelectStatement.cs` | — (null for now) |
| IQueryPlanNode + QueryRow | `Query/Planning/IQueryPlanNode.cs` | — |
| FetchXmlScanNode | `Query/Planning/Nodes/FetchXmlScanNode.cs` | Scan node unit tests |
| ProjectNode | `Query/Planning/Nodes/ProjectNode.cs` | Projection tests |
| QueryPlanner | `Query/Planning/QueryPlanner.cs` | Plan construction tests |
| PlanExecutor | `Query/Execution/PlanExecutor.cs` | End-to-end plan execution |
| IExpressionEvaluator | `Query/Execution/IExpressionEvaluator.cs` | — |
| ExpressionEvaluator | `Query/Execution/ExpressionEvaluator.cs` | Arithmetic, null, comparison |
| SqlQueryService update | `Services/Query/SqlQueryService.cs` | Existing tests pass unchanged |

**Acceptance criteria:** All existing `Category=TuiUnit` and SQL unit tests pass. `ISqlQueryService.ExecuteAsync` produces byte-identical results for every existing test case. The plan layer is exercised but invisible to callers.

---

## 3. Phase 1: Core SQL Gaps

**Goal:** HAVING, CASE/IIF, basic computed column expressions, COUNT(*) optimization.

### 3.1 HAVING Clause

**Parser:** Add `SqlTokenType.Having`. After GROUP BY parsing, look for `HAVING` keyword. Parse condition into `ISqlCondition` stored in `SqlSelectStatement.Having`.

**AST change:**
```csharp
// SqlSelectStatement gains:
public ISqlCondition? Having { get; }
```

**Planning:** The planner checks whether the HAVING condition can be expressed purely in terms of FetchXML aggregate aliases. Since FetchXML does NOT support filtering on aggregate results, HAVING always produces a `ClientFilterNode` after the `FetchXmlScanNode`.

```
FetchXmlScanNode (with aggregate=true) → ClientFilterNode(having condition) → ProjectNode
```

**ClientFilterNode:**
```csharp
public sealed class ClientFilterNode : IQueryPlanNode
{
    public IQueryPlanNode Input { get; }
    public ISqlCondition Condition { get; }
    // ExecuteAsync: foreach row in input, if evaluator.EvaluateCondition(condition, row) → yield row
}
```

**Expression evaluator:** Must handle aggregate alias references in HAVING (e.g., `HAVING total_revenue > 1000000` where `total_revenue` is `SUM(revenue) AS total_revenue`). The evaluator looks up the alias name in the row's values.

### 3.2 CASE / IIF Expressions

**Parser:** Add `CASE`, `WHEN`, `THEN`, `ELSE`, `END`, `IIF` keywords.

CASE parsing:
```
CASE
  WHEN condition THEN expression
  [WHEN condition THEN expression ...]
  [ELSE expression]
END
```

IIF parsing:
```
IIF(condition, true_expression, false_expression)
```

Both produce `ISqlExpression` nodes (`SqlCaseExpression`, `SqlIifExpression`).

**Where they appear:** In SELECT column list as computed columns, in WHERE/HAVING conditions, in ORDER BY.

**Expression evaluator:** `Evaluate(SqlCaseExpression, row)` walks WHEN clauses in order, evaluates each condition, returns first matching THEN result (or ELSE, or NULL).

**Planning:** CASE/IIF in SELECT creates a `ProjectNode` with computed output columns. CASE/IIF in WHERE that references only real columns could be split: the server-side filter handles the pushable parts, and a `ClientFilterNode` handles the CASE comparison.

### 3.3 Computed Column Expressions

**Parser:** The `ParseSelectColumn()` method currently checks for aggregates or column refs. Extend to detect expressions:

- `revenue * 0.1 AS tax` → `SqlComputedColumn(SqlBinaryExpression(revenue, Multiply, 0.1), "tax")`
- `firstname + ' ' + lastname AS fullname` → string concatenation expression

Operator tokens needed: `+`, `-`, `*` (already exists as Star), `/`, `%`. The lexer needs context-awareness: `*` after `SELECT` is wildcard, `*` in an expression is multiply. Resolution: in `ParseSelectColumn`, if the current position isn't right after SELECT or a comma with no column yet, treat `*` as multiply operator.

**Planning:** Computed columns that reference only server-returned columns are evaluated by `ProjectNode` using the expression evaluator. The `FetchXmlScanNode` requests the base columns needed by the expressions.

### 3.4 COUNT(*) Optimization

**Problem:** `SELECT COUNT(*) FROM account` currently generates aggregate FetchXML. For unfiltered counts, Dataverse offers `RetrieveTotalRecordCountRequest` which is near-instant (reads from metadata, not table scan).

**Planning rule:** When the planner sees:
- `SELECT COUNT(*) FROM entity` (no WHERE, no JOIN, no GROUP BY)
- → Generate `CountOptimizedNode` instead of `FetchXmlScanNode`

```csharp
public sealed class CountOptimizedNode : IQueryPlanNode
{
    public string EntityLogicalName { get; }
    // ExecuteAsync: calls RetrieveTotalRecordCountRequest, yields single row with count
}
```

**Fallback:** If `RetrieveTotalRecordCountRequest` fails (some entities don't support it), fall back to aggregate FetchXML.

### 3.5 Condition Expressions (WHERE with expressions)

**Current limitation:** WHERE conditions are `column op literal` only. Cannot do `WHERE revenue > cost` (column-to-column) or `WHERE YEAR(createdon) = 2024`.

**Phase 1 extension:** Allow `ISqlExpression` on the right side of comparisons in WHERE conditions.

```csharp
// Extend ISqlCondition with:
public sealed class SqlExpressionCondition : ISqlCondition
{
    public ISqlExpression Left { get; }
    public SqlComparisonOperator Operator { get; }
    public ISqlExpression Right { get; }
}
```

**Planning:**
- If both sides are column-ref and literal → push to FetchXML (existing behavior)
- If either side is a computed expression or column-to-column → `ClientFilterNode`
- Mixed: push what you can, client-filter the rest

### 3.6 Deliverables

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

## 4. Phase 2: Query Composition

**Goal:** IN/EXISTS subqueries, UNION/UNION ALL.

### 4.1 IN Subquery → JOIN Rewrite

```sql
SELECT name FROM account
WHERE accountid IN (SELECT parentaccountid FROM opportunity WHERE revenue > 1000000)
```

**Parser:** Extend `ParseInList()` — when `(` is followed by `SELECT`, parse a full `SqlSelectStatement` as a subquery instead of a literal list.

**AST:**
```csharp
// Extend SqlInCondition:
public sealed class SqlInSubqueryCondition : ISqlCondition
{
    public SqlColumnRef Column { get; }
    public SqlSelectStatement Subquery { get; }
    public bool IsNegated { get; }
}
```

**Planning — JOIN rewrite strategy:**

```sql
-- Original:
SELECT name FROM account WHERE accountid IN (SELECT parentaccountid FROM opportunity WHERE revenue > 1000000)

-- Rewritten to:
SELECT DISTINCT a.name FROM account a
INNER JOIN opportunity o ON a.accountid = o.parentaccountid
WHERE o.revenue > 1000000
```

The planner performs this rewrite at the AST level before transpiling to FetchXML, so Dataverse handles the join server-side. This is the highest-performance path because it avoids client-side data transfer.

**When rewrite isn't possible** (correlated subqueries, complex expressions): Fall back to two-phase execution:
1. Execute subquery → collect result set
2. If result set is small (<2000 values), inject as `IN (value1, value2, ...)` in FetchXML
3. If result set is large, use `ClientFilterNode` with a hash set lookup

### 4.2 EXISTS Subquery

```sql
SELECT name FROM account a
WHERE EXISTS (SELECT 1 FROM opportunity o WHERE o.parentaccountid = a.accountid)
```

**AST:**
```csharp
public sealed class SqlExistsCondition : ISqlCondition
{
    public SqlSelectStatement Subquery { get; }
    public bool IsNegated { get; }  // NOT EXISTS
}
```

**Planning:** EXISTS with a correlated reference (subquery references outer table column) → rewrite to JOIN:

```sql
-- Rewritten:
SELECT DISTINCT a.name FROM account a
INNER JOIN opportunity o ON a.accountid = o.parentaccountid
```

NOT EXISTS → rewrite to LEFT JOIN + IS NULL:
```sql
SELECT a.name FROM account a
LEFT JOIN opportunity o ON a.accountid = o.parentaccountid
WHERE o.opportunityid IS NULL
```

Both rewrites produce FetchXML `<link-entity>` — fully server-side.

### 4.3 UNION / UNION ALL

```sql
SELECT name, 'Account' AS type FROM account WHERE revenue > 1000000
UNION ALL
SELECT fullname, 'Contact' AS type FROM contact WHERE parentcustomerid IS NOT NULL
```

**AST:**
```csharp
public sealed class SqlUnionStatement : ISqlStatement
{
    public IReadOnlyList<SqlSelectStatement> Queries { get; }
    public IReadOnlyList<bool> IsUnionAll { get; }  // false = UNION (deduplicate)
    public IReadOnlyList<SqlOrderByItem>? OrderBy { get; }  // Optional trailing ORDER BY
    public int? Top { get; }
    public int SourcePosition { get; }
}
```

**Parser:** After parsing a `SqlSelectStatement`, if the next token is `UNION`, parse another SELECT. Repeat. Track `ALL` modifier per union boundary.

**Planning:**
```
ConcatenateNode
├── FetchXmlScanNode (query 1)
├── FetchXmlScanNode (query 2)
└── [DistinctNode] (if UNION without ALL)
    └── [ClientSortNode] (if ORDER BY on union)
```

**ConcatenateNode:** Simply yields all rows from child 1, then all rows from child 2. Validates column count matches at planning time.

**DistinctNode:** For UNION (without ALL), maintains a `HashSet<CompositeKey>` and deduplicates rows. Memory-bounded: if row count exceeds a threshold (e.g., 100K), switch to external sort-based dedup or warn.

### 4.4 Deliverables

| Item | Tests |
|------|-------|
| IN subquery parsing | `WHERE id IN (SELECT ...)` parses |
| IN → JOIN rewrite | Rewrite produces correct FetchXML |
| IN fallback (large results) | Hash set client filter works |
| EXISTS parsing | `WHERE EXISTS (SELECT ...)` parses |
| EXISTS → JOIN rewrite | Correlated EXISTS becomes INNER JOIN |
| NOT EXISTS → LEFT JOIN | Produces IS NULL filter |
| UNION/UNION ALL parsing | Multiple SELECTs parse into SqlUnionStatement |
| ConcatenateNode | Yields rows from both children |
| DistinctNode | Deduplicates on UNION |
| Column count validation | Mismatched UNION column counts error |

---

## 5. Phase 3: Functions

**Goal:** String functions, date functions, CAST/CONVERT. Client-side evaluated with server-side pushdown where FetchXML supports it.

### 5.1 String Functions

| Function | Signature | Notes |
|----------|-----------|-------|
| `UPPER(expr)` | → string | Client-side |
| `LOWER(expr)` | → string | Client-side |
| `LEN(expr)` | → int | Client-side |
| `LEFT(expr, n)` | → string | Client-side |
| `RIGHT(expr, n)` | → string | Client-side |
| `SUBSTRING(expr, start, length)` | → string | 1-based start per T-SQL |
| `TRIM(expr)` | → string | Client-side |
| `LTRIM(expr)` | → string | Client-side |
| `RTRIM(expr)` | → string | Client-side |
| `REPLACE(expr, find, replace)` | → string | Client-side |
| `CHARINDEX(find, expr [, start])` | → int | 1-based result, 0 = not found |
| `CONCAT(expr, expr, ...)` | → string | Variadic, NULL-safe (NULL → '') |
| `STUFF(expr, start, length, replace)` | → string | Client-side |
| `REVERSE(expr)` | → string | Client-side |

**Implementation:** Each function is registered in a `FunctionRegistry` keyed by name. The expression evaluator dispatches `SqlFunctionExpression` to the registry.

```csharp
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, IScalarFunction> _functions;

    public object? Invoke(string name, object?[] args);
}

public interface IScalarFunction
{
    object? Execute(object?[] arguments);
    int MinArgs { get; }
    int MaxArgs { get; }
}
```

### 5.2 Date Functions

| Function | Signature | Notes |
|----------|-----------|-------|
| `GETDATE()` | → datetime | Current UTC time |
| `GETUTCDATE()` | → datetime | Current UTC time |
| `YEAR(expr)` | → int | FetchXML-native in GROUP BY |
| `MONTH(expr)` | → int | FetchXML-native in GROUP BY |
| `DAY(expr)` | → int | FetchXML-native in GROUP BY |
| `DATEADD(part, n, expr)` | → datetime | Client-side |
| `DATEDIFF(part, start, end)` | → int | Client-side |
| `DATEPART(part, expr)` | → int | Client-side |
| `DATETRUNC(part, expr)` | → datetime | Client-side |

**Server pushdown for GROUP BY:** `YEAR(createdon)`, `MONTH(createdon)`, `DAY(createdon)` in GROUP BY map to native FetchXML date grouping:

```xml
<attribute name="createdon" groupby="true" dategrouping="year" alias="yr" />
```

The planner detects `YEAR/MONTH/DAY(column)` in GROUP BY and pushes to FetchXML instead of client-side evaluation. This is a significant performance optimization.

### 5.3 CAST / CONVERT

```sql
SELECT CAST(revenue AS int), CONVERT(nvarchar, createdon, 120) FROM account
```

**Supported target types:** `int`, `bigint`, `decimal(p,s)`, `float`, `nvarchar(n)`, `varchar(n)`, `datetime`, `date`, `bit`, `uniqueidentifier`, `money`.

**Expression evaluator:** `Evaluate(SqlCastExpression)` performs .NET type conversion with SQL Server semantics (truncation, rounding rules).

### 5.4 Deliverables

| Item | Tests |
|------|-------|
| FunctionRegistry + IScalarFunction | Registry lookup and dispatch |
| 14 string functions | Each function with edge cases, NULL handling |
| 8 date functions | Date arithmetic, boundary cases |
| YEAR/MONTH/DAY GROUP BY pushdown | FetchXML dategrouping generation |
| CAST/CONVERT evaluation | Type conversions, overflow handling |
| Parser: function call syntax | `UPPER(name)`, `DATEADD(day, 7, x)` |
| `+` as string concatenation | `'a' + 'b'` → `'ab'` |

---

## 6. Phase 3.5: TDS Endpoint

**Goal:** Optional acceleration path for read queries using the Dataverse TDS Endpoint (SQL Server wire protocol against a read-only replica).

### 6.1 Architecture

```
SqlQueryService.ExecuteAsync():
  1. Parse SQL → AST
  2. Plan:
     a. Is TDS enabled in options? (opt-in per profile)
     b. Is query TDS-compatible? (compatibility check)
     c. YES → TdsScanNode (fast path)
     d. NO → FetchXmlScanNode (full-capability fallback)
  3. Execute plan → results
```

### 6.2 TDS Compatibility Check

A query is TDS-compatible when:
- It is a SELECT (no DML)
- All referenced entities support TDS (no elastic tables, no virtual entities)
- No PPDS-specific features used (virtual *name columns — these are a PPDS abstraction)
- The SQL is expressible in standard T-SQL (which it is, since we parse T-SQL subset)

```csharp
public static class TdsCompatibilityChecker
{
    public static TdsCompatibility Check(SqlSelectStatement statement, EntityMetadata metadata);
}

public enum TdsCompatibility
{
    Compatible,
    IncompatibleEntity,      // elastic/virtual entity
    IncompatibleFeature,     // virtual columns, etc.
    IncompatibleDml          // INSERT/UPDATE/DELETE
}
```

### 6.3 TdsScanNode

```csharp
public sealed class TdsScanNode : IQueryPlanNode
{
    public string SqlText { get; }  // The original SQL (or reconstructed from AST)
    // ExecuteAsync: opens SqlConnection, executes SqlCommand, yields rows via SqlDataReader
}
```

### 6.4 ITdsQueryExecutor

```csharp
public interface ITdsQueryExecutor
{
    Task<QueryResult> ExecuteSqlAsync(
        string sql,
        int? maxRows = null,
        CancellationToken cancellationToken = default);
}
```

**Implementation:**
```csharp
public sealed class TdsQueryExecutor : ITdsQueryExecutor
{
    // Connection string: {orgUrl}:5558 with AccessToken from MSAL auth
    // Uses Microsoft.Data.SqlClient
    // Maps SqlDataReader columns to QueryColumn
    // Maps SqlDataReader rows to QueryRow/QueryValue
}
```

### 6.5 Auth Integration

No new auth flow needed. The TDS endpoint accepts the same OAuth bearer token:

```csharp
var connection = new SqlConnection($"Server={orgUrl},5558;");
connection.AccessToken = existingMsalToken;  // Reuse from current profile
```

### 6.6 Profile Configuration

Add to existing auth profile:
```json
{
  "useTdsEndpoint": true  // opt-in, default false
}
```

### 6.7 Deliverables

| Item | Tests |
|------|-------|
| TdsCompatibilityChecker | Compatible/incompatible classification |
| TdsScanNode | Integration test against TDS endpoint |
| ITdsQueryExecutor / TdsQueryExecutor | SqlDataReader → QueryResult mapping |
| Auth token reuse | Token passed to SqlConnection |
| Profile setting | `useTdsEndpoint` flag round-trips |
| Fallback behavior | Incompatible queries fall back to FetchXML |
| CLI flag: `--tds` | Force TDS path for debugging |

---

## 7. Phase 4: Parallel Execution Intelligence

**Goal:** Pool-aware parallel aggregate partitioning, parallel page fetching, EXPLAIN command.

### 7.1 Parallel Aggregate Partitioning

**Problem:** FetchXML aggregate queries fail when they exceed the AggregateQueryRecordLimit (50,000 records by default).

**Solution:** When an aggregate query fails with the 50K limit error, the planner retries with a partitioned strategy:

1. Estimate record count via `RetrieveTotalRecordCountRequest`
2. Calculate partition count: `ceil(estimatedCount / 40000)` (leave margin below 50K)
3. Determine date range for the entity (min/max createdon via FetchXML MIN/MAX aggregate)
4. Generate N date-range partitions with non-overlapping `createdon` filters
5. Execute ALL partitions in parallel across the connection pool
6. Merge-aggregate the partial results

```
ParallelPartitionNode
├── FetchXmlScanNode (partition 1: createdon >= X AND createdon < Y, aggregate=true)
├── FetchXmlScanNode (partition 2: createdon >= Y AND createdon < Z, aggregate=true)
├── FetchXmlScanNode (partition 3: ...)
└── MergeAggregateNode (combines partial aggregates)
```

**MergeAggregateNode:**
- COUNT: sum the partial counts
- SUM: sum the partial sums
- AVG: sum(partial_sums) / sum(partial_counts) — requires tracking both
- MIN: min of all partial mins
- MAX: max of all partial maxes

```csharp
public sealed class ParallelPartitionNode : IQueryPlanNode
{
    public IReadOnlyList<IQueryPlanNode> Partitions { get; }
    public int MaxParallelism { get; }  // From pool capacity
    // ExecuteAsync: Task.WhenAll with SemaphoreSlim(MaxParallelism)
}
```

**Why this beats single-connection approaches:** With a pool capacity of 48 (4 app users × DOP 12), all 48 partitions execute simultaneously. A single-connection tool would execute them sequentially — 48x slower for large aggregations.

### 7.2 Parallel Page Prefetch

**Current behavior:** Pages are fetched sequentially — page 1, wait, page 2, wait...

**Improvement:** After receiving page 1 with `moreRecords=true`, the planner can speculatively prefetch pages while the consumer (TUI/CLI) is processing current results.

```csharp
public sealed class PrefetchScanNode : IQueryPlanNode
{
    public FetchXmlScanNode BaseQuery { get; }
    public int PrefetchDepth { get; }  // How many pages ahead to buffer (default: 3)
    // Implementation: Channel<QueryRow> with producer fetching pages ahead
}
```

This is especially valuable in the TUI where the user is scrolling through results — by the time they reach the end of page 1, pages 2-4 are already in memory.

### 7.3 EXPLAIN Command

```sql
EXPLAIN SELECT COUNT(*) FROM account GROUP BY ownerid
```

Output:
```
Execution Plan:
├── ParallelPartitionNode (estimated: 200K records, 5 partitions)
│   ├── FetchXmlScanNode (partition 1: 2020-01-01 to 2021-03-15, aggregate)
│   ├── FetchXmlScanNode (partition 2: 2021-03-15 to 2022-05-28, aggregate)
│   ├── FetchXmlScanNode (partition 3: 2022-05-28 to 2023-08-10, aggregate)
│   ├── FetchXmlScanNode (partition 4: 2023-08-10 to 2024-10-22, aggregate)
│   └── FetchXmlScanNode (partition 5: 2024-10-22 to 2026-01-04, aggregate)
├── MergeAggregateNode (functions: COUNT)
└── ProjectNode (columns: ownerid, count)

Pool capacity: 48 (4 sources × DOP 12)
Effective parallelism: 5 (partition count)
Estimated execution: ~2s
```

**CLI:** `ppds query sql "..." --explain` or `ppds query explain "..."`
**TUI:** Ctrl+Shift+E in SQL query screen

### 7.4 Deliverables

| Item | Tests |
|------|-------|
| ParallelPartitionNode | Mock partitions execute in parallel |
| MergeAggregateNode | COUNT/SUM/AVG/MIN/MAX merge correctly |
| Date range partitioning | Correct non-overlapping ranges |
| Aggregate limit detection | 50K error triggers retry with partitioning |
| PrefetchScanNode | Prefetch stays ahead of consumer |
| EXPLAIN output formatting | Plan tree renders correctly |
| CLI --explain flag | Shows plan without executing |
| TUI Ctrl+Shift+E | Opens explain dialog |

---

## 8. Phase 5: DML via SQL

**Goal:** INSERT, UPDATE, DELETE via SQL syntax, leveraging the existing `IBulkOperationExecutor` infrastructure.

### 8.1 Safety Model (Strict)

**Requirements:**
- `DELETE FROM entity` (no WHERE) is BLOCKED at parse time → tell user to use `ppds truncate`
- `UPDATE entity SET ...` (no WHERE) is BLOCKED at parse time
- Before execution, show estimated row count and require confirmation
- CLI: `--confirm` flag required, or interactive prompt
- CLI: `--dry-run` shows plan + estimated rows without executing
- TUI: confirmation dialog with estimated rows
- Default row cap: 10,000. Override with `--no-limit`
- All DML operations report progress via `IProgressReporter`

```csharp
public sealed class DmlSafetyGuard
{
    public DmlSafetyResult Check(ISqlStatement statement, DmlSafetyOptions options);
}

public sealed class DmlSafetyResult
{
    public bool IsBlocked { get; }          // No WHERE on UPDATE/DELETE
    public string? BlockReason { get; }
    public long EstimatedAffectedRows { get; }
    public bool RequiresConfirmation { get; }
    public bool ExceedsRowCap { get; }
}
```

### 8.2 INSERT

```sql
INSERT INTO account (name, revenue, industrycode)
VALUES ('Contoso', 1000000, 1)

INSERT INTO account (name, revenue)
SELECT name, revenue FROM account WHERE statecode = 0
```

**AST:**
```csharp
public sealed class SqlInsertStatement : ISqlStatement
{
    public string TargetEntity { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyList<ISqlExpression>>? ValueRows { get; }  // INSERT VALUES
    public SqlSelectStatement? SourceQuery { get; }  // INSERT SELECT
    public int SourcePosition { get; }
}
```

**Planning:**
```
DmlExecuteNode(BulkOperation.Create)
├── [Source: ValueListNode] or [Source: FetchXmlScanNode → ProjectNode]
└── target: account, columns: [name, revenue, industrycode]
```

**DmlExecuteNode** pipelines source rows through `IBulkOperationExecutor.CreateMultipleAsync()` in configurable batch sizes, using the existing BatchCoordinator for pool-aware parallelism.

### 8.3 UPDATE

```sql
UPDATE account SET revenue = revenue * 1.1
WHERE statecode = 0 AND industrycode = 3

UPDATE a SET a.revenue = o.totalamount
FROM account a
INNER JOIN opportunity o ON a.accountid = o.parentaccountid
WHERE o.statecode = 1
```

**AST:**
```csharp
public sealed class SqlUpdateStatement : ISqlStatement
{
    public SqlTableRef TargetTable { get; }
    public IReadOnlyList<SqlSetClause> SetClauses { get; }
    public SqlTableRef? FromTable { get; }          // UPDATE ... FROM ... JOIN
    public IReadOnlyList<SqlJoin>? Joins { get; }
    public ISqlCondition? Where { get; }
    public int SourcePosition { get; }
}

public sealed class SqlSetClause
{
    public string ColumnName { get; }
    public ISqlExpression Value { get; }  // Can be expression: revenue * 1.1
}
```

**Planning — two-phase execution:**
1. **Retrieve phase:** Transpile the WHERE + FROM/JOIN into FetchXML SELECT to identify target records. Include all columns referenced in SET expressions.
2. **Transform phase:** For each retrieved record, evaluate SET expressions using `IExpressionEvaluator` to compute new values.
3. **Write phase:** Feed transformed entities to `IBulkOperationExecutor.UpdateMultipleAsync()`.

```
DmlExecuteNode(BulkOperation.Update)
├── Source: FetchXmlScanNode (WHERE → FetchXML, columns needed for SET evaluation)
├── Transform: ExpressionEvaluator (apply SET expressions per row)
└── Sink: BulkOperationExecutor.UpdateMultipleAsync
```

### 8.4 DELETE

```sql
DELETE FROM opportunity WHERE statecode = 2 AND actualclosedate < '2020-01-01'
```

**AST:**
```csharp
public sealed class SqlDeleteStatement : ISqlStatement
{
    public SqlTableRef TargetTable { get; }
    public ISqlCondition? Where { get; }  // null = blocked by safety guard
    public SqlTableRef? FromTable { get; }  // DELETE FROM ... USING / JOIN
    public IReadOnlyList<SqlJoin>? Joins { get; }
    public int SourcePosition { get; }
}
```

**Planning:**
1. Transpile WHERE to FetchXML SELECT to identify target record IDs
2. Feed IDs to `IBulkOperationExecutor.DeleteMultipleAsync()`

**DELETE * protection:** Parser detects `DELETE FROM entity` (no WHERE) and throws `SqlParseException("DELETE without WHERE is not allowed. Use 'ppds truncate <entity>' for bulk deletion.")`.

### 8.5 Deliverables

| Item | Tests |
|------|-------|
| DmlSafetyGuard | Block no-WHERE, row cap enforcement |
| INSERT VALUES parsing | Single and multi-row VALUES |
| INSERT SELECT parsing | Source query parsing |
| UPDATE parsing | SET with expressions, FROM/JOIN |
| DELETE parsing | With WHERE, without WHERE (blocked) |
| DmlExecuteNode | Routes to BulkOperationExecutor |
| UPDATE expression evaluation | `SET revenue = revenue * 1.1` |
| --dry-run flag | Shows plan without executing |
| --confirm flag | Execution gated on flag |
| Progress reporting | IProgressReporter during DML |
| CLI `--no-limit` | Overrides 10K row cap |

---

## 9. Phase 6: Metadata & Streaming

**Goal:** Query Dataverse metadata via SQL, progressive result streaming in TUI.

### 9.1 Metadata Schema

```sql
SELECT logicalname, displayname, iscustomentity
FROM metadata.entity
WHERE iscustomentity = 1
ORDER BY logicalname

SELECT e.logicalname, a.logicalname AS attribute, a.attributetype
FROM metadata.entity e
JOIN metadata.attribute a ON e.logicalname = a.entitylogicalname
WHERE e.logicalname = 'account'
```

**Virtual tables:**

| Table | Maps To | Key Columns |
|-------|---------|-------------|
| `metadata.entity` | `RetrieveMetadataChangesRequest` | logicalname, displayname, iscustomentity, description, ownershiptype |
| `metadata.attribute` | `RetrieveMetadataChangesRequest` | logicalname, entitylogicalname, attributetype, displayname, isrequired |
| `metadata.relationship_1_n` | `RetrieveMetadataChangesRequest` | schemaname, referencingentity, referencedentity, referencingattribute |
| `metadata.relationship_n_n` | `RetrieveMetadataChangesRequest` | schemaname, entity1logicalname, entity2logicalname, intersectentityname |
| `metadata.optionset` | `RetrieveAllOptionSetsRequest` | name, displayname, isglobal, optionsettype |
| `metadata.optionsetvalue` | `RetrieveAllOptionSetsRequest` | optionsetname, value, label, description |

**Planning:** Parser detects `metadata.` schema prefix in FROM clause. Planner generates `MetadataScanNode` instead of `FetchXmlScanNode`.

```csharp
public sealed class MetadataScanNode : IQueryPlanNode
{
    public string MetadataTable { get; }  // "entity", "attribute", etc.
    public ISqlCondition? Filter { get; }  // For predicate pushdown to metadata API
    // ExecuteAsync: calls appropriate metadata request, yields rows
}
```

### 9.2 Progressive Result Streaming

**Current TUI behavior:** Query executes → all results load → display.

**Target behavior:** First page displays immediately. Subsequent pages stream in background with a progress indicator.

**Implementation in TUI (`SqlQueryScreen`):**

```csharp
// PlanExecutor already yields rows via IAsyncEnumerable
// Modify SqlQueryScreen to consume incrementally:

await foreach (var row in planExecutor.ExecuteAsync(plan, ctx))
{
    AppendRowToTable(row);

    if (rowCount % 100 == 0)
    {
        // Trigger UI refresh every 100 rows
        Application.MainLoop.Invoke(RefreshTableView);
    }
}
```

The Volcano model's lazy evaluation makes this natural — `FetchXmlScanNode` yields rows page-by-page, and the TUI consumes them incrementally.

**Combine with Phase 4's PrefetchScanNode:** The prefetch node fills a bounded channel. The TUI reads from the channel at display speed while the channel refills from the network at fetch speed. Zero-wait pagination.

### 9.3 Deliverables

| Item | Tests |
|------|-------|
| MetadataScanNode | Mock metadata responses |
| metadata.entity queries | Entity listing, filtering |
| metadata.attribute queries | Column discovery per entity |
| metadata.optionset queries | Option set inspection |
| Parser: schema prefix detection | `metadata.entity` routes correctly |
| TUI incremental rendering | Rows appear as they load |
| PrefetchScanNode + TUI integration | Smooth scrolling experience |

---

## 10. Phase 7: Advanced

**Goal:** Window functions, variables, flow control. These are power-user features with lower priority but high value for scripted operations.

### 10.1 Window Functions

```sql
SELECT name, revenue,
  ROW_NUMBER() OVER (ORDER BY revenue DESC) AS rank,
  SUM(revenue) OVER (PARTITION BY industrycode) AS industry_total
FROM account
WHERE statecode = 0
```

**AST:**
```csharp
public sealed class SqlWindowExpression : ISqlExpression
{
    public SqlAggregateFunction Function { get; }
    public ISqlExpression? Operand { get; }
    public IReadOnlyList<ISqlExpression>? PartitionBy { get; }
    public IReadOnlyList<SqlOrderByItem>? OrderBy { get; }
}
```

**Planning:** Window functions ALWAYS execute client-side (FetchXML has no window function support). The plan fetches all matching rows first, then applies windows.

```
ClientWindowNode
├── Input: FetchXmlScanNode (all rows matching WHERE)
├── Windows: [ROW_NUMBER() OVER (ORDER BY revenue DESC),
│             SUM(revenue) OVER (PARTITION BY industrycode)]
└── Output: original columns + window columns
```

**ClientWindowNode implementation:**
1. Materialize all input rows (required for window computation)
2. For each window specification, sort/partition as needed
3. Compute function values using running state
4. Yield enriched rows

### 10.2 Variables

```sql
DECLARE @threshold MONEY = 1000000
DECLARE @entityName NVARCHAR(100) = 'account'

SELECT name, revenue FROM account
WHERE revenue > @threshold
```

**Parser:** Add `DECLARE`, `SET`, `@` token handling. Variables are stored in a `VariableScope` on the `QueryPlanContext`.

**Planning:** Variable references in WHERE clauses are resolved at plan time (substituted as literals into FetchXML). Variable references in expressions are resolved at evaluation time.

### 10.3 Flow Control

```sql
DECLARE @count INT

SELECT @count = COUNT(*) FROM account WHERE statecode = 0

IF @count > 1000
BEGIN
    SELECT TOP 100 name, revenue FROM account ORDER BY revenue DESC
END
ELSE
BEGIN
    SELECT name, revenue FROM account WHERE statecode = 0
END
```

**AST:** `SqlIfStatement`, `SqlWhileStatement`, `SqlBeginEndBlock`, `SqlSetVariableStatement`.

**Planning:** Flow control creates a `ScriptExecutionNode` that evaluates statements sequentially, managing variable scope. This is a significant departure from single-query execution — it's a mini interpreter.

**Priority:** This is the lowest-priority feature. Variables alone (without flow control) cover 80% of use cases. Flow control is a stretch goal.

### 10.4 Deliverables

| Item | Tests |
|------|-------|
| Window function parsing | OVER clause with PARTITION BY, ORDER BY |
| ClientWindowNode | ROW_NUMBER, RANK, DENSE_RANK |
| Window aggregates | SUM/COUNT/AVG OVER (PARTITION BY) |
| Variable declaration + substitution | @var in WHERE becomes literal |
| IF/ELSE parsing | Conditional execution |
| WHILE parsing | Loop execution |
| ScriptExecutionNode | Multi-statement execution |

---

## 11. Cross-Cutting Concerns

### 11.1 Error Handling

All new plan nodes propagate `SqlParseException` for syntax errors and `PpdsException` with `ErrorCode` for runtime errors (per CLAUDE.md ALWAYS rules).

New error codes:
- `QUERY_AGGREGATE_LIMIT` — 50K aggregate limit hit (before partitioning retry)
- `QUERY_DML_BLOCKED` — DML without WHERE
- `QUERY_DML_ROW_CAP` — exceeded row cap without --no-limit
- `QUERY_TDS_INCOMPATIBLE` — query can't use TDS endpoint
- `QUERY_PLAN_TIMEOUT` — plan execution exceeded timeout
- `QUERY_TYPE_MISMATCH` — expression evaluator type error

### 11.2 Cancellation

All plan nodes accept `CancellationToken` via `QueryPlanContext`. Long-running nodes (parallel partition, page fetch) check cancellation between iterations. The TUI's Ctrl+C handler sets the token.

### 11.3 Progress Reporting

All plan nodes that take >1 second accept `IProgressReporter` (per CLAUDE.md ALWAYS rules). The reporter is threaded through `QueryPlanContext`.

- FetchXmlScanNode: reports page progress
- ParallelPartitionNode: reports partition completion
- DmlExecuteNode: reports batch progress (delegates to BulkOperationExecutor's existing progress)

### 11.4 Memory Bounds

Client-side nodes that materialize data (DistinctNode, ClientWindowNode, HashJoinNode) must have configurable memory limits. Default: 500MB. If exceeded, throw `PpdsException(ErrorCode.QUERY_MEMORY_LIMIT)` with a message suggesting adding a TOP/WHERE clause.

### 11.5 Logging

All plan nodes log at `Debug` level via `ILogger`. The EXPLAIN output captures what would be logged without executing.

---

## 12. Testing Strategy

### 12.1 Unit Test Categories

| Category | Scope | Infrastructure |
|----------|-------|----------------|
| `TuiUnit` | AST, parser, transpiler, expression evaluator | No mocks needed |
| `PlanUnit` | Plan construction, node logic | Mock IQueryExecutor, mock data |
| `IntegrationQuery` | End-to-end against live Dataverse | Real connection pool |
| `IntegrationTds` | TDS endpoint queries | Real TDS connection |

### 12.2 Test Patterns

**Parser tests:** Input SQL → assert AST structure. One test per syntax construct.

**Expression evaluator tests:** Expression + row → assert output value. Cover NULL propagation, type coercion, overflow.

**Plan node tests:** Create node with mock child that yields known rows → assert output rows. Test cancellation, empty inputs, large inputs.

**Integration tests:** SQL string → SqlQueryService.ExecuteAsync → assert row count, column names, specific values. These are the acceptance tests that prove end-to-end correctness.

### 12.3 Regression Suite

Build a SQL conformance test suite: a JSON file mapping SQL input to expected FetchXML output and/or expected result shapes. Run on every commit to catch regressions.

```json
{
  "tests": [
    {
      "name": "basic_select_top",
      "sql": "SELECT TOP 10 name FROM account",
      "expectedFetchXml": "<fetch top=\"10\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>",
      "expectedColumns": ["name"]
    }
  ]
}
```

---

## Phase Dependency Graph

```
Phase 0 (Foundation)
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
