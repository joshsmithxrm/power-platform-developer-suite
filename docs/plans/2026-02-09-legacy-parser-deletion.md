# Legacy Parser Deletion Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Delete the old custom SQL parser, AST types, and transpiler from PPDS.Dataverse (~7,800 lines across ~46 files) by migrating all consumers to ScriptDom types or compiled delegates.

**Architecture:** Plan nodes currently consume legacy AST types (ISqlExpression, ISqlCondition, etc.) via a ScriptDomAdapter bridge in ExecutionPlanBuilder. We replace these with compiled delegates (`Func<Row, object?>` / `Func<Row, bool>`) so plan nodes have zero AST dependency at execution time. Surface-level callers (commands, services) swap from `new SqlParser()` to `QueryParser`. The old ExpressionEvaluator is replaced by an ExpressionCompiler that produces delegates from ScriptDom types.

**Tech Stack:** C# (.NET 8/9/10), Microsoft.SqlServer.TransactSql.ScriptDom, xUnit

**Dependency chain:**
```
Task 1 ──► Task 3 (DmlSafetyGuard unlocks ParseLegacyStatement removal)
Task 2 ──► Task 10 (command callers unlocks SqlParser deletion)
Task 4 ──► Tasks 5-7 (ExpressionCompiler unlocks plan node migration)
Tasks 5-7 ──► Task 9 (nodes migrated unlocks ScriptDomAdapter removal)
Task 8 ──► Task 9 (ScriptExecutionNode eliminated unlocks QueryPlanner deletion)
Task 9 ──► Task 10 (bridge removed unlocks AST deletion)
```

---

### Task 1: Migrate DmlSafetyGuard to ScriptDom Types

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs`
- Test: `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs` (existing — update)

**Context:** DmlSafetyGuard.Check() accepts `ISqlStatement` and pattern-matches on `SqlDeleteStatement`, `SqlUpdateStatement`, etc. The ScriptDom equivalents are `DeleteStatement`, `UpdateStatement`, `InsertStatement`, `SelectStatement`, `BeginEndBlockStatement`, `IfStatement`. The logic is identical — check for WHERE clause presence + row caps.

**Step 1: Write failing test for ScriptDom-based DmlSafetyGuard**

```csharp
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Parsing;

[Fact]
public void Check_DeleteWithoutWhere_IsBlocked_ScriptDom()
{
    var parser = new QueryParser();
    var stmt = parser.ParseStatement("DELETE FROM account");
    var guard = new DmlSafetyGuard();
    var result = guard.Check(stmt, new DmlSafetyOptions());
    Assert.True(result.IsBlocked);
    Assert.Contains("DELETE without WHERE", result.BlockReason);
}

[Fact]
public void Check_UpdateWithWhere_RequiresConfirmation_ScriptDom()
{
    var parser = new QueryParser();
    var stmt = parser.ParseStatement("UPDATE account SET name = 'x' WHERE accountid = '123'");
    var guard = new DmlSafetyGuard();
    var result = guard.Check(stmt, new DmlSafetyOptions());
    Assert.False(result.IsBlocked);
    Assert.True(result.RequiresConfirmation);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Check_DeleteWithoutWhere_IsBlocked_ScriptDom" --no-build`
Expected: FAIL — `Check()` only accepts `ISqlStatement`, not `TSqlStatement`

**Step 3: Implement — change DmlSafetyGuard to accept TSqlStatement**

Replace the `Check` method signature and switch expression in `DmlSafetyGuard.cs`:

```csharp
using Microsoft.SqlServer.TransactSql.ScriptDom;

public DmlSafetyResult Check(TSqlStatement statement, DmlSafetyOptions options)
{
    return statement switch
    {
        DeleteStatement delete => CheckDelete(delete, options),
        UpdateStatement update => CheckUpdate(update, options),
        InsertStatement => new DmlSafetyResult
        {
            IsBlocked = false,
            RequiresConfirmation = !options.IsConfirmed,
            EstimatedAffectedRows = -1
        },
        SelectStatement => new DmlSafetyResult { IsBlocked = false },
        BeginEndBlockStatement block => CheckBlock(block, options),
        IfStatement ifStmt => CheckIf(ifStmt, options),
        _ => new DmlSafetyResult { IsBlocked = false }
    };
}
```

Update private methods to use ScriptDom types:
- `CheckDelete`: `DeleteStatement` — check `delete.DeleteSpecification.WhereClause == null`; target table via `delete.DeleteSpecification.Target` → `NamedTableReference`
- `CheckUpdate`: `UpdateStatement` — check `update.UpdateSpecification.WhereClause == null`
- `CheckBlock`: `BeginEndBlockStatement` — iterate `block.StatementList.Statements`
- `CheckIf`: `IfStatement` — `ifStmt.ThenStatement` / `ifStmt.ElseStatement` (wrap in Check, handle `BeginEndBlockStatement` containing list)

Remove `using PPDS.Dataverse.Sql.Ast;` import.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "DmlSafetyGuard" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs
git commit -m "refactor(query): migrate DmlSafetyGuard to ScriptDom types"
```

---

### Task 2: Migrate Direct SqlParser Callers to QueryParser

**Files:**
- Modify: `src/PPDS.Cli/Commands/Data/UpdateCommand.cs:818`
- Modify: `src/PPDS.Cli/Commands/Data/DeleteCommand.cs:659`
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:596`
- Modify: `src/PPDS.Mcp/Tools/QuerySqlTool.cs:57`

**Context:** These four files call `new SqlParser(sql).ParseStatement()` to get an `ISqlStatement`. They need to switch to `new QueryParser().ParseStatement(sql)` which returns a `TSqlStatement`. Each caller's downstream usage must be updated to work with ScriptDom types.

**Step 1: Audit each caller's usage pattern**

Read each file at the call site to understand what it does with the parsed result:
- `UpdateCommand:818` — parses filter expression, extracts WHERE from a SELECT wrapper. Replace with QueryParser + extract WhereClause from SelectStatement
- `DeleteCommand:659` — same pattern as UpdateCommand
- `RpcMethodHandler:596` — parses SQL for transpilation. Replace with QueryParser + FetchXmlGeneratorService (already available in PPDS.Query)
- `QuerySqlTool:57` — parses for full execution. Replace with QueryParser + route through SqlQueryService

**Step 2: Update each file — replace `new SqlParser()` with QueryParser**

For each file:
1. Replace `using PPDS.Dataverse.Sql.Parsing;` with `using PPDS.Query.Parsing;`
2. Replace `var parser = new SqlParser(sql); var stmt = parser.ParseStatement();` with `var parser = new QueryParser(); var stmt = parser.ParseStatement(sql);`
3. Update downstream code to work with ScriptDom types instead of legacy AST types
4. Remove `using PPDS.Dataverse.Sql.Ast;` if no other references

**Step 3: Build to verify compilation**

Run: `dotnet build src/PPDS.Cli`
Run: `dotnet build src/PPDS.Mcp`
Expected: zero errors

**Step 4: Run existing tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category!=Integration" -v minimal`
Run: `dotnet test tests/PPDS.Mcp.Tests -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Cli/Commands/Data/UpdateCommand.cs src/PPDS.Cli/Commands/Data/DeleteCommand.cs src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs src/PPDS.Mcp/Tools/QuerySqlTool.cs
git commit -m "refactor(query): migrate command handlers from SqlParser to QueryParser"
```

---

### Task 3: Remove ParseLegacyStatement Bridge + Delete Old IntelliSense

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` — remove `ParseLegacyStatement()`, update callers at lines 133 and 298 to pass ScriptDom statement directly to DmlSafetyGuard
- Delete: `src/PPDS.Dataverse/Sql/Intellisense/SqlCursorContext.cs` (replaced by `src/PPDS.Query/Intellisense/SqlCursorContext.cs`)
- Delete: `src/PPDS.Dataverse/Sql/Intellisense/SqlCompletionEngine.cs` (replaced by `src/PPDS.Query/Intellisense/SqlCompletionEngine.cs`)
- Delete: `src/PPDS.Dataverse/Sql/Intellisense/SqlValidator.cs` (replaced by `src/PPDS.Query/Intellisense/SqlValidator.cs`)
- Delete: `src/PPDS.Dataverse/Sql/Intellisense/SqlSourceTokenizer.cs` (replaced by `src/PPDS.Query/Intellisense/SqlSourceTokenizer.cs`)
- Delete: All remaining files in `src/PPDS.Dataverse/Sql/Intellisense/`

**Depends on:** Task 1 (DmlSafetyGuard now accepts `TSqlStatement`)

**Step 1: Update SqlQueryService to pass ScriptDom statement to DmlSafetyGuard**

In `ExecuteAsync()` (line ~133) and `ExecuteStreamingAsync()` (line ~298), replace:
```csharp
var legacyStatement = ParseLegacyStatement(request.Sql);
var safetyResult = _dmlSafetyGuard.Check(legacyStatement, safetyOptions);
```
with:
```csharp
var safetyResult = _dmlSafetyGuard.Check(statement, safetyOptions);
```
where `statement` is the already-parsed `TSqlStatement` from `_queryParser`.

Delete the `ParseLegacyStatement()` method (lines 555-564).

Remove `using PPDS.Dataverse.Sql.Parsing;` if no other references remain.

**Step 2: Verify callers of old IntelliSense are wired to PPDS.Query versions**

Before deleting, verify that `SqlLanguageService` and any TUI/RPC callers reference the PPDS.Query IntelliSense classes, not the PPDS.Dataverse ones. Update any remaining references.

**Step 3: Delete old IntelliSense files**

Delete the entire `src/PPDS.Dataverse/Sql/Intellisense/` directory (12 files, ~1,769 lines).

**Step 4: Build and test**

Run: `dotnet build`
Run: `dotnet test --filter "Category!=Integration" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add -A
git commit -m "refactor(query): remove ParseLegacyStatement bridge, delete old IntelliSense"
```

---

### Task 4: Build ExpressionCompiler in PPDS.Query

**Files:**
- Create: `src/PPDS.Query/Execution/ExpressionCompiler.cs`
- Create: `src/PPDS.Query/Execution/CompiledExpression.cs` (type aliases)
- Test: `tests/PPDS.Query.Tests/Execution/ExpressionCompilerTests.cs`

**Context:** This is the critical infrastructure. ExpressionCompiler takes ScriptDom expression/condition types and returns typed delegates that plan nodes can call at execution time. The logic mirrors `PPDS.Dataverse.Query.Execution.ExpressionEvaluator` but accepts ScriptDom AST input and produces closures. It uses the existing `FunctionRegistry` for function dispatch and `CastConverter` for type conversion.

**Step 1: Define the delegate types and interface**

```csharp
// src/PPDS.Query/Execution/CompiledExpression.cs
namespace PPDS.Query.Execution;

/// <summary>Row type alias for expression evaluation context.</summary>
public delegate object? CompiledScalarExpression(IReadOnlyDictionary<string, QueryValue> row);

/// <summary>Row type alias for condition evaluation context.</summary>
public delegate bool CompiledPredicate(IReadOnlyDictionary<string, QueryValue> row);
```

**Step 2: Write failing tests for core expression compilation**

```csharp
// tests/PPDS.Query.Tests/Execution/ExpressionCompilerTests.cs
[Fact]
public void CompileScalar_IntegerLiteral_ReturnsValue()
{
    var compiler = new ExpressionCompiler();
    var expr = ParseExpression("42");
    var compiled = compiler.CompileScalar(expr);
    var result = compiled(EmptyRow);
    Assert.Equal(42, result);
}

[Fact]
public void CompileScalar_ColumnReference_ReturnsRowValue()
{
    var compiler = new ExpressionCompiler();
    var expr = ParseExpression("name");
    var compiled = compiler.CompileScalar(expr);
    var row = new Dictionary<string, QueryValue> { ["name"] = QueryValue.Simple("Contoso") };
    Assert.Equal("Contoso", compiled(row));
}

[Fact]
public void CompilePredicate_Comparison_EvaluatesCorrectly()
{
    var compiler = new ExpressionCompiler();
    var pred = ParsePredicate("x > 5");
    var compiled = compiler.CompilePredicate(pred);
    var row = new Dictionary<string, QueryValue> { ["x"] = QueryValue.Simple(10) };
    Assert.True(compiled(row));
}

// Helper: parse a ScriptDom expression from a SELECT wrapper
private static ScalarExpression ParseExpression(string expr)
{
    var parser = new QueryParser();
    var stmt = (SelectStatement)parser.ParseStatement($"SELECT {expr}");
    var query = (QuerySpecification)stmt.QueryExpression;
    var col = (SelectScalarExpression)query.SelectElements[0];
    return col.Expression;
}

private static BooleanExpression ParsePredicate(string pred)
{
    var parser = new QueryParser();
    var stmt = (SelectStatement)parser.ParseStatement($"SELECT 1 WHERE {pred}");
    var query = (QuerySpecification)stmt.QueryExpression;
    return query.WhereClause.SearchCondition;
}
```

**Step 3: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "ExpressionCompiler" -v minimal`
Expected: FAIL — `ExpressionCompiler` class doesn't exist

**Step 4: Implement ExpressionCompiler**

```csharp
// src/PPDS.Query/Execution/ExpressionCompiler.cs
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;

namespace PPDS.Query.Execution;

/// <summary>
/// Compiles ScriptDom expression/condition AST nodes into executable delegates.
/// Delegates capture evaluation logic as closures — no AST types at execution time.
/// </summary>
public sealed class ExpressionCompiler
{
    private readonly FunctionRegistry _functionRegistry;

    public ExpressionCompiler(FunctionRegistry? functionRegistry = null)
    {
        _functionRegistry = functionRegistry ?? FunctionRegistry.CreateDefault();
    }

    /// <summary>Compile a ScriptDom scalar expression to a delegate.</summary>
    public CompiledScalarExpression CompileScalar(ScalarExpression expression)
    {
        return expression switch
        {
            IntegerLiteral lit => _ => ParseInt(lit.Value),
            StringLiteral lit => _ => lit.Value,
            NullLiteral => _ => null,
            NumericLiteral lit => _ => decimal.Parse(lit.Value, CultureInfo.InvariantCulture),
            ColumnReferenceExpression col => CompileColumnRef(col),
            BinaryExpression bin => CompileBinary(bin),
            UnaryExpression unary => CompileUnary(unary),
            ParenthesisExpression paren => CompileScalar(paren.Expression),
            SearchedCaseExpression caseExpr => CompileSearchedCase(caseExpr),
            IIfCall iif => CompileIif(iif),
            FunctionCall func => CompileFunction(func),
            CastCall cast => CompileCast(cast),
            VariableReference varRef => CompileVariable(varRef),
            _ => throw new NotSupportedException(
                $"Expression type {expression.GetType().Name} is not yet supported by ExpressionCompiler.")
        };
    }

    /// <summary>Compile a ScriptDom boolean expression to a predicate delegate.</summary>
    public CompiledPredicate CompilePredicate(BooleanExpression condition)
    {
        return condition switch
        {
            BooleanComparisonExpression comp => CompileComparison(comp),
            BooleanIsNullExpression isNull => CompileIsNull(isNull),
            LikePredicate like => CompileLike(like),
            InPredicate inPred => CompileIn(inPred),
            BooleanBinaryExpression logical => CompileLogical(logical),
            BooleanNotExpression not => CompileNot(not),
            BooleanParenthesisExpression paren => CompilePredicate(paren.Expression),
            _ => throw new NotSupportedException(
                $"Condition type {condition.GetType().Name} is not yet supported by ExpressionCompiler.")
        };
    }

    // ... private Compile* methods follow same logic as ExpressionEvaluator
    // but return closures instead of evaluating immediately.
    // Each method recursively compiles sub-expressions into delegates.
}
```

The private methods follow the same patterns as `ExpressionEvaluator` but produce closures. For example:

```csharp
private CompiledScalarExpression CompileBinary(BinaryExpression bin)
{
    var left = CompileScalar(bin.FirstExpression);
    var right = CompileScalar(bin.SecondExpression);
    return row =>
    {
        var l = left(row);
        var r = right(row);
        if (l is null || r is null) return null;
        return EvaluateArithmetic(l, bin.BinaryExpressionType, r);
    };
}

private CompiledPredicate CompileComparison(BooleanComparisonExpression comp)
{
    var left = CompileScalar(comp.FirstExpression);
    var right = CompileScalar(comp.SecondExpression);
    return row =>
    {
        var l = left(row);
        var r = right(row);
        if (l is null || r is null) return false;
        int cmp = CompareValues(l, r);
        return comp.ComparisonType switch
        {
            BooleanComparisonType.Equals => cmp == 0,
            BooleanComparisonType.NotEqualToBrackets or
            BooleanComparisonType.NotEqualToExclamation => cmp != 0,
            BooleanComparisonType.LessThan => cmp < 0,
            BooleanComparisonType.GreaterThan => cmp > 0,
            BooleanComparisonType.LessThanOrEqualTo => cmp <= 0,
            BooleanComparisonType.GreaterThanOrEqualTo => cmp >= 0,
            _ => false
        };
    };
}
```

Port the helper methods (`CompareValues`, `PromoteNumeric`, `EvaluateArithmetic`, `MatchLikePattern`, `NegateValue`, `ParseNumber`) from `ExpressionEvaluator.cs` — they are pure functions with no AST dependency.

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Query.Tests --filter "ExpressionCompiler" -v minimal`
Expected: ALL PASS

**Step 6: Add comprehensive tests for all expression/condition types**

Cover: string literals, column refs, binary ops (+, -, *, /, %), unary negation, CASE/WHEN, IIF, functions (UPPER, LEN, GETDATE), CAST, variable refs, IS NULL, LIKE, IN, AND/OR/NOT, nested expressions.

**Step 7: Commit**

```bash
git add src/PPDS.Query/Execution/ExpressionCompiler.cs src/PPDS.Query/Execution/CompiledExpression.cs tests/PPDS.Query.Tests/Execution/ExpressionCompilerTests.cs
git commit -m "feat(query): add ExpressionCompiler - ScriptDom to delegate compilation"
```

---

### Task 5: Migrate Shallow Plan Nodes to Compiled Delegates

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ClientFilterNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ProjectNode.cs` (+ `ProjectColumn`)
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/MetadataScanNode.cs`
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` — update node construction to pass compiled delegates
- Test: existing tests + new delegate-based tests

**Depends on:** Task 4 (ExpressionCompiler)

**Step 1: Update ClientFilterNode to accept CompiledPredicate**

```csharp
// Before:
public ISqlCondition Condition { get; }
// After:
public CompiledPredicate Predicate { get; }
public string PredicateDescription { get; }
```

Update constructor, `ExecuteAsync` (call `Predicate(row.Values)` instead of `context.ExpressionEvaluator.EvaluateCondition`), and `Description` (use `PredicateDescription` string).

Remove `using PPDS.Dataverse.Sql.Ast;`.

**Step 2: Update ProjectColumn to accept CompiledScalarExpression**

```csharp
// Before:
public ISqlExpression? Expression { get; }
public static ProjectColumn Computed(string outputName, ISqlExpression expression) => ...;
// After:
public CompiledScalarExpression? Expression { get; }
public static ProjectColumn Computed(string outputName, CompiledScalarExpression expression) => ...;
```

Update `ProjectNode.ExecuteAsync` to call `col.Expression(inputRow.Values)` directly instead of going through `context.ExpressionEvaluator.Evaluate`.

Remove `using PPDS.Dataverse.Sql.Ast;` from both files.

**Step 3: Update MetadataScanNode to accept CompiledPredicate**

```csharp
// Before:
public ISqlCondition? Filter { get; }
// After:
public CompiledPredicate? Filter { get; }
```

Update filtering logic in `ExecuteAsync` to call `Filter(record)` directly.

Remove `using PPDS.Dataverse.Sql.Ast;`.

**Step 4: Update ExecutionPlanBuilder to compile delegates at plan construction**

In `ExecutionPlanBuilder`, where it currently does:
```csharp
var condition = ScriptDomAdapter.ConvertBooleanExpression(whereClause);
new ClientFilterNode(input, condition);
```

Change to:
```csharp
var predicate = _expressionCompiler.CompilePredicate(whereClause);
var description = whereClause.ToString(); // ScriptDom provides SQL text
new ClientFilterNode(input, predicate, description);
```

Add `ExpressionCompiler` field to `ExecutionPlanBuilder`. Repeat for ProjectNode and MetadataScanNode construction sites.

**Step 5: Build and test**

Run: `dotnet build`
Run: `dotnet test --filter "Category!=Integration" -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Dataverse/Query/Planning/Nodes/ClientFilterNode.cs src/PPDS.Dataverse/Query/Planning/Nodes/ProjectNode.cs src/PPDS.Dataverse/Query/Planning/Nodes/MetadataScanNode.cs src/PPDS.Query/Planning/ExecutionPlanBuilder.cs
git commit -m "refactor(query): migrate ClientFilter/Project/MetadataScan nodes to compiled delegates"
```

---

### Task 6: Migrate DmlExecuteNode to Compiled Delegates

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/DmlExecuteNode.cs`
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**Depends on:** Task 4

**Step 1: Replace ISqlExpression and SqlSetClause with delegates**

```csharp
// Before:
public IReadOnlyList<IReadOnlyList<ISqlExpression>>? InsertValueRows { get; }
public IReadOnlyList<SqlSetClause>? SetClauses { get; }

// After:
public IReadOnlyList<IReadOnlyList<CompiledScalarExpression>>? InsertValueRows { get; }
public IReadOnlyList<CompiledSetClause>? SetClauses { get; }
```

Define `CompiledSetClause`:
```csharp
public sealed record CompiledSetClause(string ColumnName, CompiledScalarExpression Value);
```

**Step 2: Update ExecuteInsertValuesAsync**

```csharp
// Before:
var value = context.ExpressionEvaluator.Evaluate(row[i], EmptyRow);
// After:
var value = row[i](EmptyRow);
```

**Step 3: Update ExecuteUpdateAsync**

```csharp
// Before:
var value = context.ExpressionEvaluator.Evaluate(clause.Value, row.Values);
entity[clause.ColumnName] = value;
// After:
var value = clause.Value(row.Values);
entity[clause.ColumnName] = value;
```

**Step 4: Update ExecutionPlanBuilder DML planning**

Where it currently calls `ScriptDomAdapter.GetInsertValueRows()` / `ScriptDomAdapter.GetUpdateSetClauses()`, compile expressions:
```csharp
var compiledRows = rawRows.Select(row =>
    row.Select(expr => _expressionCompiler.CompileScalar(expr)).ToList()
).ToList();
```

Remove `using PPDS.Dataverse.Sql.Ast;` from DmlExecuteNode.

**Step 5: Build, test, commit**

Run: `dotnet build && dotnet test --filter "Category!=Integration" -v minimal`

```bash
git add src/PPDS.Dataverse/Query/Planning/Nodes/DmlExecuteNode.cs src/PPDS.Query/Planning/ExecutionPlanBuilder.cs
git commit -m "refactor(query): migrate DmlExecuteNode to compiled delegates"
```

---

### Task 7: Migrate ClientWindowNode to Compiled Delegates

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ClientWindowNode.cs`
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**Depends on:** Task 4

**Context:** ClientWindowNode uses `SqlWindowExpression` (function name, partition-by expressions, order-by items) and `ISqlExpression` for partition key computation. Replace with a compiled `WindowDefinition` struct.

**Step 1: Create compiled WindowDefinition**

Replace the current `WindowDefinition` class (line 570-584):

```csharp
public sealed class WindowDefinition
{
    public string OutputColumnName { get; }
    public string FunctionName { get; }
    public CompiledScalarExpression? Operand { get; }
    public IReadOnlyList<CompiledScalarExpression> PartitionBy { get; }
    public IReadOnlyList<CompiledOrderByItem> OrderBy { get; }

    // ... constructor
}

public sealed record CompiledOrderByItem(
    string ColumnName,
    CompiledScalarExpression Value,
    bool Descending);
```

**Step 2: Update ClientWindowNode execution methods**

- `ComputePartitionKey`: call `partitionBy[i](row.Values)` instead of `context.ExpressionEvaluator.Evaluate`
- `SortPartition`: use `CompiledOrderByItem.ColumnName` and `.Descending` instead of `SqlOrderByItem`
- `CompareRowsByOrderBy`: use compiled items
- Aggregate computation: call `w.Operand(row.Values)` instead of evaluator

**Step 3: Update ExecutionPlanBuilder window planning**

Where it constructs `WindowDefinition`, compile the ScriptDom expressions:
```csharp
var partitionBy = windowExpr.PartitionBy
    .Select(e => _expressionCompiler.CompileScalar(e))
    .ToList();
```

Remove `using PPDS.Dataverse.Sql.Ast;` from ClientWindowNode.

**Step 4: Build, test, commit**

Run: `dotnet build && dotnet test --filter "Category!=Integration" -v minimal`

```bash
git add src/PPDS.Dataverse/Query/Planning/Nodes/ClientWindowNode.cs src/PPDS.Query/Planning/ExecutionPlanBuilder.cs
git commit -m "refactor(query): migrate ClientWindowNode to compiled delegates"
```

---

### Task 8: Replace ScriptExecutionNode with Direct ExecutionPlanBuilder Planning

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` — `PlanScript()` method
- Modify or delete: `src/PPDS.Dataverse/Query/Planning/Nodes/ScriptExecutionNode.cs`
- Modify or delete: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs`

**Depends on:** Tasks 4-7

**Context:** ScriptExecutionNode is the hardest migration target. It pattern-matches on legacy `ISqlStatement` hierarchy (DECLARE, SET, IF, WHILE, TRY/CATCH, BLOCK) and uses the old `QueryPlanner` to plan inner SELECT/DML statements. The v3 ExecutionPlanBuilder already has `ConvertToLegacyStatement()` that converts ScriptDom → legacy AST just for this node.

**Approach:** Instead of converting ScriptDom → legacy statements → ScriptExecutionNode → old QueryPlanner, have ExecutionPlanBuilder construct a plan node tree directly from ScriptDom script blocks. The v3 architecture already defines the needed node types. Either:

**(A) Inline approach:** Expand `PlanScript()` to recursively build plan nodes for each statement type. IF → create a `ConditionalNode` (predicate delegate + then plan + else plan). WHILE → `WhileNode` (predicate delegate + body plan). DECLARE/SET → `DeclareVariablesNode`/`AssignVariablesNode`. Inner SELECT/DML → plan via normal `PlanStatement()`.

**(B) Minimal approach:** Rewrite `ScriptExecutionNode` to accept ScriptDom `TSqlStatement` list directly instead of legacy `ISqlStatement`. Keep the same pattern-matching logic but switch on ScriptDom types. Use `ExecutionPlanBuilder` instead of `QueryPlanner` for inner statement planning.

**Recommended: Approach B** — less disruptive, same behavior, avoids creating new node types.

**Step 1: Rewrite ScriptExecutionNode to accept TSqlStatement**

Move `ScriptExecutionNode` to `src/PPDS.Query/Planning/Nodes/ScriptExecutionNode.cs` (or keep in place and change imports).

```csharp
using Microsoft.SqlServer.TransactSql.ScriptDom;

public sealed class ScriptExecutionNode : IQueryPlanNode
{
    private readonly IReadOnlyList<TSqlStatement> _statements;
    private readonly ExecutionPlanBuilder _planBuilder;

    // switch (statement) cases become:
    // case DeclareVariableStatement declare: ...
    // case SetVariableStatement setVar: ...
    // case IfStatement ifStmt: ...
    // case WhileStatement whileStmt: ...
    // case TryCatchStatement tryCatch: ...
    // case BeginEndBlockStatement block: ...
    // default: plan via _planBuilder.PlanStatement(statement, options)
}
```

Use `_expressionCompiler.CompileScalar()` for variable values, `_expressionCompiler.CompilePredicate()` for IF/WHILE conditions.

**Step 2: Remove `ConvertToLegacyStatement()` from ExecutionPlanBuilder**

Delete the entire `ConvertToLegacyStatement()` method and all its helper methods (`ConvertDeclareToLegacy`, `ConvertSetVariableToLegacy`, `ConvertIfToLegacy`, `ConvertWhileToLegacy`, `ConvertTryCatchToLegacy`, etc. — lines ~1439-1680).

Update `PlanScript()` to pass `TSqlStatement` list directly to the new ScriptExecutionNode.

**Step 3: Delete old QueryPlanner if no other consumers**

Verify `QueryPlanner` is only used by ScriptExecutionNode. If so, delete `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs`.

Also delete rewrite files if they are only used by QueryPlanner:
- `src/PPDS.Dataverse/Query/Planning/Rewrites/InSubqueryToJoinRewrite.cs`
- `src/PPDS.Dataverse/Query/Planning/Rewrites/ExistsToJoinRewrite.cs`

**Step 4: Build, test, commit**

Run: `dotnet build && dotnet test --filter "Category!=Integration" -v minimal`

```bash
git add -A
git commit -m "refactor(query): rewrite ScriptExecutionNode to use ScriptDom types directly"
```

---

### Task 9: Remove ScriptDomAdapter

**Files:**
- Delete: `src/PPDS.Query/Planning/ScriptDomAdapter.cs` (1,304 lines)
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` — replace all `ScriptDomAdapter.Convert*()` calls with `_expressionCompiler.Compile*()` calls or direct ScriptDom property access

**Depends on:** Tasks 5-8 (all plan nodes migrated)

**Step 1: Audit remaining ScriptDomAdapter usages in ExecutionPlanBuilder**

Search for `ScriptDomAdapter.` in ExecutionPlanBuilder.cs. After Tasks 5-8, the remaining calls should be limited to:
- `ConvertSelectStatement()` — used for FetchXML transpilation path (may still be needed by old transpiler)
- `ConvertExpression()` — should be replaced by `_expressionCompiler.CompileScalar()`
- `ConvertBooleanExpression()` — should be replaced by `_expressionCompiler.CompilePredicate()`
- `ConvertColumnRef()`, `ConvertJoins()`, `ConvertFromClause()` — used for legacy QueryPlanner (deleted in Task 8)

**Step 2: Replace remaining calls**

For each remaining `ScriptDomAdapter.Convert*()` call:
- Expression/condition conversions → `_expressionCompiler.Compile*()`
- Table/column extractions → access ScriptDom properties directly (e.g., `NamedTableReference.SchemaObject.BaseIdentifier.Value`)
- SELECT conversion for FetchXML → FetchXmlGenerator already works with ScriptDom types directly

**Step 3: Delete ScriptDomAdapter.cs**

Remove the file entirely. Remove any `using` references.

**Step 4: Build, test, commit**

Run: `dotnet build && dotnet test --filter "Category!=Integration" -v minimal`

```bash
git add -A
git commit -m "refactor(query): remove ScriptDomAdapter bridge (1,304 lines)"
```

---

### Task 10: Delete Old Parser, AST, Transpiler, and Tests

**Files to delete:**
- `src/PPDS.Dataverse/Sql/Parsing/` (5 files, 3,209 lines): SqlParser.cs, SqlLexer.cs, SqlTokenType.cs, SqlToken.cs, SqlParseException.cs
- `src/PPDS.Dataverse/Sql/Ast/` (27 files, 1,714 lines): all AST type definitions
- `src/PPDS.Dataverse/Sql/Transpilation/` (2 files, 1,152 lines): SqlToFetchXmlTranspiler.cs, TranspileResult.cs
- `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs` (~645 lines) + `IExpressionEvaluator.cs`
- Old test files in `tests/PPDS.Dataverse.Tests/Sql/` (~20 files)

**Depends on:** Tasks 1-9 (all consumers migrated)

**Step 1: Verify zero references to old namespaces**

Search for any remaining references:
```
grep -r "PPDS.Dataverse.Sql.Ast" src/
grep -r "PPDS.Dataverse.Sql.Parsing" src/
grep -r "PPDS.Dataverse.Sql.Transpilation" src/
grep -r "IExpressionEvaluator" src/
grep -r "ExpressionEvaluator" src/
```

If any references remain, fix them before proceeding.

**Step 2: Delete all old files**

Delete the directories and files listed above.

Also remove `IExpressionEvaluator` from `QueryPlanContext` if it's still referenced there — plan nodes now use compiled delegates directly, so the evaluator field is no longer needed.

**Step 3: Clean up QueryPlanContext**

Remove `ExpressionEvaluator` property from `QueryPlanContext` if present. Plan nodes now carry their own compiled delegates and don't need a shared evaluator.

**Step 4: Build and test everything**

Run: `dotnet build`
Run: `dotnet test --filter "Category!=Integration" -v minimal`
Run: `dotnet test --filter "Category=TuiUnit" -v minimal`
Expected: ALL PASS, zero warnings about missing types

**Step 5: Commit**

```bash
git add -A
git commit -m "chore(query): delete legacy SQL parser, AST types, and transpiler (~7,800 lines)"
```

---

## Summary

| Task | What | Lines Removed | Lines Added | Effort |
|------|------|---------------|-------------|--------|
| 1 | DmlSafetyGuard → ScriptDom | ~50 | ~50 | Small |
| 2 | Command handlers → QueryParser | ~40 | ~40 | Small |
| 3 | Remove ParseLegacyStatement + old IntelliSense | ~1,800 | ~10 | Small |
| 4 | Build ExpressionCompiler | 0 | ~500 | Medium |
| 5 | Migrate shallow plan nodes | ~30 | ~40 | Small |
| 6 | Migrate DmlExecuteNode | ~20 | ~25 | Small |
| 7 | Migrate ClientWindowNode | ~40 | ~50 | Medium |
| 8 | Rewrite ScriptExecutionNode + delete QueryPlanner | ~600 | ~300 | Large |
| 9 | Remove ScriptDomAdapter | ~1,304 | ~50 | Medium |
| 10 | Delete old parser/AST/transpiler/tests | ~5,800 | 0 | Small |
| **Total** | | **~9,684** | **~1,065** | |

**Net reduction: ~8,600 lines of code.**
