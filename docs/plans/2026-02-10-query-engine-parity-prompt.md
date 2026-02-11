# Query Engine Parity — Implementation Prompt

Use this prompt in an isolated session to implement the query engine parity design.

---

## Prompt

```
Read the design doc at docs/plans/2026-02-10-query-engine-parity-design.md thoroughly before doing anything.

You are implementing the PPDS Query Engine feature parity plan. This is a C# .NET project targeting net8.0/net9.0/net10.0. The query engine uses Microsoft's TSql160Parser (ScriptDom) to parse SQL, then builds an execution plan tree of IQueryPlanNode instances that execute against Dataverse via FetchXML or TDS.

Key architecture files to read first:
- src/PPDS.Query/Parsing/QueryParser.cs — SQL parsing entry point
- src/PPDS.Query/Parsing/QueryParseException.cs — error formatting (recently enhanced with whitespace hints)
- src/PPDS.Query/Planning/ExecutionPlanBuilder.cs — the main planner (~105KB, the most important file)
- src/PPDS.Query/Execution/ExpressionCompiler.cs — compiles ScriptDom AST expressions into delegates
- src/PPDS.Dataverse/Query/Execution/Functions/FunctionRegistry.cs — scalar function registry
- src/PPDS.Dataverse/Query/Planning/Nodes/ — all Dataverse-specific plan nodes
- src/PPDS.Query/Planning/Nodes/ — all platform-agnostic plan nodes
- src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs — DML execution
- tests/PPDS.Query.Tests/ — existing test structure

Use TDD: write failing tests first, then implement, then verify green. Follow the existing test patterns in tests/PPDS.Query.Tests/Parsing/QueryParserTests.cs and tests/PPDS.Cli.Tests/Services/Query/ExplainTests.cs.

IMPORTANT CONVENTIONS:
- All business logic goes in Application Services (src/PPDS.Cli/Services/), never in UI code
- Use IProgressReporter for operations >1 second
- Wrap exceptions in PpdsException with ErrorCode
- Use connection pool for Dataverse requests, never store clients
- Use bulk APIs (CreateMultiple/UpdateMultiple) not ExecuteMultiple
- Run tests with: dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore
- Do NOT use shell redirections (2>&1, >, >>) — triggers permission prompts
- Do NOT regenerate PPDS.Plugins.snk
- Do NOT edit files in src/PPDS.Dataverse/Generated/

Implement Wave 1 from the design doc:

1. AGGREGATE ALIAS RESOLUTION IN HAVING/ORDER BY
   - The #1 bug. ExpressionCompiler treats COUNT(*) in HAVING as a scalar function call.
   - Fix: When compiling HAVING/ORDER BY expressions, maintain a mapping from aggregate function signatures to their plan-output alias columns. When CompileFunctionCall encounters a known aggregate (COUNT, SUM, AVG, MIN, MAX, COUNT_BIG, STDEV, STDEVP, VAR, VARP), check the alias map first and compile to a column reference.
   - Key files: ExpressionCompiler.cs, ExecutionPlanBuilder.cs (where HAVING predicate is compiled)
   - Test: SELECT col, COUNT(*) AS cnt FROM account GROUP BY col HAVING COUNT(*) > 1 ORDER BY cnt DESC

2. ISNULL / COALESCE / NULLIF
   - ISNULL: register in FunctionRegistry as two-arg function returning arg0 ?? arg1
   - COALESCE: handle CoalesceExpression in CompileScalar — evaluate each expr, return first non-null
   - NULLIF: handle NullIfExpression in CompileScalar — return null if equal, else expr1
   - Test: SELECT ISNULL(name, 'Unknown'), COALESCE(phone1, phone2, 'N/A'), revenue / NULLIF(quantity, 0)

3. SIMPLE CASE EXPRESSION
   - Handle SimpleCaseExpression in CompileScalar (currently only SearchedCaseExpression works)
   - Compile input expr once, compare against each WHEN value, fall through to ELSE or NULL
   - Test: SELECT CASE statecode WHEN 0 THEN 'Active' WHEN 1 THEN 'Inactive' ELSE 'Other' END FROM account

4. BETWEEN CLIENT-SIDE
   - Handle BetweenExpression in CompilePredicate — compile as expr >= low AND expr <= high
   - Currently only works in FetchXML push-down, fails in client-side contexts (HAVING, computed exprs)
   - Test: HAVING with BETWEEN, computed column with BETWEEN

5. QUERY CANCELLATION IN TUI
   - Add query-level CancellationTokenSource to SqlQueryScreen (src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs)
   - Create/reset at start of ExecuteQueryAsync, link to ScreenCancellation
   - Register Escape as cancel trigger while _isExecuting == true
   - Show status: "Executing... (press Escape to cancel)" → "Cancelling..." → "Cancelled."
   - Cancellation stops between pages; in-flight requests complete server-side

6. PASTE BUG FIX
   - Root cause: Windows Terminal injects paste as individual key events; autocomplete popup activates mid-paste via CheckFromJoinTrigger, mangling text
   - Fix: Override Ctrl+V in SyntaxHighlightedTextView to read clipboard and insert as single bulk operation
   - Add _isPasting flag, skip CheckAutocompleteTrigger when true
   - Also fix Ctrl+Y handler in SqlQueryScreen.cs:201 (currently triggers paste instead of redo)
   - File: src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs

After completing all 6 items, run the full test suite: dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore

Commit each item separately with descriptive commit messages following the pattern: feat(query): <description> or fix(tui): <description>
```
