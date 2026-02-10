# Query Engine v3 Review Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all bugs and completeness gaps identified in the code review of the query-engine-v3 branch.

**Architecture:** Targeted fixes across existing files. Bug fixes convert silent failures into explicit errors or implement missing logic. Completeness items add missing window functions (RANK, DENSE_RANK, CUME_DIST, PERCENT_RANK), a missing date function (TIMEFROMPARTS), and the OPENJSON table-valued function. Stale doc comments and a parameter substitution vulnerability are also addressed.

**Tech Stack:** C# (.NET 8/9/10), Microsoft.SqlServer.TransactSql.ScriptDom, xUnit, FluentAssertions, Moq

**Dependency chain:**
```
Tasks 1-4: independent (parallel-safe)
Task 5 ──► Task 6 (RANK/DENSE_RANK pattern enables CUME_DIST/PERCENT_RANK)
Tasks 7-9: independent
Task 10 ──► Task 11 (OpenJsonNode must exist before wiring into ExecutionPlanBuilder)
```

---

### Task 1: Fix Stale XML Doc `cref` Comments

**Files:**
- Modify: `src/PPDS.Query/Execution/ExpressionCompiler.cs:14`
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs:133,1352,1463,2099`

**Context:** Three doc comments reference deleted types (`ExpressionEvaluator`, `QueryPlanner`) via `<see cref="..."/>` which produce XML documentation warnings.

**Step 1: Fix ExpressionCompiler.cs stale cref**

Replace lines 13-16 in `src/PPDS.Query/Execution/ExpressionCompiler.cs`:

```csharp
// Before:
/// Compiles ScriptDom AST expression and condition nodes into executable delegates.
/// Mirrors the evaluation logic of <see cref="ExpressionEvaluator"/> but produces
/// closures (<see cref="CompiledScalarExpression"/> and <see cref="CompiledPredicate"/>)
/// instead of walking the AST at evaluation time.

// After:
/// Compiles ScriptDom AST expression and condition nodes into executable delegates.
/// Produces closures (<see cref="CompiledScalarExpression"/> and <see cref="CompiledPredicate"/>)
/// instead of walking the AST at evaluation time.
```

**Step 2: Fix ExecutionPlanBuilder.cs stale crefs**

In `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`:

Line 133: Replace `Mirrors the existing QueryPlanner.PlanSelect behavior.` with `Plans a regular SELECT query (non-UNION).` (remove duplicate summary line).

Line 1352: Replace `Ported from <see cref="QueryPlanner.ShouldPartitionAggregate"/> to work with ScriptDom types.` with `Determines whether an aggregate query should use parallel partitioning with ScriptDom types.`

Line 1463: Replace `Ported from <see cref="QueryPlanner.BuildMergeAggregateColumns"/> to work with ScriptDom types.` with `Builds merge aggregate column descriptors from a ScriptDom QuerySpecification.`

Line 2099: Replace `(duplicated from QueryPlanner` with `(ported from legacy planner`.

**Step 3: Build to verify**

Run: `dotnet build src/PPDS.Query`
Expected: zero errors, zero new warnings

**Step 4: Commit**

```bash
git add src/PPDS.Query/Execution/ExpressionCompiler.cs src/PPDS.Query/Planning/ExecutionPlanBuilder.cs
git commit -m "docs(query): fix stale XML doc cref references to deleted types"
```

---

### Task 2: Fix PpdsDbCommand Parameter Name Overlap

**Files:**
- Modify: `src/PPDS.Query/Provider/PpdsDbCommand.cs:238-253`
- Test: `tests/PPDS.Query.Tests/Provider/PpdsDbCommandTests.cs`

**Context:** `ApplyParameters` uses `string.Replace` to substitute parameter names. If parameters `@param1` and `@param10` both exist, replacing `@param1` first corrupts `@param10`. Fix: sort parameters by name length descending before substitution.

**Step 1: Write failing test**

Add to `tests/PPDS.Query.Tests/Provider/PpdsDbCommandTests.cs`:

```csharp
[Fact]
public void ApplyParameters_OverlappingNames_SubstitutesCorrectly()
{
    // Use reflection to test internal ApplyParameters method, or test via Prepare path.
    // Actually, test via the public API: parameter values should be correctly resolved.
    var cmd = new PpdsDbCommand();
    cmd.Parameters.AddWithValue("@p1", "short");
    cmd.Parameters.AddWithValue("@p10", "long");
    cmd.CommandText = "SELECT @p1, @p10";

    // After parameter application, @p10 should NOT become 'short'0
    // We can't directly call ApplyParameters (internal), but we can validate via Prepare.
    // Prepare validates syntax — if @p10 becomes 'short'0 it will fail to parse.
    var act = () => cmd.Prepare();
    act.Should().NotThrow();
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Query.Tests --filter "ApplyParameters_OverlappingNames" -v minimal`
Expected: FAIL — `'short'0` is not valid SQL

**Step 3: Fix ApplyParameters to sort by name length descending**

Replace the `ApplyParameters` method in `src/PPDS.Query/Provider/PpdsDbCommand.cs`:

```csharp
private string ApplyParameters(string sql)
{
    if (_parameters.Count == 0)
        return sql;

    var result = sql;
    // Sort by name length descending to prevent @p1 matching inside @p10
    var sorted = _parameters.InternalList
        .OrderByDescending(p => p.ParameterName.Length)
        .ToList();

    foreach (var param in sorted)
    {
        var paramName = param.ParameterName;
        if (!paramName.StartsWith("@"))
            paramName = "@" + paramName;

        result = result.Replace(paramName, param.ToSqlLiteral());
    }
    return result;
}
```

Add `using System.Linq;` to the file if not already present.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Query.Tests --filter "PpdsDbCommand" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Provider/PpdsDbCommand.cs tests/PPDS.Query.Tests/Provider/PpdsDbCommandTests.cs
git commit -m "fix(query): sort parameter names by length to prevent overlap in substitution"
```

---

### Task 3: Convert ExecuteMessageNode Stub to NotSupportedException

**Files:**
- Modify: `src/PPDS.Query/Planning/Nodes/ExecuteMessageNode.cs:51-73`
- Modify: `tests/PPDS.Query.Tests/Planning/ExecuteMessageNodeTests.cs`

**Context:** ExecuteMessageNode returns a "pending" row instead of doing anything. This misleads users. Throw `NotSupportedException` with a clear message until actual Dataverse message execution is wired.

**Step 1: Write failing test for new behavior**

Replace the execution tests in `tests/PPDS.Query.Tests/Planning/ExecuteMessageNodeTests.cs`:

```csharp
[Fact]
public async Task ExecuteAsync_ThrowsNotSupportedException()
{
    var node = new ExecuteMessageNode("WhoAmI", new List<MessageParameter>());
    var act = async () => await TestHelpers.CollectRowsAsync(node);
    await act.Should().ThrowAsync<NotSupportedException>()
        .WithMessage("*EXECUTE*not yet supported*");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Query.Tests --filter "ExecuteAsync_ThrowsNotSupportedException" -v minimal`
Expected: FAIL — currently returns a row instead of throwing

**Step 3: Update ExecuteMessageNode to throw**

Replace the `ExecuteAsync` method body in `src/PPDS.Query/Planning/Nodes/ExecuteMessageNode.cs`:

```csharp
public async IAsyncEnumerable<QueryRow> ExecuteAsync(
    QueryPlanContext context,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    await Task.CompletedTask;
    throw new NotSupportedException(
        $"EXECUTE '{MessageName}' is not yet supported. " +
        "Dataverse message execution will be available in a future release.");
    yield break; // Required for async iterator signature
}
```

**Step 4: Update existing tests to expect NotSupportedException**

Remove the old execution tests that assert on "pending" status rows. Replace them with the single `ExecuteAsync_ThrowsNotSupportedException` test. Keep the constructor, description, and metadata tests unchanged.

**Step 5: Run all tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "ExecuteMessageNode" -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/ExecuteMessageNode.cs tests/PPDS.Query.Tests/Planning/ExecuteMessageNodeTests.cs
git commit -m "fix(query): ExecuteMessageNode throws NotSupportedException instead of returning stub"
```

---

### Task 4: Convert ImpersonationNode Fake GUID to NotSupportedException

**Files:**
- Modify: `src/PPDS.Query/Planning/Nodes/ImpersonationNode.cs:56-87`
- Modify: `tests/PPDS.Query.Tests/Planning/ImpersonationNodeTests.cs`

**Context:** `ExecuteAsNode` generates a fake deterministic GUID from MD5 of UPN when no pre-resolved CallerObjectId is provided. This sets an incorrect caller ID on the session. Keep the pre-resolved GUID path (for future callers that resolve the GUID externally), but throw when no GUID is provided.

**Step 1: Write failing test**

Add to `tests/PPDS.Query.Tests/Planning/ImpersonationNodeTests.cs`:

```csharp
[Fact]
public async Task ExecuteAsNode_WithoutPreResolvedGuid_ThrowsNotSupportedException()
{
    var session = new SessionContext();
    var node = new ExecuteAsNode("user@contoso.com", session);

    var act = async () => await TestHelpers.CollectRowsAsync(node);
    await act.Should().ThrowAsync<NotSupportedException>()
        .WithMessage("*EXECUTE AS*resolve*systemuserid*");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Query.Tests --filter "WithoutPreResolvedGuid_ThrowsNotSupportedException" -v minimal`
Expected: FAIL — currently sets a fake GUID

**Step 3: Update ExecuteAsNode to throw when no pre-resolved GUID**

Replace the `ExecuteAsync` method body in `src/PPDS.Query/Planning/Nodes/ImpersonationNode.cs`:

```csharp
public async IAsyncEnumerable<QueryRow> ExecuteAsync(
    QueryPlanContext context,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    if (CallerObjectId.HasValue)
    {
        Session.CallerObjectId = CallerObjectId.Value;
    }
    else
    {
        await Task.CompletedTask;
        throw new NotSupportedException(
            $"EXECUTE AS USER = '{UserPrincipalName}' cannot resolve the systemuserid. " +
            "Provide a pre-resolved CallerObjectId or use a future release with Dataverse user lookup.");
    }

    await Task.CompletedTask;
    yield break;
}
```

Delete the `GenerateDeterministicGuid` private method.

**Step 4: Update existing tests**

- Remove the test `ExecuteAsNode_SetsCallerObjectIdOnSession` (tested UPN-only path which now throws).
- Update `ExecuteAsAndRevert_FullLifecycle` to pass an explicit `callerObjectId` GUID.
- Remove `ExecuteAsNode_DeterministicGuid_SameInputSameOutput` if it exists.
- Keep the test for explicit GUID: `ExecuteAsNode_WithExplicitGuid_SetsDirectly`.

**Step 5: Run all tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Impersonation" -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/ImpersonationNode.cs tests/PPDS.Query.Tests/Planning/ImpersonationNodeTests.cs
git commit -m "fix(query): ImpersonationNode throws when no pre-resolved CallerObjectId"
```

---

### Task 5: Add RANK and DENSE_RANK to WindowSpoolNode

**Files:**
- Modify: `src/PPDS.Query/Planning/Nodes/WindowSpoolNode.cs:280-321`
- Test: `tests/PPDS.Query.Tests/Planning/WindowSpoolNodeTests.cs` (create)

**Context:** WindowSpoolNode handles LAG, LEAD, NTILE, FIRST_VALUE, LAST_VALUE, ROW_NUMBER, and aggregates but is missing RANK and DENSE_RANK. These fall through to `NotSupportedException`. Both are straightforward: compare ORDER BY values between adjacent rows in the sorted partition.

**Step 1: Write failing tests**

Create `tests/PPDS.Query.Tests/Planning/WindowSpoolNodeTests.cs`:

```csharp
using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class WindowSpoolNodeTests
{
    [Fact]
    public async Task Rank_WithTies_AssignsRankWithGaps()
    {
        // Input: scores 100, 90, 90, 80 → RANK should be 1, 2, 2, 4
        var rows = TestSourceNode.Create("test",
            TestSourceNode.MakeRow("test", ("name", "A"), ("score", 100)),
            TestSourceNode.MakeRow("test", ("name", "B"), ("score", 90)),
            TestSourceNode.MakeRow("test", ("name", "C"), ("score", 90)),
            TestSourceNode.MakeRow("test", ("name", "D"), ("score", 80)));

        var orderBy = new List<CompiledOrderByItem>
        {
            new("score", r => r.TryGetValue("score", out var v) ? v.Value : null, true) // DESC
        };

        var windowDef = new ExtendedWindowDefinition("rnk", "RANK", null, null, orderBy);
        var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().HaveCount(4);
        result[0].Values["rnk"].Value.Should().Be(1);
        result[1].Values["rnk"].Value.Should().Be(2);
        result[2].Values["rnk"].Value.Should().Be(2);
        result[3].Values["rnk"].Value.Should().Be(4);
    }

    [Fact]
    public async Task DenseRank_WithTies_AssignsRankWithoutGaps()
    {
        // Input: scores 100, 90, 90, 80 → DENSE_RANK should be 1, 2, 2, 3
        var rows = TestSourceNode.Create("test",
            TestSourceNode.MakeRow("test", ("name", "A"), ("score", 100)),
            TestSourceNode.MakeRow("test", ("name", "B"), ("score", 90)),
            TestSourceNode.MakeRow("test", ("name", "C"), ("score", 90)),
            TestSourceNode.MakeRow("test", ("name", "D"), ("score", 80)));

        var orderBy = new List<CompiledOrderByItem>
        {
            new("score", r => r.TryGetValue("score", out var v) ? v.Value : null, true)
        };

        var windowDef = new ExtendedWindowDefinition("drnk", "DENSE_RANK", null, null, orderBy);
        var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().HaveCount(4);
        result[0].Values["drnk"].Value.Should().Be(1);
        result[1].Values["drnk"].Value.Should().Be(2);
        result[2].Values["drnk"].Value.Should().Be(2);
        result[3].Values["drnk"].Value.Should().Be(3);
    }

    [Fact]
    public async Task Rank_NoTies_SequentialNumbers()
    {
        var rows = TestSourceNode.Create("test",
            TestSourceNode.MakeRow("test", ("v", 3)),
            TestSourceNode.MakeRow("test", ("v", 1)),
            TestSourceNode.MakeRow("test", ("v", 2)));

        var orderBy = new List<CompiledOrderByItem>
        {
            new("v", r => r.TryGetValue("v", out var qv) ? qv.Value : null, false)
        };

        var windowDef = new ExtendedWindowDefinition("r", "RANK", null, null, orderBy);
        var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().HaveCount(3);
        // Sorted ASC: 1, 2, 3 → ranks 1, 2, 3
        result[0].Values["r"].Value.Should().Be(1);
        result[1].Values["r"].Value.Should().Be(2);
        result[2].Values["r"].Value.Should().Be(3);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "WindowSpoolNodeTests" -v minimal`
Expected: FAIL — `NotSupportedException: Window function 'RANK' is not supported.`

**Step 3: Add RANK and DENSE_RANK to the switch statement and implement compute methods**

In `src/PPDS.Query/Planning/Nodes/WindowSpoolNode.cs`, add cases to the switch at line 280:

```csharp
case "RANK":
    ComputeRank(sortedIndices, allRows, windowDef.OrderBy, windowValues, columnName);
    break;

case "DENSE_RANK":
    ComputeDenseRank(sortedIndices, allRows, windowDef.OrderBy, windowValues, columnName);
    break;
```

Add after the `ComputeRowNumber` method (around line 439):

```csharp
/// <summary>
/// RANK(): 1-based rank within partition. Ties get the same rank;
/// the next rank after a tie skips (1, 2, 2, 4).
/// </summary>
private static void ComputeRank(
    List<int> sortedIndices, List<QueryRow> allRows,
    IReadOnlyList<CompiledOrderByItem>? orderBy,
    Dictionary<string, object?>[] windowValues, string columnName)
{
    if (sortedIndices.Count == 0) return;

    windowValues[sortedIndices[0]][columnName] = 1;

    for (var i = 1; i < sortedIndices.Count; i++)
    {
        if (orderBy != null && orderBy.Count > 0 &&
            CompareRowsByOrderBy(allRows[sortedIndices[i]], allRows[sortedIndices[i - 1]], orderBy) == 0)
        {
            // Tie: same rank as previous
            windowValues[sortedIndices[i]][columnName] = windowValues[sortedIndices[i - 1]][columnName];
        }
        else
        {
            // No tie: rank = position + 1 (1-based)
            windowValues[sortedIndices[i]][columnName] = i + 1;
        }
    }
}

/// <summary>
/// DENSE_RANK(): 1-based rank within partition. Ties get the same rank;
/// the next rank after a tie increments by 1 (1, 2, 2, 3).
/// </summary>
private static void ComputeDenseRank(
    List<int> sortedIndices, List<QueryRow> allRows,
    IReadOnlyList<CompiledOrderByItem>? orderBy,
    Dictionary<string, object?>[] windowValues, string columnName)
{
    if (sortedIndices.Count == 0) return;

    var currentRank = 1;
    windowValues[sortedIndices[0]][columnName] = currentRank;

    for (var i = 1; i < sortedIndices.Count; i++)
    {
        if (orderBy == null || orderBy.Count == 0 ||
            CompareRowsByOrderBy(allRows[sortedIndices[i]], allRows[sortedIndices[i - 1]], orderBy) != 0)
        {
            currentRank++;
        }
        windowValues[sortedIndices[i]][columnName] = currentRank;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Query.Tests --filter "WindowSpoolNodeTests" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/WindowSpoolNode.cs tests/PPDS.Query.Tests/Planning/WindowSpoolNodeTests.cs
git commit -m "feat(query): add RANK and DENSE_RANK to WindowSpoolNode"
```

---

### Task 6: Add CUME_DIST and PERCENT_RANK to WindowSpoolNode

**Files:**
- Modify: `src/PPDS.Query/Planning/Nodes/WindowSpoolNode.cs`
- Modify: `tests/PPDS.Query.Tests/Planning/WindowSpoolNodeTests.cs`

**Depends on:** Task 5 (uses same patterns)

**Context:**
- `CUME_DIST()` = (number of rows with value <= current row's value) / (total rows in partition)
- `PERCENT_RANK()` = (RANK - 1) / (total rows in partition - 1). Returns 0 for a single-row partition.

**Step 1: Write failing tests**

Add to `tests/PPDS.Query.Tests/Planning/WindowSpoolNodeTests.cs`:

```csharp
[Fact]
public async Task CumeDist_ReturnsCorrectDistribution()
{
    // Values: 1, 2, 2, 3 → CUME_DIST: 0.25, 0.75, 0.75, 1.0
    var rows = TestSourceNode.Create("test",
        TestSourceNode.MakeRow("test", ("v", 1)),
        TestSourceNode.MakeRow("test", ("v", 2)),
        TestSourceNode.MakeRow("test", ("v", 2)),
        TestSourceNode.MakeRow("test", ("v", 3)));

    var orderBy = new List<CompiledOrderByItem>
    {
        new("v", r => r.TryGetValue("v", out var qv) ? qv.Value : null, false)
    };

    var windowDef = new ExtendedWindowDefinition("cd", "CUME_DIST", null, null, orderBy);
    var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

    var result = await TestHelpers.CollectRowsAsync(node);

    result.Should().HaveCount(4);
    Convert.ToDouble(result[0].Values["cd"].Value).Should().BeApproximately(0.25, 0.001);
    Convert.ToDouble(result[1].Values["cd"].Value).Should().BeApproximately(0.75, 0.001);
    Convert.ToDouble(result[2].Values["cd"].Value).Should().BeApproximately(0.75, 0.001);
    Convert.ToDouble(result[3].Values["cd"].Value).Should().BeApproximately(1.0, 0.001);
}

[Fact]
public async Task PercentRank_ReturnsCorrectPercentage()
{
    // Values: 1, 2, 2, 3 → RANK: 1, 2, 2, 4 → PERCENT_RANK: 0/3=0, 1/3=0.333, 1/3=0.333, 3/3=1.0
    var rows = TestSourceNode.Create("test",
        TestSourceNode.MakeRow("test", ("v", 1)),
        TestSourceNode.MakeRow("test", ("v", 2)),
        TestSourceNode.MakeRow("test", ("v", 2)),
        TestSourceNode.MakeRow("test", ("v", 3)));

    var orderBy = new List<CompiledOrderByItem>
    {
        new("v", r => r.TryGetValue("v", out var qv) ? qv.Value : null, false)
    };

    var windowDef = new ExtendedWindowDefinition("pr", "PERCENT_RANK", null, null, orderBy);
    var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

    var result = await TestHelpers.CollectRowsAsync(node);

    result.Should().HaveCount(4);
    Convert.ToDouble(result[0].Values["pr"].Value).Should().BeApproximately(0.0, 0.001);
    Convert.ToDouble(result[1].Values["pr"].Value).Should().BeApproximately(0.333, 0.001);
    Convert.ToDouble(result[2].Values["pr"].Value).Should().BeApproximately(0.333, 0.001);
    Convert.ToDouble(result[3].Values["pr"].Value).Should().BeApproximately(1.0, 0.001);
}

[Fact]
public async Task PercentRank_SingleRow_ReturnsZero()
{
    var rows = TestSourceNode.Create("test",
        TestSourceNode.MakeRow("test", ("v", 42)));

    var orderBy = new List<CompiledOrderByItem>
    {
        new("v", r => r.TryGetValue("v", out var qv) ? qv.Value : null, false)
    };

    var windowDef = new ExtendedWindowDefinition("pr", "PERCENT_RANK", null, null, orderBy);
    var node = new WindowSpoolNode(rows, new List<ExtendedWindowDefinition> { windowDef });

    var result = await TestHelpers.CollectRowsAsync(node);

    result.Should().HaveCount(1);
    Convert.ToDouble(result[0].Values["pr"].Value).Should().Be(0.0);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "CumeDist|PercentRank" -v minimal`
Expected: FAIL — `NotSupportedException`

**Step 3: Add cases to the switch and implement**

In `src/PPDS.Query/Planning/Nodes/WindowSpoolNode.cs`, add to the switch:

```csharp
case "CUME_DIST":
    ComputeCumeDist(sortedIndices, allRows, windowDef.OrderBy, windowValues, columnName);
    break;

case "PERCENT_RANK":
    ComputePercentRank(sortedIndices, allRows, windowDef.OrderBy, windowValues, columnName);
    break;
```

Add compute methods:

```csharp
/// <summary>
/// CUME_DIST(): cumulative distribution = (rows with value &lt;= current) / (total rows in partition).
/// </summary>
private static void ComputeCumeDist(
    List<int> sortedIndices, List<QueryRow> allRows,
    IReadOnlyList<CompiledOrderByItem>? orderBy,
    Dictionary<string, object?>[] windowValues, string columnName)
{
    var n = sortedIndices.Count;
    if (n == 0) return;

    // Walk from end to find how many rows share each ORDER BY value
    var i = n - 1;
    while (i >= 0)
    {
        // Find the start of the current tie group
        var groupEnd = i;
        while (i > 0 && orderBy != null && orderBy.Count > 0 &&
               CompareRowsByOrderBy(allRows[sortedIndices[i]], allRows[sortedIndices[i - 1]], orderBy) == 0)
        {
            i--;
        }

        // All rows in this group get CUME_DIST = (groupEnd + 1) / n
        var cumeDist = (double)(groupEnd + 1) / n;
        for (var j = i; j <= groupEnd; j++)
        {
            windowValues[sortedIndices[j]][columnName] = cumeDist;
        }

        i--;
    }
}

/// <summary>
/// PERCENT_RANK(): (RANK - 1) / (total rows in partition - 1). Returns 0 for single-row partitions.
/// </summary>
private static void ComputePercentRank(
    List<int> sortedIndices, List<QueryRow> allRows,
    IReadOnlyList<CompiledOrderByItem>? orderBy,
    Dictionary<string, object?>[] windowValues, string columnName)
{
    var n = sortedIndices.Count;
    if (n == 0) return;

    if (n == 1)
    {
        windowValues[sortedIndices[0]][columnName] = 0.0;
        return;
    }

    // First compute RANK values
    var ranks = new int[n];
    ranks[0] = 1;
    for (var i = 1; i < n; i++)
    {
        if (orderBy != null && orderBy.Count > 0 &&
            CompareRowsByOrderBy(allRows[sortedIndices[i]], allRows[sortedIndices[i - 1]], orderBy) == 0)
        {
            ranks[i] = ranks[i - 1];
        }
        else
        {
            ranks[i] = i + 1;
        }
    }

    // PERCENT_RANK = (rank - 1) / (n - 1)
    for (var i = 0; i < n; i++)
    {
        windowValues[sortedIndices[i]][columnName] = (double)(ranks[i] - 1) / (n - 1);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Query.Tests --filter "WindowSpoolNodeTests" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/WindowSpoolNode.cs tests/PPDS.Query.Tests/Planning/WindowSpoolNodeTests.cs
git commit -m "feat(query): add CUME_DIST and PERCENT_RANK to WindowSpoolNode"
```

---

### Task 7: Surface MergeNode WHEN MATCHED Limitation at Plan Time

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs:1291`
- Modify: `tests/PPDS.Query.Tests/Planning/MergeNodeTests.cs`

**Context:** MergeNode's `hasMatch` is always `false` because target row lookup is not implemented. WHEN MATCHED clauses (UPDATE/DELETE) silently do nothing. Fix: throw `NotSupportedException` at plan time when WHEN MATCHED is specified, so users get a clear error instead of silent data loss.

**Step 1: Write failing test**

Add to `tests/PPDS.Query.Tests/Planning/MergeNodeTests.cs`:

```csharp
[Fact]
public void Plan_MergeWithWhenMatched_ThrowsNotSupportedException()
{
    var parser = new QueryParser();
    var mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
    mockFetchXmlService
        .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
        .Returns(TranspileResult.Simple(
            "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

    var builder = new ExecutionPlanBuilder(mockFetchXmlService.Object);

    var sql = @"
        MERGE INTO account AS target
        USING source_table AS src
        ON target.accountid = src.id
        WHEN MATCHED THEN
            UPDATE SET name = src.name;";

    var fragment = parser.Parse(sql);
    var act = () => builder.Plan(fragment);
    act.Should().Throw<NotSupportedException>()
        .WithMessage("*WHEN MATCHED*not yet supported*");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Plan_MergeWithWhenMatched_ThrowsNotSupportedException" -v minimal`
Expected: FAIL — currently returns a MergeNode

**Step 3: Add guard in PlanMerge**

In `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`, after the `whenMatched` / `whenNotMatched` parsing loop (line ~1289), add before the `new MergeNode(...)` call:

```csharp
if (whenMatched != null)
{
    throw new NotSupportedException(
        "MERGE WHEN MATCHED (UPDATE/DELETE) is not yet supported. " +
        "Target row lookup from Dataverse is required. " +
        "Use WHEN NOT MATCHED (INSERT) only, or use separate UPDATE/DELETE statements.");
}
```

**Step 4: Update existing Plan_MergeStatement_ProducesMergeNode test**

The existing test uses a MERGE with WHEN MATCHED. Update it to only use WHEN NOT MATCHED:

```csharp
[Fact]
public void Plan_MergeStatement_ProducesMergeNode()
{
    var parser = new QueryParser();
    var mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
    mockFetchXmlService
        .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
        .Returns(TranspileResult.Simple(
            "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

    var builder = new ExecutionPlanBuilder(mockFetchXmlService.Object);

    var sql = @"
        MERGE INTO account AS target
        USING source_table AS src
        ON target.accountid = src.id
        WHEN NOT MATCHED THEN
            INSERT (name) VALUES (src.name);";

    var fragment = parser.Parse(sql);
    var result = builder.Plan(fragment);

    result.RootNode.Should().BeOfType<MergeNode>();
    result.EntityLogicalName.Should().Be("account");
}
```

**Step 5: Run all merge tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "MergeNode" -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/PPDS.Query.Tests/Planning/MergeNodeTests.cs
git commit -m "fix(query): throw NotSupportedException for MERGE WHEN MATCHED until target lookup is implemented"
```

---

### Task 8: Add TIMEFROMPARTS to DateFunctions

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Execution/Functions/DateFunctions.cs`
- Test: `tests/PPDS.Query.Tests/Execution/ExpressionCompilerTests.cs` (add test) or create `tests/PPDS.Dataverse.Tests/Query/Execution/Functions/DateFunctionsTests.cs`

**Context:** The v3 plan section 5.6 lists TIMEFROMPARTS as missing. DATEFROMPARTS and DATETIMEFROMPARTS exist. TIMEFROMPARTS(hour, minute, seconds, fractions, precision) returns a `TimeSpan`.

**Step 1: Write failing test**

Add a test in a convenient location (same pattern as existing function tests — test via ExpressionCompiler since that's how functions are invoked at runtime):

```csharp
[Fact]
public void CompileScalar_TimeFromParts_ReturnsTimeSpan()
{
    var compiler = new ExpressionCompiler();
    var expr = ParseExpression("TIMEFROMPARTS(14, 30, 45, 0, 0)");
    var compiled = compiler.CompileScalar(expr);
    var result = compiled(EmptyRow);
    result.Should().BeOfType<TimeSpan>();
    ((TimeSpan)result!).Hours.Should().Be(14);
    ((TimeSpan)result!).Minutes.Should().Be(30);
    ((TimeSpan)result!).Seconds.Should().Be(45);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Query.Tests --filter "TimeFromParts" -v minimal`
Expected: FAIL — function not registered

**Step 3: Implement TimeFromPartsFunction and register it**

In `src/PPDS.Dataverse/Query/Execution/Functions/DateFunctions.cs`, add registration in `RegisterAll`:

```csharp
registry.Register("TIMEFROMPARTS", new TimeFromPartsFunction());
```

Add the class at the end of the file:

```csharp
// ── TIMEFROMPARTS ───────────────────────────────────────────────
/// <summary>
/// TIMEFROMPARTS(hour, minute, seconds, fractions, precision) - constructs a time from parts.
/// </summary>
private sealed class TimeFromPartsFunction : IScalarFunction
{
    public int MinArgs => 5;
    public int MaxArgs => 5;

    public object? Execute(object?[] args)
    {
        if (args[0] is null || args[1] is null || args[2] is null)
            return null;

        var hour = Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
        var minute = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
        var seconds = Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
        var fractions = args[3] is null ? 0 : Convert.ToInt32(args[3], CultureInfo.InvariantCulture);
        var precision = args[4] is null ? 0 : Convert.ToInt32(args[4], CultureInfo.InvariantCulture);

        // Fractions are in units of 10^(-precision) seconds
        var fractionalTicks = precision > 0
            ? (long)(fractions * Math.Pow(10, 7 - precision))
            : 0;

        return new TimeSpan(0, hour, minute, seconds) + TimeSpan.FromTicks(fractionalTicks);
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "TimeFromParts" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add src/PPDS.Dataverse/Query/Execution/Functions/DateFunctions.cs tests/PPDS.Query.Tests/Execution/ExpressionCompilerTests.cs
git commit -m "feat(query): add TIMEFROMPARTS date function"
```

---

### Task 9: Add @@ERROR Tracking to SessionContext

**Files:**
- Modify: `src/PPDS.Query/Execution/SessionContext.cs`
- Modify: `src/PPDS.Query/Planning/Nodes/ScriptExecutionNode.cs` (update TRY/CATCH to set ErrorNumber)
- Test: `tests/PPDS.Query.Tests/Planning/SessionContextTests.cs`

**Context:** Plan section 4.6 specifies `int ErrorNumber` (@@ERROR) on SessionContext. Currently missing. ScriptExecutionNode handles TRY/CATCH but doesn't track @@ERROR. The ExpressionCompiler needs @@ERROR to resolve the `ERROR_NUMBER()` function.

**Step 1: Write failing test**

Add to `tests/PPDS.Query.Tests/Planning/SessionContextTests.cs`:

```csharp
[Fact]
public void ErrorNumber_DefaultsToZero()
{
    var session = new SessionContext();
    session.ErrorNumber.Should().Be(0);
}

[Fact]
public void ErrorNumber_SetAndGet()
{
    var session = new SessionContext();
    session.ErrorNumber = 50000;
    session.ErrorNumber.Should().Be(50000);
}

[Fact]
public void ErrorMessage_DefaultsToEmpty()
{
    var session = new SessionContext();
    session.ErrorMessage.Should().BeEmpty();
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "ErrorNumber|ErrorMessage" -v minimal`
Expected: FAIL — property doesn't exist

**Step 3: Add ErrorNumber and ErrorMessage to SessionContext**

In `src/PPDS.Query/Execution/SessionContext.cs`, add properties:

```csharp
/// <summary>@@ERROR value — the error number from the last statement. 0 means success.</summary>
public int ErrorNumber { get; set; }

/// <summary>ERROR_MESSAGE() value — the error message from the last caught exception.</summary>
public string ErrorMessage { get; set; } = string.Empty;
```

**Step 4: Update ScriptExecutionNode TRY/CATCH to set error state**

In `src/PPDS.Query/Planning/Nodes/ScriptExecutionNode.cs`, in the TRY/CATCH handling, set `session.ErrorNumber` and `session.ErrorMessage` when an exception is caught:

Find the catch block for `TryCatchStatement` and add:

```csharp
// In the catch handler:
session.ErrorNumber = 50000; // Generic user-defined error number
session.ErrorMessage = ex.Message;
```

And reset on successful TRY:
```csharp
session.ErrorNumber = 0;
session.ErrorMessage = string.Empty;
```

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "SessionContext" -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Query/Execution/SessionContext.cs src/PPDS.Query/Planning/Nodes/ScriptExecutionNode.cs tests/PPDS.Query.Tests/Planning/SessionContextTests.cs
git commit -m "feat(query): add @@ERROR and ERROR_MESSAGE() tracking to SessionContext"
```

---

### Task 10: Create OpenJsonNode for OPENJSON Table-Valued Function

**Files:**
- Create: `src/PPDS.Query/Planning/Nodes/OpenJsonNode.cs`
- Test: `tests/PPDS.Query.Tests/Planning/OpenJsonNodeTests.cs`

**Context:** Plan sections 3.1 and 5.4 specify OPENJSON support. OPENJSON shreds a JSON array into rows. Two modes: (1) without WITH clause returns key/value/type columns, (2) with WITH clause returns typed columns per schema. Start with mode 1 (without WITH).

**Step 1: Write failing tests**

Create `tests/PPDS.Query.Tests/Planning/OpenJsonNodeTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class OpenJsonNodeTests
{
    [Fact]
    public async Task ExecuteAsync_JsonArray_ReturnsKeyValueRows()
    {
        var json = "[\"red\",\"green\",\"blue\"]";
        CompiledScalarExpression jsonExpr = _ => json;

        var node = new OpenJsonNode(jsonExpr);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["key"].Value.Should().Be("0");
        rows[0].Values["value"].Value.Should().Be("red");
        rows[0].Values["type"].Value.Should().Be(1); // string type
        rows[1].Values["value"].Value.Should().Be("green");
        rows[2].Values["value"].Value.Should().Be("blue");
    }

    [Fact]
    public async Task ExecuteAsync_JsonObject_ReturnsPropertyRows()
    {
        var json = "{\"name\":\"Contoso\",\"city\":\"Redmond\",\"count\":42}";
        CompiledScalarExpression jsonExpr = _ => json;

        var node = new OpenJsonNode(jsonExpr);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["key"].Value.Should().Be("name");
        rows[0].Values["value"].Value.Should().Be("Contoso");
        rows[1].Values["key"].Value.Should().Be("city");
        rows[2].Values["key"].Value.Should().Be("count");
        rows[2].Values["value"].Value.Should().Be("42");
    }

    [Fact]
    public async Task ExecuteAsync_NullJson_ReturnsEmpty()
    {
        CompiledScalarExpression jsonExpr = _ => null;

        var node = new OpenJsonNode(jsonExpr);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithPath_ExtractsNestedArray()
    {
        var json = "{\"data\":[1,2,3]}";
        CompiledScalarExpression jsonExpr = _ => json;

        var node = new OpenJsonNode(jsonExpr, path: "$.data");
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["value"].Value.Should().Be("1");
    }

    [Fact]
    public void Constructor_NullExpression_Throws()
    {
        var act = () => new OpenJsonNode(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "OpenJsonNode" -v minimal`
Expected: FAIL — class doesn't exist

**Step 3: Implement OpenJsonNode**

Create `src/PPDS.Query/Planning/Nodes/OpenJsonNode.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Plan node for OPENJSON(json_expression [, path]).
/// Shreds a JSON string into rows with key, value, and type columns.
/// </summary>
public sealed class OpenJsonNode : IQueryPlanNode
{
    private readonly CompiledScalarExpression _jsonExpression;
    private readonly string? _path;

    /// <inheritdoc />
    public string Description => _path != null ? $"OpenJson: {_path}" : "OpenJson";

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    public OpenJsonNode(CompiledScalarExpression jsonExpression, string? path = null)
    {
        _jsonExpression = jsonExpression ?? throw new ArgumentNullException(nameof(jsonExpression));
        _path = path;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var jsonValue = _jsonExpression(new Dictionary<string, QueryValue>());
        if (jsonValue is null) yield break;

        var jsonString = jsonValue.ToString();
        if (string.IsNullOrEmpty(jsonString)) yield break;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            yield break;
        }

        // Navigate to path if specified
        var target = root;
        if (_path != null)
        {
            target = NavigatePath(root, _path);
            if (target.ValueKind == JsonValueKind.Undefined) yield break;
        }

        if (target.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var element in target.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return MakeRow(index.ToString(), element);
                index++;
            }
        }
        else if (target.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in target.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return MakeRow(property.Name, property.Value);
            }
        }
    }

    private static QueryRow MakeRow(string key, JsonElement element)
    {
        var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = QueryValue.Simple(key),
            ["value"] = QueryValue.Simple(GetStringValue(element)),
            ["type"] = QueryValue.Simple(GetJsonType(element))
        };
        return new QueryRow(values, "openjson");
    }

    private static string? GetStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.GetRawText(),
            JsonValueKind.Array => element.GetRawText(),
            _ => null
        };
    }

    /// <summary>
    /// Returns the OPENJSON type code: 0=null, 1=string, 2=number, 3=boolean, 4=array, 5=object.
    /// </summary>
    private static int GetJsonType(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.String => 1,
            JsonValueKind.Number => 2,
            JsonValueKind.True or JsonValueKind.False => 3,
            JsonValueKind.Array => 4,
            JsonValueKind.Object => 5,
            _ => 0
        };
    }

    private static JsonElement NavigatePath(JsonElement root, string path)
    {
        // Simple JSON path support: $.prop.prop or $.prop[0]
        var current = root;
        var segments = path.TrimStart('$', '.').Split('.');

        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)) continue;

            if (current.ValueKind == JsonValueKind.Object &&
                current.TryGetProperty(segment, out var child))
            {
                current = child;
            }
            else
            {
                return default; // Undefined
            }
        }

        return current;
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "OpenJsonNode" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/OpenJsonNode.cs tests/PPDS.Query.Tests/Planning/OpenJsonNodeTests.cs
git commit -m "feat(query): add OpenJsonNode for OPENJSON table-valued function"
```

---

### Task 11: Wire OPENJSON into ExecutionPlanBuilder

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`
- Test: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Depends on:** Task 10

**Context:** OPENJSON is typically used with `CROSS APPLY OPENJSON(column) AS j`. The ExecutionPlanBuilder needs to detect `OPENJSON` in a `FROM` clause or `CROSS APPLY` and route to `OpenJsonNode`. Since CROSS APPLY integration is complex, start by supporting the standalone form: `SELECT * FROM OPENJSON(@json)`.

**Step 1: Write failing test**

Add to `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`:

```csharp
[Fact]
public void Plan_OpenJson_ProducesOpenJsonNode()
{
    var fragment = _parser.Parse("SELECT [key], value, type FROM OPENJSON('[1,2,3]')");
    var result = _builder.Plan(fragment);
    result.RootNode.Should().Match<IQueryPlanNode>(n =>
        n is OpenJsonNode || n.Children.Any(c => c is OpenJsonNode));
}
```

Add required using: `using PPDS.Query.Planning.Nodes;`

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Plan_OpenJson" -v minimal`
Expected: FAIL — OPENJSON not recognized as a table source

**Step 3: Add OPENJSON detection in ExecutionPlanBuilder**

In `ExecutionPlanBuilder.cs`, find the table-valued function routing in `PlanSelect` (around the `TryPlanTableValuedFunction` call). Add OPENJSON detection alongside STRING_SPLIT:

In `TryPlanTableValuedFunction` method, add a case for OPENJSON:

```csharp
if (string.Equals(funcName, "OPENJSON", StringComparison.OrdinalIgnoreCase))
{
    return PlanOpenJson(funcCall, querySpec, options);
}
```

Implement `PlanOpenJson`:

```csharp
private QueryPlanResult PlanOpenJson(
    SchemaObjectFunctionTableReference funcRef,
    QuerySpecification querySpec,
    QueryPlanOptions options)
{
    var parameters = funcRef.Parameters;
    if (parameters == null || parameters.Count == 0)
        throw new QueryParseException("OPENJSON requires at least one argument.");

    var jsonExpr = _expressionCompiler.CompileScalar(parameters[0]);
    string? path = null;

    if (parameters.Count > 1 && parameters[1] is StringLiteral pathLit)
    {
        path = pathLit.Value;
    }

    IQueryPlanNode node = new OpenJsonNode(jsonExpr, path);

    // Apply WHERE if present
    if (querySpec.WhereClause?.SearchCondition != null)
    {
        var predicate = _expressionCompiler.CompilePredicate(querySpec.WhereClause.SearchCondition);
        var description = querySpec.WhereClause.SearchCondition.ToString() ?? "filter";
        node = new ClientFilterNode(node, predicate, description);
    }

    return new QueryPlanResult
    {
        RootNode = node,
        FetchXml = "-- OPENJSON",
        VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
        EntityLogicalName = "openjson"
    };
}
```

Add `using PPDS.Query.Planning.Nodes;` if not present. Add `using Microsoft.SqlServer.TransactSql.ScriptDom;` if needed for `StringLiteral`.

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Plan_OpenJson|OpenJsonNode" -v minimal`
Expected: ALL PASS

**Step 5: Run full test suite**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Category!=Integration" -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "feat(query): wire OPENJSON into ExecutionPlanBuilder"
```

---

## Summary

| Task | What | Type | Effort |
|------|------|------|--------|
| 1 | Fix stale XML doc crefs | Cleanup | Tiny |
| 2 | Fix parameter name overlap | Bug fix | Small |
| 3 | ExecuteMessageNode → NotSupportedException | Bug fix | Small |
| 4 | ImpersonationNode → NotSupportedException | Bug fix | Small |
| 5 | Add RANK/DENSE_RANK to WindowSpoolNode | Feature | Medium |
| 6 | Add CUME_DIST/PERCENT_RANK to WindowSpoolNode | Feature | Medium |
| 7 | Surface MERGE WHEN MATCHED limitation | Bug fix | Small |
| 8 | Add TIMEFROMPARTS function | Feature | Small |
| 9 | Add @@ERROR tracking to SessionContext | Feature | Small |
| 10 | Create OpenJsonNode | Feature | Medium |
| 11 | Wire OPENJSON into ExecutionPlanBuilder | Feature | Medium |

## Justified Deviations (Not Fixed)

These items were flagged as gaps but are **architecturally justified**:

| Gap | Reason Not Fixed |
|-----|-----------------|
| ExecutionPlanOptimizer passes are no-ops | Code comments explain: compiled predicates cannot be inspected. Pushdown happens at plan-build time. Sort elimination has no SortNode yet. This is correct. |
| Aggregate strategy nodes (Hash/Stream/Partitioned) not in PPDS.Query | Existing nodes in PPDS.Dataverse (ClientAggregateNode, MergeAggregateNode, ParallelPartitionNode, AdaptiveAggregateScanNode) cover the same functionality under different names. |
| Functions remain in PPDS.Dataverse | PPDS.Query depends on PPDS.Dataverse, so the direction is correct. Moving functions would be a large refactor with no behavioral change. |
| Visitors directory not created | AST transforms are inlined in ExecutionPlanBuilder. Separate visitor classes would add abstraction with no functional benefit at current scale. |
| Types/Safety directories empty | Placeholder directories for future migration. No behavioral impact. |
| FetchXmlConditionMapper not separate | Condition mapping is inlined in FetchXmlGenerator. Extracting would add complexity with no benefit. |
| COLLATE not implemented | COLLATE is a clause modifier requiring collation-aware string comparison throughout the engine. P3 scope — not a function to register. |
| SortNode not separate | Sorts are pushed into FetchXML at plan time. Correct for Dataverse where server-side sort is free. |
| Script nodes consolidated | IF/WHILE/DECLARE/SET handled by single ScriptExecutionNode. Cleaner than separate nodes needing complex wiring. |
| SessionContext.Variables | Variables are managed by VariableScope in ExpressionCompiler. Different location than plan, same functionality. |
