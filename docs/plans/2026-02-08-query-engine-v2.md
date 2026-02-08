# Query Engine v2 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> **Spec:** `docs/specs/query-engine-v2.md`
> **Review:** Use superpowers:requesting-code-review after each phase.

**Goal:** Implement the query engine v2 as specified, phase-by-phase. Each phase has independent tasks that can be parallelized. Each task includes files, steps, and commit message.

**Worktree:** Create a new worktree `query-engine-v2` from `main` for this work.

**Test command:** `dotnet test --filter Category=TuiUnit` (plus new `Category=PlanUnit`)

---

## Phase 0: Foundation (Infrastructure)

All subsequent phases depend on Phase 0. No user-visible features.

---

### Task 0.1: AST Statement Hierarchy + Expression Types

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/ISqlStatement.cs`
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlExpression.cs`
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlComputedColumn.cs`
- Modify: `src/PPDS.Dataverse/Sql/Ast/SqlSelectStatement.cs` (implement ISqlStatement, add Having property)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (return type ISqlStatement, no parsing changes yet)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Ast/SqlExpressionTests.cs`

**Step 1: Create ISqlStatement**

```csharp
// src/PPDS.Dataverse/Sql/Ast/ISqlStatement.cs
namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Base interface for all SQL statement types.
/// </summary>
public interface ISqlStatement
{
    /// <summary>Position of the first token for error reporting.</summary>
    int SourcePosition { get; }
}
```

**Step 2: Create expression AST types**

Create `SqlExpression.cs` with the full expression hierarchy:
- `ISqlExpression` (base interface)
- `SqlLiteralExpression` (wraps existing `SqlLiteral`)
- `SqlColumnExpression` (wraps existing `SqlColumnRef`)
- `SqlBinaryExpression` (left, operator, right)
- `SqlUnaryExpression` (operator, operand)
- `SqlFunctionExpression` (name, arguments)
- `SqlCaseExpression` (when clauses, else)
- `SqlIifExpression` (condition, true, false)
- `SqlCastExpression` (expression, target type)
- `SqlAggregateExpression` (function, operand, distinct)
- `SqlSubqueryExpression` (subquery)
- `SqlBinaryOperator` enum: Add, Subtract, Multiply, Divide, Modulo, StringConcat
- `SqlUnaryOperator` enum: Negate, Not

Create `SqlComputedColumn.cs` implementing `ISqlSelectColumn`.

**Step 3: Modify SqlSelectStatement**

- Add `ISqlStatement` implementation with `SourcePosition` property
- Add `ISqlCondition? Having { get; }` property (null for now)
- Add `Having` parameter to constructor (default null)
- Update `WithTop()` and other `With*()` methods to preserve Having

**Step 4: Modify SqlParser.Parse() return type**

Change `public SqlSelectStatement Parse()` to `public ISqlStatement Parse()`.
Add `public SqlSelectStatement ParseSelect()` that returns the concrete type.
Existing callers that need `SqlSelectStatement` use pattern match or `ParseSelect()`.

**Step 5: Write expression AST unit tests**

Test construction, equality, and helper methods on all expression types.

**Commit:**
```
feat(query): add ISqlStatement hierarchy and expression AST types

Foundation for query engine v2 execution plan layer. Introduces
expression tree types (binary, unary, function, case, cast, aggregate)
and statement type hierarchy. No behavioral changes.
```

---

### Task 0.2: Expression Evaluator

**Files:**
- Create: `src/PPDS.Dataverse/Query/Execution/IExpressionEvaluator.cs`
- Create: `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Execution/ExpressionEvaluatorTests.cs`

**Step 1: Create IExpressionEvaluator**

```csharp
public interface IExpressionEvaluator
{
    object? Evaluate(ISqlExpression expression, IReadOnlyDictionary<string, QueryValue> row);
    bool EvaluateCondition(ISqlCondition condition, IReadOnlyDictionary<string, QueryValue> row);
}
```

**Step 2: Implement ExpressionEvaluator**

Phase 0 scope:
- `SqlLiteralExpression` → return literal value (parse number strings to numeric types)
- `SqlColumnExpression` → lookup column in row, return raw value
- `SqlBinaryExpression` → arithmetic: `+`, `-`, `*`, `/`, `%` with numeric type promotion
- `SqlBinaryExpression` → string concatenation: `+` on strings
- `SqlUnaryExpression` → `-` (negate), `NOT` (boolean)
- NULL propagation: any operation with NULL operand returns NULL (SQL three-valued logic)
- Type coercion: int + decimal → decimal, int + float → float

**Step 3: Implement EvaluateCondition**

Evaluate existing `ISqlCondition` types against a row:
- `SqlComparisonCondition` → evaluate column vs literal
- `SqlLikeCondition` → regex match with `%` and `_` wildcards
- `SqlNullCondition` → check for null
- `SqlInCondition` → check membership
- `SqlLogicalCondition` → AND/OR with short-circuit

**Step 4: Write thorough tests**

- Arithmetic: `2 + 3 = 5`, `10 / 3 = 3` (int division), `10.0 / 3 = 3.333...`
- NULL: `NULL + 5 = NULL`, `NULL = NULL → false` (SQL semantics)
- String: `'hello' + ' ' + 'world' = 'hello world'`
- Conditions: all operators with edge cases
- Type promotion: int vs decimal vs float

**Commit:**
```
feat(query): add expression evaluator for client-side computation

Evaluates SQL expressions against data rows. Supports arithmetic,
string concatenation, NULL propagation, and all comparison operators.
Foundation for HAVING, CASE, computed columns, and functions.
```

---

### Task 0.3: Plan Layer Infrastructure

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/IQueryPlanNode.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/QueryRow.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/QueryPlanContext.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/QueryPlanStatistics.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/QueryPlanDescription.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/PlanInfrastructureTests.cs`

**Step 1: Create IQueryPlanNode**

As specified in the spec § 2.3. Include `IAsyncEnumerable<QueryRow>` return type on ExecuteAsync.

**Step 2: Create QueryRow**

Lightweight wrapper around `IReadOnlyDictionary<string, QueryValue>` with entity logical name. Include a static factory for creating from `QueryResult` records.

**Step 3: Create context and statistics types**

`QueryPlanContext` holds: connection pool, expression evaluator, cancellation token, statistics, logger.
`QueryPlanStatistics` tracks: rows read, rows written, execution time per node, page count.

**Step 4: Test infrastructure**

Verify QueryRow construction, context creation, statistics accumulation.

**Commit:**
```
feat(query): add execution plan infrastructure types

Introduces IQueryPlanNode (Volcano/iterator model), QueryRow,
QueryPlanContext, and QueryPlanStatistics. Plan nodes produce
rows via IAsyncEnumerable for lazy streaming evaluation.
```

---

### Task 0.4: FetchXmlScanNode + ProjectNode

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/FetchXmlScanNode.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/ProjectNode.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/FetchXmlScanNodeTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ProjectNodeTests.cs`

**Step 1: Implement FetchXmlScanNode**

- Takes FetchXML string, entity name, auto-page flag, max rows
- ExecuteAsync: calls `IQueryExecutor.ExecuteFetchXmlAsync` for page 1
- If auto-page and moreRecords, loops with paging cookie
- Yields `QueryRow` per record
- Respects cancellation between pages

**Step 2: Implement ProjectNode**

- Takes input node and list of output column projections
- Each projection: source column name OR expression + output name
- ExecuteAsync: foreach input row, build output row by evaluating projections
- Handles virtual column expansion (moved from SqlQueryResultExpander)

**Step 3: Test with mock IQueryExecutor**

- FetchXmlScanNode: mock returns 2 pages of data, verify all rows yielded
- FetchXmlScanNode: cancellation between pages stops iteration
- ProjectNode: column rename, expression evaluation, virtual column expansion

**Commit:**
```
feat(query): add FetchXmlScanNode and ProjectNode plan operators

FetchXmlScanNode executes FetchXML with automatic paging.
ProjectNode handles column selection, renaming, and expression
evaluation. Together they reproduce current query pipeline behavior.
```

---

### Task 0.5: QueryPlanner + PlanExecutor + Service Integration

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs`
- Create: `src/PPDS.Dataverse/Query/Execution/PlanExecutor.cs`
- Modify: `src/PPDS.Cli/Services/Query/ISqlQueryService.cs` (add ExplainAsync)
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` (route through planner)
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/QueryPlannerTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Execution/PlanExecutorTests.cs`

**Step 1: Implement QueryPlanner**

Phase 0 logic:
1. Accept `ISqlStatement`, verify it's `SqlSelectStatement`
2. Transpile to FetchXML via existing `SqlToFetchXmlTranspiler`
3. Create `FetchXmlScanNode` wrapping the FetchXML
4. Create `ProjectNode` for virtual column expansion
5. Return root node

**Step 2: Implement PlanExecutor**

- Takes root `IQueryPlanNode` + `QueryPlanContext`
- Executes the plan by consuming the root node's `IAsyncEnumerable`
- Collects results into `QueryResult` format
- Records execution statistics

**Step 3: Update SqlQueryService**

Change `ExecuteAsync` internals from direct transpile-execute to:
1. Parse → AST
2. Plan → execution plan via `QueryPlanner`
3. Execute → `PlanExecutor`
4. Expand → `SqlQueryResultExpander` (unchanged)
5. Return `SqlQueryResult` (unchanged contract)

Add `ExplainAsync` method that returns plan description without executing.

**Step 4: Verify all existing tests pass**

Run full test suite. Every existing SQL test must produce identical results.

**Commit:**
```
feat(query): integrate execution plan layer into query pipeline

SqlQueryService now routes through QueryPlanner → PlanExecutor.
External contract unchanged - all existing tests pass with identical
results. Adds ExplainAsync for future EXPLAIN command support.
```

---

## Phase 1: Core SQL Gaps

**Depends on:** Phase 0 complete
**Parallelizable tasks:** 1.1 and 1.2 can run in parallel. 1.3 depends on 1.1 (expression parsing).

---

### Task 1.1: HAVING Clause

**Files:**
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (add Having token)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlLexer.cs` (add HAVING keyword)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (parse HAVING)
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/ClientFilterNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (emit ClientFilterNode for HAVING)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/HavingParserTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ClientFilterNodeTests.cs`

**Steps:**
1. Add `Having` keyword to lexer and token type
2. Parse HAVING condition after GROUP BY in `ParseSelectStatement`
3. Store as `ISqlCondition? Having` on `SqlSelectStatement`
4. Implement `ClientFilterNode` (input node + condition → filtered rows)
5. Update QueryPlanner: if statement has HAVING, insert ClientFilterNode between scan and project
6. Test: `SELECT ownerid, COUNT(*) as cnt FROM account GROUP BY ownerid HAVING cnt > 5`

**Commit:**
```
feat(query): add HAVING clause support

Parses HAVING as post-aggregation filter. Since FetchXML doesn't
support HAVING natively, uses client-side ClientFilterNode after
the aggregate FetchXML scan completes.
```

---

### Task 1.2: COUNT(*) Optimization

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/CountOptimizedNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/CountOptimizedNodeTests.cs`

**Steps:**
1. Implement `CountOptimizedNode` using `RetrieveTotalRecordCountRequest`
2. Add detection rule in QueryPlanner: bare `SELECT COUNT(*) FROM entity` → CountOptimizedNode
3. Add fallback: if RetrieveTotalRecordCountRequest fails, fall back to FetchXML aggregate
4. Test with mock `IOrganizationService`

**Commit:**
```
feat(query): optimize bare COUNT(*) with RetrieveTotalRecordCountRequest

SELECT COUNT(*) FROM entity (no WHERE/JOIN/GROUP BY) now uses
the near-instant metadata count instead of aggregate FetchXML scan.
Falls back to FetchXML if the optimized path fails.
```

---

### Task 1.3: CASE / IIF Expressions

**Files:**
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Case, When, Then, Else, End, Iif tokens)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlLexer.cs` (keywords)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (ParseCaseExpression, ParseIifExpression)
- Modify: `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs` (evaluate CASE/IIF)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/CaseExpressionParserTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Execution/CaseExpressionEvalTests.cs`

**Steps:**
1. Add all new keyword tokens to lexer
2. Parse CASE expression: `CASE WHEN cond THEN expr [WHEN...] [ELSE expr] END`
3. Parse IIF: `IIF(cond, expr, expr)` — treat as sugar for CASE with one WHEN
4. Both usable in SELECT column list (as `SqlComputedColumn` wrapping the expression)
5. Add evaluation logic to ExpressionEvaluator
6. Update QueryPlanner: if SELECT contains computed columns, project through `ProjectNode` with expression evaluation

**Commit:**
```
feat(query): add CASE/WHEN/THEN/ELSE and IIF expression support

Parses CASE expressions and IIF function in SELECT columns.
Evaluated client-side by the expression evaluator after FetchXML
retrieves the base column data.
```

---

### Task 1.4: Computed Columns + Arithmetic Expressions in SELECT

**Files:**
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Plus, Minus, Slash, Percent tokens)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlLexer.cs` (arithmetic operators)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (ParseExpression with precedence)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (detect computed columns)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/ExpressionParserTests.cs`

**Steps:**
1. Add arithmetic operator tokens: `+`, `-`, `/`, `%` (star already exists)
2. Implement `ParseExpression()` with operator precedence (*, /, % before +, -)
3. Context-aware `*`: in SELECT column position = column ref, in expression = multiply
4. Parse `revenue * 0.1 AS tax`, `price + shipping AS total`
5. Update `ParseSelectColumn` to detect expressions (if next token after identifier is operator)
6. QueryPlanner: scan for `SqlComputedColumn` in SELECT, include base columns in FetchXML, add ProjectNode with expression evaluation

**Commit:**
```
feat(query): add computed column expressions in SELECT

Supports arithmetic expressions in SELECT: revenue * 0.1 AS tax,
price + shipping AS total. Expressions evaluated client-side
after FetchXML retrieves base columns.
```

---

### Task 1.5: Expression Conditions in WHERE

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlExpressionCondition.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (parse expression comparisons)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (split pushable vs client filters)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/ExpressionConditionTests.cs`

**Steps:**
1. Create `SqlExpressionCondition : ISqlCondition` with Left/Operator/Right as `ISqlExpression`
2. Modify `ParsePrimaryCondition`: detect when right side is expression (not just literal)
3. Modify QueryPlanner: analyze WHERE conditions
   - Column op Literal → push to FetchXML (existing)
   - Column op Column → ClientFilterNode (FetchXML can't do column-to-column)
   - Expression op anything → ClientFilterNode
4. Split compound conditions: pushable parts go to FetchXML, rest to client filter

**Commit:**
```
feat(query): support expressions in WHERE conditions

Enables column-to-column comparisons (WHERE revenue > cost) and
computed conditions. Pushable conditions go to FetchXML, others
evaluated client-side via ClientFilterNode.
```

---

## Phase 2: Query Composition

**Depends on:** Phase 0 complete. Task 2.1/2.2 JOIN rewrite path needs only Phase 0. Large-result-set fallback path needs Task 1.1 (ClientFilterNode).
**Parallelizable:** Tasks 2.1+2.2 can run together, then 2.3

---

### Task 2.1: IN Subquery with JOIN Rewrite

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlInSubqueryCondition.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (detect SELECT after IN `(`)
- Create: `src/PPDS.Dataverse/Query/Planning/Rewrites/InSubqueryToJoinRewrite.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (apply rewrite)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/InSubqueryParserTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Rewrites/InSubqueryRewriteTests.cs`

**Steps:**
1. Extend `ParseInList`: after `(`, if next is SELECT, parse subquery
2. Create `SqlInSubqueryCondition` AST node
3. Implement `InSubqueryToJoinRewrite`:
   - Analyze subquery: single column, single table, simple WHERE
   - Generate INNER JOIN on the subquery column = outer column
   - Add DISTINCT to prevent row multiplication
   - Merge subquery WHERE into outer WHERE
4. Fallback: if rewrite isn't possible, execute subquery first, then inject values as IN list
5. Test both paths

**Commit:**
```
feat(query): add IN (SELECT ...) subquery support

Parses IN subqueries and rewrites to JOINs for server-side execution
when possible. Falls back to two-phase execution (subquery first,
then IN list) for complex cases.
```

---

### Task 2.2: EXISTS / NOT EXISTS with JOIN Rewrite

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlExistsCondition.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Exists token)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (parse EXISTS)
- Create: `src/PPDS.Dataverse/Query/Planning/Rewrites/ExistsToJoinRewrite.cs`
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/ExistsParserTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Rewrites/ExistsRewriteTests.cs`

**Steps:**
1. Add EXISTS keyword to lexer
2. Parse: `[NOT] EXISTS (SELECT ...)` → `SqlExistsCondition`
3. Implement rewrite:
   - EXISTS → INNER JOIN (find correlated column references)
   - NOT EXISTS → LEFT JOIN + linked-entity-column IS NULL
4. Test with correlated subqueries

**Commit:**
```
feat(query): add EXISTS/NOT EXISTS subquery support

Parses EXISTS subqueries and rewrites to JOINs. EXISTS becomes
INNER JOIN, NOT EXISTS becomes LEFT JOIN with IS NULL filter.
Both execute server-side via FetchXML link-entity.
```

---

### Task 2.3: UNION / UNION ALL

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlUnionStatement.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Union, All tokens)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (parse UNION between SELECTs)
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/ConcatenateNode.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/DistinctNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (plan UNION)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/UnionParserTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ConcatenateNodeTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/DistinctNodeTests.cs`

**Steps:**
1. Add UNION, ALL keywords to lexer
2. After parsing first SELECT, check for UNION → parse next SELECT → repeat
3. Create `SqlUnionStatement` with list of queries and union-all flags
4. Implement `ConcatenateNode`: yields all rows from child 1, then child 2
5. Implement `DistinctNode`: deduplicates using composite key hash set
6. Planner: UNION ALL → ConcatenateNode, UNION → ConcatenateNode → DistinctNode
7. Validate column count matches at plan time

**Commit:**
```
feat(query): add UNION and UNION ALL support

Parses UNION/UNION ALL between SELECT statements. UNION ALL
concatenates results, UNION additionally deduplicates. Each
SELECT executes as a separate FetchXML query.
```

---

## Phase 3: Functions

**Depends on:** Phase 0 (expression evaluator), Task 1.3 (function parsing infrastructure)
**Parallelizable:** Tasks 3.1, 3.2, 3.3 can all run in parallel

---

### Task 3.1: String Functions

**Files:**
- Create: `src/PPDS.Dataverse/Query/Execution/Functions/FunctionRegistry.cs`
- Create: `src/PPDS.Dataverse/Query/Execution/Functions/IScalarFunction.cs`
- Create: `src/PPDS.Dataverse/Query/Execution/Functions/StringFunctions.cs`
- Modify: `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs` (function dispatch)
- Create: `tests/PPDS.Dataverse.Tests/Query/Execution/Functions/StringFunctionTests.cs`

**Steps:**
1. Create `FunctionRegistry` with `Register(name, IScalarFunction)` and `Invoke(name, args)`
2. Create `IScalarFunction` interface with `Execute(args)`, `MinArgs`, `MaxArgs`
3. Implement all 14 string functions (UPPER, LOWER, LEN, LEFT, RIGHT, SUBSTRING, TRIM, LTRIM, RTRIM, REPLACE, CHARINDEX, CONCAT, STUFF, REVERSE)
4. Wire FunctionRegistry into ExpressionEvaluator for `SqlFunctionExpression` dispatch
5. Test each function including NULL handling and edge cases

**Commit:**
```
feat(query): add string functions (UPPER, LOWER, LEN, SUBSTRING, etc.)

Implements 14 T-SQL string functions evaluated client-side.
Functions are registered in a pluggable FunctionRegistry.
```

---

### Task 3.2: Date Functions + GROUP BY Pushdown

**Files:**
- Create: `src/PPDS.Dataverse/Query/Execution/Functions/DateFunctions.cs`
- Modify: `src/PPDS.Dataverse/Sql/Transpilation/SqlToFetchXmlTranspiler.cs` (dategrouping)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (detect date GROUP BY pushdown)
- Create: `tests/PPDS.Dataverse.Tests/Query/Execution/Functions/DateFunctionTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Sql/Transpilation/DateGroupingTests.cs`

**Steps:**
1. Implement 8 date functions: GETDATE, GETUTCDATE, YEAR, MONTH, DAY, DATEADD, DATEDIFF, DATEPART, DATETRUNC
2. Detect `YEAR(column)`, `MONTH(column)`, `DAY(column)` in GROUP BY
3. Transpile to FetchXML `dategrouping` attribute instead of client-side evaluation
4. Test: `SELECT YEAR(createdon), COUNT(*) FROM account GROUP BY YEAR(createdon)`

**Commit:**
```
feat(query): add date functions with FetchXML GROUP BY pushdown

Implements T-SQL date functions. YEAR/MONTH/DAY in GROUP BY
are pushed to FetchXML dategrouping for server-side performance.
Other date functions evaluate client-side.
```

---

### Task 3.3: CAST / CONVERT

**Files:**
- Create: `src/PPDS.Dataverse/Query/Execution/Functions/CastConverter.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Cast, Convert tokens)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (parse CAST/CONVERT)
- Modify: `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs` (evaluate casts)
- Create: `tests/PPDS.Dataverse.Tests/Query/Execution/CastConvertTests.cs`

**Steps:**
1. Add CAST, CONVERT keywords to lexer
2. Parse `CAST(expr AS type)` and `CONVERT(type, expr [, style])`
3. Implement `CastConverter` with type conversion rules (int, bigint, decimal, float, nvarchar, datetime, date, bit, uniqueidentifier, money)
4. Wire into ExpressionEvaluator for `SqlCastExpression`
5. Test all conversion pairs + overflow behavior

**Commit:**
```
feat(query): add CAST and CONVERT type conversion expressions

Supports CAST(expr AS type) and CONVERT(type, expr) with
T-SQL type conversion semantics for common Dataverse types.
```

---

## Phase 3.5: TDS Endpoint

**Depends on:** Phase 0 (plan layer for routing)
**Parallelizable:** Can run in parallel with Phase 2 and Phase 3

---

### Task 3.5.1: TDS Query Executor

**Files:**
- Create: `src/PPDS.Dataverse/Query/ITdsQueryExecutor.cs`
- Create: `src/PPDS.Dataverse/Query/TdsQueryExecutor.cs`
- Create: `src/PPDS.Dataverse/Query/TdsCompatibilityChecker.cs`
- Add NuGet: `Microsoft.Data.SqlClient` to `PPDS.Dataverse.csproj`
- Create: `tests/PPDS.Dataverse.Tests/Query/TdsQueryExecutorTests.cs`

**Steps:**
1. Add `Microsoft.Data.SqlClient` package reference
2. Implement `ITdsQueryExecutor` with `ExecuteSqlAsync` method
3. Build connection string: `Server={orgUrl},5558` + `AccessToken` from MSAL token
4. Map `SqlDataReader` columns → `QueryColumn`, rows → `QueryValue`
5. Implement `TdsCompatibilityChecker`: check entity support, SQL feature support
6. Unit test with mocked connection (integration test requires live environment)

**Commit:**
```
feat(query): add TDS Endpoint query executor

Enables SQL queries against Dataverse's read-only replica via
the TDS Endpoint (SQL Server wire protocol). Reuses existing
MSAL auth tokens. Includes compatibility checker for automatic
fallback to FetchXML path.
```

---

### Task 3.5.2: TDS Plan Node + Query Router

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/TdsScanNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (TDS routing)
- Modify: `src/PPDS.Cli/Commands/Query/SqlCommand.cs` (--tds flag)
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/TdsRoutingTests.cs`

**Steps:**
1. Implement `TdsScanNode` wrapping `ITdsQueryExecutor`
2. Update QueryPlanner: if TDS enabled + query compatible → TdsScanNode, else → FetchXmlScanNode
3. Add `--tds` CLI flag for explicit TDS routing
4. Add `useTdsEndpoint` to profile configuration
5. Test routing logic with mock compatibility checker

**Commit:**
```
feat(query): add TDS Endpoint plan routing

QueryPlanner routes compatible queries to TDS Endpoint when enabled.
Incompatible queries fall back to FetchXML. Configurable per profile
with --tds CLI flag override.
```

---

## Phase 4: Parallel Execution Intelligence

**Depends on:** Phase 0 (plan layer), Phase 1.1 (ClientFilterNode)

---

### Task 4.1: Parallel Aggregate Partitioning

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/ParallelPartitionNode.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/MergeAggregateNode.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/Partitioning/DateRangePartitioner.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (aggregate limit fallback)
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ParallelPartitionNodeTests.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Partitioning/DateRangePartitionerTests.cs`

**Steps:**
1. Implement `DateRangePartitioner`: estimate record count, calculate partitions, generate date ranges
2. Implement `ParallelPartitionNode`: executes child nodes in parallel using SemaphoreSlim limited by pool capacity
3. Implement `MergeAggregateNode`: merges partial aggregates (COUNT=sum, SUM=sum, AVG=weighted, MIN/MAX=extremes)
4. Add retry logic in planner: if FetchXML aggregate fails with 50K limit → re-plan with partitioning
5. Test with mock data: verify parallel execution, correct merge results

**Note:** `COUNT(DISTINCT column)` cannot be parallel-partitioned by summing partial counts (duplicates across partitions). Falls back to single-query execution.

**Commit:**
```
feat(query): add parallel aggregate partitioning for 50K limit

When aggregate queries exceed the AggregateQueryRecordLimit,
automatically partitions by date range and executes all partitions
in parallel across the connection pool. Dramatically faster than
sequential binary splitting.
```

---

### Task 4.2: Prefetch Scan Node

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/PrefetchScanNode.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/PrefetchScanNodeTests.cs`

**Steps:**
1. Wrap `FetchXmlScanNode` with a prefetch buffer (System.Threading.Channels)
2. Background task fetches pages ahead of consumer
3. Configurable depth (default: 3 pages ahead)
4. Bounded buffer to prevent unbounded memory growth
5. Test: consumer slower than producer → pages buffered ahead

**Commit:**
```
feat(query): add prefetch scan node for page-ahead buffering

Speculatively fetches upcoming FetchXML pages while consumer
processes current results. Uses bounded Channel for backpressure.
Enables zero-wait pagination in TUI.
```

---

### Task 4.3: EXPLAIN Command

**Files:**
- Create: `src/PPDS.Cli/Commands/Query/ExplainCommand.cs`
- Modify: `src/PPDS.Cli/Commands/Query/SqlCommand.cs` (--explain flag)
- Create: `src/PPDS.Dataverse/Query/Planning/PlanFormatter.cs`
- Create: `tests/PPDS.Cli.Tests/Commands/Query/ExplainCommandTests.cs`

**Steps:**
1. Implement `PlanFormatter`: renders plan tree as indented text with statistics
2. Add `--explain` flag to `ppds query sql` command
3. Create standalone `ppds query explain` subcommand
4. TUI: Ctrl+Shift+E opens explain dialog for current query
5. Test: verify plan output format for various query patterns

**Commit:**
```
feat(query): add EXPLAIN command for query plan inspection

Shows the execution plan tree with estimated rows and parallelism
info. Available as --explain flag or standalone command.
```

---

## Phase 5: DML via SQL

**Depends on:** Phase 0 (plan layer + expression evaluator)

---

### Task 5.1: DML Safety Guard

**Files:**
- Create: `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs`
- Modify: `src/PPDS.Cli/Commands/Query/SqlCommand.cs` (--confirm, --dry-run, --no-limit)
- Create: `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs`

**Steps:**
1. Implement `DmlSafetyGuard.Check()` — analyzes statement for safety
2. Block DELETE/UPDATE without WHERE at parse level
3. Row cap enforcement: estimate affected rows, block if > 10K without `--no-limit`
4. Add `--confirm`, `--dry-run`, `--no-limit` flags to SqlCommand
5. TUI: confirmation dialog before DML execution
6. Test all safety scenarios

**Commit:**
```
feat(query): add DML safety guard with strict protections

Blocks DELETE/UPDATE without WHERE, enforces 10K row cap by default,
requires --confirm for CLI execution. --dry-run shows plan without
executing. Prevents accidental mass data modification.
```

---

### Task 5.2: INSERT Parsing + Execution

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlInsertStatement.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Insert, Into, Values tokens)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (ParseInsertStatement)
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/DmlExecuteNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (plan INSERT)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/InsertParserTests.cs`

**Steps:**
1. Add INSERT, INTO, VALUES keywords
2. Parse `INSERT INTO entity (columns) VALUES (values)` and `INSERT INTO entity (columns) SELECT ...`
3. Implement `DmlExecuteNode` that pipelines rows to `IBulkOperationExecutor.CreateMultipleAsync`
4. Test parsing and plan construction

**Commit:**
```
feat(query): add INSERT statement support

Parses INSERT INTO ... VALUES and INSERT INTO ... SELECT.
Routes through existing BulkOperationExecutor for high-performance
batch creation with connection pool parallelism.
```

---

### Task 5.3: UPDATE Parsing + Execution

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlUpdateStatement.cs`
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlSetClause.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Update, Set tokens)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (ParseUpdateStatement)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (plan UPDATE)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/UpdateParserTests.cs`

**Steps:**
1. Add UPDATE, SET keywords
2. Parse `UPDATE entity SET col = expr [, ...] [FROM ... JOIN ...] WHERE ...`
3. Block UPDATE without WHERE
4. Plan: retrieve matching records → evaluate SET expressions → UpdateMultipleAsync
5. Test with expression evaluation: `SET revenue = revenue * 1.1`

**Commit:**
```
feat(query): add UPDATE statement support

Parses UPDATE with SET expressions including computed values.
Two-phase execution: retrieve matching records, evaluate SET
expressions, bulk update via connection pool.
```

---

### Task 5.4: DELETE Parsing + Execution

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlDeleteStatement.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Delete token)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (ParseDeleteStatement)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (plan DELETE)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/DeleteParserTests.cs`

**Steps:**
1. Add DELETE keyword
2. Parse `DELETE FROM entity WHERE ...`
3. Block DELETE without WHERE (point to `ppds truncate`)
4. Plan: retrieve matching record IDs → DeleteMultipleAsync
5. Test parsing and safety guard integration

**Commit:**
```
feat(query): add DELETE statement support

Parses DELETE with WHERE clause. DELETE without WHERE is blocked
with guidance to use 'ppds truncate'. Routes through existing
BulkOperationExecutor for parallel deletion.
```

---

## Phase 6: Metadata & Streaming

**Depends on:** Phase 0 (plan layer)

---

### Task 6.1: Metadata Schema Virtual Tables

**Files:**
- Create: `src/PPDS.Dataverse/Metadata/IMetadataQueryExecutor.cs`
- Create: `src/PPDS.Dataverse/Metadata/MetadataQueryExecutor.cs`
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/MetadataScanNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (detect metadata schema)
- Create: `tests/PPDS.Dataverse.Tests/Metadata/MetadataQueryExecutorTests.cs`

**Steps:**
1. Implement `IMetadataQueryExecutor` with methods per virtual table
2. Use `RetrieveMetadataChangesRequest` for entity/attribute tables
3. Use `RetrieveAllOptionSetsRequest` for optionset tables
4. Implement `MetadataScanNode` wrapping metadata executor
5. QueryPlanner: detect `metadata.` prefix in FROM → MetadataScanNode
6. Test with mock metadata responses

**Commit:**
```
feat(query): add metadata schema for querying entity definitions

Exposes Dataverse metadata as virtual SQL tables: metadata.entity,
metadata.attribute, metadata.optionset, etc. Enables SQL-based
schema discovery and administration.
```

---

### Task 6.2: TUI Progressive Streaming

**Files:**
- Modify: `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` (incremental rendering)
- Modify: `src/PPDS.Cli/Tui/Views/QueryResultsTableView.cs` (streaming append)

**Steps:**
1. Modify query execution to use `IAsyncEnumerable` from plan executor
2. Append rows to table view incrementally (batch of 100 rows → UI refresh)
3. Show live progress: "Loading... 5,000 rows" with data already visible
4. Integrate PrefetchScanNode for zero-wait pagination
5. Test: verify UI responsiveness during large result loading

**Commit:**
```
feat(tui): add progressive result streaming for SQL queries

Results appear incrementally as pages load. Combined with prefetch
buffering, enables zero-wait scrolling through large result sets.
```

---

## Phase 7: Advanced Features

**Depends on:** All previous phases

---

### Task 7.1: Window Functions

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlWindowExpression.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (parse OVER clause)
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/ClientWindowNode.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ClientWindowNodeTests.cs`

**Steps:**
1. Parse OVER clause: `function() OVER ([PARTITION BY ...] [ORDER BY ...])`
2. Support: ROW_NUMBER, RANK, DENSE_RANK, plus aggregate functions with OVER
3. Implement `ClientWindowNode`: materialize all rows, partition, sort, compute
4. Test: ROW_NUMBER with ORDER BY, SUM with PARTITION BY

**Commit:**
```
feat(query): add window functions (ROW_NUMBER, RANK, DENSE_RANK)

Supports window functions with OVER(PARTITION BY ... ORDER BY ...).
Evaluated client-side after all matching rows are retrieved.
```

---

### Task 7.2: Variables

**Files:**
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs` (Declare, Set, At tokens)
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (parse DECLARE, SET, @var references)
- Create: `src/PPDS.Dataverse/Query/Execution/VariableScope.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (substitute variables)
- Create: `tests/PPDS.Dataverse.Tests/Sql/Parsing/VariableParserTests.cs`

**Steps:**
1. Parse `DECLARE @name TYPE [= value]` and `SET @name = expression`
2. Store variables in `VariableScope` on plan context
3. Substitute @variable references in WHERE as literal values (pushable to FetchXML)
4. Evaluate @variable references in expressions via expression evaluator
5. Test: DECLARE + SELECT with @var in WHERE

**Commit:**
```
feat(query): add DECLARE/SET variable support

Supports declaring typed variables and using them in queries.
Variables in WHERE conditions are substituted as literals for
FetchXML pushdown. Expression references evaluated client-side.
```

---

### Task 7.3: IF/ELSE Flow Control (Stretch Goal)

**Files:**
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlIfStatement.cs`
- Create: `src/PPDS.Dataverse/Sql/Ast/SqlBlockStatement.cs`
- Modify: `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs` (parse IF/ELSE/BEGIN/END)
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/ScriptExecutionNode.cs`
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ScriptExecutionNodeTests.cs`

**Steps:**
1. Parse IF condition BEGIN...END [ELSE BEGIN...END]
2. Implement `ScriptExecutionNode` that evaluates statements sequentially
3. Variable scope management across blocks
4. Test: IF with COUNT check → conditional query execution

**Commit:**
```
feat(query): add IF/ELSE flow control for multi-statement scripts

Supports conditional query execution with variable-based branching.
Enables scripted Dataverse operations with SQL syntax.
```

---

## Execution Notes for Parallel Agents

### Phase 0 task ordering:
```
0.1 (AST) → 0.2 (evaluator, uses AST) → 0.3 (plan infra) → 0.4 (nodes, uses infra) → 0.5 (integration)
```
These are sequential — each builds on the previous.

### After Phase 0, these can run in parallel:
```
[Phase 1.1 HAVING] ─────┐
[Phase 1.2 COUNT(*)] ────┤
[Phase 1.3 CASE/IIF] ────┤── all require Phase 0
[Phase 2.1 IN subquery] ─┤
[Phase 2.2 EXISTS] ──────┤
[Phase 3.1 String funcs] ┤
[Phase 3.2 Date funcs] ──┤
[Phase 3.3 CAST/CONVERT] ┤
[Phase 3.5.1 TDS exec] ──┘
```

### Phase 1.4 and 1.5 require 1.3 (expression parsing):
```
Phase 1.3 → Phase 1.4 (computed columns)
Phase 1.3 → Phase 1.5 (expression conditions)
```

### Phase 2.3 (UNION) requires no special dependencies beyond Phase 0.

### Phase 4 requires Phase 0 + 1.1:
```
Phase 1.1 → Phase 4.1 (parallel partitioning)
Phase 0 → Phase 4.2 (prefetch)
Phase 0 → Phase 4.3 (EXPLAIN)
```

### Phase 5 requires Phase 0 + expression evaluator:
```
Phase 0 → Phase 5.1 (safety guard)
Phase 5.1 → Phase 5.2, 5.3, 5.4 (all DML, in parallel)
```

### Phase 6 and 7 can proceed when their dependencies are met.

### Review checkpoints:
- After Phase 0: full regression test + code review
- After Phase 1: code review
- After Phase 2 + 3: code review
- After Phase 3.5: code review (new NuGet dependency)
- After Phase 5: code review (DML safety is critical)
- After Phase 7: final review
