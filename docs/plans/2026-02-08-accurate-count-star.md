# Accurate COUNT(*) via Partitioned Aggregates

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the stale `RetrieveTotalRecordCountRequest` default for bare `SELECT COUNT(*) FROM entity` with accurate partitioned aggregate execution that leverages our connection pool parallelism.

**Architecture:** Remove `CountOptimizedNode` as the default path for bare `COUNT(*)`. Instead, route bare `COUNT(*)` through the existing `PlanAggregateWithPartitioning` path (which already builds `MergeAggregateNode > ParallelPartitionNode > FetchXmlScanNode[]`). The caller (`SqlQueryService`) must provide `EstimatedRecordCount`, `MinDate`, `MaxDate`, and `PoolCapacity` in `QueryPlanOptions` by querying Dataverse metadata before planning. For entities with <50K records, the single FetchXML aggregate path still works fine (no partitioning needed).

**Tech Stack:** C# .NET 8+, xUnit, Moq, Terminal.Gui (TUI), Dataverse SDK

---

## Context

### Problem
- `CountOptimizedNode` uses `RetrieveTotalRecordCountRequest` which returns **stale cached counts** (can be hours behind)
- The FetchXML aggregate fallback hits the Dataverse **50,000 record aggregate limit** for any non-trivial table
- It's possible to solve this with partitioned aggregates — we already have the infrastructure (`DateRangePartitioner`, `ParallelPartitionNode`, `MergeAggregateNode`) but bare `COUNT(*)` bypasses it

### Solution
- Bare `COUNT(*)` goes through the normal aggregate path instead of the special `CountOptimizedNode` shortcut
- `SqlQueryService` provides metadata (record count estimate, date bounds, pool capacity) so the planner can decide whether to partition
- Small tables (<50K): single FetchXML aggregate (fast, accurate)
- Large tables (>50K): partitioned across date ranges, executed in parallel across connection pool

### What stays
- `CountOptimizedNode` stays in the codebase (not deleted) for future opt-in stale-count hint support
- `ShouldPartitionAggregate` logic unchanged — it already handles bare COUNT(*) with aggregates correctly
- `PlanAggregateWithPartitioning`, `DateRangePartitioner`, `ParallelPartitionNode`, `MergeAggregateNode` all unchanged

### Key files
- Planner: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs`
- Service: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`
- Interface: `src/PPDS.Dataverse/Query/IQueryExecutor.cs`
- Count node: `src/PPDS.Dataverse/Query/Planning/Nodes/CountOptimizedNode.cs`
- Options: `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs`
- Tests: `tests/PPDS.Dataverse.Tests/Query/Planning/QueryPlannerTests.cs`
- Service tests: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs` (may need creation)

---

### Task 1: Implement GetTotalRecordCountAsync in QueryExecutor

The default interface implementation returns `null`. The concrete `QueryExecutor` never overrides it. We need a real implementation that calls the Dataverse `RetrieveTotalRecordCountRequest` API so the caller can provide `EstimatedRecordCount` to the planner.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/QueryExecutor.cs`
- Test: `tests/PPDS.Dataverse.Tests/Query/QueryExecutorTests.cs` (create if needed)

**Step 1: Write the failing test**

Check if `tests/PPDS.Dataverse.Tests/Query/QueryExecutorTests.cs` exists. If not, create it. Add a test that verifies `GetTotalRecordCountAsync` calls the Dataverse SDK correctly.

Note: This requires mocking `IDataverseConnectionPool` and `IOrganizationServiceAsync2`. The test should verify the `RetrieveTotalRecordCountRequest` message is sent and the response count is returned.

```csharp
[Trait("Category", "PlanUnit")]
public class QueryExecutorGetCountTests
{
    [Fact]
    public async Task GetTotalRecordCountAsync_ReturnsCount()
    {
        // Arrange: mock pool + client that returns a RetrieveTotalRecordCountResponse
        var mockPool = new Mock<IDataverseConnectionPool>();
        // ... setup to return a client whose ExecuteAsync returns count=42000

        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var count = await executor.GetTotalRecordCountAsync("account");

        // Assert
        Assert.Equal(42000L, count);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~QueryExecutorGetCountTests" -v n`
Expected: FAIL — `GetTotalRecordCountAsync` returns `null` (default interface implementation)

**Step 3: Implement GetTotalRecordCountAsync override**

In `QueryExecutor.cs`, add the override. Use the connection pool to get a client, execute `RetrieveTotalRecordCountRequest`, return the count.

```csharp
public override async Task<long?> GetTotalRecordCountAsync(
    string entityLogicalName,
    CancellationToken cancellationToken = default)
{
    await using var client = await _connectionPool.GetClientAsync(
        cancellationToken: cancellationToken).ConfigureAwait(false);

    var request = new RetrieveTotalRecordCountRequest
    {
        EntityNames = new[] { entityLogicalName }
    };

    var response = (RetrieveTotalRecordCountResponse)await client.ExecuteAsync(
        request, cancellationToken).ConfigureAwait(false);

    if (response.EntityRecordCountCollection != null
        && response.EntityRecordCountCollection.TryGetValue(entityLogicalName, out var count))
    {
        return count;
    }

    return null;
}
```

Note: `IQueryExecutor.GetTotalRecordCountAsync` is a default interface method, not virtual. Change it to a regular interface method (remove the default implementation body) so `QueryExecutor` must implement it. This is a breaking change to the interface — update any other implementations (check test stubs).

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~QueryExecutorGetCountTests" -v n`
Expected: PASS

**Step 5: Commit**

```
feat(query): implement GetTotalRecordCountAsync in QueryExecutor
```

---

### Task 2: Add GetMinMaxCreatedOnAsync to IQueryExecutor

The planner needs `MinDate` and `MaxDate` for date-range partitioning. Add a method to retrieve the earliest and latest `createdon` values for an entity.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/IQueryExecutor.cs`
- Modify: `src/PPDS.Dataverse/Query/QueryExecutor.cs`
- Test: `tests/PPDS.Dataverse.Tests/Query/QueryExecutorTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task GetMinMaxCreatedOnAsync_ReturnsDateRange()
{
    // Arrange: mock pool + client that returns min/max via aggregate FetchXML
    // ...

    var executor = new QueryExecutor(mockPool.Object);

    // Act
    var (min, max) = await executor.GetMinMaxCreatedOnAsync("account");

    // Assert
    Assert.NotNull(min);
    Assert.NotNull(max);
    Assert.True(min < max);
}
```

**Step 2: Run test to verify it fails**

Expected: FAIL — method doesn't exist yet

**Step 3: Add interface method and implement**

In `IQueryExecutor.cs`:
```csharp
/// <summary>
/// Gets the min and max createdon dates for an entity, used for aggregate partitioning.
/// </summary>
Task<(DateTime? Min, DateTime? Max)> GetMinMaxCreatedOnAsync(
    string entityLogicalName,
    CancellationToken cancellationToken = default);
```

In `QueryExecutor.cs`, implement using two FetchXML aggregate queries (MIN and MAX of createdon):
```csharp
public async Task<(DateTime? Min, DateTime? Max)> GetMinMaxCreatedOnAsync(
    string entityLogicalName,
    CancellationToken cancellationToken = default)
{
    var fetchXml = $@"<fetch aggregate='true'>
      <entity name='{entityLogicalName}'>
        <attribute name='createdon' alias='mindate' aggregate='min' />
        <attribute name='createdon' alias='maxdate' aggregate='max' />
      </entity>
    </fetch>";

    // This aggregate query scans metadata, not rows — won't hit 50K limit
    var result = await ExecuteFetchXmlAsync(fetchXml, cancellationToken: cancellationToken)
        .ConfigureAwait(false);

    DateTime? min = null, max = null;
    if (result.Records.Count > 0)
    {
        var row = result.Records[0];
        if (row.TryGetValue("mindate", out var minVal) && minVal.Value is DateTime minDt)
            min = minDt;
        if (row.TryGetValue("maxdate", out var maxVal) && maxVal.Value is DateTime maxDt)
            max = maxDt;
    }

    return (min, max);
}
```

Important: MIN/MAX aggregate queries do NOT hit the 50K limit — they are metadata operations that scan the index, not individual records. This is safe for any table size.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~GetMinMaxCreatedOn" -v n`
Expected: PASS

**Step 5: Commit**

```
feat(query): add GetMinMaxCreatedOnAsync for aggregate partitioning
```

---

### Task 3: Route bare COUNT(*) through normal aggregate path

Remove the `IsBareCountStar` short-circuit in the planner so bare `COUNT(*)` flows through the standard aggregate transpilation path and benefits from `ShouldPartitionAggregate`.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/QueryPlannerTests.cs`

**Step 1: Write/update the failing tests**

Update the existing tests that assert `CountOptimizedNode` to expect the new behavior:

```csharp
[Fact]
public void Plan_BareCountStar_WithoutPartitioningOptions_ProducesFetchXmlScan()
{
    // Without EstimatedRecordCount, bare COUNT(*) uses single aggregate FetchXML
    var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");

    var result = _planner.Plan(stmt);

    // Should be a standard FetchXmlScanNode with aggregate FetchXML
    Assert.IsType<FetchXmlScanNode>(result.RootNode);
    Assert.Contains("aggregate", result.FetchXml);
}

[Fact]
public void Plan_BareCountStar_WithPartitioningOptions_ProducesPartitionedPlan()
{
    // With EstimatedRecordCount > limit, bare COUNT(*) gets partitioned
    var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");
    var options = MakePartitioningOptions(200_000);

    var result = _planner.Plan(stmt, options);

    var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
    var parallelNode = Assert.IsType<ParallelPartitionNode>(mergeNode.Input);
    Assert.True(parallelNode.Partitions.Count > 1);
}

[Fact]
public void Plan_BareCountStarWithAlias_PreservesAlias()
{
    var stmt = SqlParser.Parse("SELECT COUNT(*) AS total FROM account");
    var result = _planner.Plan(stmt);

    // Alias should appear in the FetchXML
    Assert.Contains("alias=\"total\"", result.FetchXml);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~BareCountStar" -v n`
Expected: FAIL — tests still expect `CountOptimizedNode`

**Step 3: Remove the IsBareCountStar short-circuit**

In `QueryPlanner.cs` `PlanSelect` method (around lines 109-115), comment out or remove the bare COUNT(*) shortcut:

```csharp
// Bare COUNT(*) now flows through the normal aggregate path so it benefits
// from partitioning for large tables. The CountOptimizedNode path is retained
// for future opt-in stale-count hint support.
//
// Previously:
// if (IsBareCountStar(statement))
// {
//     return PlanBareCountStar(statement);
// }
```

Do NOT delete `IsBareCountStar`, `PlanBareCountStar`, or `CountOptimizedNode` — they will be used for future hint-based stale count support.

**Step 4: Run all planner tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~QueryPlannerTests" -v n`
Expected: PASS (update any remaining tests that expected `CountOptimizedNode`)

**Step 5: Commit**

```
feat(query): route bare COUNT(*) through aggregate path for accuracy

Bare SELECT COUNT(*) FROM entity now uses FetchXML aggregate instead
of RetrieveTotalRecordCountRequest (which returns stale cached counts).
When EstimatedRecordCount exceeds 50K, the query is automatically
partitioned by date range and executed in parallel across the pool.
```

---

### Task 4: Wire up SqlQueryService to provide metadata for partitioning

`SqlQueryService.ExecuteAsync` currently builds `QueryPlanOptions` without `EstimatedRecordCount`, `MinDate`, `MaxDate`, or `PoolCapacity`. The planner can't partition without these. Add a pre-planning metadata fetch for aggregate queries.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`
- Modify: `src/PPDS.Cli/Services/Query/ISqlQueryService.cs` (if constructor signature changes)
- Test: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`

**Step 1: Identify what SqlQueryService needs**

`SqlQueryService` needs access to:
1. `IQueryExecutor.GetTotalRecordCountAsync` — already available via `_queryExecutor`
2. `IQueryExecutor.GetMinMaxCreatedOnAsync` — already available via `_queryExecutor`
3. Pool capacity — needs `IDataverseConnectionPool` or just the int value

Add `IDataverseConnectionPool` (or just `int poolCapacity`) to `SqlQueryService` constructor.

**Step 2: Write the failing test**

```csharp
[Trait("Category", "PlanUnit")]
public class SqlQueryServiceAggregateTests
{
    [Fact]
    public async Task ExecuteAsync_AggregateQuery_ProvidesPartitioningMetadata()
    {
        // Setup mock executor that returns count + date range
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor.Setup(x => x.GetTotalRecordCountAsync("account", It.IsAny<CancellationToken>()))
            .ReturnsAsync(200_000L);
        mockExecutor.Setup(x => x.GetMinMaxCreatedOnAsync("account", It.IsAny<CancellationToken>()))
            .ReturnsAsync((new DateTime(2020, 1, 1), new DateTime(2026, 1, 1)));
        // Setup ExecuteFetchXmlAsync to return aggregate results for each partition
        mockExecutor.Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult { /* single-row count result */ });

        var service = new SqlQueryService(mockExecutor.Object, poolCapacity: 4);

        var result = await service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT COUNT(*) FROM account" });

        // Verify that metadata methods were called
        mockExecutor.Verify(x => x.GetTotalRecordCountAsync("account", It.IsAny<CancellationToken>()), Times.Once);
        mockExecutor.Verify(x => x.GetMinMaxCreatedOnAsync("account", It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

**Step 3: Implement the pre-planning metadata fetch**

In `SqlQueryService.ExecuteAsync`, after parsing the AST and before building `QueryPlanOptions`, detect aggregate queries and fetch metadata:

```csharp
// For aggregate queries, fetch metadata needed for partitioning decisions.
// This enables the planner to partition large aggregates across the pool.
long? estimatedRecordCount = null;
DateTime? minDate = null, maxDate = null;

if (statement is SqlSelectStatement selectForMeta && selectForMeta.HasAggregates())
{
    var entityName = selectForMeta.GetEntityName();

    // Fetch estimated count and date range in parallel
    var countTask = _queryExecutor.GetTotalRecordCountAsync(entityName, cancellationToken);
    var dateTask = _queryExecutor.GetMinMaxCreatedOnAsync(entityName, cancellationToken);

    await Task.WhenAll(countTask, dateTask).ConfigureAwait(false);

    estimatedRecordCount = countTask.Result;
    (minDate, maxDate) = dateTask.Result;
}

var planOptions = new QueryPlanOptions
{
    MaxRows = request.TopOverride,
    PageNumber = request.PageNumber,
    PagingCookie = request.PagingCookie,
    IncludeCount = request.IncludeCount,
    UseTdsEndpoint = request.UseTdsEndpoint,
    OriginalSql = request.Sql,
    TdsQueryExecutor = _tdsQueryExecutor,
    DmlRowCap = dmlRowCap,
    PoolCapacity = _poolCapacity,
    EstimatedRecordCount = estimatedRecordCount,
    MinDate = minDate,
    MaxDate = maxDate
};
```

Key design note: The `RetrieveTotalRecordCountRequest` used here for the *estimate* is fine because it's only used for a *planning decision* (partition or not), not for the actual count result. A stale estimate that's off by 10% still produces correct partition boundaries — the actual FetchXML aggregate within each partition returns the accurate count.

Add `_poolCapacity` field to the class, populated from constructor:

```csharp
private readonly int _poolCapacity;

public SqlQueryService(
    IQueryExecutor queryExecutor,
    ITdsQueryExecutor? tdsQueryExecutor = null,
    IBulkOperationExecutor? bulkOperationExecutor = null,
    IMetadataQueryExecutor? metadataQueryExecutor = null,
    int poolCapacity = 1)
{
    // ...existing...
    _poolCapacity = poolCapacity;
}
```

Also apply the same metadata fetch logic to `ExecuteStreamingAsync`.

**Step 4: Update DI registration**

Find where `SqlQueryService` is registered (likely `ServiceRegistration.cs` or `ProfileServiceFactory.cs`) and pass `pool.GetTotalRecommendedParallelism()` as `poolCapacity`.

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~SqlQueryServiceAggregate" -v n`
Expected: PASS

**Step 6: Commit**

```
feat(query): wire SqlQueryService to provide partitioning metadata

SqlQueryService now fetches EstimatedRecordCount and MinDate/MaxDate
before planning aggregate queries. This enables the planner to
partition large aggregates (>50K records) across the connection pool.
The metadata fetch runs in parallel (count + date range) and uses
RetrieveTotalRecordCountRequest only as a planning estimate, not
as the final result — the actual count comes from accurate FetchXML
aggregates within each partition.
```

---

### Task 5: Update existing tests and verify end-to-end

Ensure all existing tests pass with the new flow and add edge case coverage.

**Files:**
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/QueryPlannerTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/CountOptimizedNodeTests.cs`

**Step 1: Update CountOptimizedNodeTests**

These tests are still valid — `CountOptimizedNode` still exists and works. No changes needed unless the interface change from Task 1 breaks the default implementation. Mark them with a comment noting they test the stale-count path for future hint support.

**Step 2: Update QueryPlannerTests for BareCountStar**

Replace the existing `Plan_BareCountStar_*` tests (which assert `CountOptimizedNode`) with tests asserting the new aggregate path behavior. These were already written in Task 3.

Also add:

```csharp
[Fact]
public void Plan_BareCountStar_BelowLimit_ProducesSingleAggregateScan()
{
    var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");
    var options = new QueryPlanOptions
    {
        EstimatedRecordCount = 30_000, // Below 50K limit
        MinDate = new DateTime(2020, 1, 1),
        MaxDate = new DateTime(2026, 1, 1),
        PoolCapacity = 4
    };

    var result = _planner.Plan(stmt, options);

    // Below limit: single scan, no partitioning
    Assert.IsType<FetchXmlScanNode>(result.RootNode);
}
```

**Step 3: Run the full test suite**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "Category=PlanUnit" -v n`
Expected: ALL PASS

Run: `dotnet test tests/PPDS.Cli.Tests -v n`
Expected: ALL PASS

**Step 4: Commit**

```
test(query): update tests for accurate COUNT(*) via partitioned aggregates
```

---

## Execution Plan Summary

| Task | What | Risk | Estimated changes |
|------|------|------|-------------------|
| 1 | Implement `GetTotalRecordCountAsync` in `QueryExecutor` | Low — additive | ~20 lines |
| 2 | Add `GetMinMaxCreatedOnAsync` | Low — additive | ~30 lines |
| 3 | Remove `IsBareCountStar` short-circuit in planner | Medium — changes behavior | ~5 lines removed, ~30 lines test updates |
| 4 | Wire `SqlQueryService` to provide metadata | Medium — integration point | ~40 lines + DI update |
| 5 | Update tests, verify end-to-end | Low — cleanup | Test updates |

## What this achieves

**Before:** `SELECT COUNT(*) FROM account` → stale cached count (fast, wrong) or 50K aggregate limit error (broken)

**After:** `SELECT COUNT(*) FROM account` on a 500K-record table with DOP 52 pool:
1. Pre-plan: fetch estimated count (500K) + date range (2020-2026) — 2 parallel API calls, <500ms
2. Plan: `DateRangePartitioner` creates 13 partitions of ~40K records each
3. Execute: `ParallelPartitionNode` runs all 13 FetchXML aggregates across 13 pool connections simultaneously
4. Merge: `MergeAggregateNode` sums the 13 partial counts into one accurate result
5. Result: accurate count in ~2-5 seconds
