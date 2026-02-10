# Query Engine v3 — Phase 1: Foundation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the custom SQL parser with Microsoft's TSql160Parser and wire the entire query engine through PPDS.Query, deleting all old parser code.

**Architecture:** ScriptDom AST flows through ExecutionPlanBuilder → plan node tree → PlanExecutor. FetchXmlGenerator replaces SqlToFetchXmlTranspiler. All IntelliSense already uses ScriptDom. The AST expression/condition types move from PPDS.Dataverse.Sql.Ast to PPDS.Query.Types because plan nodes consume them.

**Tech Stack:** .NET 8/9/10, Microsoft.SqlServer.TransactSql.ScriptDom, xUnit

---

## Current State Assessment

**Already complete (ScriptDom-based):**
- `PPDS.Query/Parsing/QueryParser.cs` — wraps TSql160Parser (279 lines)
- `PPDS.Query/Planning/ExecutionPlanBuilder.cs` — visitor over ScriptDom AST (1094 lines)
- `PPDS.Query/Transpilation/FetchXmlGenerator.cs` — FetchXML from ScriptDom (1349 lines)
- All IntelliSense: SqlSourceTokenizer, SqlCursorContext, SqlCompletionEngine, SqlValidator
- SqlLanguageService — uses QueryParser + all IntelliSense
- DmlSafetyGuard — takes TSqlFragment/TSqlStatement

**Broken / half-migrated:**
- `SqlQueryService.cs` — references undeclared `_planner` field, undefined `ParseWithNewParser()` and `ValidateWithNewParser()` methods. **Does not compile.**

**Features in old QueryPlanner NOT YET in ExecutionPlanBuilder:**
1. UNION support (ConcatenateNode + DistinctNode)
2. TDS endpoint routing (TdsScanNode)
3. IN subquery → JOIN rewrite
4. EXISTS/NOT EXISTS → JOIN rewrite
5. Variable substitution in WHERE
6. Aggregate partitioning (ParallelPartitionNode + MergeAggregateNode)
7. Window functions (ClientWindowNode)
8. Script/IF/ELSE planning (ScriptExecutionNode)
9. UPDATE/DELETE source planning uses old SqlToFetchXmlTranspiler

**Critical constraint:** dotnet is not installed in this sandbox. Run all build/test commands in an environment with .NET SDK.

---

## Task Breakdown

### Task 1: Fix SqlQueryService to compile — wire ExecutionPlanBuilder

**Goal:** Make SqlQueryService.cs compile by replacing all legacy parser usage with ExecutionPlanBuilder. This is the critical path — everything else depends on this.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`

**What to do:**

1. Remove `using PPDS.Dataverse.Sql.Parsing;` import (SqlParser)
2. Remove `using PPDS.Dataverse.Query.Planning;` import (old QueryPlanner) — keep the Nodes import
3. Remove the undeclared `_planner` field references
4. Delete `ParseWithNewParser()` and `ValidateWithNewParser()` calls — QueryParser.TryParse already handles validation
5. Replace `ExecuteAsync()` body:
   - Parse with `_queryParser` (already exists) → get TSqlFragment
   - Extract first statement from script
   - DML safety check with `_dmlSafetyGuard.Check(fragment, ...)` (already works with TSqlFragment)
   - Build plan with `new ExecutionPlanBuilder(planOptions).Build(statement)`
   - Execute with `_planExecutor`
   - Expand virtual columns using `planResult.VirtualColumns`
   - Detect aggregates from ScriptDom AST (check for FunctionCall with COUNT/SUM/AVG/MIN/MAX)
6. Replace `ExplainAsync()` body similarly
7. Replace `ExecuteStreamingAsync()` body similarly
8. Rewrite `FetchAggregateMetadataAsync()` to work with ScriptDom `TSqlStatement` instead of `ISqlStatement`:
   - Check if statement is `SelectStatement`, extract `QuerySpecification`
   - Check `HasAggregates()` on SelectElements
   - Extract entity name from FROM clause
9. Remove `ISqlStatement`/`SqlSelectStatement` imports and all references to old AST statement types in the service

**Key signature changes in SqlQueryService:**

```csharp
// Old:
private async Task<(long?, DateTime?, DateTime?)> FetchAggregateMetadataAsync(
    ISqlStatement statement, CancellationToken ct)

// New:
private async Task<(long?, DateTime?, DateTime?)> FetchAggregateMetadataAsync(
    TSqlStatement statement, CancellationToken ct)
```

The aggregate detection helper becomes:

```csharp
private static bool HasAggregates(TSqlStatement statement)
{
    if (statement is not SelectStatement select) return false;
    if (select.QueryExpression is not QuerySpecification spec) return false;
    return spec.SelectElements.OfType<SelectScalarExpression>()
        .Any(s => s.Expression is FunctionCall func &&
            func.FunctionName.Value.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX");
}

private static string GetEntityName(TSqlStatement statement)
{
    if (statement is not SelectStatement select) return "";
    if (select.QueryExpression is not QuerySpecification spec) return "";
    if (spec.FromClause?.TableReferences.Count > 0 &&
        spec.FromClause.TableReferences[0] is NamedTableReference named)
        return named.SchemaObject.BaseIdentifier?.Value ?? "";
    // Handle QualifiedJoin
    if (spec.FromClause?.TableReferences[0] is QualifiedJoin join &&
        join.FirstTableReference is NamedTableReference joinNamed)
        return joinNamed.SchemaObject.BaseIdentifier?.Value ?? "";
    return "";
}
```

**Step 1:** Edit SqlQueryService.cs with all changes above
**Step 2:** Run `dotnet build src/PPDS.Cli` to verify compilation
**Step 3:** Commit: `fix: wire ExecutionPlanBuilder into SqlQueryService, remove legacy parser`

---

### Task 2: Port UNION support to ExecutionPlanBuilder

**Goal:** Handle UNION/UNION ALL queries through ExecutionPlanBuilder.

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**What to do:**

ScriptDom represents UNION as `BinaryQueryExpression` with `BinaryQueryExpressionType.Union`. The `SelectStatement.QueryExpression` can be either `QuerySpecification` (simple SELECT) or `BinaryQueryExpression` (UNION).

Add a visitor method for `BinaryQueryExpression`:

```csharp
public override void ExplicitVisit(SelectStatement node)
{
    if (node.QueryExpression is BinaryQueryExpression binary)
    {
        var planResult = PlanBinaryQuery(binary, node);
        // ... set result fields
    }
    else
    {
        var planResult = PlanSelect(node);
        // ... existing code
    }
}

private QueryPlanResult PlanBinaryQuery(BinaryQueryExpression binary, SelectStatement parentSelect)
{
    // Recursively collect all branches (handles nested UNION chains)
    var branches = new List<(QueryExpression query, bool isUnionAll)>();
    CollectBinaryBranches(binary, branches);

    var branchNodes = new List<IQueryPlanNode>();
    var allFetchXml = new List<string>();
    string? entityName = null;

    foreach (var (query, _) in branches)
    {
        var wrapperSelect = new SelectStatement { QueryExpression = query };
        var branchResult = PlanSelect(wrapperSelect);
        branchNodes.Add(branchResult.RootNode);
        allFetchXml.Add(branchResult.FetchXml);
        entityName ??= branchResult.EntityLogicalName;
    }

    IQueryPlanNode rootNode = new ConcatenateNode(branchNodes);

    // Check if any boundary is UNION (not ALL) — needs DISTINCT
    bool needsDistinct = branches.Any(b => !b.isUnionAll);
    if (needsDistinct)
    {
        rootNode = new DistinctNode(rootNode);
    }

    return new QueryPlanResult { ... };
}

private static void CollectBinaryBranches(
    QueryExpression expr, List<(QueryExpression query, bool isUnionAll)> branches)
{
    if (expr is BinaryQueryExpression binary)
    {
        CollectBinaryBranches(binary.FirstQueryExpression, branches);
        branches.Add((binary.SecondQueryExpression,
            binary.BinaryQueryExpressionType == BinaryQueryExpressionType.UnionAll));
    }
    else
    {
        branches.Add((expr, true)); // First branch is always "union all" with itself
    }
}
```

**Step 1:** Add the UNION handling code to ExecutionPlanBuilder.cs
**Step 2:** Run `dotnet build src/PPDS.Query`
**Step 3:** Commit: `feat: add UNION/UNION ALL support to ExecutionPlanBuilder`

---

### Task 3: Port TDS endpoint routing to ExecutionPlanBuilder

**Goal:** Route compatible queries to TDS endpoint when enabled.

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**What to do:**

Add TDS compatibility check to `PlanSelect()`, before FetchXML generation:

```csharp
private QueryPlanResult PlanSelect(SelectStatement selectStatement)
{
    var querySpec = selectStatement.QueryExpression as QuerySpecification
        ?? throw new InvalidOperationException("...");

    var entityName = GetEntityName(querySpec);

    // Metadata virtual table routing (existing)
    if (entityName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        return PlanMetadataQuery(querySpec, entityName);

    // TDS endpoint routing
    if (_options.UseTdsEndpoint && _options.TdsQueryExecutor != null
        && !string.IsNullOrEmpty(_options.OriginalSql))
    {
        var compatibility = TdsCompatibilityChecker.CheckCompatibility(
            _options.OriginalSql, entityName);
        if (compatibility == TdsCompatibility.Compatible)
        {
            return PlanTds(entityName);
        }
    }

    // ... rest of existing PlanSelect
}

private QueryPlanResult PlanTds(string entityName)
{
    var tdsNode = new TdsScanNode(
        _options.OriginalSql!,
        entityName,
        _options.TdsQueryExecutor!,
        maxRows: _options.MaxRows);

    return new QueryPlanResult
    {
        RootNode = tdsNode,
        FetchXml = $"-- TDS Endpoint: SQL passed directly --\n{_options.OriginalSql}",
        VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
        EntityLogicalName = entityName
    };
}
```

Also add `OriginalSql`, `UseTdsEndpoint`, `TdsQueryExecutor` to `QueryPlanOptions` if not already there.

**Step 1:** Add TDS routing to ExecutionPlanBuilder
**Step 2:** Run `dotnet build src/PPDS.Query`
**Step 3:** Commit: `feat: add TDS endpoint routing to ExecutionPlanBuilder`

---

### Task 4: Port aggregate partitioning to ExecutionPlanBuilder

**Goal:** Handle large aggregate queries with date-range partitioning.

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**What to do:**

Port the aggregate partitioning logic from `QueryPlanner.PlanAggregateWithPartitioning()`. The key methods:

1. `ShouldPartitionAggregate()` — check if aggregates + pool > 1 + count > limit + date range available + no COUNT(DISTINCT)
2. `PlanAggregateWithPartitioning()` — create DateRangePartitioner, build AdaptiveAggregateScanNode per partition, wrap in ParallelPartitionNode + MergeAggregateNode
3. `BuildMergeAggregateColumns()` — map aggregate columns for merge
4. `InjectAvgCompanionCounts()` — add COUNT companion for AVG columns
5. `ContainsCountDistinct()` — check for COUNT(DISTINCT)

These methods need to work with ScriptDom types. For `ShouldPartitionAggregate`, detect aggregates from `SelectScalarExpression` → `FunctionCall`. For `BuildMergeAggregateColumns`, enumerate aggregate functions from SELECT elements.

The `DateRangePartitioner`, `AdaptiveAggregateScanNode`, `ParallelPartitionNode`, `MergeAggregateNode` stay in PPDS.Dataverse — just call them from ExecutionPlanBuilder.

Insert into `PlanSelect()` after FetchXML generation, before scan node creation:

```csharp
// Aggregate partitioning
if (ShouldPartitionAggregate(querySpec))
{
    return PlanAggregateWithPartitioning(querySpec, entityName, transpileResult);
}
```

**Step 1:** Port aggregate partitioning methods to ExecutionPlanBuilder
**Step 2:** Run `dotnet build src/PPDS.Query`
**Step 3:** Commit: `feat: add aggregate partitioning to ExecutionPlanBuilder`

---

### Task 5: Port window functions to ExecutionPlanBuilder

**Goal:** Handle window functions (ROW_NUMBER, RANK, DENSE_RANK, aggregate OVER) through ExecutionPlanBuilder.

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**What to do:**

Detect window functions in SELECT elements. ScriptDom represents them as `FunctionCall` with `OverClause`, or specific types like `PartitionFunctionCall`.

In `PlanSelect()`, after PrefetchScanNode but before ProjectNode:

```csharp
// Window functions
if (HasWindowFunctions(querySpec.SelectElements))
{
    rootNode = BuildWindowNode(rootNode, querySpec.SelectElements);
}
```

`HasWindowFunctions` checks for `SelectScalarExpression` with `FunctionCall` that has `OverClause != null`.

`BuildWindowNode` creates `WindowDefinition` objects from the ScriptDom AST, converting partition-by and order-by to old AST types for `ClientWindowNode`.

```csharp
private static bool HasWindowFunctions(IList<SelectElement> elements)
{
    return elements.OfType<SelectScalarExpression>()
        .Any(s => s.Expression is FunctionCall { OverClause: not null });
}

private static ClientWindowNode BuildWindowNode(IQueryPlanNode input, IList<SelectElement> elements)
{
    var windows = new List<WindowDefinition>();
    foreach (var element in elements)
    {
        if (element is SelectScalarExpression scalar &&
            scalar.Expression is FunctionCall { OverClause: not null } func)
        {
            var outputName = scalar.ColumnName?.Value ?? func.FunctionName.Value.ToLowerInvariant();
            var windowExpr = ConvertWindowFunction(func);
            windows.Add(new WindowDefinition(outputName, windowExpr));
        }
    }
    return new ClientWindowNode(input, windows);
}
```

The `ConvertWindowFunction` method creates a `SqlWindowExpression` from the ScriptDom `FunctionCall` + `OverClause`.

**Step 1:** Add window function support to ExecutionPlanBuilder
**Step 2:** Run `dotnet build src/PPDS.Query`
**Step 3:** Commit: `feat: add window function support to ExecutionPlanBuilder`

---

### Task 6: Port script/IF/ELSE execution to ExecutionPlanBuilder

**Goal:** Handle multi-statement scripts with DECLARE, SET, IF/ELSE through ExecutionPlanBuilder.

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**What to do:**

Add visitor methods for script-level constructs. When a `TSqlScript` has multiple statements, or contains IF/BEGIN/DECLARE, wrap in `ScriptExecutionNode`.

The challenge: `ScriptExecutionNode` takes `IReadOnlyList<ISqlStatement>` (old AST type) and a `QueryPlanner` reference. We need to either:
- **Option A:** Convert ScriptDom statements to old AST ISqlStatement (complex, defeats purpose)
- **Option B:** Modify ScriptExecutionNode to work with ScriptDom types
- **Option C:** Create a new ScriptExecutionNode that takes TSqlStatement list and ExecutionPlanBuilder

**Recommended: Option C** — Create overload or modify ScriptExecutionNode.

Actually, looking at ScriptExecutionNode more carefully: it takes `ISqlStatement` list and re-plans each statement during execution. With the old planner, it calls `_planner.Plan(statement)` for each. We need it to call `ExecutionPlanBuilder.Build(statement)` instead.

Modify `ScriptExecutionNode` to accept a `Func<TSqlStatement, QueryPlanResult>` planner delegate instead of a `QueryPlanner` instance:

```csharp
// In ExecutionPlanBuilder:
public override void ExplicitVisit(TSqlScript node) // called for multi-statement batches
{
    if (node.Batches.Count == 0) return;
    var batch = node.Batches[0];
    if (batch.Statements.Count == 1)
    {
        batch.Statements[0].Accept(this);
        return;
    }
    // Multi-statement: wrap in script node
    var planResult = PlanScript(batch.Statements);
    // ... set results
}
```

This is a significant change. Defer the full ScriptExecutionNode rewrite to a sub-task if the existing ScriptExecutionNode interface is too coupled to old AST types.

**Step 1:** Add script planning support (or note it as blocked if ScriptExecutionNode coupling is too deep)
**Step 2:** Run `dotnet build`
**Step 3:** Commit

---

### Task 7: Fix UPDATE/DELETE to use FetchXmlGenerator instead of old SqlToFetchXmlTranspiler

**Goal:** Remove the ExecutionPlanBuilder dependency on the old SqlToFetchXmlTranspiler.

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**What to do:**

Currently `PlanUpdate()` (line 393) and `PlanDelete()` (line 446) use `new SqlToFetchXmlTranspiler()` to generate FetchXML for the source SELECT. Replace with FetchXmlGenerator.

Instead of building an old `SqlSelectStatement` and transpiling it, build a ScriptDom `SelectStatement` programmatically:

```csharp
private QueryPlanResult PlanUpdate(UpdateStatement updateStatement)
{
    var updateSpec = updateStatement.UpdateSpecification;
    var targetTable = updateSpec.Target as NamedTableReference;
    var entityName = GetTableName(targetTable);
    var idColumn = entityName + "id";

    // Build source SELECT using FetchXmlGenerator
    // SELECT entityid, [referenced cols] FROM entity WHERE ...
    var selectSpec = new QuerySpecification();
    // Add SELECT columns
    var idColRef = CreateColumnReference(idColumn);
    selectSpec.SelectElements.Add(new SelectScalarExpression { Expression = idColRef });

    // Add referenced columns from SET clauses
    var referencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var clause in updateSpec.SetClauses)
    {
        if (clause is AssignmentSetClause assignment)
            CollectReferencedColumns(assignment.NewValue, referencedColumns);
    }
    foreach (var col in referencedColumns)
    {
        if (!col.Equals(idColumn, StringComparison.OrdinalIgnoreCase))
            selectSpec.SelectElements.Add(new SelectScalarExpression {
                Expression = CreateColumnReference(col) });
    }

    // Add FROM clause (copy from UPDATE target)
    selectSpec.FromClause = new FromClause();
    selectSpec.FromClause.TableReferences.Add(targetTable);

    // Copy WHERE clause
    selectSpec.WhereClause = updateSpec.WhereClause;

    // Generate FetchXML using FetchXmlGenerator
    var wrapperSelect = new SelectStatement { QueryExpression = selectSpec };
    var transpileResult = _fetchXmlGenerator.Generate(wrapperSelect);

    // Create scan node
    var scanNode = new FetchXmlScanNode(
        transpileResult.FetchXml, entityName, autoPage: true);

    // Build SET clauses using old AST types (for DmlExecuteNode compatibility)
    var setClauses = new List<SqlSetClause>();
    foreach (var clause in updateSpec.SetClauses)
    {
        if (clause is AssignmentSetClause assignment)
        {
            var columnName = GetColumnName(assignment.Column);
            var expression = ConvertToSqlExpression(assignment.NewValue);
            setClauses.Add(new SqlSetClause(columnName, expression));
        }
    }

    var rootNode = DmlExecuteNode.Update(entityName, scanNode, setClauses,
        rowCap: _options.DmlRowCap ?? int.MaxValue);

    return new QueryPlanResult { ... };
}
```

Apply the same pattern to `PlanDelete()`.

**Step 1:** Rewrite PlanUpdate to use FetchXmlGenerator
**Step 2:** Rewrite PlanDelete to use FetchXmlGenerator
**Step 3:** Remove `using PPDS.Dataverse.Sql.Transpilation;` from ExecutionPlanBuilder
**Step 4:** Run `dotnet build src/PPDS.Query`
**Step 5:** Commit: `refactor: replace SqlToFetchXmlTranspiler with FetchXmlGenerator in ExecutionPlanBuilder`

---

### Task 8: Port IN subquery and EXISTS rewrites to ExecutionPlanBuilder

**Goal:** Handle IN (SELECT ...) and EXISTS (SELECT ...) patterns.

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**What to do:**

The old QueryPlanner uses `InSubqueryToJoinRewrite` and `ExistsToJoinRewrite` from `PPDS.Dataverse.Query.Planning.Rewrites`. These operate on old AST types.

For Phase 1, the simplest approach is:
1. Detect IN subqueries and EXISTS in the ScriptDom WHERE clause
2. For now, handle them as client-side filters (extract the subquery, plan it separately, use results for filtering)
3. The full JOIN rewrite optimization can come later

Alternatively, if the rewrite classes can be adapted to work with ScriptDom types, that's better.

The `InPredicate` in ScriptDom has a `Subquery` property. Check for `inPred.Subquery != null` to detect IN subqueries. For EXISTS, check for `ExistsPredicate`.

For Phase 1 minimal: treat these as client-side filters using the existing `ClientFilterNode`. The WHERE conditions that can't go to FetchXML are already extracted client-side.

**Step 1:** Add IN subquery detection to client filter extraction
**Step 2:** Add EXISTS detection to client filter extraction
**Step 3:** Run `dotnet build`
**Step 4:** Commit

---

### Task 9: Port variable substitution to ExecutionPlanBuilder

**Goal:** Handle @variable references in WHERE clauses.

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**What to do:**

Add `VariableScope` to `QueryPlanOptions`. When variables are present in WHERE conditions and a VariableScope is provided, substitute `VariableReference` nodes with literal values before FetchXML generation.

ScriptDom represents variables as `VariableReference` (in BooleanComparisonExpression.SecondExpression).

For Phase 1: detect `VariableReference` in WHERE, treat as client-side filter if no VariableScope is available. If VariableScope is available, substitute with literal values in the condition before passing to FetchXmlGenerator.

**Step 1:** Add variable handling
**Step 2:** Run `dotnet build`
**Step 3:** Commit

---

### Task 10: Delete old parser code

**Goal:** Remove all old parser files that are no longer referenced.

**Files to delete:**
- `src/PPDS.Dataverse/Sql/Parsing/SqlLexer.cs`
- `src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs`
- `src/PPDS.Dataverse/Sql/Parsing/SqlToken.cs`
- `src/PPDS.Dataverse/Sql/Parsing/SqlTokenType.cs`
- `src/PPDS.Dataverse/Sql/Parsing/SqlComment.cs`
- `src/PPDS.Dataverse/Sql/Transpilation/SqlToFetchXmlTranspiler.cs`
- `src/PPDS.Dataverse/Sql/Transpilation/TranspileResult.cs` (if not used by FetchXmlGenerator — check)
- `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs`

**Files to keep (used by plan nodes):**
- All files in `src/PPDS.Dataverse/Sql/Ast/` — ISqlCondition, ISqlExpression, SqlSetClause, etc. are used by plan nodes
- `src/PPDS.Dataverse/Sql/Parsing/SqlParseException.cs` — used by QueryParser

**Important:** Before deleting, `grep -r "SqlParser\b" src/` and `grep -r "SqlLexer" src/` to verify no remaining references. Fix any found references first.

Also check: `TranspileResult` is used by FetchXmlGenerator (which has its own `TranspileResult` in the `PPDS.Dataverse.Sql.Transpilation` namespace). Verify whether FetchXmlGenerator uses the same type or its own.

**Step 1:** grep for all remaining references to old parser classes
**Step 2:** Fix any references found
**Step 3:** Delete the files listed above
**Step 4:** Run `dotnet build` to verify no compilation errors
**Step 5:** Commit: `chore: delete old SqlLexer, SqlParser, QueryPlanner, SqlToFetchXmlTranspiler`

---

### Task 11: Delete old parser tests and update remaining tests

**Goal:** Remove tests that test old parser internals; update service tests.

**Test files to DELETE (test old parser internals):**
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/SqlLexerTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/SqlParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/BetweenParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/CaseExpressionParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/DeleteParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/ExistsParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/ExpressionParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/HavingParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/IfElseParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/InSubqueryParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/InsertParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/UnionParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/UpdateParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/VariableParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Parsing/WindowFunctionParserTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Ast/SqlExpressionTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Ast/SqlStatementHierarchyTests.cs`
- `tests/PPDS.Dataverse.Tests/Sql/Transpilation/SqlToFetchXmlTranspilerTests.cs`

**Test files to UPDATE:**
- `tests/PPDS.Dataverse.Tests/Query/Planning/QueryPlannerTests.cs` — rewrite to test ExecutionPlanBuilder
- `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs` — update to match new service API
- `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs` — should already work (uses ScriptDom)

**Test files that should still pass (ScriptDom-based, no changes needed):**
- `tests/PPDS.Cli.Tests/Tui/SqlCompletionEngineTests.cs` (TuiUnit)
- `tests/PPDS.Cli.Tests/Tui/SqlCursorContextTests.cs` (TuiUnit)
- `tests/PPDS.Cli.Tests/Tui/SqlSourceTokenizerTests.cs` (TuiUnit)
- `tests/PPDS.Cli.Tests/Tui/SqlValidatorTests.cs` (TuiUnit)

**Step 1:** Delete the old parser test files
**Step 2:** Run `dotnet build` to verify test projects compile
**Step 3:** Rewrite QueryPlannerTests → ExecutionPlanBuilderTests (test same SQL inputs produce same plan structure)
**Step 4:** Update SqlQueryServiceTests to remove old parser references
**Step 5:** Run `dotnet test --filter Category!=Integration`
**Step 6:** Run `dotnet test --filter Category=TuiUnit`
**Step 7:** Fix any failing tests
**Step 8:** Commit: `test: rewrite tests for new query engine, delete old parser tests`

---

### Task 12: Final cleanup and verification

**Goal:** Ensure zero compilation errors, zero analyzer warnings, no dead code.

**Steps:**

1. Run full build: `dotnet build`
2. Run unit tests: `dotnet test --filter Category!=Integration`
3. Run TUI tests: `dotnet test --filter Category=TuiUnit`
4. Search for any remaining references to deleted types:
   ```
   grep -r "SqlLexer\b" src/
   grep -r "SqlParser\b" src/ (excluding SqlParseException)
   grep -r "SqlToFetchXmlTranspiler" src/
   grep -r "QueryPlanner\b" src/ (excluding comments/docs)
   ```
5. Fix any issues found
6. Verify zero analyzer warnings
7. Final commit: `chore: Phase 1 complete — TSql160Parser replaces custom parser`

---

## Execution Order & Dependencies

```
Task 1 (Fix SqlQueryService) ──────── MUST BE FIRST
    │
    ├── Task 2 (UNION) ─────────────┐
    ├── Task 3 (TDS routing) ───────┤
    ├── Task 4 (Aggregate partition)┤── PARALLEL (independent features)
    ├── Task 5 (Window functions) ──┤
    ├── Task 6 (Script/IF/ELSE) ────┤
    ├── Task 7 (UPDATE/DELETE fix) ─┤
    ├── Task 8 (IN/EXISTS) ─────────┤
    └── Task 9 (Variables) ─────────┘
                 │
    Task 10 (Delete old code) ──── AFTER all features ported
                 │
    Task 11 (Update tests) ─────── AFTER old code deleted
                 │
    Task 12 (Final verification) ── LAST
```

## Quality Gate

Before declaring Phase 1 complete:

```bash
dotnet build src/PPDS.Query        # PPDS.Query builds alone
dotnet build                        # Full solution builds
dotnet test --filter Category!=Integration   # All unit tests pass
dotnet test --filter Category=TuiUnit        # All TUI tests pass
```

Zero errors. Zero analyzer warnings. No references to SqlLexer, SqlParser, or old QueryPlanner in source code.
