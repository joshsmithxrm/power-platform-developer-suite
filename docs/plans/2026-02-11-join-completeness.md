# JOIN Completeness Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add RIGHT JOIN, FULL OUTER JOIN, CROSS JOIN, and CROSS APPLY/OUTER APPLY to the query engine, enabling client-side join execution for cases FetchXML cannot handle.

**Architecture:** Extend the existing `JoinType` enum and all three join nodes (Hash, NestedLoop, Merge) to support Right, FullOuter, and Cross. For RIGHT JOIN, use a planning-time swap to LEFT JOIN (no runtime changes). For FULL OUTER, add right-side tracking to all nodes. For CROSS JOIN, add a predicate-free NestedLoop path. For APPLY, add correlated re-evaluation to NestedLoopJoinNode. Fix FetchXmlGenerator to reject unsupported join types instead of silently generating incorrect FetchXML.

**Tech Stack:** C# (.NET 8/9/10), Microsoft.SqlServer.TransactSql.ScriptDom, xUnit, FluentAssertions

**Dependency chain:**
```
Task 1: JoinType enum extension (independent)
Task 2: Fix FetchXmlGenerator MapJoinType (independent)
Task 3 ──► Task 4 ──► Task 5 (NestedLoop gets Cross, then Right/FullOuter propagate to Hash+Merge)
Task 6: CROSS APPLY/OUTER APPLY (depends on Task 3 for NestedLoop correlated mode)
Task 7: Wire client-side joins into ExecutionPlanBuilder (depends on Tasks 3-5)
Task 8: End-to-end planning tests (depends on Task 7)
```

---

### Task 1: Extend JoinType Enum

**Files:**
- Modify: `src/PPDS.Query/Planning/JoinType.cs`

**Step 1: Add new enum values**

Replace `src/PPDS.Query/Planning/JoinType.cs`:

```csharp
namespace PPDS.Query.Planning;

/// <summary>Specifies the logical join type for client-side join nodes.</summary>
public enum JoinType
{
    /// <summary>INNER JOIN: only matching rows from both sides.</summary>
    Inner,

    /// <summary>LEFT JOIN: all rows from left side, matching rows from right (or nulls).</summary>
    Left,

    /// <summary>RIGHT JOIN: all rows from right side, matching rows from left (or nulls).</summary>
    Right,

    /// <summary>FULL OUTER JOIN: all rows from both sides (nulls where no match).</summary>
    FullOuter,

    /// <summary>CROSS JOIN: Cartesian product of both sides (no predicate).</summary>
    Cross
}
```

**Step 2: Build to verify**

Run: `dotnet build src/PPDS.Query`
Expected: zero errors (existing code only references `JoinType.Inner` and `JoinType.Left`, so adding values doesn't break anything)

**Step 3: Commit**

```bash
git add src/PPDS.Query/Planning/JoinType.cs
git commit -m "feat(query): extend JoinType enum with Right, FullOuter, Cross"
```

---

### Task 2: Fix FetchXmlGenerator MapJoinType to Reject Unsupported Types

**Files:**
- Modify: `src/PPDS.Query/Transpilation/FetchXmlGenerator.cs:308-315`
- Test: `tests/PPDS.Query.Tests/Transpilation/FetchXmlGeneratorTests.cs`

**Context:** `MapJoinType` currently maps RIGHT OUTER and FULL OUTER to `"outer"` (LEFT JOIN), producing silently incorrect FetchXML. FetchXML only supports `inner` and `outer` (LEFT). Fix: throw `NotSupportedException` for RIGHT and FULL OUTER at the FetchXML level — the planner will route these to client-side join nodes instead.

**Step 1: Write failing tests**

Add to `tests/PPDS.Query.Tests/Transpilation/FetchXmlGeneratorTests.cs`:

```csharp
[Fact]
public void Generate_RightJoin_ThrowsNotSupportedException()
{
    var sql = "SELECT a.name, c.fullname FROM account a RIGHT JOIN contact c ON a.accountid = c.parentcustomerid";
    var act = () => _generator.Generate(_parser.Parse(sql));
    act.Should().Throw<NotSupportedException>()
        .WithMessage("*RIGHT*client-side*");
}

[Fact]
public void Generate_FullOuterJoin_ThrowsNotSupportedException()
{
    var sql = "SELECT a.name, c.fullname FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid";
    var act = () => _generator.Generate(_parser.Parse(sql));
    act.Should().Throw<NotSupportedException>()
        .WithMessage("*FULL OUTER*client-side*");
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "RightJoin_Throws|FullOuterJoin_Throws" -v minimal`
Expected: FAIL — currently returns FetchXML without error

**Step 3: Fix MapJoinType**

In `src/PPDS.Query/Transpilation/FetchXmlGenerator.cs`, replace the `MapJoinType` method (around line 308):

```csharp
private static string MapJoinType(QualifiedJoinType joinType) => joinType switch
{
    QualifiedJoinType.Inner => "inner",
    QualifiedJoinType.LeftOuter => "outer",
    QualifiedJoinType.RightOuter => throw new NotSupportedException(
        "RIGHT JOIN cannot be transpiled to FetchXML. Use client-side join execution."),
    QualifiedJoinType.FullOuter => throw new NotSupportedException(
        "FULL OUTER JOIN cannot be transpiled to FetchXML. Use client-side join execution."),
    _ => "inner"
};
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Query.Tests --filter "FetchXmlGenerator" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Transpilation/FetchXmlGenerator.cs tests/PPDS.Query.Tests/Transpilation/FetchXmlGeneratorTests.cs
git commit -m "fix(query): reject RIGHT/FULL OUTER JOIN in FetchXML transpilation"
```

---

### Task 3: Add Right, FullOuter, and Cross to NestedLoopJoinNode

**Files:**
- Modify: `src/PPDS.Query/Planning/Nodes/NestedLoopJoinNode.cs`
- Modify: `tests/PPDS.Query.Tests/Planning/NestedLoopJoinNodeTests.cs`

**Context:** NestedLoopJoinNode is the most general join operator and the natural home for CROSS JOIN (no predicate) and CROSS APPLY (correlated). Currently supports Inner and Left only. The key changes: (1) CROSS JOIN removes the key-match check, (2) RIGHT JOIN tracks matched inner rows, (3) FULL OUTER combines Left + Right tracking.

**Step 1: Write failing tests for new join types**

Add to `tests/PPDS.Query.Tests/Planning/NestedLoopJoinNodeTests.cs`:

```csharp
[Fact]
public async Task CrossJoin_ProducesCartesianProduct()
{
    var left = TestSourceNode.Create("a",
        TestSourceNode.MakeRow("a", ("x", 1)),
        TestSourceNode.MakeRow("a", ("x", 2)));
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("y", "A")),
        TestSourceNode.MakeRow("b", ("y", "B")),
        TestSourceNode.MakeRow("b", ("y", "C")));

    var join = new NestedLoopJoinNode(left, right, null, null, JoinType.Cross);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().HaveCount(6); // 2 × 3
    rows.Select(r => $"{r.Values["x"].Value}-{r.Values["y"].Value}")
        .Should().BeEquivalentTo("1-A", "1-B", "1-C", "2-A", "2-B", "2-C");
}

[Fact]
public async Task RightJoin_PreservesAllRightRows()
{
    var left = TestSourceNode.Create("a",
        TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")));
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
        TestSourceNode.MakeRow("b", ("aid", 99), ("val", "Y")));

    var join = new NestedLoopJoinNode(left, right, "id", "aid", JoinType.Right);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().HaveCount(2);
    rows[0].Values["name"].Value.Should().Be("Contoso");
    rows[0].Values["val"].Value.Should().Be("X");
    rows[1].Values.TryGetValue("name", out var nameVal);
    (nameVal?.Value).Should().BeNull(); // Left side is null for unmatched right row
    rows[1].Values["val"].Value.Should().Be("Y");
}

[Fact]
public async Task FullOuterJoin_PreservesAllRows()
{
    var left = TestSourceNode.Create("a",
        TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")),
        TestSourceNode.MakeRow("a", ("id", 2), ("name", "Fabrikam")));
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
        TestSourceNode.MakeRow("b", ("aid", 3), ("val", "Z")));

    var join = new NestedLoopJoinNode(left, right, "id", "aid", JoinType.FullOuter);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().HaveCount(3);
    // Row 1: id=1 matched
    rows.Should().Contain(r => r.Values["name"].Value!.Equals("Contoso")
        && r.Values["val"].Value!.Equals("X"));
    // Row 2: id=2 unmatched left (val is null)
    rows.Should().Contain(r => r.Values["name"].Value!.Equals("Fabrikam")
        && (r.Values.TryGetValue("val", out var v) && v.Value == null));
    // Row 3: aid=3 unmatched right (name is null)
    rows.Should().Contain(r => r.Values["val"].Value!.Equals("Z")
        && (r.Values.TryGetValue("name", out var v2) && v2.Value == null));
}

[Fact]
public async Task CrossJoin_EmptyLeft_ReturnsEmpty()
{
    var left = TestSourceNode.Create("a");
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("y", 1)));

    var join = new NestedLoopJoinNode(left, right, null, null, JoinType.Cross);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().BeEmpty();
}

[Fact]
public async Task RightJoin_NoMatches_AllRightRowsWithNulls()
{
    var left = TestSourceNode.Create("a",
        TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")));
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("aid", 99), ("val", "X")));

    var join = new NestedLoopJoinNode(left, right, "id", "aid", JoinType.Right);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().HaveCount(1);
    rows[0].Values["val"].Value.Should().Be("X");
    rows[0].Values.TryGetValue("name", out var nameVal);
    (nameVal?.Value).Should().BeNull();
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "CrossJoin|RightJoin_Preserves|FullOuterJoin" -v minimal`
Expected: FAIL — new join types not handled

**Step 3: Update NestedLoopJoinNode constructor to accept nullable keys**

In `src/PPDS.Query/Planning/Nodes/NestedLoopJoinNode.cs`, update the constructor to allow null key columns (for CROSS JOIN):

```csharp
public NestedLoopJoinNode(
    IQueryPlanNode left,
    IQueryPlanNode right,
    string? leftKeyColumn,
    string? rightKeyColumn,
    JoinType joinType = JoinType.Inner)
{
    Left = left ?? throw new ArgumentNullException(nameof(left));
    Right = right ?? throw new ArgumentNullException(nameof(right));
    LeftKeyColumn = leftKeyColumn;
    RightKeyColumn = rightKeyColumn;
    JoinType = joinType;

    if (joinType != JoinType.Cross && (leftKeyColumn is null || rightKeyColumn is null))
        throw new ArgumentException("Key columns are required for non-CROSS joins.");
}
```

**Step 4: Rewrite ExecuteAsync to handle all join types**

Replace the `ExecuteAsync` method:

```csharp
public async IAsyncEnumerable<QueryRow> ExecuteAsync(
    QueryPlanContext context,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    // Phase 1: Materialize inner (right) side
    var innerRows = new List<QueryRow>();
    QueryRow? rightTemplate = null;
    await foreach (var row in Right.ExecuteAsync(context, cancellationToken))
    {
        innerRows.Add(row);
        rightTemplate ??= row;
    }

    if (innerRows.Count == 0 && JoinType is JoinType.Inner or JoinType.Cross)
        yield break;

    // Track which inner rows have been matched (for Right and FullOuter)
    var innerMatched = (JoinType is JoinType.Right or JoinType.FullOuter)
        ? new bool[innerRows.Count]
        : null;

    // Build a left-side null template (for Right join unmatched rows)
    QueryRow? leftTemplate = null;

    // Phase 2: For each outer (left) row, scan inner rows
    await foreach (var outerRow in Left.ExecuteAsync(context, cancellationToken))
    {
        cancellationToken.ThrowIfCancellationRequested();
        leftTemplate ??= outerRow;
        var matched = false;

        for (var i = 0; i < innerRows.Count; i++)
        {
            var innerRow = innerRows[i];

            if (JoinType == JoinType.Cross || KeysMatch(outerRow, innerRow))
            {
                matched = true;
                if (innerMatched != null) innerMatched[i] = true;
                yield return CombineRows(outerRow, innerRow);
            }
        }

        // LEFT or FULL OUTER: emit unmatched left row with nulls
        if (!matched && JoinType is JoinType.Left or JoinType.FullOuter)
        {
            if (rightTemplate != null)
                yield return CombineWithNulls(outerRow, rightTemplate);
        }
    }

    // RIGHT or FULL OUTER: emit unmatched right rows with nulls
    if (innerMatched != null && leftTemplate != null)
    {
        for (var i = 0; i < innerRows.Count; i++)
        {
            if (!innerMatched[i])
            {
                yield return CombineWithNullsReversed(leftTemplate, innerRows[i]);
            }
        }
    }
}
```

**Step 5: Add CombineWithNullsReversed helper**

Add after the existing `CombineWithNulls` method:

```csharp
/// <summary>
/// Combines a null-filled left template with an actual right row.
/// Used for RIGHT and FULL OUTER JOIN unmatched right-side rows.
/// </summary>
private static QueryRow CombineWithNullsReversed(QueryRow leftTemplate, QueryRow rightRow)
{
    var combined = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

    // Left side: all nulls
    foreach (var kvp in leftTemplate.Values)
        combined[kvp.Key] = QueryValue.Simple(null);

    // Right side: actual values
    foreach (var kvp in rightRow.Values)
    {
        if (combined.ContainsKey(kvp.Key))
            combined[rightRow.EntityLogicalName + "." + kvp.Key] = kvp.Value;
        else
            combined[kvp.Key] = kvp.Value;
    }

    return new QueryRow(combined, rightRow.EntityLogicalName);
}
```

**Step 6: Update Description property**

Update the `Description` getter to handle nullable key columns:

```csharp
public string Description => JoinType == JoinType.Cross
    ? $"NestedLoopJoin: CROSS JOIN"
    : $"NestedLoopJoin: {Left.Description} {JoinType} {Right.Description} ON {LeftKeyColumn} = {RightKeyColumn}";
```

**Step 7: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Query.Tests --filter "NestedLoopJoinNode" -v minimal`
Expected: ALL PASS (existing + new tests)

**Step 8: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/NestedLoopJoinNode.cs tests/PPDS.Query.Tests/Planning/NestedLoopJoinNodeTests.cs
git commit -m "feat(query): add Right, FullOuter, Cross support to NestedLoopJoinNode"
```

---

### Task 4: Add Right and FullOuter to HashJoinNode

**Files:**
- Modify: `src/PPDS.Query/Planning/Nodes/HashJoinNode.cs`
- Modify: `tests/PPDS.Query.Tests/Planning/HashJoinNodeTests.cs`

**Step 1: Write failing tests**

Add to `tests/PPDS.Query.Tests/Planning/HashJoinNodeTests.cs`:

```csharp
[Fact]
public async Task RightJoin_PreservesAllBuildSideRows()
{
    var left = TestSourceNode.Create("a",
        TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")));
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
        TestSourceNode.MakeRow("b", ("aid", 99), ("val", "Y")));

    var join = new HashJoinNode(left, right, "id", "aid", JoinType.Right);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().HaveCount(2);
    rows.Should().Contain(r => r.Values["val"].Value!.Equals("X")
        && r.Values["name"].Value!.Equals("Contoso"));
    rows.Should().Contain(r => r.Values["val"].Value!.Equals("Y")
        && (r.Values.TryGetValue("name", out var v) && v.Value == null));
}

[Fact]
public async Task FullOuterJoin_PreservesAllRows()
{
    var left = TestSourceNode.Create("a",
        TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")),
        TestSourceNode.MakeRow("a", ("id", 2), ("name", "Fabrikam")));
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
        TestSourceNode.MakeRow("b", ("aid", 3), ("val", "Z")));

    var join = new HashJoinNode(left, right, "id", "aid", JoinType.FullOuter);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().HaveCount(3);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "HashJoinNode" --filter "RightJoin|FullOuter" -v minimal`
Expected: FAIL

**Step 3: Update HashJoinNode.ExecuteAsync**

In `src/PPDS.Query/Planning/Nodes/HashJoinNode.cs`, update `ExecuteAsync` to track matched build-side rows and emit unmatched rows for Right/FullOuter:

```csharp
public async IAsyncEnumerable<QueryRow> ExecuteAsync(
    QueryPlanContext context,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    // Phase 1: Build hash table from right (build) side
    var hashTable = new Dictionary<string, List<(QueryRow row, int index)>>(StringComparer.OrdinalIgnoreCase);
    QueryRow? rightTemplate = null;
    var buildRowCount = 0;

    await foreach (var row in Right.ExecuteAsync(context, cancellationToken))
    {
        rightTemplate ??= row;
        var key = NormalizeKey(row, RightKeyColumn);
        if (!hashTable.TryGetValue(key, out var bucket))
        {
            bucket = new List<(QueryRow, int)>();
            hashTable[key] = bucket;
        }
        bucket.Add((row, buildRowCount));
        buildRowCount++;
    }

    if (buildRowCount == 0 && JoinType == JoinType.Inner)
        yield break;

    // Track matched build-side rows (for Right and FullOuter)
    var buildMatched = (JoinType is JoinType.Right or JoinType.FullOuter)
        ? new bool[buildRowCount]
        : null;

    QueryRow? leftTemplate = null;

    // Phase 2: Probe with left (probe) side
    await foreach (var probeRow in Left.ExecuteAsync(context, cancellationToken))
    {
        cancellationToken.ThrowIfCancellationRequested();
        leftTemplate ??= probeRow;
        var key = NormalizeKey(probeRow, LeftKeyColumn);
        var matched = false;

        if (hashTable.TryGetValue(key, out var bucket))
        {
            matched = true;
            foreach (var (buildRow, buildIndex) in bucket)
            {
                if (buildMatched != null) buildMatched[buildIndex] = true;
                yield return NestedLoopJoinNode.CombineRows(probeRow, buildRow);
            }
        }

        // LEFT or FULL OUTER: emit unmatched probe row with nulls
        if (!matched && JoinType is JoinType.Left or JoinType.FullOuter)
        {
            if (rightTemplate != null)
                yield return CombineWithNulls(probeRow, rightTemplate);
        }
    }

    // RIGHT or FULL OUTER: emit unmatched build-side rows
    if (buildMatched != null && leftTemplate != null)
    {
        foreach (var bucket in hashTable.Values)
        {
            foreach (var (buildRow, buildIndex) in bucket)
            {
                if (!buildMatched[buildIndex])
                {
                    yield return NestedLoopJoinNode.CombineWithNullsReversed(leftTemplate, buildRow);
                }
            }
        }
    }
}
```

Note: Make `CombineWithNullsReversed` in NestedLoopJoinNode `internal static` so HashJoinNode can reuse it.

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "HashJoinNode" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/HashJoinNode.cs src/PPDS.Query/Planning/Nodes/NestedLoopJoinNode.cs tests/PPDS.Query.Tests/Planning/HashJoinNodeTests.cs
git commit -m "feat(query): add Right and FullOuter support to HashJoinNode"
```

---

### Task 5: Add Right and FullOuter to MergeJoinNode

**Files:**
- Modify: `src/PPDS.Query/Planning/Nodes/MergeJoinNode.cs`
- Modify: `tests/PPDS.Query.Tests/Planning/MergeJoinNodeTests.cs`

**Step 1: Write failing tests**

Add to `tests/PPDS.Query.Tests/Planning/MergeJoinNodeTests.cs`:

```csharp
[Fact]
public async Task RightJoin_PreservesAllRightRows()
{
    // Both sides must be sorted on join key
    var left = TestSourceNode.Create("a",
        TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")));
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
        TestSourceNode.MakeRow("b", ("aid", 99), ("val", "Y")));

    var join = new MergeJoinNode(left, right, "id", "aid", JoinType.Right);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().HaveCount(2);
    rows.Should().Contain(r => r.Values["val"].Value!.Equals("Y"));
}

[Fact]
public async Task FullOuterJoin_PreservesAllRows()
{
    var left = TestSourceNode.Create("a",
        TestSourceNode.MakeRow("a", ("id", 1), ("name", "Contoso")),
        TestSourceNode.MakeRow("a", ("id", 2), ("name", "Fabrikam")));
    var right = TestSourceNode.Create("b",
        TestSourceNode.MakeRow("b", ("aid", 1), ("val", "X")),
        TestSourceNode.MakeRow("b", ("aid", 3), ("val", "Z")));

    var join = new MergeJoinNode(left, right, "id", "aid", JoinType.FullOuter);
    var rows = await TestHelpers.CollectRowsAsync(join);

    rows.Should().HaveCount(3);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "MergeJoinNode" --filter "RightJoin|FullOuter" -v minimal`
Expected: FAIL

**Step 3: Update MergeJoinNode.ExecuteAsync**

The merge join algorithm walks two sorted lists with pointers. Update to handle Right and FullOuter:

- When left key < right key and join is **Right** or **FullOuter**: skip left (don't emit). For **FullOuter** and **Left**: emit left with nulls.
- When left key > right key and join is **Right** or **FullOuter**: emit right with nulls.
- When right exhausted and join is **Left** or **FullOuter**: emit remaining left with nulls.
- When left exhausted and join is **Right** or **FullOuter**: emit remaining right with nulls.

Replace the `ExecuteAsync` method body with the updated logic. The core merge walk structure remains the same — add emit paths for the new join types at each comparison branch.

Key changes to each branch of the merge walk:

```csharp
// LEFT KEY < RIGHT KEY:
if (JoinType is JoinType.Left or JoinType.FullOuter)
    yield return CombineWithNulls(leftRows[li], rightTemplate!);
li++;

// LEFT KEY > RIGHT KEY:
if (JoinType is JoinType.Right or JoinType.FullOuter)
    yield return CombineWithNullsReversed(leftTemplate!, rightRows[ri]);
ri++;

// RIGHT EXHAUSTED (after main loop):
if (JoinType is JoinType.Left or JoinType.FullOuter)
    // Emit remaining left rows with nulls

// LEFT EXHAUSTED (after main loop):
if (JoinType is JoinType.Right or JoinType.FullOuter)
    // Emit remaining right rows with nulls
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "MergeJoinNode" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/MergeJoinNode.cs tests/PPDS.Query.Tests/Planning/MergeJoinNodeTests.cs
git commit -m "feat(query): add Right and FullOuter support to MergeJoinNode"
```

---

### Task 6: Add CROSS APPLY and OUTER APPLY to NestedLoopJoinNode

**Files:**
- Modify: `src/PPDS.Query/Planning/JoinType.cs` (add `CrossApply`, `OuterApply`)
- Modify: `src/PPDS.Query/Planning/Nodes/NestedLoopJoinNode.cs`
- Create: `tests/PPDS.Query.Tests/Planning/ApplyJoinTests.cs`

**Context:** APPLY is a correlated join — the inner side is re-evaluated per outer row. The inner plan node receives the current outer row as context. OUTER APPLY emits nulls when inner produces no rows (like LEFT JOIN).

**Step 1: Extend JoinType with CrossApply and OuterApply**

Add to `JoinType.cs`:

```csharp
/// <summary>CROSS APPLY: inner side re-evaluated per outer row, only matching rows.</summary>
CrossApply,

/// <summary>OUTER APPLY: inner side re-evaluated per outer row, nulls when inner is empty.</summary>
OuterApply
```

**Step 2: Write failing tests**

Create `tests/PPDS.Query.Tests/Planning/ApplyJoinTests.cs`:

```csharp
using FluentAssertions;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ApplyJoinTests
{
    [Fact]
    public async Task CrossApply_ReEvaluatesInnerPerOuterRow()
    {
        // Outer: two rows with different JSON
        var outer = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("data", "[\"x\",\"y\"]")),
            TestSourceNode.MakeRow("a", ("id", 2), ("data", "[\"z\"]")));

        // Inner: OpenJsonNode that reads "data" from outer row context
        // For APPLY tests, we use a CorrelatedSourceNode that produces
        // different results based on the outer row
        var innerFactory = (QueryRow outerRow) =>
        {
            var json = outerRow.Values["data"].Value?.ToString() ?? "[]";
            var items = System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
            return items.Select((item, i) =>
                TestSourceNode.MakeRow("j", ("key", i.ToString()), ("value", item)));
        };

        var join = new NestedLoopJoinNode(
            outer,
            correlatedInnerFactory: innerFactory,
            joinType: JoinType.CrossApply);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(3); // 2 from first outer row + 1 from second
        rows[0].Values["id"].Value.Should().Be(1);
        rows[0].Values["value"].Value.Should().Be("x");
        rows[2].Values["id"].Value.Should().Be(2);
        rows[2].Values["value"].Value.Should().Be("z");
    }

    [Fact]
    public async Task OuterApply_EmitsNullsWhenInnerEmpty()
    {
        var outer = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("data", "[\"x\"]")),
            TestSourceNode.MakeRow("a", ("id", 2), ("data", "[]")));

        var innerFactory = (QueryRow outerRow) =>
        {
            var json = outerRow.Values["data"].Value?.ToString() ?? "[]";
            var items = System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
            return items.Select((item, i) =>
                TestSourceNode.MakeRow("j", ("key", i.ToString()), ("value", item)));
        };

        var join = new NestedLoopJoinNode(
            outer,
            correlatedInnerFactory: innerFactory,
            joinType: JoinType.OuterApply);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(2);
        rows[0].Values["value"].Value.Should().Be("x");
        rows[1].Values["id"].Value.Should().Be(2);
        rows[1].Values.TryGetValue("value", out var val);
        (val?.Value).Should().BeNull(); // OUTER APPLY emits nulls
    }
}
```

**Step 3: Add correlated constructor overload to NestedLoopJoinNode**

Add a second constructor for APPLY mode:

```csharp
private readonly Func<QueryRow, IEnumerable<QueryRow>>? _correlatedInnerFactory;

/// <summary>
/// Creates a correlated (APPLY) nested loop join.
/// The inner factory is invoked per outer row to produce the correlated inner rows.
/// </summary>
public NestedLoopJoinNode(
    IQueryPlanNode left,
    Func<QueryRow, IEnumerable<QueryRow>> correlatedInnerFactory,
    JoinType joinType)
{
    Left = left ?? throw new ArgumentNullException(nameof(left));
    Right = null!; // Not used in APPLY mode
    _correlatedInnerFactory = correlatedInnerFactory ?? throw new ArgumentNullException(nameof(correlatedInnerFactory));
    LeftKeyColumn = null;
    RightKeyColumn = null;
    JoinType = joinType;

    if (joinType is not (JoinType.CrossApply or JoinType.OuterApply))
        throw new ArgumentException("Correlated constructor requires CrossApply or OuterApply join type.");
}
```

**Step 4: Update ExecuteAsync for APPLY mode**

Add an early branch at the top of `ExecuteAsync`:

```csharp
if (_correlatedInnerFactory != null)
{
    await foreach (var outerRow in Left.ExecuteAsync(context, cancellationToken))
    {
        cancellationToken.ThrowIfCancellationRequested();
        var innerRows = _correlatedInnerFactory(outerRow).ToList();

        if (innerRows.Count > 0)
        {
            foreach (var innerRow in innerRows)
            {
                yield return CombineRows(outerRow, innerRow);
            }
        }
        else if (JoinType == JoinType.OuterApply)
        {
            // Need a template for null columns — use first inner row or empty
            if (innerRows.Count == 0)
            {
                // Emit outer row only (no inner columns to null-fill without a template)
                yield return outerRow;
            }
        }
    }
    yield break;
}
```

Note: The null-filling for OUTER APPLY when no inner rows exist requires knowing the inner schema. A practical approach is to use the first successful inner invocation as a template, or accept that the row will only have outer columns when no inner rows exist (which matches SQL Server behavior for `OUTER APPLY` with no matching rows — the inner columns are simply NULL).

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "ApplyJoin" -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Query/Planning/JoinType.cs src/PPDS.Query/Planning/Nodes/NestedLoopJoinNode.cs tests/PPDS.Query.Tests/Planning/ApplyJoinTests.cs
git commit -m "feat(query): add CROSS APPLY and OUTER APPLY to NestedLoopJoinNode"
```

---

### Task 7: Wire Client-Side Joins into ExecutionPlanBuilder

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`
- Modify: `src/PPDS.Query/Transpilation/FetchXmlGenerator.cs`

**Context:** Currently ALL joins go through FetchXML transpilation. After Task 2, RIGHT and FULL OUTER throw `NotSupportedException` in FetchXmlGenerator. ExecutionPlanBuilder needs a fallback: when FetchXML transpilation fails for a join, plan a client-side join instead.

The strategy:
1. Try FetchXML transpilation first (pushes work to Dataverse — optimal)
2. If transpilation throws `NotSupportedException`, fall back to client-side join planning
3. Client-side planning: plan each table independently, then join with the appropriate node

**Step 1: Add client-side join planning method to ExecutionPlanBuilder**

Add after `PlanSelect` (around line 260):

```csharp
/// <summary>
/// Plans a query with joins that cannot be transpiled to FetchXML.
/// Each table is scanned independently and joined client-side.
/// </summary>
private QueryPlanResult PlanClientSideJoin(
    SelectStatement selectStmt,
    QuerySpecification querySpec,
    QueryPlanOptions options)
{
    var fromClause = querySpec.FromClause;
    if (fromClause?.TableReferences.Count != 1 || fromClause.TableReferences[0] is not QualifiedJoin rootJoin)
        throw new QueryParseException("Expected a qualified join in FROM clause.");

    // Recursively build the join tree
    var (node, entityName) = PlanJoinTree(rootJoin, querySpec, options);

    // Apply WHERE filter (client-side)
    if (querySpec.WhereClause?.SearchCondition != null)
    {
        var predicate = _expressionCompiler.CompilePredicate(querySpec.WhereClause.SearchCondition);
        var description = querySpec.WhereClause.SearchCondition.ToString() ?? "filter";
        node = new ClientFilterNode(node, predicate, description);
    }

    // Apply SELECT projection
    node = ApplyProjection(node, querySpec);

    return new QueryPlanResult
    {
        RootNode = node,
        FetchXml = "-- client-side join",
        VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
        EntityLogicalName = entityName
    };
}

private (IQueryPlanNode node, string entityName) PlanJoinTree(
    QualifiedJoin join,
    QuerySpecification querySpec,
    QueryPlanOptions options)
{
    // Plan left side
    var leftNode = PlanTableReference(join.FirstTableReference, options);
    var rightNode = PlanTableReference(join.SecondTableReference, options);

    // Extract join columns from ON condition
    var (leftCol, rightCol) = ExtractJoinColumns(join.SearchCondition);

    // Map ScriptDom join type to our JoinType
    var joinType = join.QualifiedJoinType switch
    {
        QualifiedJoinType.Inner => JoinType.Inner,
        QualifiedJoinType.LeftOuter => JoinType.Left,
        QualifiedJoinType.RightOuter => JoinType.Right,
        QualifiedJoinType.FullOuter => JoinType.FullOuter,
        _ => JoinType.Inner
    };

    // Use HashJoin by default (best general-purpose performance)
    var joinNode = new HashJoinNode(
        leftNode.node, rightNode.node,
        leftCol, rightCol, joinType);

    var entityName = leftNode.entityName;
    return (joinNode, entityName);
}

private (IQueryPlanNode node, string entityName) PlanTableReference(
    TableReference tableRef,
    QueryPlanOptions options)
{
    if (tableRef is QualifiedJoin nestedJoin)
        return PlanJoinTree(nestedJoin, null!, options);

    if (tableRef is NamedTableReference named)
    {
        var entityName = GetMultiPartName(named.SchemaObject);
        // Generate FetchXML for this single table (no joins)
        var fetchXml = GenerateSingleTableFetchXml(entityName);
        var scanNode = new FetchXmlScanNode(fetchXml, entityName);
        return (scanNode, entityName);
    }

    throw new QueryParseException($"Unsupported table reference type: {tableRef.GetType().Name}");
}

private static (string leftCol, string rightCol) ExtractJoinColumns(BooleanExpression searchCondition)
{
    if (searchCondition is BooleanComparisonExpression comp
        && comp.ComparisonType == BooleanComparisonType.Equals
        && comp.FirstExpression is ColumnReferenceExpression leftRef
        && comp.SecondExpression is ColumnReferenceExpression rightRef)
    {
        var leftCol = leftRef.MultiPartIdentifier.Identifiers.Last().Value;
        var rightCol = rightRef.MultiPartIdentifier.Identifiers.Last().Value;
        return (leftCol, rightCol);
    }

    throw new QueryParseException("JOIN ON condition must be a simple equality comparison (e.g., a.id = b.id).");
}
```

**Step 2: Update PlanSelect to try FetchXML first, fallback to client-side**

In the `PlanSelect` method, wrap the FetchXML generation in a try/catch:

```csharp
// In PlanSelect, replace the direct _fetchXmlGenerator.Generate call:
TranspileResult transpileResult;
try
{
    transpileResult = _fetchXmlGenerator.Generate(selectStmt);
}
catch (NotSupportedException)
{
    // FetchXML can't handle this query (e.g., RIGHT/FULL OUTER JOIN)
    // Fall back to client-side join planning
    return PlanClientSideJoin(selectStmt, querySpec, options);
}
```

**Step 3: Build and test**

Run: `dotnet build src/PPDS.Query`
Run: `dotnet test tests/PPDS.Query.Tests --filter "Category!=Integration" -v minimal`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs
git commit -m "feat(query): wire client-side join fallback into ExecutionPlanBuilder"
```

---

### Task 8: Add End-to-End Planning Tests for New Join Types

**Files:**
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Step 1: Write planning tests**

```csharp
[Fact]
public void Plan_RightJoin_ProducesClientSideHashJoin()
{
    var sql = "SELECT a.name, c.fullname FROM account a RIGHT JOIN contact c ON a.accountid = c.parentcustomerid";
    var fragment = _parser.Parse(sql);
    var result = _builder.Plan(fragment);

    // Should fall back to client-side join since FetchXML can't handle RIGHT JOIN
    result.RootNode.Should().Match<IQueryPlanNode>(n =>
        ContainsNodeOfType<HashJoinNode>(n));
}

[Fact]
public void Plan_FullOuterJoin_ProducesClientSideHashJoin()
{
    var sql = "SELECT a.name, c.fullname FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid";
    var fragment = _parser.Parse(sql);
    var result = _builder.Plan(fragment);

    result.RootNode.Should().Match<IQueryPlanNode>(n =>
        ContainsNodeOfType<HashJoinNode>(n));
}

[Fact]
public void Plan_InnerJoin_StillUsesFetchXml()
{
    var sql = "SELECT a.name, c.fullname FROM account a INNER JOIN contact c ON a.accountid = c.parentcustomerid";
    var fragment = _parser.Parse(sql);
    var result = _builder.Plan(fragment);

    // INNER JOIN should still go through FetchXML (not client-side)
    result.RootNode.Should().BeOfType<FetchXmlScanNode>();
}

[Fact]
public void Plan_LeftJoin_StillUsesFetchXml()
{
    var sql = "SELECT a.name, c.fullname FROM account a LEFT JOIN contact c ON a.accountid = c.parentcustomerid";
    var fragment = _parser.Parse(sql);
    var result = _builder.Plan(fragment);

    result.RootNode.Should().BeOfType<FetchXmlScanNode>();
}

private static bool ContainsNodeOfType<T>(IQueryPlanNode node) where T : IQueryPlanNode
{
    if (node is T) return true;
    return node.Children.Any(ContainsNodeOfType<T>);
}
```

**Step 2: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Plan_RightJoin|Plan_FullOuterJoin|Plan_InnerJoin_Still|Plan_LeftJoin_Still" -v minimal`
Expected: ALL PASS

**Step 3: Run full test suite**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Category!=Integration" -v minimal`
Expected: ALL PASS — no regressions

**Step 4: Commit**

```bash
git add tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "test(query): add end-to-end planning tests for RIGHT/FULL OUTER JOIN"
```

---

## Summary

| Task | What | Type | Effort |
|------|------|------|--------|
| 1 | Extend JoinType enum | Foundation | Tiny |
| 2 | Fix FetchXmlGenerator MapJoinType | Bug fix | Small |
| 3 | Add Right/FullOuter/Cross to NestedLoopJoinNode | Feature | Medium |
| 4 | Add Right/FullOuter to HashJoinNode | Feature | Medium |
| 5 | Add Right/FullOuter to MergeJoinNode | Feature | Medium |
| 6 | Add CROSS APPLY/OUTER APPLY | Feature | Large |
| 7 | Wire client-side joins into ExecutionPlanBuilder | Feature | Large |
| 8 | End-to-end planning tests | Testing | Small |

**Note:** CROSS JOIN wiring into ExecutionPlanBuilder is not included because `UnqualifiedJoin` (ScriptDom's CROSS JOIN AST type) requires separate detection in the FROM clause handler. This can be added as a follow-up — the NestedLoopJoinNode already supports it.
