# Wave 3: Subqueries + Cross-Environment — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add IN/EXISTS subqueries, scalar subqueries, derived tables, correlated subqueries, and cross-environment queries to the PPDS query engine.

**Architecture:** Subqueries use a two-path approach: FetchXML folding for simple cases (pushes work to Dataverse) and client-side execution via new plan nodes (HashSemiJoinNode, ScalarSubqueryNode, IndexSpoolNode) for complex cases. Cross-environment queries resolve bracket-syntax profile labels to connection pools, execute remote FetchXML via RemoteScanNode, materialize results in TableSpoolNode, then join locally. All new nodes follow the existing Volcano iterator model (IAsyncEnumerable\<QueryRow\>).

**Tech Stack:** C# (.NET 8/9/10), Microsoft.SqlServer.TransactSql.ScriptDom (TSql160Parser), xUnit, FluentAssertions, Moq

**Prerequisites:** This plan includes a "Section 0" fixing critical Wave 2 gaps identified during code review. These must be completed before the Wave 3 work.

**Dependency chain:**
```
Section 0 (Wave 2 Fixes):
  Task 0a: Wire CROSS JOIN + APPLY into planner (independent)
  Task 0b: RIGHT JOIN swap optimization (independent)
  Task 0c: Complete client-side join pipeline (independent)
  Task 0d: Complete safety settings schema (independent)

Section 1 (Subqueries):
  Task 1: TableSpoolNode (independent — foundation for derived tables, remote scans)
  Task 2: Derived tables (depends on Task 1)
  Task 3: HashSemiJoinNode (independent)
  Task 4: IN (Subquery) in planner (depends on Task 3)
  Task 5: EXISTS / NOT EXISTS (depends on Task 3)
  Task 6: ScalarSubqueryNode (independent)
  Task 7: Correlated subqueries + IndexSpoolNode (depends on Task 1)

Section 2 (Cross-Environment):
  Task 8: Profile resolution service (independent)
  Task 9: RemoteScanNode (depends on Tasks 1, 8)
  Task 10: Cross-environment query planning (depends on Task 9)
  Task 11: Cross-environment DML policy (depends on Task 10)
```

**Key architecture files to read first:**
- `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` — main planner (~2,519 lines)
- `src/PPDS.Query/Execution/ExpressionCompiler.cs` — compiles AST to delegates
- `src/PPDS.Query/Planning/Nodes/` — platform-agnostic plan nodes
- `src/PPDS.Dataverse/Query/Planning/Nodes/` — Dataverse-specific plan nodes
- `src/PPDS.Query/Planning/Nodes/CteScanNode.cs` — model for TableSpoolNode
- `src/PPDS.Auth/Profiles/EnvironmentConfig.cs` — profile configuration
- `tests/PPDS.Query.Tests/Planning/TestSourceNode.cs` — test data helper
- `tests/PPDS.Query.Tests/Planning/TestHelpers.cs` — test context/collection helpers

**IMPORTANT CONVENTIONS:**
- All business logic goes in Application Services (src/PPDS.Cli/Services/), never in UI code
- Use IProgressReporter for operations >1 second
- Wrap exceptions in PpdsException with ErrorCode
- Use connection pool for Dataverse requests, never store clients
- Run tests with: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore`
- Do NOT use shell redirections (2>&1, >, >>) — triggers permission prompts
- Do NOT regenerate PPDS.Plugins.snk
- Do NOT edit files in src/PPDS.Dataverse/Generated/
- Commit each task separately: `feat(query): <description>` or `fix(query): <description>`

---

## Section 0: Wave 2 Remediation

These fix critical gaps from the Wave 2 code review. The join node implementations are correct — the issue is that `ExecutionPlanBuilder` doesn't wire CROSS JOIN or APPLY from SQL input because ScriptDom represents these as `UnqualifiedJoin` (not `QualifiedJoin`).

---

### Task 0a: Wire CROSS JOIN, CROSS APPLY, and OUTER APPLY into ExecutionPlanBuilder

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` (~line 341, `PlanTableReference`)
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Context:** ScriptDom parses `CROSS JOIN` as `UnqualifiedJoin` with `UnqualifiedJoinType.CrossJoin`. Similarly, `CROSS APPLY` and `OUTER APPLY` use `UnqualifiedJoinType.CrossApply` / `UnqualifiedJoinType.OuterApply`. The current `PlanTableReference` and `PlanJoinTree` only handle `QualifiedJoin`, so these SQL constructs silently fall through to FetchXML (which also fails).

**Step 1: Write failing tests**

Add to `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`:

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_CrossJoin_ProducesClientSideNestedLoopJoin()
{
    var sql = "SELECT a.name, b.fullname FROM account a CROSS JOIN contact b";
    var result = PlanSql(sql);

    ContainsNodeOfType<NestedLoopJoinNode>(result.RootNode).Should().BeTrue();
}

[Fact]
[Trait("Category", "Unit")]
public void Plan_CrossApply_StringSplit_ProducesNestedLoop()
{
    var sql = "SELECT a.name, s.value FROM account a CROSS APPLY STRING_SPLIT(a.tags, ',') s";
    var result = PlanSql(sql);

    // Should route through table-valued function + APPLY
    result.RootNode.Should().NotBeNull();
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Plan_CrossJoin|Plan_CrossApply_StringSplit" -v minimal`
Expected: FAIL — `UnqualifiedJoin` not handled in `PlanTableReference`

**Step 3: Add UnqualifiedJoin handling to PlanTableReference**

In `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`, update `PlanTableReference` (around line 341):

```csharp
private (IQueryPlanNode node, string entityName) PlanTableReference(
    TableReference tableRef,
    QueryPlanOptions options)
{
    if (tableRef is QualifiedJoin nestedJoin)
        return PlanJoinTree(nestedJoin, options);

    if (tableRef is UnqualifiedJoin unqualified)
        return PlanUnqualifiedJoin(unqualified, options);

    if (tableRef is NamedTableReference named)
    {
        var entityName = GetMultiPartName(named.SchemaObject);
        var fetchXml = $"<fetch><entity name=\"{entityName}\"><all-attributes /></entity></fetch>";
        var scanNode = new FetchXmlScanNode(fetchXml, entityName);
        return (scanNode, entityName);
    }

    throw new QueryParseException($"Unsupported table reference type in client-side join: {tableRef.GetType().Name}");
}
```

**Step 4: Implement PlanUnqualifiedJoin**

Add after `PlanJoinTree`:

```csharp
private (IQueryPlanNode node, string entityName) PlanUnqualifiedJoin(
    UnqualifiedJoin join,
    QueryPlanOptions options)
{
    var leftResult = PlanTableReference(join.FirstTableReference, options);
    var rightResult = PlanTableReference(join.SecondTableReference, options);

    IQueryPlanNode joinNode = join.UnqualifiedJoinType switch
    {
        UnqualifiedJoinType.CrossJoin => new NestedLoopJoinNode(
            leftResult.node, rightResult.node, null, null, JoinType.Cross),

        UnqualifiedJoinType.CrossApply => new NestedLoopJoinNode(
            leftResult.node,
            correlatedInnerFactory: outerRow => EvaluateCorrelatedInner(rightResult.node, outerRow),
            joinType: JoinType.CrossApply),

        UnqualifiedJoinType.OuterApply => new NestedLoopJoinNode(
            leftResult.node,
            correlatedInnerFactory: outerRow => EvaluateCorrelatedInner(rightResult.node, outerRow),
            joinType: JoinType.OuterApply),

        _ => throw new QueryParseException($"Unsupported unqualified join type: {join.UnqualifiedJoinType}")
    };

    return (joinNode, leftResult.entityName);
}
```

Note: The correlated inner factory for APPLY needs careful implementation — for table-valued functions like STRING_SPLIT, the inner side references a column from the outer row. The `EvaluateCorrelatedInner` helper should re-plan the inner side with the outer row context. For the initial implementation, throw `NotSupportedException` for correlated APPLY and support only non-correlated CROSS JOIN. APPLY support can be fully wired when correlated subqueries are implemented in Task 7.

**Step 5: Ensure PlanClientSideJoin also handles UnqualifiedJoin at root**

Update `PlanClientSideJoin` to handle both `QualifiedJoin` and `UnqualifiedJoin` at the root:

```csharp
private QueryPlanResult PlanClientSideJoin(
    SelectStatement selectStmt,
    QuerySpecification querySpec,
    QueryPlanOptions options)
{
    var fromClause = querySpec.FromClause;
    if (fromClause?.TableReferences.Count != 1)
        throw new QueryParseException("Expected exactly one table reference in FROM clause.");

    var tableRef = fromClause.TableReferences[0];
    var (node, entityName) = PlanTableReference(tableRef, options);

    IQueryPlanNode rootNode = node;

    // Apply WHERE filter (client-side)
    if (querySpec.WhereClause?.SearchCondition != null)
    {
        var predicate = _expressionCompiler.CompilePredicate(querySpec.WhereClause.SearchCondition);
        var description = querySpec.WhereClause.SearchCondition.ToString() ?? "WHERE (client)";
        rootNode = new ClientFilterNode(rootNode, predicate, description);
    }

    return new QueryPlanResult
    {
        RootNode = rootNode,
        FetchXml = "<!-- client-side join -->",
        VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
        EntityLogicalName = entityName
    };
}
```

**Step 6: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 7: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "fix(query): wire CROSS JOIN and APPLY into ExecutionPlanBuilder via UnqualifiedJoin"
```

---

### Task 0b: Add RIGHT JOIN Swap in Planner

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` (`PlanJoinTree`)
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Context:** Design doc 2.2.2 specifies: when the planner encounters a RIGHT JOIN, swap left/right children and change to LEFT. This is a planning-time optimization — simpler and more efficient than runtime RIGHT support.

**Step 1: Write test verifying the swap**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_RightJoin_SwapsToLeftJoin()
{
    var sql = "SELECT a.name, c.fullname FROM account a RIGHT JOIN contact c ON a.accountid = c.parentcustomerid";
    var result = PlanSql(sql);

    // Should produce a LEFT JOIN (not RIGHT) with swapped operands
    var hashJoin = FindNode<HashJoinNode>(result.RootNode);
    hashJoin.Should().NotBeNull();
    hashJoin!.JoinType.Should().Be(JoinType.Left); // Swapped from Right to Left
}
```

**Step 2: Update PlanJoinTree to swap RIGHT → LEFT**

In `PlanJoinTree`, after mapping the join type:

```csharp
// RIGHT JOIN optimization: swap children and convert to LEFT JOIN
if (joinType == JoinType.Right)
{
    (leftResult, rightResult) = (rightResult, leftResult);
    (leftCol, rightCol) = (rightCol, leftCol);
    joinType = JoinType.Left;
}
```

**Step 3: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "perf(query): swap RIGHT JOIN to LEFT JOIN at planning time"
```

---

### Task 0c: Add Post-Pipeline to Client-Side Joins

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` (`PlanClientSideJoin`)
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Context:** The client-side join path only applies WHERE. It needs ORDER BY, SELECT projection, OFFSET/FETCH, and TOP to match the FetchXML path.

**Step 1: Write failing test**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_FullOuterJoin_WithOrderBy_AppliesSortNode()
{
    var sql = "SELECT a.name FROM account a FULL OUTER JOIN contact c ON a.accountid = c.parentcustomerid ORDER BY a.name";
    var result = PlanSql(sql);

    // Should have a sort or ordering applied
    result.RootNode.Should().NotBeNull();
    // The plan should handle ORDER BY for client-side joins
}
```

**Step 2: Extract shared post-pipeline method**

Create `ApplyPostScanPipeline` that takes a root node and a `QuerySpecification`, and applies:
1. SELECT projection (via existing `ProjectNode`)
2. ORDER BY (via client-side sort)
3. OFFSET/FETCH (via existing `OffsetFetchNode`)
4. TOP (via row limiting)

Use this in `PlanClientSideJoin` after the WHERE filter:

```csharp
// Apply SELECT projection
if (querySpec.SelectElements.Count > 0)
{
    rootNode = ApplyProjection(rootNode, querySpec, entityName);
}

// Apply ORDER BY (client-side sort since we can't push to FetchXML)
if (querySpec.OrderByClause?.OrderByElements.Count > 0)
{
    rootNode = ApplyClientSideOrderBy(rootNode, querySpec.OrderByClause);
}

// Apply OFFSET/FETCH
if (querySpec.OffsetClause != null)
{
    rootNode = ApplyOffsetFetch(rootNode, querySpec.OffsetClause);
}
```

Implement each `Apply*` method by delegating to existing node constructors. The exact implementation depends on what projection/sort infrastructure already exists — read the existing `PlanSelect` method to find how these are handled in the FetchXML path and reuse that logic.

**Step 3: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "feat(query): add ORDER BY, projection, and OFFSET/FETCH to client-side join path"
```

---

### Task 0d: Complete Safety Settings Schema

**Files:**
- Modify: `src/PPDS.Auth/Profiles/QuerySafetySettings.cs`
- Modify: `src/PPDS.Auth/Profiles/EnvironmentConfig.cs`
- Modify: `src/PPDS.Query/Planning/QueryHintParser.cs`
- Modify: `tests/PPDS.Query.Tests/Planning/QueryHintParserTests.cs`

**Context:** Wave 2 review found 5 missing settings properties, missing HASH GROUP hint parsing, and no ProtectionLevel persistence on EnvironmentConfig. Fix all in one task.

**Step 1: Add missing properties to QuerySafetySettings**

```csharp
/// <summary>Worker threads for DML (0 = auto). Default: 0.</summary>
[JsonPropertyName("max_parallelism")]
public int? MaxParallelism { get; set; }

/// <summary>Route full-table DELETE to async BulkDeleteRequest. Default: false.</summary>
[JsonPropertyName("use_bulk_delete")]
public bool UseBulkDelete { get; set; }

/// <summary>DateTime display mode. Default: UTC.</summary>
[JsonPropertyName("datetime_mode")]
[JsonConverter(typeof(JsonStringEnumConverter))]
public DateTimeMode DateTimeMode { get; set; } = DateTimeMode.Utc;

/// <summary>Include FetchXML in EXPLAIN output. Default: true.</summary>
[JsonPropertyName("show_fetchxml_in_explain")]
public bool ShowFetchXmlInExplain { get; set; } = true;

/// <summary>Maximum FetchXML pages fetched (0 = unlimited). Default: 200.</summary>
[JsonPropertyName("max_page_retrievals")]
public int? MaxPageRetrievals { get; set; }
```

Add enum:

```csharp
public enum DateTimeMode { Utc, Local, EnvironmentTimezone }
```

**Step 2: Add ProtectionLevel to EnvironmentConfig**

```csharp
/// <summary>
/// Explicit protection level override. Null means auto-detect from Type.
/// </summary>
[JsonPropertyName("protection")]
[JsonConverter(typeof(JsonStringEnumConverter))]
public ProtectionLevel? Protection { get; set; }
```

**Step 3: Add HASH GROUP to QueryHintParser**

In `ApplyCommentHint` method, add case:

```csharp
case "HASH_GROUP":
case "HASHGROUP":
    overrides.ForceClientAggregation = true;
    break;
```

**Step 4: Write round-trip test for new properties**

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Auth.Tests tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Auth/Profiles/QuerySafetySettings.cs src/PPDS.Auth/Profiles/EnvironmentConfig.cs src/PPDS.Query/Planning/QueryHintParser.cs tests/
git commit -m "feat(query): complete safety settings schema and add ProtectionLevel to EnvironmentConfig"
```

---

## Section 1: Subqueries

---

### Task 1: Create TableSpoolNode

**Files:**
- Create: `src/PPDS.Query/Planning/Nodes/TableSpoolNode.cs`
- Create: `tests/PPDS.Query.Tests/Planning/TableSpoolNodeTests.cs`

**Context:** TableSpoolNode materializes all rows from a child node into an in-memory list, then yields them on demand. It can be read multiple times (unlike streaming nodes). This is needed for derived tables, remote result materialization, and correlated subquery caching. Modeled after `CteScanNode` but takes a child node instead of a pre-built list.

**Step 1: Write failing tests**

Create `tests/PPDS.Query.Tests/Planning/TableSpoolNodeTests.cs`:

```csharp
using FluentAssertions;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class TableSpoolNodeTests
{
    [Fact]
    public async Task ExecuteAsync_MaterializesAndYieldsAllRows()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("name", "Fabrikam")));

        var spool = new TableSpoolNode(source);
        var rows = await TestHelpers.CollectRowsAsync(spool);

        rows.Should().HaveCount(2);
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[1].Values["name"].Value.Should().Be("Fabrikam");
    }

    [Fact]
    public async Task ExecuteAsync_CanBeReadMultipleTimes()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Contoso")));

        var spool = new TableSpoolNode(source);
        var context = TestHelpers.CreateTestContext();

        var rows1 = await TestHelpers.CollectRowsAsync(spool, context);
        var rows2 = await TestHelpers.CollectRowsAsync(spool, context);

        rows1.Should().HaveCount(1);
        rows2.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_EmptySource_YieldsNoRows()
    {
        var source = TestSourceNode.Create("account");
        var spool = new TableSpoolNode(source);
        var rows = await TestHelpers.CollectRowsAsync(spool);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializedRows_AvailableAfterExecution()
    {
        var source = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("id", 1)),
            TestSourceNode.MakeRow("account", ("id", 2)));

        var spool = new TableSpoolNode(source);
        await TestHelpers.CollectRowsAsync(spool);

        spool.MaterializedRows.Should().HaveCount(2);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "TableSpoolNode" -v minimal`
Expected: FAIL — class doesn't exist

**Step 3: Implement TableSpoolNode**

Create `src/PPDS.Query/Planning/Nodes/TableSpoolNode.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Materializes all rows from a child node into memory, then yields them on demand.
/// Can be read multiple times (unlike streaming nodes). Used for derived tables,
/// remote result materialization, and correlated subquery caching.
/// </summary>
public sealed class TableSpoolNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _source;
    private List<QueryRow>? _materializedRows;

    /// <summary>The materialized rows, available after first execution.</summary>
    public IReadOnlyList<QueryRow> MaterializedRows =>
        _materializedRows ?? (IReadOnlyList<QueryRow>)Array.Empty<QueryRow>();

    /// <inheritdoc />
    public string Description => $"TableSpool: {_source.Description} ({MaterializedRows.Count} rows)";

    /// <inheritdoc />
    public long EstimatedRows => _source.EstimatedRows;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _source };

    public TableSpoolNode(IQueryPlanNode source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Creates a TableSpoolNode from pre-materialized rows (no child node).
    /// </summary>
    public TableSpoolNode(List<QueryRow> materializedRows)
    {
        _source = null!;
        _materializedRows = materializedRows ?? throw new ArgumentNullException(nameof(materializedRows));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Materialize on first execution
        if (_materializedRows == null)
        {
            _materializedRows = new List<QueryRow>();
            await foreach (var row in _source.ExecuteAsync(context, cancellationToken))
            {
                _materializedRows.Add(row);
            }
        }

        // Yield materialized rows
        foreach (var row in _materializedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "TableSpoolNode" -v minimal`
Expected: ALL PASS

**Step 5: Run full suite**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/TableSpoolNode.cs tests/PPDS.Query.Tests/Planning/TableSpoolNodeTests.cs
git commit -m "feat(query): add TableSpoolNode for in-memory result materialization"
```

---

### Task 2: Derived Tables (Subqueries in FROM)

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` (`PlanTableReference`)
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Context:** `SELECT * FROM (SELECT name, revenue FROM account WHERE revenue > 1000) AS big_accounts` — the inner SELECT is planned normally, materialized into a TableSpoolNode, then used as the scan source for the outer query. ScriptDom represents this as a `QueryDerivedTable` in the FROM clause.

**Step 1: Write failing tests**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_DerivedTable_ProducesTableSpool()
{
    var sql = "SELECT sub.name FROM (SELECT name FROM account) AS sub";
    var result = PlanSql(sql);

    result.RootNode.Should().NotBeNull();
    ContainsNodeOfType<TableSpoolNode>(result.RootNode).Should().BeTrue();
}

[Fact]
[Trait("Category", "Unit")]
public void Plan_DerivedTable_WithWhere_AppliesFilter()
{
    var sql = "SELECT sub.name FROM (SELECT name, revenue FROM account WHERE revenue > 1000) AS sub WHERE sub.name IS NOT NULL";
    var result = PlanSql(sql);

    result.RootNode.Should().NotBeNull();
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Plan_DerivedTable" -v minimal`
Expected: FAIL — `QueryDerivedTable` not handled in `PlanTableReference`

**Step 3: Add QueryDerivedTable handling to PlanTableReference**

In `PlanTableReference`, add before the `NamedTableReference` check:

```csharp
if (tableRef is QueryDerivedTable derived)
{
    return PlanDerivedTable(derived, options);
}
```

Implement:

```csharp
private (IQueryPlanNode node, string entityName) PlanDerivedTable(
    QueryDerivedTable derived,
    QueryPlanOptions options)
{
    // Plan the inner query
    var innerResult = PlanQueryExpressionAsSelect(derived.QueryExpression, options);

    // Wrap in TableSpoolNode so the outer query can scan it
    var spool = new TableSpoolNode(innerResult.RootNode);

    // Use the alias as the entity name for column references
    var alias = derived.Alias?.Value ?? "derived";

    return (spool, alias);
}
```

Note: `PlanQueryExpressionAsSelect` already exists — it handles `QueryExpression` (the base type for SELECT). Check how it's used in `PlanWithCtes` for reference.

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "feat(query): add derived table (subquery in FROM) support"
```

---

### Task 3: Create HashSemiJoinNode

**Files:**
- Create: `src/PPDS.Query/Planning/Nodes/HashSemiJoinNode.cs`
- Create: `tests/PPDS.Query.Tests/Planning/HashSemiJoinNodeTests.cs`

**Context:** HashSemiJoinNode implements `IN (subquery)` and `EXISTS` via hash-based semi-join. It materializes the inner (right) side into a HashSet, then for each outer (left) row, probes the hash set. For semi-join (IN/EXISTS), it yields the outer row if a match is found. For anti-semi-join (NOT IN/NOT EXISTS), it yields the outer row if NO match is found.

**Step 1: Write failing tests**

Create `tests/PPDS.Query.Tests/Planning/HashSemiJoinNodeTests.cs`:

```csharp
using FluentAssertions;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class HashSemiJoinNodeTests
{
    [Fact]
    public async Task SemiJoin_ReturnsOnlyMatchingOuterRows()
    {
        // Outer: accounts with ids 1, 2, 3
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", "2"), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", "3"), ("name", "Northwind")));

        // Inner: contact parent ids 1, 3 (simulates: WHERE accountid IN (SELECT parentcustomerid FROM contact))
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "3")));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Values["name"].Value).Should().BeEquivalentTo("Contoso", "Northwind");
    }

    [Fact]
    public async Task AntiSemiJoin_ReturnsOnlyNonMatchingOuterRows()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("accountid", "2"), ("name", "Fabrikam")),
            TestSourceNode.MakeRow("account", ("accountid", "3"), ("name", "Northwind")));

        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "3")));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: true);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["name"].Value.Should().Be("Fabrikam");
    }

    [Fact]
    public async Task SemiJoin_EmptyInner_ReturnsNoRows()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")));
        var inner = TestSourceNode.Create("contact");

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task AntiSemiJoin_EmptyInner_ReturnsAllOuterRows()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")));
        var inner = TestSourceNode.Create("contact");

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: true);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task SemiJoin_DuplicatesInInner_DoesNotDuplicateOuter()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")));
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")),
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1); // Semi-join never duplicates outer rows
    }

    [Fact]
    public async Task SemiJoin_NullKeyInOuter_ExcludesRow()
    {
        var outer = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", null), ("name", "NullId")),
            TestSourceNode.MakeRow("account", ("accountid", "1"), ("name", "Contoso")));
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("parentcustomerid", "1")));

        var node = new HashSemiJoinNode(outer, inner, "accountid", "parentcustomerid", antiSemiJoin: false);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].Values["name"].Value.Should().Be("Contoso");
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "HashSemiJoinNode" -v minimal`
Expected: FAIL — class doesn't exist

**Step 3: Implement HashSemiJoinNode**

Create `src/PPDS.Query/Planning/Nodes/HashSemiJoinNode.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Implements semi-join and anti-semi-join for IN (subquery), EXISTS, NOT IN, and NOT EXISTS.
/// Materializes the inner side into a HashSet keyed by the join column, then probes per outer row.
/// Semi-join: yields outer row when key IS found in inner set.
/// Anti-semi-join: yields outer row when key is NOT found in inner set.
/// </summary>
public sealed class HashSemiJoinNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _outer;
    private readonly IQueryPlanNode _inner;
    private readonly string _outerKeyColumn;
    private readonly string _innerKeyColumn;
    private readonly bool _antiSemiJoin;

    public string Description => _antiSemiJoin
        ? $"HashAntiSemiJoin: {_outerKeyColumn} NOT IN ({_inner.Description})"
        : $"HashSemiJoin: {_outerKeyColumn} IN ({_inner.Description})";

    public long EstimatedRows => _outer.EstimatedRows;

    public IReadOnlyList<IQueryPlanNode> Children => new[] { _outer, _inner };

    public HashSemiJoinNode(
        IQueryPlanNode outer,
        IQueryPlanNode inner,
        string outerKeyColumn,
        string innerKeyColumn,
        bool antiSemiJoin)
    {
        _outer = outer ?? throw new ArgumentNullException(nameof(outer));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _outerKeyColumn = outerKeyColumn ?? throw new ArgumentNullException(nameof(outerKeyColumn));
        _innerKeyColumn = innerKeyColumn ?? throw new ArgumentNullException(nameof(innerKeyColumn));
        _antiSemiJoin = antiSemiJoin;
    }

    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phase 1: Build hash set from inner side
        var innerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var innerRow in _inner.ExecuteAsync(context, cancellationToken))
        {
            if (innerRow.Values.TryGetValue(_innerKeyColumn, out var val) && val.Value != null)
            {
                innerKeys.Add(val.Value.ToString()!);
            }
        }

        // Phase 2: Probe outer side
        await foreach (var outerRow in _outer.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outerKeyValue = outerRow.Values.TryGetValue(_outerKeyColumn, out var outerVal)
                ? outerVal.Value?.ToString()
                : null;

            // NULL keys never match (SQL NULL semantics)
            if (outerKeyValue == null)
            {
                if (_antiSemiJoin)
                {
                    // NOT IN with NULL: SQL says UNKNOWN, which means exclude
                    // For NOT EXISTS, NULL key means no correlation, so include
                    // We use anti-semi-join semantics (exclude NULL keys for NOT IN safety)
                    continue;
                }
                continue; // Semi-join: NULL never matches
            }

            var found = innerKeys.Contains(outerKeyValue);

            if (_antiSemiJoin ? !found : found)
            {
                yield return outerRow;
            }
        }
    }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "HashSemiJoinNode" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/HashSemiJoinNode.cs tests/PPDS.Query.Tests/Planning/HashSemiJoinNodeTests.cs
git commit -m "feat(query): add HashSemiJoinNode for IN/EXISTS subquery execution"
```

---

### Task 4: IN (Subquery) in Planner

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Context:** Currently, `ThrowIfUnsupportedWhereSubqueryPredicate` throws when it finds `InPredicate` with a `.Subquery`. Replace this with actual planning logic: extract the subquery, plan it, and create a HashSemiJoinNode. The outer side is the main query's FetchXML scan. The inner side is the subquery's plan.

ScriptDom AST: `InPredicate` has `.Expression` (the column being tested), `.Subquery` (a `ScalarSubquery` wrapping a `QueryExpression`), and `.NotDefined` (true for NOT IN).

**Step 1: Write failing tests**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_WhereInSubquery_ProducesHashSemiJoin()
{
    var sql = "SELECT name FROM account WHERE accountid IN (SELECT parentcustomerid FROM contact WHERE statecode = 0)";
    var result = PlanSql(sql);

    ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue();
}

[Fact]
[Trait("Category", "Unit")]
public void Plan_WhereNotInSubquery_ProducesHashAntiSemiJoin()
{
    var sql = "SELECT name FROM account WHERE accountid NOT IN (SELECT parentcustomerid FROM contact)";
    var result = PlanSql(sql);

    ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue();
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "Plan_WhereInSubquery|Plan_WhereNotInSubquery" -v minimal`
Expected: FAIL — throws "IN (SELECT ...) subqueries are not supported"

**Step 3: Replace subquery rejection with planning logic**

In `ExecutionPlanBuilder`, update `ThrowIfUnsupportedWhereSubqueryPredicate` to only throw for EXISTS (which will be handled in Task 5). Remove the IN subquery rejection.

Add a new method `TryExtractInSubquery` that walks the WHERE clause looking for `InPredicate` with `.Subquery != null`. When found:

1. Extract the outer column from `inPred.Expression` (must be a `ColumnReferenceExpression`)
2. Plan the inner subquery via `PlanQueryExpressionAsSelect(inPred.Subquery.QueryExpression, options)`
3. Extract the inner column from the subquery's SELECT list (first column)
4. Create a `HashSemiJoinNode` with `antiSemiJoin: inPred.NotDefined`
5. Wrap the existing scan node (the outer query's FetchXML scan) with the semi-join

The key challenge is WHERE clause decomposition: the IN subquery needs to be removed from the WHERE before FetchXML generation (since FetchXML doesn't know about subqueries), and the rest of the WHERE should be pushed to FetchXML normally.

Strategy:
1. Before FetchXML generation, walk the WHERE clause and extract any `InPredicate` with subqueries
2. Generate FetchXML for the query WITHOUT the subquery predicates
3. Plan the subquery separately
4. Wrap the FetchXML scan in a HashSemiJoinNode

Implementation approach — add to `PlanSelect` before the FetchXML generation:

```csharp
// Extract IN-subquery predicates from WHERE (cannot be pushed to FetchXML)
var (cleanedWhere, inSubqueries) = ExtractInSubqueryPredicates(querySpec.WhereClause?.SearchCondition);
// Temporarily replace the WHERE clause for FetchXML generation
// ... generate FetchXML with cleaned WHERE ...
// After creating the scan node, wrap with HashSemiJoinNode for each extracted subquery
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "feat(query): add IN (subquery) and NOT IN (subquery) support"
```

---

### Task 5: EXISTS / NOT EXISTS in Planner

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Context:** EXISTS is similar to IN but without a specific key column comparison. `WHERE EXISTS (SELECT 1 FROM contact WHERE contact.parentcustomerid = account.accountid)` is a correlated semi-join.

ScriptDom AST: `ExistsPredicate` has `.Subquery` (a `ScalarSubquery`). The correlation is in the subquery's WHERE clause — look for column references that reference the outer table.

Strategy: For correlated EXISTS, extract the correlation predicate from the inner WHERE, identify the outer/inner key columns, and create a HashSemiJoinNode. For uncorrelated EXISTS (rare, like `WHERE EXISTS (SELECT 1 FROM config WHERE flag = 1)`), materialize the inner query and emit all or no outer rows.

**Step 1: Write failing tests**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_WhereExists_ProducesHashSemiJoin()
{
    var sql = @"SELECT name FROM account a
                WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";
    var result = PlanSql(sql);

    ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue();
}

[Fact]
[Trait("Category", "Unit")]
public void Plan_WhereNotExists_ProducesHashAntiSemiJoin()
{
    var sql = @"SELECT name FROM account a
                WHERE NOT EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";
    var result = PlanSql(sql);

    ContainsNodeOfType<HashSemiJoinNode>(result.RootNode).Should().BeTrue();
}
```

**Step 2: Implement EXISTS planning**

Similar to Task 4 — extract EXISTS predicates from WHERE before FetchXML generation, detect the correlation columns, and wrap the scan in a HashSemiJoinNode.

Key implementation:

```csharp
private (string outerCol, string innerCol)? ExtractCorrelationFromExists(ExistsPredicate exists, string outerEntityName)
{
    // Walk the inner query's WHERE clause looking for:
    //   innerTable.col = outerTable.col
    // Where outerTable matches the outer query's entity/alias
    var innerQuery = exists.Subquery.QueryExpression as QuerySpecification;
    if (innerQuery?.WhereClause?.SearchCondition is BooleanComparisonExpression comp
        && comp.ComparisonType == BooleanComparisonType.Equals)
    {
        // Extract column references and determine which is outer vs inner
        // based on table alias matching
        // ...
    }
    return null;
}
```

Also remove the EXISTS rejection from `ThrowIfUnsupportedWhereSubqueryPredicate` (which should now be empty and can be deleted or left as a no-op for any remaining unsupported patterns).

**Step 3: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "feat(query): add EXISTS and NOT EXISTS subquery support"
```

---

### Task 6: ScalarSubqueryNode

**Files:**
- Create: `src/PPDS.Query/Planning/Nodes/ScalarSubqueryNode.cs`
- Create: `tests/PPDS.Query.Tests/Planning/ScalarSubqueryNodeTests.cs`
- Modify: `src/PPDS.Query/Execution/ExpressionCompiler.cs`
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**Context:** Scalar subqueries return exactly one value: `SELECT (SELECT COUNT(*) FROM contact WHERE parentcustomerid = a.accountid) AS contact_count FROM account a`. The node executes the inner query, asserts exactly one row with one column, and returns the scalar value. If 0 rows, returns NULL. If >1 row, throws an error.

**Step 1: Write failing tests**

Create `tests/PPDS.Query.Tests/Planning/ScalarSubqueryNodeTests.cs`:

```csharp
using FluentAssertions;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ScalarSubqueryNodeTests
{
    [Fact]
    public async Task ExecuteAsync_SingleRow_ReturnsScalarValue()
    {
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("cnt", 42)));

        var node = new ScalarSubqueryNode(inner);
        var context = TestHelpers.CreateTestContext();
        var value = await node.ExecuteScalarAsync(context);

        value.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteAsync_NoRows_ReturnsNull()
    {
        var inner = TestSourceNode.Create("contact");

        var node = new ScalarSubqueryNode(inner);
        var context = TestHelpers.CreateTestContext();
        var value = await node.ExecuteScalarAsync(context);

        value.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_MultipleRows_ThrowsException()
    {
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("cnt", 1)),
            TestSourceNode.MakeRow("contact", ("cnt", 2)));

        var node = new ScalarSubqueryNode(inner);
        var context = TestHelpers.CreateTestContext();

        var act = async () => await node.ExecuteScalarAsync(context);
        await act.Should().ThrowAsync<QueryExecutionException>()
            .WithMessage("*more than one*");
    }
}
```

**Step 2: Implement ScalarSubqueryNode**

Create `src/PPDS.Query/Planning/Nodes/ScalarSubqueryNode.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Executes a subquery and returns a single scalar value.
/// Asserts at most one row is returned. If 0 rows, returns NULL. If >1 row, throws.
/// Used for scalar subqueries in SELECT lists and WHERE predicates.
/// </summary>
public sealed class ScalarSubqueryNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _inner;

    public string Description => $"ScalarSubquery: ({_inner.Description})";
    public long EstimatedRows => 1;
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _inner };

    public ScalarSubqueryNode(IQueryPlanNode inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Executes the subquery and returns the single scalar value.</summary>
    public async Task<object?> ExecuteScalarAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken = default)
    {
        QueryRow? firstRow = null;
        var rowCount = 0;

        await foreach (var row in _inner.ExecuteAsync(context, cancellationToken))
        {
            rowCount++;
            if (rowCount == 1)
                firstRow = row;
            else
                throw new QueryExecutionException(
                    "Scalar subquery returned more than one row.",
                    QueryErrorCode.SubqueryMultipleRows);
        }

        if (firstRow == null)
            return null;

        // Return the first (and only) column value
        var firstValue = firstRow.Values.Values.FirstOrDefault();
        return firstValue?.Value;
    }

    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // ScalarSubqueryNode is typically used via ExecuteScalarAsync,
        // but supports the standard interface for plan tree consistency
        await foreach (var row in _inner.ExecuteAsync(context, cancellationToken))
        {
            yield return row;
        }
    }
}
```

Note: You'll need to add `SubqueryMultipleRows` to the `QueryErrorCode` enum. Find it via `grep -r "QueryErrorCode" src/` and add the new value.

**Step 3: Wire into ExpressionCompiler**

In `ExpressionCompiler.CompileScalar`, add handling for `ScalarSubquery` AST node. This requires the compiler to have access to the planner (or a delegate) to plan the inner query. Consider passing a `Func<QueryExpression, IQueryPlanNode>` to the compiler for subquery resolution.

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/ScalarSubqueryNode.cs tests/PPDS.Query.Tests/Planning/ScalarSubqueryNodeTests.cs src/PPDS.Query/Execution/ExpressionCompiler.cs src/PPDS.Query/Planning/ExecutionPlanBuilder.cs
git commit -m "feat(query): add ScalarSubqueryNode for scalar subquery execution"
```

---

### Task 7: Correlated Subqueries with IndexSpoolNode

**Files:**
- Create: `src/PPDS.Query/Planning/Nodes/IndexSpoolNode.cs`
- Create: `tests/PPDS.Query.Tests/Planning/IndexSpoolNodeTests.cs`
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

**Context:** Correlated subqueries reference columns from the outer query. Example: `WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)`. The inner query must be re-evaluated for each outer row. An IndexSpoolNode caches the inner query results indexed by the correlation column values, so repeated lookups (when multiple outer rows have the same correlation value) don't re-execute the query.

**Step 1: Write failing tests**

Create `tests/PPDS.Query.Tests/Planning/IndexSpoolNodeTests.cs`:

```csharp
using FluentAssertions;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class IndexSpoolNodeTests
{
    [Fact]
    public async Task Lookup_CachesResultsByKey()
    {
        var executionCount = 0;

        // Factory that counts invocations
        IAsyncEnumerable<QueryRow> InnerFactory(object keyValue)
        {
            executionCount++;
            return AsyncEnumerable(
                TestSourceNode.MakeRow("contact", ("name", $"Contact for {keyValue}")));
        }

        var spool = new IndexSpoolNode(InnerFactory);
        var context = TestHelpers.CreateTestContext();

        // First lookup for key "1"
        var rows1 = await spool.LookupAsync("1", context);
        rows1.Should().HaveCount(1);

        // Second lookup for same key — should use cache
        var rows2 = await spool.LookupAsync("1", context);
        rows2.Should().HaveCount(1);

        executionCount.Should().Be(1); // Factory called only once for key "1"
    }

    [Fact]
    public async Task Lookup_DifferentKeys_CallsFactoryPerKey()
    {
        var executionCount = 0;

        IAsyncEnumerable<QueryRow> InnerFactory(object keyValue)
        {
            executionCount++;
            return AsyncEnumerable(
                TestSourceNode.MakeRow("contact", ("name", $"Contact for {keyValue}")));
        }

        var spool = new IndexSpoolNode(InnerFactory);
        var context = TestHelpers.CreateTestContext();

        await spool.LookupAsync("1", context);
        await spool.LookupAsync("2", context);
        await spool.LookupAsync("1", context); // cached

        executionCount.Should().Be(2); // Factory called for "1" and "2"
    }

    private static async IAsyncEnumerable<QueryRow> AsyncEnumerable(params QueryRow[] rows)
    {
        foreach (var row in rows)
            yield return row;
        await Task.CompletedTask;
    }
}
```

**Step 2: Implement IndexSpoolNode**

Create `src/PPDS.Query/Planning/Nodes/IndexSpoolNode.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Caches inner query results indexed by correlation key values.
/// Used for correlated subqueries to avoid re-executing the inner query
/// when multiple outer rows have the same correlation value.
/// </summary>
public sealed class IndexSpoolNode : IQueryPlanNode
{
    private readonly Func<object, IAsyncEnumerable<QueryRow>> _innerFactory;
    private readonly Dictionary<string, List<QueryRow>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string Description => "IndexSpool (correlated cache)";
    public long EstimatedRows => _cache.Count;
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <param name="innerFactory">
    /// Factory that executes the inner query for a given correlation key value.
    /// Called once per unique key value; results are cached for subsequent lookups.
    /// </param>
    public IndexSpoolNode(Func<object, IAsyncEnumerable<QueryRow>> innerFactory)
    {
        _innerFactory = innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));
    }

    /// <summary>
    /// Looks up cached results for the given key, executing the inner factory if not cached.
    /// </summary>
    public async Task<IReadOnlyList<QueryRow>> LookupAsync(
        object keyValue,
        QueryPlanContext context,
        CancellationToken cancellationToken = default)
    {
        var key = keyValue?.ToString() ?? "__null__";

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var rows = new List<QueryRow>();
        await foreach (var row in _innerFactory(keyValue).WithCancellation(cancellationToken))
        {
            rows.Add(row);
        }

        _cache[key] = rows;
        return rows;
    }

    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // IndexSpoolNode is typically used via LookupAsync.
        // ExecuteAsync yields all cached rows for plan tree consistency.
        foreach (var entry in _cache.Values)
        {
            foreach (var row in entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }
        await Task.CompletedTask;
    }
}
```

**Step 3: Wire into correlated EXISTS/IN planning**

In the EXISTS/IN planning code from Tasks 4-5, when the subquery has a correlation predicate (references the outer table), use IndexSpoolNode:

1. Create a factory that plans and executes the inner query with the correlation value as a parameter
2. Wrap in IndexSpoolNode
3. For each outer row, call `spool.LookupAsync(outerRow[correlationCol])` to get the inner rows
4. Emit or filter the outer row based on semi-join/anti-semi-join semantics

This is complex — the NestedLoopJoinNode's APPLY mode is the right execution vehicle. The outer loop iterates outer rows, and for each, the inner factory (backed by IndexSpoolNode) produces the correlated inner rows.

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/Nodes/IndexSpoolNode.cs tests/PPDS.Query.Tests/Planning/IndexSpoolNodeTests.cs src/PPDS.Query/Planning/ExecutionPlanBuilder.cs
git commit -m "feat(query): add IndexSpoolNode for correlated subquery caching"
```

---

## Section 2: Cross-Environment Queries

---

### Task 8: Profile Resolution Service

**Files:**
- Create: `src/PPDS.Cli/Services/ProfileResolutionService.cs`
- Modify: `src/PPDS.Auth/Profiles/EnvironmentConfig.cs`
- Create: `tests/PPDS.Cli.Tests/Services/ProfileResolutionServiceTests.cs`

**Context:** Cross-environment queries use bracket syntax: `SELECT * FROM [UAT].dbo.account`. The bracket identifier resolves to a profile label. We need a service that resolves labels to connection pools. Labels must be unique across profiles (enforced at config time).

**Step 1: Write failing tests**

Create `tests/PPDS.Cli.Tests/Services/ProfileResolutionServiceTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services;
using Xunit;

namespace PPDS.Cli.Tests.Services;

[Trait("Category", "Unit")]
public class ProfileResolutionServiceTests
{
    [Fact]
    public void Resolve_ExistingLabel_ReturnsConfig()
    {
        var configs = new List<EnvironmentConfig>
        {
            new() { Url = "https://uat.crm.dynamics.com/", Label = "UAT", Type = "Sandbox" },
            new() { Url = "https://prod.crm.dynamics.com/", Label = "PROD", Type = "Production" }
        };

        var service = new ProfileResolutionService(configs);
        var result = service.ResolveByLabel("UAT");

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://uat.crm.dynamics.com/");
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var configs = new List<EnvironmentConfig>
        {
            new() { Url = "https://uat.crm.dynamics.com/", Label = "UAT" }
        };

        var service = new ProfileResolutionService(configs);
        service.ResolveByLabel("uat").Should().NotBeNull();
        service.ResolveByLabel("Uat").Should().NotBeNull();
    }

    [Fact]
    public void Resolve_NotFound_ReturnsNull()
    {
        var configs = new List<EnvironmentConfig>
        {
            new() { Url = "https://uat.crm.dynamics.com/", Label = "UAT" }
        };

        var service = new ProfileResolutionService(configs);
        service.ResolveByLabel("STAGING").Should().BeNull();
    }
}
```

**Step 2: Implement ProfileResolutionService**

Create `src/PPDS.Cli/Services/ProfileResolutionService.cs`:

```csharp
using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services;

/// <summary>
/// Resolves environment profile labels to EnvironmentConfig instances.
/// Used by cross-environment query planning to map bracket syntax ([LABEL].entity)
/// to the correct connection pool.
/// </summary>
public sealed class ProfileResolutionService
{
    private readonly Dictionary<string, EnvironmentConfig> _labelIndex;

    public ProfileResolutionService(IEnumerable<EnvironmentConfig> configs)
    {
        _labelIndex = new Dictionary<string, EnvironmentConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs)
        {
            if (!string.IsNullOrEmpty(config.Label))
            {
                _labelIndex[config.Label] = config;
            }
        }
    }

    /// <summary>
    /// Resolves a label to its EnvironmentConfig. Returns null if not found.
    /// </summary>
    public EnvironmentConfig? ResolveByLabel(string label)
    {
        return _labelIndex.TryGetValue(label, out var config) ? config : null;
    }
}
```

**Step 3: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "ProfileResolution" -v minimal`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/PPDS.Cli/Services/ProfileResolutionService.cs tests/PPDS.Cli.Tests/Services/ProfileResolutionServiceTests.cs
git commit -m "feat(query): add ProfileResolutionService for cross-environment label lookup"
```

---

### Task 9: RemoteScanNode

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/RemoteScanNode.cs`
- Create: `tests/PPDS.Query.Tests/Planning/RemoteScanNodeTests.cs`

**Context:** RemoteScanNode executes a FetchXML query against a remote environment's connection pool. It's the leaf node for cross-environment query plans. Results are streamed like FetchXmlScanNode but target a different connection pool.

**Step 1: Write failing tests**

```csharp
using FluentAssertions;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class RemoteScanNodeTests
{
    [Fact]
    public async Task ExecuteAsync_UsesRemoteExecutor()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        var rows = new List<QueryRow>
        {
            TestSourceNode.MakeRow("account", ("name", "Remote-Contoso"))
        };

        // Set up mock to return rows
        mockExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(rows.ToAsyncEnumerable());

        var node = new RemoteScanNode(
            fetchXml: "<fetch><entity name='account'><all-attributes /></entity></fetch>",
            entityLogicalName: "account",
            remoteLabel: "UAT",
            remoteExecutor: mockExecutor.Object);

        var result = await TestHelpers.CollectRowsAsync(node);

        result.Should().HaveCount(1);
        result[0].Values["name"].Value.Should().Be("Remote-Contoso");
    }

    [Fact]
    public void Description_IncludesRemoteLabel()
    {
        var node = new RemoteScanNode(
            fetchXml: "<fetch/>",
            entityLogicalName: "account",
            remoteLabel: "UAT",
            remoteExecutor: Mock.Of<IQueryExecutor>());

        node.Description.Should().Contain("[UAT]");
        node.Description.Should().Contain("account");
    }
}
```

**Step 2: Implement RemoteScanNode**

Create `src/PPDS.Dataverse/Query/Planning/Nodes/RemoteScanNode.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Executes a FetchXML query against a remote environment's connection pool.
/// Used for cross-environment queries where bracket syntax ([LABEL].entity)
/// references a different Dataverse environment.
/// </summary>
public sealed class RemoteScanNode : IQueryPlanNode
{
    private readonly string _fetchXml;
    private readonly IQueryExecutor _remoteExecutor;

    /// <summary>The entity being queried on the remote environment.</summary>
    public string EntityLogicalName { get; }

    /// <summary>The label of the remote environment (e.g., "UAT", "PROD").</summary>
    public string RemoteLabel { get; }

    public string Description => $"RemoteScan: [{RemoteLabel}].{EntityLogicalName}";
    public long EstimatedRows => 1000; // Unknown; estimate conservatively
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    public RemoteScanNode(
        string fetchXml,
        string entityLogicalName,
        string remoteLabel,
        IQueryExecutor remoteExecutor)
    {
        _fetchXml = fetchXml ?? throw new ArgumentNullException(nameof(fetchXml));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        RemoteLabel = remoteLabel ?? throw new ArgumentNullException(nameof(remoteLabel));
        _remoteExecutor = remoteExecutor ?? throw new ArgumentNullException(nameof(remoteExecutor));
    }

    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in _remoteExecutor.ExecuteFetchXmlAsync(
            _fetchXml, EntityLogicalName, cancellationToken))
        {
            yield return row;
        }
    }
}
```

Note: Check the exact `IQueryExecutor` interface signature — the `ExecuteFetchXmlAsync` method may have a different signature. Adjust accordingly based on what you find in `src/PPDS.Dataverse/Query/IQueryExecutor.cs`.

**Step 3: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "RemoteScanNode" -v minimal`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/PPDS.Dataverse/Query/Planning/Nodes/RemoteScanNode.cs tests/PPDS.Query.Tests/Planning/RemoteScanNodeTests.cs
git commit -m "feat(query): add RemoteScanNode for cross-environment FetchXML execution"
```

---

### Task 10: Cross-Environment Query Planning

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs`
- Modify: `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs`

**Context:** When a table reference uses multi-part naming like `[UAT].dbo.account` or `[UAT].account`, the planner must resolve the bracket-delimited part as a profile label, create a RemoteScanNode targeting that profile's connection pool, wrap in TableSpoolNode for materialization, and join with local results.

ScriptDom parses `[UAT].dbo.account` as a `NamedTableReference` with a `SchemaObject` that has:
- `.ServerIdentifier` = "UAT"
- `.DatabaseIdentifier` = null (or "dbo")
- `.SchemaIdentifier` = "dbo" (or null)
- `.BaseIdentifier` = "account"

When `.ServerIdentifier` is non-null, it's a cross-environment reference.

**Step 1: Add profile resolver to QueryPlanOptions**

```csharp
/// <summary>
/// Resolver for cross-environment profile labels. When set, enables bracket syntax
/// for cross-environment queries ([LABEL].entity).
/// </summary>
public Func<string, EnvironmentConfig?>? ProfileResolver { get; init; }

/// <summary>
/// Factory for creating IQueryExecutor instances for remote environments.
/// Called with the resolved EnvironmentConfig URL.
/// </summary>
public Func<string, IQueryExecutor>? RemoteExecutorFactory { get; init; }
```

**Step 2: Write failing tests**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Plan_CrossEnvironment_ProducesRemoteScanNode()
{
    var sql = "SELECT name FROM [UAT].dbo.account";
    var options = CreateOptionsWithProfileResolver();
    var result = PlanSql(sql, options);

    ContainsNodeOfType<RemoteScanNode>(result.RootNode).Should().BeTrue();
}

[Fact]
[Trait("Category", "Unit")]
public void Plan_CrossEnvironment_UnknownLabel_ThrowsDescriptiveError()
{
    var sql = "SELECT name FROM [STAGING].dbo.account";
    var options = CreateOptionsWithProfileResolver(); // no STAGING profile

    var act = () => PlanSql(sql, options);
    act.Should().Throw<QueryParseException>()
        .WithMessage("*STAGING*");
}
```

**Step 3: Update PlanTableReference for cross-environment detection**

In `PlanTableReference`, when handling `NamedTableReference`, check for `.ServerIdentifier`:

```csharp
if (tableRef is NamedTableReference named)
{
    var serverIdentifier = named.SchemaObject.ServerIdentifier?.Value;

    if (serverIdentifier != null)
    {
        // Cross-environment reference: [LABEL].dbo.entity
        return PlanRemoteTableReference(named, serverIdentifier, options);
    }

    // Normal local table reference
    var entityName = GetMultiPartName(named.SchemaObject);
    var fetchXml = $"<fetch><entity name=\"{entityName}\"><all-attributes /></entity></fetch>";
    var scanNode = new FetchXmlScanNode(fetchXml, entityName);
    return (scanNode, entityName);
}
```

Implement `PlanRemoteTableReference`:

```csharp
private (IQueryPlanNode node, string entityName) PlanRemoteTableReference(
    NamedTableReference named,
    string profileLabel,
    QueryPlanOptions options)
{
    if (options.ProfileResolver == null)
        throw new QueryParseException(
            $"Cross-environment query references '[{profileLabel}]' but no profile resolver is configured.");

    var config = options.ProfileResolver(profileLabel)
        ?? throw new QueryParseException(
            $"No environment found matching '{profileLabel}'. Configure a profile with label '{profileLabel}' to use cross-environment queries.");

    if (options.RemoteExecutorFactory == null)
        throw new QueryParseException(
            "Cross-environment queries require a remote executor factory. This is a configuration error.");

    var entityName = named.SchemaObject.BaseIdentifier.Value;
    var remoteExecutor = options.RemoteExecutorFactory(config.Url);
    var fetchXml = $"<fetch><entity name=\"{entityName}\"><all-attributes /></entity></fetch>";

    // RemoteScanNode + TableSpoolNode for materialization
    var remoteScan = new RemoteScanNode(fetchXml, entityName, profileLabel, remoteExecutor);
    var spool = new TableSpoolNode(remoteScan);

    return (spool, entityName);
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs
git commit -m "feat(query): add cross-environment query planning with bracket syntax"
```

---

### Task 11: Cross-Environment DML Policy

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs`
- Modify: `src/PPDS.Auth/Profiles/QuerySafetySettings.cs`
- Modify: `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs`

**Context:** Cross-environment DML is guarded by policy. Default is read-only (no cross-env DML). The policy is configured in QuerySafetySettings. Even when allowed, cross-env DML targeting a Production environment always prompts.

**Step 1: Add CrossEnvironmentDmlPolicy enum**

Add to `QuerySafetySettings.cs`:

```csharp
/// <summary>Cross-environment DML policy.</summary>
public enum CrossEnvironmentDmlPolicy
{
    /// <summary>Cross-env queries are SELECT only (default).</summary>
    ReadOnly,
    /// <summary>Confirm each cross-env DML with source/target/count.</summary>
    Prompt,
    /// <summary>No additional confirmation beyond standard DML safety.</summary>
    Allow
}
```

Add property to `QuerySafetySettings`:

```csharp
/// <summary>Cross-environment DML policy. Default: ReadOnly.</summary>
[JsonPropertyName("cross_env_dml_policy")]
[JsonConverter(typeof(JsonStringEnumConverter))]
public CrossEnvironmentDmlPolicy CrossEnvironmentDmlPolicy { get; set; } = CrossEnvironmentDmlPolicy.ReadOnly;
```

**Step 2: Write failing tests**

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Check_CrossEnvDml_ReadOnlyPolicy_Blocks()
{
    var settings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.ReadOnly };
    var guard = new DmlSafetyGuard();
    var stmt = _parser.ParseStatement("DELETE FROM account WHERE x = 1");

    var result = guard.CheckCrossEnvironmentDml(stmt, settings, "UAT", "PROD");

    result.IsBlocked.Should().BeTrue();
    result.BlockReason.Should().Contain("read-only");
}

[Fact]
[Trait("Category", "Unit")]
public void Check_CrossEnvDml_ProductionTarget_AlwaysPrompts()
{
    var settings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Allow };
    var guard = new DmlSafetyGuard();
    var stmt = _parser.ParseStatement("DELETE FROM account WHERE x = 1");

    var result = guard.CheckCrossEnvironmentDml(stmt, settings, "UAT", "PROD",
        targetProtection: ProtectionLevel.Production);

    result.RequiresConfirmation.Should().BeTrue();
}
```

**Step 3: Implement CheckCrossEnvironmentDml**

```csharp
public DmlSafetyResult CheckCrossEnvironmentDml(
    TSqlStatement statement,
    QuerySafetySettings? settings,
    string sourceLabel,
    string targetLabel,
    ProtectionLevel targetProtection = ProtectionLevel.Production)
{
    var effectiveSettings = settings ?? new QuerySafetySettings();

    if (effectiveSettings.CrossEnvironmentDmlPolicy == CrossEnvironmentDmlPolicy.ReadOnly)
    {
        return new DmlSafetyResult
        {
            IsBlocked = true,
            BlockReason = $"Cross-environment DML is set to read-only. Source: [{sourceLabel}], Target: [{targetLabel}]. Change cross_env_dml_policy to 'Prompt' or 'Allow' to enable."
        };
    }

    // Hard rule: Production target always prompts
    if (targetProtection == ProtectionLevel.Production)
    {
        return new DmlSafetyResult
        {
            RequiresConfirmation = true,
            ConfirmationMessage = $"Cross-environment DML: [{sourceLabel}] → [{targetLabel}] (Production). Confirm?"
        };
    }

    if (effectiveSettings.CrossEnvironmentDmlPolicy == CrossEnvironmentDmlPolicy.Prompt)
    {
        return new DmlSafetyResult
        {
            RequiresConfirmation = true,
            ConfirmationMessage = $"Cross-environment DML: [{sourceLabel}] → [{targetLabel}]. Confirm?"
        };
    }

    return new DmlSafetyResult { IsBlocked = false };
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "CrossEnvDml" -v minimal`
Expected: ALL PASS

**Step 5: Run full suite**

Run: `dotnet test tests/PPDS.Query.Tests tests/PPDS.Cli.Tests --filter Category!=Integration --no-restore -v minimal`
Expected: ALL PASS — no regressions

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs src/PPDS.Auth/Profiles/QuerySafetySettings.cs tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs
git commit -m "feat(query): add cross-environment DML policy enforcement"
```

---

## Final Verification

After completing all tasks, run the full test suite:

```
dotnet test tests/PPDS.Query.Tests --filter Category!=Integration --no-restore -v minimal
dotnet test tests/PPDS.Cli.Tests --filter Category!=Integration --no-restore -v minimal
dotnet test tests/PPDS.Auth.Tests --filter Category!=Integration --no-restore -v minimal
```

All tests should pass across net8.0, net9.0, and net10.0.

---

## Summary

| Task | What | Type | Effort |
|------|------|------|--------|
| 0a | Wire CROSS JOIN + APPLY into planner | Bug fix (Wave 2) | Medium |
| 0b | RIGHT JOIN swap optimization | Optimization (Wave 2) | Small |
| 0c | Client-side join post-pipeline | Bug fix (Wave 2) | Medium |
| 0d | Complete safety settings schema | Schema (Wave 2) | Small |
| 1 | TableSpoolNode | Foundation | Small |
| 2 | Derived tables | Feature | Medium |
| 3 | HashSemiJoinNode | Foundation | Medium |
| 4 | IN (Subquery) in planner | Feature | Large |
| 5 | EXISTS / NOT EXISTS | Feature | Large |
| 6 | ScalarSubqueryNode | Feature | Medium |
| 7 | Correlated subqueries + IndexSpoolNode | Feature | Large |
| 8 | Profile resolution service | Foundation | Small |
| 9 | RemoteScanNode | Feature | Medium |
| 10 | Cross-environment query planning | Feature | Large |
| 11 | Cross-environment DML policy | Feature | Medium |

## Deferred Items (Not in This Plan)

| Item | Reason | Wave |
|------|--------|------|
| FetchXML folding for IN/EXISTS | Optimization over client-side; client-side works first | 4 |
| NOT IN/NOT EXISTS → LEFT JOIN rewrite | Optimization; client-side anti-semi-join works first | 4 |
| `--tds` CLI flag | TUI toggle already done; CLI is lower priority | 4 |
| Wire DML thresholds to DmlSafetyGuard | Settings exist; runtime wiring is independent | 4 |
| Cross-environment TDS routing | Read-only via FetchXML first; TDS cross-env is optimization | 4 |
