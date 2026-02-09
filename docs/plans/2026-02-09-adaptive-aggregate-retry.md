# Adaptive Aggregate Retry Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make partitioned aggregate queries self-correcting by retrying failed partitions with recursive date-range splitting, so COUNT(*) and other aggregates return correct results regardless of data distribution skew.

**Architecture:** Introduce an `AdaptiveAggregateScanNode` that wraps each partition's `FetchXmlScanNode`. On a Dataverse 50K aggregate limit error, the node splits its date range in half and retries both halves recursively. The existing `ParallelPartitionNode` orchestrates concurrent execution unchanged; the adaptive behavior is encapsulated per-partition. The `ParallelPartitionNode` error-wrapping logic is removed since partitions now self-heal.

**Tech Stack:** C# (.NET 8/9/10), Terminal.Gui, xUnit, Moq

---

## Background

The Dataverse `AggregateQueryRecordLimit` (50K) fails aggregate FetchXML queries that scan more than 50,000 records. The current partitioning system divides date ranges uniformly, but real data is skewed — bulk imports can concentrate hundreds of thousands of records in a single day. When a partition exceeds 50K, the error propagates and the entire query fails.

The fix: each partition handles its own retry with binary splitting. This guarantees convergence for any data distribution.

---

### Task 1: Create `AdaptiveAggregateScanNode`

**Files:**
- Create: `src/PPDS.Dataverse/Query/Planning/Nodes/AdaptiveAggregateScanNode.cs`

**Step 1: Write the node**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Wraps a FetchXML aggregate scan with adaptive retry. When the Dataverse
/// 50K AggregateQueryRecordLimit is hit, splits the date range in half and
/// retries both halves recursively. Guarantees convergence for any data
/// distribution.
/// </summary>
public sealed class AdaptiveAggregateScanNode : IQueryPlanNode
{
    /// <summary>The template FetchXML (without date range filter).</summary>
    public string TemplateFetchXml { get; }

    /// <summary>The entity logical name being queried.</summary>
    public string EntityLogicalName { get; }

    /// <summary>Inclusive start of this partition's date range.</summary>
    public DateTime RangeStart { get; }

    /// <summary>Exclusive end of this partition's date range.</summary>
    public DateTime RangeEnd { get; }

    /// <summary>Current recursion depth (0 = original partition).</summary>
    public int Depth { get; }

    /// <summary>Maximum recursion depth to prevent infinite splitting.</summary>
    public const int MaxDepth = 15;

    /// <inheritdoc />
    public string Description =>
        $"AdaptiveAggregateScan: {EntityLogicalName} [{RangeStart:yyyy-MM-dd} .. {RangeEnd:yyyy-MM-dd})" +
        (Depth > 0 ? $" depth={Depth}" : "");

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>Initializes a new instance of the <see cref="AdaptiveAggregateScanNode"/> class.</summary>
    public AdaptiveAggregateScanNode(
        string templateFetchXml,
        string entityLogicalName,
        DateTime rangeStart,
        DateTime rangeEnd,
        int depth = 0)
    {
        TemplateFetchXml = templateFetchXml ?? throw new ArgumentNullException(nameof(templateFetchXml));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        Depth = depth;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Inject date range filter into template FetchXML
        var fetchXml = InjectDateRangeFilter(TemplateFetchXml, RangeStart, RangeEnd);
        var scanNode = new FetchXmlScanNode(fetchXml, EntityLogicalName, autoPage: false);

        List<QueryRow>? rows = null;

        try
        {
            // Try executing the aggregate query for this date range
            rows = new List<QueryRow>();
            await foreach (var row in scanNode.ExecuteAsync(context, cancellationToken))
            {
                rows.Add(row);
            }
        }
        catch (Exception ex) when (IsAggregateLimitExceeded(ex) && Depth < MaxDepth)
        {
            // This partition is too large — split and retry
            rows = null;

            var midTicks = RangeStart.Ticks + (RangeEnd.Ticks - RangeStart.Ticks) / 2;
            var midPoint = new DateTime(midTicks, DateTimeKind.Utc);

            // Guard: if the range can't be split further (start == mid), give up
            if (midPoint <= RangeStart || midPoint >= RangeEnd)
            {
                throw new Execution.QueryExecutionException(
                    Execution.QueryErrorCode.AggregateLimitExceeded,
                    $"Aggregate query exceeded the Dataverse 50,000 record limit and the date range " +
                    $"[{RangeStart:O} .. {RangeEnd:O}) cannot be split further.",
                    ex);
            }

            var leftNode = new AdaptiveAggregateScanNode(
                TemplateFetchXml, EntityLogicalName, RangeStart, midPoint, Depth + 1);
            var rightNode = new AdaptiveAggregateScanNode(
                TemplateFetchXml, EntityLogicalName, midPoint, RangeEnd, Depth + 1);

            // Execute both halves sequentially (parallelism is handled by the
            // parent ParallelPartitionNode across partitions, not within one)
            await foreach (var row in leftNode.ExecuteAsync(context, cancellationToken))
            {
                yield return row;
            }

            await foreach (var row in rightNode.ExecuteAsync(context, cancellationToken))
            {
                yield return row;
            }
        }

        // Yield collected rows (only reached if the initial attempt succeeded)
        if (rows != null)
        {
            foreach (var row in rows)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Checks whether an exception indicates the Dataverse 50K aggregate limit was exceeded.
    /// </summary>
    private static bool IsAggregateLimitExceeded(Exception ex)
    {
        var current = ex;
        while (current != null)
        {
            if (current.Message.Contains("AggregateQueryRecordLimit", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("aggregate operation exceeded", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("maximum record limit of 50000", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Injects a createdon date range filter into FetchXML.
    /// Reuses the same approach as QueryPlanner.InjectDateRangeFilter.
    /// </summary>
    internal static string InjectDateRangeFilter(string fetchXml, DateTime start, DateTime end)
    {
        var startStr = start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var endStr = end.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var filterXml =
            $"    <filter type=\"and\">\n" +
            $"      <condition attribute=\"createdon\" operator=\"ge\" value=\"{startStr}\" />\n" +
            $"      <condition attribute=\"createdon\" operator=\"lt\" value=\"{endStr}\" />\n" +
            $"    </filter>";

        var entityCloseIndex = fetchXml.LastIndexOf("</entity>", StringComparison.Ordinal);
        if (entityCloseIndex < 0)
        {
            throw new InvalidOperationException("FetchXML does not contain a closing </entity> tag.");
        }

        return fetchXml[..entityCloseIndex] + filterXml + "\n" + fetchXml[entityCloseIndex..];
    }
}
```

**Step 2: Verify it builds**

Run: `dotnet build src/PPDS.Dataverse --no-restore -v q`
Expected: Build succeeded, 0 errors

**Step 3: Commit**

```bash
git add src/PPDS.Dataverse/Query/Planning/Nodes/AdaptiveAggregateScanNode.cs
git commit -m "feat(query): add AdaptiveAggregateScanNode with recursive retry on 50K limit"
```

---

### Task 2: Write unit tests for `AdaptiveAggregateScanNode`

**Files:**
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/AdaptiveAggregateScanNodeTests.cs`

**Step 1: Write tests**

The test file uses inline mock nodes (same pattern as `ParallelPartitionNodeTests.cs:39-60`). The key scenarios:

1. **Success on first attempt** — scan returns rows, no retry
2. **Single retry** — first attempt throws 50K error, both halves succeed, results are merged
3. **Multiple retry levels** — first attempt fails, one half fails again, sub-halves succeed
4. **Max depth exceeded** — throws `QueryExecutionException` when `MaxDepth` is reached
5. **Non-aggregate errors propagate** — a regular exception is NOT caught by the retry logic
6. **Unsplittable range** — when start == end ticks, throws rather than infinite loop

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class AdaptiveAggregateScanNodeTests
{
    private static QueryPlanContext CreateContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockExecutor.Object, new ExpressionEvaluator());
    }

    [Fact]
    public void Description_IncludesEntityAndDateRange()
    {
        var node = new AdaptiveAggregateScanNode(
            "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='count' aggregate='count'/></entity></fetch>",
            "account",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("account", node.Description);
        Assert.Contains("2020", node.Description);
        Assert.Contains("2025", node.Description);
        Assert.DoesNotContain("depth=", node.Description); // depth 0 is omitted
    }

    [Fact]
    public void Description_ShowsDepthWhenNonZero()
    {
        var node = new AdaptiveAggregateScanNode(
            "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='count' aggregate='count'/></entity></fetch>",
            "account",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            depth: 3);

        Assert.Contains("depth=3", node.Description);
    }

    [Fact]
    public void InjectDateRangeFilter_InsertsFilterBeforeEntityClose()
    {
        var fetchXml = "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='count' aggregate='count'/></entity></fetch>";
        var start = new DateTime(2020, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = AdaptiveAggregateScanNode.InjectDateRangeFilter(fetchXml, start, end);

        Assert.Contains("operator=\"ge\"", result);
        Assert.Contains("operator=\"lt\"", result);
        Assert.Contains("2020-06-15", result);
        Assert.Contains("2021-01-01", result);
        Assert.Contains("</entity>", result); // closing tag still present
    }

    [Fact]
    public void InjectDateRangeFilter_ThrowsForInvalidFetchXml()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AdaptiveAggregateScanNode.InjectDateRangeFilter("<fetch></fetch>", DateTime.UtcNow, DateTime.UtcNow));
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~AdaptiveAggregateScanNode" --no-restore -v n`
Expected: 4 passed, 0 failed

**Step 3: Commit**

```bash
git add tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/AdaptiveAggregateScanNodeTests.cs
git commit -m "test(query): add AdaptiveAggregateScanNode unit tests"
```

---

### Task 3: Wire `AdaptiveAggregateScanNode` into `QueryPlanner`

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs:1020-1054`

**Step 1: Update `PlanAggregateWithPartitioning` to wrap each partition in `AdaptiveAggregateScanNode`**

Replace the partition loop at lines 1042-1054 so each partition uses `AdaptiveAggregateScanNode` instead of bare `FetchXmlScanNode`. The `AdaptiveAggregateScanNode` stores the template FetchXML (pre-date-filter) and the date range, so it can self-split on failure.

Change this block (lines 1041-1054):
```csharp
        // Create a FetchXmlScanNode per partition, each with date range filter injected
        var partitionNodes = new List<IQueryPlanNode>();
        foreach (var partition in partitions)
        {
            var partitionedFetchXml = InjectDateRangeFilter(
                enrichedFetchXml, partition.Start, partition.End);

            var scanNode = new FetchXmlScanNode(
                partitionedFetchXml,
                entityName,
                autoPage: false); // Aggregate queries return a single page

            partitionNodes.Add(scanNode);
        }
```

To:
```csharp
        // Create an AdaptiveAggregateScanNode per partition. Each node stores the
        // template FetchXML and its date range, enabling recursive retry with
        // binary splitting if a partition exceeds the 50K aggregate limit.
        var partitionNodes = new List<IQueryPlanNode>();
        foreach (var partition in partitions)
        {
            var adaptiveNode = new AdaptiveAggregateScanNode(
                enrichedFetchXml,
                entityName,
                partition.Start,
                partition.End);

            partitionNodes.Add(adaptiveNode);
        }
```

**Step 2: Verify it builds**

Run: `dotnet build src/PPDS.Dataverse --no-restore -v q`
Expected: Build succeeded, 0 errors

**Step 3: Commit**

```bash
git add src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs
git commit -m "refactor(query): wire AdaptiveAggregateScanNode into partitioned aggregate plans"
```

---

### Task 4: Remove aggregate-limit wrapping from `ParallelPartitionNode`

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ParallelPartitionNode.cs:96-104`

**Step 1: Simplify the producer catch block**

Since `AdaptiveAggregateScanNode` now handles aggregate limit retries per-partition, the `ParallelPartitionNode` no longer needs to detect and wrap aggregate limit errors. It should just propagate whatever exception reaches it.

Change the catch block at lines 96-104:
```csharp
            catch (Exception ex)
            {
                // Detect Dataverse AggregateQueryRecordLimit (50K) failures
                // and wrap in a structured QueryExecutionException so the CLI
                // can map to ErrorCodes.Query.AggregateLimitExceeded.
                var wrapped = WrapIfAggregateLimitExceeded(ex);
                channel.Writer.Complete(wrapped);
            }
```

To:
```csharp
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
```

**Step 2: Remove the now-dead helper methods**

Remove these methods (lines 121-167):
- `WrapIfAggregateLimitExceeded`
- `ContainsAggregateLimitMessage`
- `CreateAggregateLimitException`

**Step 3: Verify it builds**

Run: `dotnet build src/PPDS.Dataverse --no-restore -v q`
Expected: Build succeeded, 0 errors

**Step 4: Run existing tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "Category=PlanUnit" --no-restore -v q`
Expected: All pass (the ParallelPartitionNode tests don't test aggregate limit wrapping directly)

**Step 5: Commit**

```bash
git add src/PPDS.Dataverse/Query/Planning/Nodes/ParallelPartitionNode.cs
git commit -m "refactor(query): remove aggregate-limit wrapping from ParallelPartitionNode

AdaptiveAggregateScanNode now handles retry per-partition, so the
orchestrator no longer needs aggregate-specific error detection."
```

---

### Task 5: Add integration-style tests for the adaptive retry flow

**Files:**
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/AdaptiveAggregateScanNodeTests.cs`

**Step 1: Add execution tests using a mock `IQueryExecutor`**

These tests verify the retry behavior by using a mock executor that throws the 50K error for specific FetchXML date ranges. The key test pattern:

- Create a mock `IQueryExecutor` that inspects the FetchXML date filter
- For "wide" date ranges, throw a `FaultException` with the 50K message
- For "narrow" date ranges (after splitting), return aggregate results
- Verify that the node retries and yields merged results

```csharp
    // Add these tests to the existing AdaptiveAggregateScanNodeTests class:

    /// <summary>
    /// Mock executor that throws the 50K error when the date range spans more
    /// than the given threshold, and returns a count result otherwise.
    /// </summary>
    private static IQueryExecutor CreateThresholdExecutor(TimeSpan maxRangeBeforeFailure, long countPerPartition)
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns<string, int?, string?, bool, CancellationToken>((fetchXml, _, _, _, _) =>
            {
                // Parse date range from the injected filter to determine if this partition is "too wide"
                // For simplicity, check if the FetchXML contains a date filter and use the threshold
                if (IsDateRangeWiderThan(fetchXml, maxRangeBeforeFailure))
                {
                    throw new InvalidOperationException(
                        "The maximum record limit of 50000 is exceeded. " +
                        "Reduce the number of aggregated or grouped records.");
                }

                // Return a simple count result
                var result = new QueryResult
                {
                    EntityLogicalName = "account",
                    Columns = new List<QueryColumn>
                    {
                        new() { LogicalName = "count" }
                    },
                    Records = new List<IReadOnlyDictionary<string, QueryValue>>
                    {
                        new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["count"] = QueryValue.Simple(countPerPartition)
                        }
                    },
                    Count = 1,
                    MoreRecords = false,
                    PageNumber = 1
                };
                return Task.FromResult(result);
            });

        return mockExecutor.Object;
    }

    /// <summary>
    /// Checks if the FetchXML date filter spans more than the given threshold.
    /// Parses the ge/lt condition values from the injected filter.
    /// </summary>
    private static bool IsDateRangeWiderThan(string fetchXml, TimeSpan threshold)
    {
        // Simple string parsing for test purposes
        var geIndex = fetchXml.IndexOf("operator=\"ge\" value=\"", StringComparison.Ordinal);
        var ltIndex = fetchXml.IndexOf("operator=\"lt\" value=\"", StringComparison.Ordinal);

        if (geIndex < 0 || ltIndex < 0)
            return true; // No filter = full range = too wide

        var geStart = geIndex + "operator=\"ge\" value=\"".Length;
        var geEnd = fetchXml.IndexOf('"', geStart);
        var ltStart = ltIndex + "operator=\"lt\" value=\"".Length;
        var ltEnd = fetchXml.IndexOf('"', ltStart);

        if (DateTime.TryParse(fetchXml[geStart..geEnd], out var start)
            && DateTime.TryParse(fetchXml[ltStart..ltEnd], out var end))
        {
            return (end - start) > threshold;
        }

        return true;
    }

    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttempt_WhenUnderLimit()
    {
        // Arrange: executor always succeeds (wide threshold)
        var executor = CreateThresholdExecutor(TimeSpan.FromDays(3650), countPerPartition: 42000);
        var context = new QueryPlanContext(executor, new ExpressionEvaluator());
        var node = new AdaptiveAggregateScanNode(
            "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='count' aggregate='count'/></entity></fetch>",
            "account",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        // Assert: single result row
        Assert.Single(rows);
        Assert.Equal(42000L, Convert.ToInt64(rows[0].Values["count"].Value));
    }

    [Fact]
    public async Task ExecuteAsync_RetriesWithSplit_WhenAggregateLimitExceeded()
    {
        // Arrange: executor fails for ranges > 1 year, succeeds for <=1 year
        // 5-year range will fail, then split to 2.5-year halves which also fail,
        // then split to ~1.25-year quarters which succeed
        var executor = CreateThresholdExecutor(TimeSpan.FromDays(365), countPerPartition: 10000);
        var context = new QueryPlanContext(executor, new ExpressionEvaluator());
        var node = new AdaptiveAggregateScanNode(
            "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='count' aggregate='count'/></entity></fetch>",
            "account",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        // Assert: multiple result rows from sub-partitions (MergeAggregateNode
        // will combine them; this node just yields partial results)
        Assert.True(rows.Count > 1, $"Expected multiple rows from split partitions, got {rows.Count}");
        Assert.All(rows, r => Assert.Equal(10000L, Convert.ToInt64(r.Values["count"].Value)));
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesNonAggregateErrors()
    {
        // Arrange: executor throws a non-aggregate error
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var context = new QueryPlanContext(mockExecutor.Object, new ExpressionEvaluator());
        var node = new AdaptiveAggregateScanNode(
            "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='count' aggregate='count'/></entity></fetch>",
            "account",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Act & Assert: non-aggregate errors are NOT caught
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(context)) { }
        });
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var executor = CreateThresholdExecutor(TimeSpan.FromDays(3650), countPerPartition: 1);
        var context = new QueryPlanContext(executor, new ExpressionEvaluator());
        var node = new AdaptiveAggregateScanNode(
            "<fetch aggregate='true'><entity name='account'><attribute name='accountid' alias='count' aggregate='count'/></entity></fetch>",
            "account",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(context, cts.Token)) { }
        });
    }
```

**Step 2: Run all AdaptiveAggregateScanNode tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~AdaptiveAggregateScanNode" --no-restore -v n`
Expected: All pass (8 tests)

**Step 3: Commit**

```bash
git add tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/AdaptiveAggregateScanNodeTests.cs
git commit -m "test(query): add adaptive retry execution tests with threshold mock"
```

---

### Task 6: Run full test suite and verify

**Step 1: Build the full solution**

Run: `dotnet build --no-restore -v q`
Expected: Build succeeded, 0 errors

**Step 2: Run all non-integration tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "Category!=Integration" --no-restore -v q`
Expected: All pass (1629+ tests)

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category!=Integration" --no-restore -v q`
Expected: All pass (1892+ tests)

**Step 3: Verify no dead code**

Confirm these are no longer referenced and can be safely removed (done in Task 4):
- `ParallelPartitionNode.WrapIfAggregateLimitExceeded`
- `ParallelPartitionNode.ContainsAggregateLimitMessage`
- `ParallelPartitionNode.CreateAggregateLimitException`

**Step 4: Final commit if any fixups needed**

---

## Summary

| Component | Change |
|-----------|--------|
| `AdaptiveAggregateScanNode` (NEW) | Wraps each partition scan; catches 50K error → splits date range in half → retries recursively |
| `QueryPlanner.PlanAggregateWithPartitioning` | Uses `AdaptiveAggregateScanNode` instead of bare `FetchXmlScanNode` |
| `ParallelPartitionNode` | Simplified: removed aggregate-specific error wrapping (now handled per-partition) |
| `DateRangePartitioner` | Unchanged — still provides optimistic initial splits |
| `MergeAggregateNode` | Unchanged — sums partial results from more sub-partitions transparently |

**Execution flow after this change:**

```
SELECT COUNT(*) FROM account  (200K records, bulk-imported on one day)

1. Metadata fetch: estimated 200K records, date range 2019-2025  (~500ms)
2. Planner: ceil(200K / 40K) = 5 partitions, evenly spaced
3. ParallelPartitionNode executes 5 AdaptiveAggregateScanNodes in parallel:
   - Partition 0 [2019, 2020.2): 500 records → SUCCESS, count=500
   - Partition 1 [2020.2, 2021.4): 180K records → FAIL 50K
     → Split to [2020.2, 2020.9) and [2020.9, 2021.4)
       → [2020.2, 2020.9): 180K records → FAIL
         → Split to [2020.2, 2020.55) and [2020.55, 2020.9)
           → [2020.2, 2020.55): 90K → FAIL → splits again...
           → Eventually converges to ~4-5 sub-partitions under 50K each
       → [2020.9, 2021.4): 200 records → SUCCESS
   - Partition 2-4: small → SUCCESS
4. MergeAggregateNode sums all partial counts → 200,000 (exact)
```
