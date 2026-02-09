# Query Engine v2: Follow-Up Fixes

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> **Context:** Gaps found during code review of the Phase 4-7 fixes on branch `fix/tui-colors`.
> **Branch:** `fix/tui-colors` in worktree `C:\VS\ppdsw\ppds\.worktrees\tui-polish`

**Goal:** Fix remaining code correctness issues and fill test coverage gaps identified by code review of the v2 plan.

**Architecture:** All fixes are in the query engine pipeline — Dataverse planning nodes, CLI service layer, and expression evaluator. No new public APIs. Tests follow the existing `[Trait("Category", "PlanUnit")]` convention using `QueryPlanContext` with mock executors.

**Test command:** `dotnet test --filter "Category=PlanUnit" --no-build`

---

## Task 1: Fix MetadataScanNode nullable type lie

**Why:** `MetadataExecutor` property is declared non-nullable (`IMetadataQueryExecutor`) but the constructor accepts `null` via `null!` suppression. Any code accessing the property outside `ExecuteAsync` gets a `NullReferenceException` despite no compiler warning.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/MetadataScanNode.cs`

**Step 1: Read the file and understand the current state**

At line 28, the property is:
```csharp
public IMetadataQueryExecutor MetadataExecutor { get; }
```

At line 47, the constructor assigns:
```csharp
MetadataExecutor = metadataExecutor!; // May be null at plan time; resolved from context at execution
```

At lines 57-61, ExecuteAsync resolves it:
```csharp
var executor = MetadataExecutor ?? context.MetadataQueryExecutor
    ?? throw new QueryExecutionException(
        QueryErrorCode.ExecutionFailed,
        "No metadata query executor available...");
```

**Step 2: Make the property nullable**

Change line 28 from:
```csharp
public IMetadataQueryExecutor MetadataExecutor { get; }
```
to:
```csharp
public IMetadataQueryExecutor? MetadataExecutor { get; }
```

**Step 3: Remove the null-forgiving operator**

Change line 47 from:
```csharp
MetadataExecutor = metadataExecutor!; // May be null at plan time; resolved from context at execution
```
to:
```csharp
MetadataExecutor = metadataExecutor;
```

**Step 4: Verify no compile errors**

Run: `dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj --no-restore`

The `??` fallback in `ExecuteAsync` (line 57) already handles the nullable case correctly. If any other code accesses `MetadataExecutor` directly, the compiler will now flag it.

**Step 5: Run existing tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~MetadataScanNode" --no-restore`
Expected: All pass — the `Constructor_AllowsNullMetadataExecutor_ResolvedFromContextAtExecution` test should still work since the behavior is unchanged.

**Step 6: Commit**

```
fix(query): make MetadataScanNode.MetadataExecutor nullable

The property was declared non-nullable but accepted null via null!
suppression. Now correctly typed as IMetadataQueryExecutor? to
match the deferred-resolution pattern used at execution time.
```

---

## Task 2: Fix ExpressionEvaluator.VariableScope thread safety

**Why:** `ExpressionEvaluator` is instantiated once in `SqlQueryService` (line 27) and shared across all concurrent executions. The mutable `VariableScope` property creates a race condition — concurrent queries with DECLARE/SET would overwrite each other's scope. The scope is also available on `QueryPlanContext.VariableScope`, creating two sources of truth.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs`
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`

**Step 1: Remove the mutable VariableScope property from ExpressionEvaluator**

In `ExpressionEvaluator.cs`, remove line 19:
```csharp
public VariableScope? VariableScope { get; set; }
```

**Step 2: Update EvaluateVariable to accept VariableScope as parameter**

Find the `EvaluateVariable` method (around line 102). It currently reads from `this.VariableScope`. Change it to accept the scope as a parameter passed through the evaluation call chain.

However, `ExpressionEvaluator` is used through `IExpressionEvaluator` which has a defined contract. The cleanest fix is to check where `VariableScope` is set on the evaluator and instead ensure it's passed through `QueryPlanContext`.

Check current usage: `ExpressionEvaluator.VariableScope` is set in `SqlQueryService.ExecuteAsync` before creating the context. The context ALSO has `VariableScope`. So the fix is:

1. In `ExpressionEvaluator.EvaluateVariable` (line 102-112), instead of reading `this.VariableScope`, have the caller pass it. But since the `Evaluate` method signature is part of `IExpressionEvaluator`, the scope should be resolved from the context that flows through the plan execution.

**Simplest correct fix:** Since `EvaluateVariable` is called during plan node execution where `QueryPlanContext` is available, and `QueryPlanContext` already has `VariableScope`, the evaluator should read from a thread-local or parameter rather than its own property.

Looking at the call chain more carefully: `ExpressionEvaluator.Evaluate()` is called from plan nodes like `ClientFilterNode` which have access to `QueryPlanContext`. But `Evaluate` doesn't take a context parameter — it's a pure expression evaluator.

**Pragmatic fix:** Make `VariableScope` a settable property but document it's per-execution (not shared). OR — make `ExpressionEvaluator` instantiation per-execution instead of shared.

The safest fix with minimal API changes:

In `SqlQueryService.cs`, change line 27 from a shared instance to creating a new evaluator per execution:

```csharp
// Remove: private readonly ExpressionEvaluator _expressionEvaluator = new();
```

In `ExecuteAsync` (around line 80), create a fresh evaluator:
```csharp
var expressionEvaluator = new ExpressionEvaluator();
```

And pass it to the context. Do the same in `ExecuteStreamingAsync` and `ExplainAsync`.

**Step 3: Update ExecuteAsync to create per-execution evaluator**

In `SqlQueryService.ExecuteAsync`, before the context creation (around line 146):
```csharp
var expressionEvaluator = new ExpressionEvaluator();
if (variableScope != null)
    expressionEvaluator.VariableScope = variableScope;
```

Replace `_expressionEvaluator` with `expressionEvaluator` in the context creation.

**Step 4: Update ExecuteStreamingAsync similarly**

Create a fresh evaluator at the start of `ExecuteStreamingAsync` and use it for the context.

**Step 5: Update ExplainAsync similarly**

Create a fresh evaluator for the explain path.

**Step 6: Remove the shared field**

Remove `private readonly ExpressionEvaluator _expressionEvaluator = new();` from SqlQueryService.

**Step 7: Build and test**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`
Run: `dotnet test --filter "Category=PlanUnit" --no-restore`
Expected: All pass — behavior is identical, just no longer shared.

**Step 8: Commit**

```
fix(query): create ExpressionEvaluator per execution to fix thread safety

ExpressionEvaluator was a shared singleton in SqlQueryService with
a mutable VariableScope property. Concurrent queries with DECLARE/SET
could race on the scope. Now created per-execution so each query
gets its own evaluator instance.
```

---

## Task 3: Add DML safety guard to streaming path

**Why:** `ExecuteStreamingAsync` skips `DmlSafetyGuard.Check()` entirely. If a DML statement is routed through streaming, it would bypass the 10K row cap, confirmation prompts, and safety blocks. `ExecuteAsync` has this at lines 86-115 but `ExecuteStreamingAsync` (line 189) has none.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` (ExecuteStreamingAsync method)
- Modify: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`

**Step 1: Write a failing test**

In `SqlQueryServiceTests.cs`, add a test that verifies DML through streaming respects the safety guard. Use the existing test patterns and `FakeSqlQueryService` or construct a real `SqlQueryService` with mock dependencies:

```csharp
[Fact]
[Trait("Category", "PlanUnit")]
public async Task ExecuteStreamingAsync_DmlStatement_ChecksSafetyGuard()
{
    // Arrange: DELETE without WHERE should be blocked
    var request = new SqlQueryRequest
    {
        Sql = "DELETE FROM account",
        DmlSafety = new DmlSafetyOptions { IsConfirmed = false }
    };

    var service = CreateServiceWithMocks();

    // Act & Assert: should throw or return blocked result
    // The exact behavior depends on how the streaming path surfaces the block
    await Assert.ThrowsAsync<PpdsException>(async () =>
    {
        await foreach (var _ in service.ExecuteStreamingAsync(request, CancellationToken.None))
        {
            // Should not yield any results
        }
    });
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "ExecuteStreamingAsync_DmlStatement_ChecksSafetyGuard" --no-restore`
Expected: FAIL — no safety check in streaming path.

**Step 3: Add DML safety guard to ExecuteStreamingAsync**

In `SqlQueryService.ExecuteStreamingAsync`, after parsing the statement (around line 206) and before creating `QueryPlanOptions` (line 210), add the same safety guard logic from `ExecuteAsync`:

```csharp
// DML safety guard — mirror ExecuteAsync behavior
int? dmlRowCap = null;
if (statement.IsDml && request.DmlSafety != null)
{
    var safetyResult = _dmlSafetyGuard.Check(statement, request.DmlSafety);
    if (safetyResult.IsBlocked)
    {
        throw new PpdsException(
            ErrorCodes.Query.DmlBlocked,
            safetyResult.BlockReason ?? "DML operation blocked by safety guard.");
    }
    if (safetyResult.RequiresConfirmation)
    {
        throw new PpdsException(
            ErrorCodes.Query.DmlBlocked,
            $"DML operation requires confirmation. {safetyResult.ConfirmationMessage}");
    }
    dmlRowCap = safetyResult.RowCap;
}
```

Then add `DmlRowCap = dmlRowCap` to the `QueryPlanOptions` construction (around line 218).

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "ExecuteStreamingAsync_DmlStatement_ChecksSafetyGuard" --no-restore`
Expected: PASS

**Step 5: Run full test suite**

Run: `dotnet test --filter "Category=PlanUnit" --no-restore`
Expected: All pass

**Step 6: Commit**

```
fix(query): add DML safety guard to streaming execution path

ExecuteStreamingAsync now checks DmlSafetyGuard before executing,
matching ExecuteAsync behavior. DML through streaming now respects
the 10K row cap, confirmation prompts, and safety blocks.
```

---

## Task 4: Narrow bare catch in FetchXmlScanNode.PrepareFetchXmlForExecution

**Why:** The bare `catch` at line 189 swallows ALL exceptions including `OutOfMemoryException` and `OperationCanceledException`. This masks errors and makes debugging difficult. The method at lines 98-109 already shows the correct pattern.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/FetchXmlScanNode.cs`

**Step 1: Narrow the catch to XmlException**

Change lines 188-191 from:
```csharp
catch
{
    return fetchXml; // If parsing fails, return original
}
```
to:
```csharp
catch (XmlException)
{
    return fetchXml; // If FetchXML is malformed, return original and let Dataverse report the error
}
```

Add `using System.Xml;` if not already present.

**Step 2: Build and test**

Run: `dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj --no-restore`
Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~FetchXmlScanNode" --no-restore`
Expected: All pass

**Step 3: Commit**

```
fix(query): narrow bare catch to XmlException in FetchXmlScanNode

PrepareFetchXmlForExecution previously caught all exceptions
silently. Now only catches XmlException, allowing fatal exceptions
like OutOfMemoryException and OperationCanceledException to propagate.
```

---

## Task 5: Create ExplainCommandTests.cs

**Why:** The EXPLAIN command CLI integration (argument parsing, profile resolution, error handling, output formatting) has no test coverage. The planner/formatter are tested at the Dataverse layer, but the CLI command layer is not.

**Files:**
- Create: `tests/PPDS.Cli.Tests/Commands/Query/ExplainCommandTests.cs`
- Reference: `src/PPDS.Cli/Commands/Query/ExplainCommand.cs` (lines 20-111)
- Reference: `tests/PPDS.Cli.Tests/Mocks/FakeSqlQueryService.cs`
- Reference: `tests/PPDS.Cli.Tests/Mocks/MockServiceProviderFactory.cs`

**Step 1: Create the test file with basic structure**

```csharp
using System.CommandLine;
using System.CommandLine.IO;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Tests.Mocks;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Query;

[Trait("Category", "PlanUnit")]
public class ExplainCommandTests
{
    // Tests go here
}
```

**Step 2: Write test — valid SELECT produces plan output**

```csharp
[Fact]
public async Task Explain_ValidSelect_ProducesPlanOutput()
{
    // Arrange
    var fakeService = new FakeSqlQueryService();
    fakeService.NextExplainResult = new QueryPlanDescription
    {
        RootNode = new PlanNodeDescription
        {
            NodeType = "Project",
            Children = new[]
            {
                new PlanNodeDescription { NodeType = "FetchXmlScan", Description = "FetchXmlScan: account" }
            }
        }
    };

    // Act: invoke the command with the fake service
    var console = new TestConsole();
    var exitCode = await InvokeExplainCommand("SELECT accountid FROM account", fakeService, console);

    // Assert
    Assert.Equal(0, exitCode);
    var output = console.Out.ToString()!;
    Assert.Contains("Project", output);
    Assert.Contains("FetchXmlScan", output);
}
```

The test helper `InvokeExplainCommand` should use `MockServiceProviderFactory` to wire up the `FakeSqlQueryService`, build the command, and invoke it programmatically.

**Step 3: Write test — invalid SQL returns error**

```csharp
[Fact]
public async Task Explain_InvalidSql_ReturnsError()
{
    var fakeService = new FakeSqlQueryService();
    fakeService.ExceptionToThrow = new QueryExecutionException(
        QueryErrorCode.ParseError, "Unexpected token 'SELET' at position 0");

    var console = new TestConsole();
    var exitCode = await InvokeExplainCommand("SELET * FORM account", fakeService, console);

    Assert.NotEqual(0, exitCode);
    var error = console.Error.ToString()!;
    Assert.Contains("ParseError", error);
}
```

**Step 4: Write test — DML produces DmlExecuteNode**

```csharp
[Fact]
public async Task Explain_DeleteStatement_ShowsDmlNode()
{
    var fakeService = new FakeSqlQueryService();
    fakeService.NextExplainResult = new QueryPlanDescription
    {
        RootNode = new PlanNodeDescription
        {
            NodeType = "DmlExecute",
            Description = "DmlExecute: DELETE account"
        }
    };

    var console = new TestConsole();
    var exitCode = await InvokeExplainCommand("DELETE FROM account WHERE name = 'test'", fakeService, console);

    Assert.Equal(0, exitCode);
    Assert.Contains("DmlExecute", console.Out.ToString()!);
}
```

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~ExplainCommandTests" --no-restore`
Expected: All pass

**Step 6: Commit**

```
test(query): add ExplainCommand CLI integration tests

Covers plan output formatting for SELECT and DML queries,
and error handling for invalid SQL. Uses FakeSqlQueryService
to test the command layer without executing real queries.
```

---

## Task 6: Create ErrorCodeTests.cs

**Why:** No tests verify that plan nodes throw `QueryExecutionException` with the correct error codes. The error codes are defined and wired but untested — regressions would be silent.

**Files:**
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ErrorCodeTests.cs`
- Reference: `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs` (EvaluateArithmetic, NegateValue, EvaluateUnary)
- Reference: `src/PPDS.Dataverse/Query/Execution/QueryExecutionException.cs`

**Step 1: Create the test file**

```csharp
using PPDS.Dataverse.Query.Execution;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class ErrorCodeTests
{
    // Tests go here
}
```

**Step 2: Write test — arithmetic type mismatch throws TypeMismatch**

```csharp
[Fact]
public void EvaluateArithmetic_IncompatibleTypes_ThrowsTypeMismatch()
{
    var evaluator = new ExpressionEvaluator();

    // Attempt to add a string and a number — should throw TypeMismatch
    // Use the expression evaluator's Evaluate method with an incompatible binary expression
    var ex = Assert.Throws<QueryExecutionException>(() =>
        evaluator.EvaluateArithmetic("hello", 42, "+"));

    Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
}
```

Note: The exact method signature and how to trigger the type mismatch depends on `ExpressionEvaluator`'s public API. Check the `EvaluateArithmetic` method at line 439 for the exact signature and the conditions that trigger the error.

**Step 3: Write test — negate non-numeric throws TypeMismatch**

```csharp
[Fact]
public void NegateValue_NonNumericType_ThrowsTypeMismatch()
{
    var evaluator = new ExpressionEvaluator();

    var ex = Assert.Throws<QueryExecutionException>(() =>
        evaluator.NegateValue("not a number"));

    Assert.Equal(QueryErrorCode.TypeMismatch, ex.ErrorCode);
}
```

**Step 4: Write test — undeclared variable throws ExecutionFailed**

```csharp
[Fact]
public void EvaluateVariable_Undeclared_ThrowsExecutionFailed()
{
    var evaluator = new ExpressionEvaluator();
    evaluator.VariableScope = new VariableScope();

    var ex = Assert.Throws<QueryExecutionException>(() =>
        evaluator.EvaluateVariable("@undeclared"));

    Assert.Equal(QueryErrorCode.ExecutionFailed, ex.ErrorCode);
}
```

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~ErrorCodeTests" --no-restore`
Expected: All pass

**Step 6: Commit**

```
test(query): add error code tests for ExpressionEvaluator exceptions

Verifies TypeMismatch for arithmetic type errors and negation of
non-numeric values, and ExecutionFailed for undeclared variables.
Guards against regressions in structured error code usage.
```

---

## Task 7: Add streaming virtual column expansion test

**Why:** The `ExpandStreamingChunk` helper in `SqlQueryService` has no dedicated test. The plan explicitly requested verification that streaming results include `*name` columns (owneridname, statuscodename).

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`
- Reference: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` (ExpandStreamingChunk, lines 303-323)
- Reference: `src/PPDS.Cli/Services/Query/SqlQueryResultExpander.cs`

**Step 1: Write the test**

Add to `SqlQueryServiceTests.cs`:

```csharp
[Fact]
[Trait("Category", "PlanUnit")]
public async Task ExecuteStreamingAsync_LookupColumn_IncludesNameExpansion()
{
    // Arrange: set up a streaming result with a lookup column
    // that has a formatted value (e.g., ownerid with owneridname)
    var request = new SqlQueryRequest
    {
        Sql = "SELECT ownerid FROM account"
    };

    // Configure the fake service to return a result with lookup metadata
    var fakeService = CreateFakeServiceWithLookupResult();

    // Act: collect streaming chunks
    var allColumns = new HashSet<string>();
    await foreach (var chunk in fakeService.ExecuteStreamingAsync(request, CancellationToken.None))
    {
        foreach (var col in chunk.Columns)
            allColumns.Add(col.Name);
    }

    // Assert: owneridname should appear from expansion
    Assert.Contains("owneridname", allColumns);
}
```

The exact setup depends on how `FakeSqlQueryService.ExecuteStreamingAsync` can be configured to return lookup columns with formatted values.

**Step 2: Run test**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "ExecuteStreamingAsync_LookupColumn_IncludesNameExpansion" --no-restore`
Expected: PASS

**Step 3: Commit**

```
test(query): add streaming virtual column expansion test

Verifies that ExecuteStreamingAsync includes *name columns
(owneridname, etc.) in streaming output, matching non-streaming
path behavior.
```

---

## Task Dependency Graph

```
[Task 1: nullable fix]         Independent
[Task 2: thread safety]        Independent
[Task 3: streaming DML guard]  Independent (touches SqlQueryService)
[Task 4: narrow catch]         Independent

[Task 5: explain tests]        Independent (test-only)
[Task 6: error code tests]     Depends on Task 2 (VariableScope API may change)
[Task 7: streaming test]       Depends on Task 3 (streaming path may change)
```

**Execution order:**
1. Tasks 1, 2, 3, 4 in parallel (all independent code fixes)
2. Tasks 5, 6, 7 after their dependencies (test-only tasks)

---

## Notes for implementer

- **Test trait:** All tests use `[Trait("Category", "PlanUnit")]`
- **Test command:** `dotnet test --filter "Category=PlanUnit" --no-restore`
- **Build command:** `dotnet build --no-restore`
- **ExpressionEvaluator API:** Check the exact method signatures before writing tests — the public API may differ from what's shown here. Read the file first.
- **FakeSqlQueryService:** Check `tests/PPDS.Cli.Tests/Mocks/FakeSqlQueryService.cs` for available configuration points before writing service-level tests.
- **ExplainCommand testing:** The command uses `System.CommandLine` — look at existing command tests in `tests/PPDS.Cli.Tests/Commands/` for the invoke pattern.
