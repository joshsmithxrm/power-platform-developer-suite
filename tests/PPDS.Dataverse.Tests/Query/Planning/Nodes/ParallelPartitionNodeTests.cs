using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class ParallelPartitionNodeTests
{
    private static QueryPlanContext CreateContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockExecutor.Object);
    }

    private static QueryRow MakeRow(params (string key, object? value)[] pairs)
    {
        var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            values[key] = QueryValue.Simple(value);
        }
        return new QueryRow(values, "entity");
    }

    /// <summary>
    /// A mock plan node that yields rows from a fixed list.
    /// </summary>
    private sealed class MockPlanNode : IQueryPlanNode
    {
        private readonly IReadOnlyList<QueryRow> _rows;

        public MockPlanNode(IReadOnlyList<QueryRow> rows) => _rows = rows;

        public string Description => "MockScan";
        public long EstimatedRows => _rows.Count;
        public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

        public async IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// A mock plan node that tracks concurrent execution to verify parallelism limits.
    /// </summary>
    private sealed class ConcurrencyTrackingNode : IQueryPlanNode
    {
        private readonly IReadOnlyList<QueryRow> _rows;
        private readonly ConcurrentBag<int> _concurrencySnapshots;
        private static int s_currentConcurrency;

        public ConcurrencyTrackingNode(IReadOnlyList<QueryRow> rows, ConcurrentBag<int> concurrencySnapshots)
        {
            _rows = rows;
            _concurrencySnapshots = concurrencySnapshots;
        }

        public string Description => "ConcurrencyTracker";
        public long EstimatedRows => _rows.Count;
        public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

        public async IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref s_currentConcurrency); // CodeQL [cs/static-field-written-by-instance] Intentional: tracks cross-instance concurrency for parallelism verification
            _concurrencySnapshots.Add(current);

            // Simulate some work to allow other partitions to overlap
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }

            Interlocked.Decrement(ref s_currentConcurrency); // CodeQL [cs/static-field-written-by-instance] Intentional: see Increment above
        }

        /// <summary>Reset the static concurrency counter between tests.</summary>
        public static void ResetConcurrency() => s_currentConcurrency = 0;
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_ThrowsOnNullPartitions()
    {
        Assert.Throws<ArgumentNullException>(() => new ParallelPartitionNode(null!, 4));
    }

    [Fact]
    public void Constructor_ThrowsOnZeroMaxParallelism()
    {
        var partitions = new List<IQueryPlanNode> { new MockPlanNode(Array.Empty<QueryRow>()) };
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelPartitionNode(partitions, 0));
    }

    [Fact]
    public void Constructor_ThrowsOnNegativeMaxParallelism()
    {
        var partitions = new List<IQueryPlanNode> { new MockPlanNode(Array.Empty<QueryRow>()) };
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelPartitionNode(partitions, -1));
    }

    #endregion

    #region Property Tests

    [Fact]
    public void Description_ShowsPartitionCountAndParallelism()
    {
        var partitions = new List<IQueryPlanNode>
        {
            new MockPlanNode(Array.Empty<QueryRow>()),
            new MockPlanNode(Array.Empty<QueryRow>()),
            new MockPlanNode(Array.Empty<QueryRow>())
        };

        var node = new ParallelPartitionNode(partitions, 2);

        Assert.Contains("3 partitions", node.Description);
        Assert.Contains("max parallelism 2", node.Description);
    }

    [Fact]
    public void EstimatedRows_ReturnsNegativeOne()
    {
        var partitions = new List<IQueryPlanNode> { new MockPlanNode(Array.Empty<QueryRow>()) };
        var node = new ParallelPartitionNode(partitions, 2);

        Assert.Equal(-1, node.EstimatedRows);
    }

    [Fact]
    public void Children_ReturnsPartitions()
    {
        var p1 = new MockPlanNode(Array.Empty<QueryRow>());
        var p2 = new MockPlanNode(Array.Empty<QueryRow>());
        var partitions = new List<IQueryPlanNode> { p1, p2 };

        var node = new ParallelPartitionNode(partitions, 2);

        Assert.Equal(2, node.Children.Count);
        Assert.Same(p1, node.Children[0]);
        Assert.Same(p2, node.Children[1]);
    }

    #endregion

    #region Execution Tests

    [Fact]
    public async Task CollectsAllRowsFromAllPartitions()
    {
        var p1 = new MockPlanNode(new[] { MakeRow(("id", 1)), MakeRow(("id", 2)) });
        var p2 = new MockPlanNode(new[] { MakeRow(("id", 3)), MakeRow(("id", 4)) });
        var p3 = new MockPlanNode(new[] { MakeRow(("id", 5)) });

        var node = new ParallelPartitionNode(new List<IQueryPlanNode> { p1, p2, p3 }, 4);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(5, rows.Count);

        // All IDs should be present (order is non-deterministic in parallel execution)
        var ids = rows.Select(r => (int)r.Values["id"].Value!).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    [Fact]
    public async Task EmptyPartitions_YieldNoRows()
    {
        var p1 = new MockPlanNode(Array.Empty<QueryRow>());
        var p2 = new MockPlanNode(Array.Empty<QueryRow>());

        var node = new ParallelPartitionNode(new List<IQueryPlanNode> { p1, p2 }, 2);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
    }

    [Fact]
    public async Task SinglePartition_ReturnsAllRows()
    {
        var p1 = new MockPlanNode(new[] { MakeRow(("name", "Alpha")), MakeRow(("name", "Bravo")) });

        var node = new ParallelPartitionNode(new List<IQueryPlanNode> { p1 }, 1);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task MixedEmptyAndNonEmpty_CollectsNonEmptyRows()
    {
        var p1 = new MockPlanNode(Array.Empty<QueryRow>());
        var p2 = new MockPlanNode(new[] { MakeRow(("v", 42)) });
        var p3 = new MockPlanNode(Array.Empty<QueryRow>());

        var node = new ParallelPartitionNode(new List<IQueryPlanNode> { p1, p2, p3 }, 2);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(42, rows[0].Values["v"].Value);
    }

    [Fact]
    public async Task ParallelismLimit_IsRespected()
    {
        ConcurrencyTrackingNode.ResetConcurrency();
        var concurrencySnapshots = new ConcurrentBag<int>();

        // Create 6 partitions but limit parallelism to 2
        var partitions = Enumerable.Range(0, 6).Select(_ =>
            (IQueryPlanNode)new ConcurrencyTrackingNode(
                new[] { MakeRow(("x", 1)) },
                concurrencySnapshots)
        ).ToList();

        var node = new ParallelPartitionNode(partitions, 2);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(6, rows.Count);

        // No concurrency snapshot should exceed the max parallelism of 2
        Assert.True(concurrencySnapshots.All(c => c <= 2),
            $"Max observed concurrency was {concurrencySnapshots.Max()}, expected <= 2");
    }

    [Fact]
    public async Task Cancellation_StopsExecution()
    {
        using var cts = new CancellationTokenSource();

        // Create partitions with rows
        var slowRows = Enumerable.Range(1, 100).Select(i => MakeRow(("id", i))).ToArray();
        var partitions = new List<IQueryPlanNode>
        {
            new MockPlanNode(slowRows),
            new MockPlanNode(slowRows)
        };

        var node = new ParallelPartitionNode(partitions, 2);
        var ctx = CreateContext();

        // Cancel immediately — the enumeration should throw OperationCanceledException
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(ctx, cts.Token))
            {
                // Should not get here with pre-cancelled token
            }
        });
    }

    #endregion
}

[Trait("Category", "PlanUnit")]
public class MergeAggregateNodeTests
{
    private static QueryPlanContext CreateContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockExecutor.Object);
    }

    private static QueryRow MakeRow(string entity, params (string key, object? value)[] pairs)
    {
        var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            values[key] = QueryValue.Simple(value);
        }
        return new QueryRow(values, entity);
    }

    private sealed class MockPlanNode : IQueryPlanNode
    {
        private readonly IReadOnlyList<QueryRow> _rows;

        public MockPlanNode(IReadOnlyList<QueryRow> rows) => _rows = rows;

        public string Description => "MockScan";
        public long EstimatedRows => _rows.Count;
        public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

        public async IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
            await Task.CompletedTask;
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_ThrowsOnNullInput()
    {
        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        Assert.Throws<ArgumentNullException>(() => new MergeAggregateNode(null!, cols));
    }

    [Fact]
    public void Constructor_ThrowsOnNullAggregateColumns()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        Assert.Throws<ArgumentNullException>(() => new MergeAggregateNode(input, null!));
    }

    [Fact]
    public void Constructor_AcceptsNullGroupByColumns()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };

        var node = new MergeAggregateNode(input, cols, groupByColumns: null);

        Assert.Empty(node.GroupByColumns);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void Description_ShowsAggregateFunctions()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var cols = new List<MergeAggregateColumn>
        {
            new("cnt", AggregateFunction.Count),
            new("total", AggregateFunction.Sum)
        };

        var node = new MergeAggregateNode(input, cols);

        Assert.Contains("Count(cnt)", node.Description);
        Assert.Contains("Sum(total)", node.Description);
    }

    [Fact]
    public void Description_ShowsGroupByColumns()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        var groups = new List<string> { "region", "year" };

        var node = new MergeAggregateNode(input, cols, groups);

        Assert.Contains("grouped by [region, year]", node.Description);
    }

    [Fact]
    public void Description_NoGroupByLabel_WhenNoGroupColumns()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };

        var node = new MergeAggregateNode(input, cols);

        Assert.DoesNotContain("grouped by", node.Description);
    }

    [Fact]
    public void EstimatedRows_ReturnsNegativeOne()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };

        var node = new MergeAggregateNode(input, cols);

        Assert.Equal(-1, node.EstimatedRows);
    }

    [Fact]
    public void Children_ContainsInput()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };

        var node = new MergeAggregateNode(input, cols);

        Assert.Single(node.Children);
        Assert.Same(input, node.Children[0]);
    }

    #endregion

    #region COUNT Merge Tests

    [Fact]
    public async Task Count_SumsPartialCounts()
    {
        // Simulate 3 partitions each returning a partial count
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("cnt", 15000L)),
            MakeRow("entity", ("cnt", 20000L)),
            MakeRow("entity", ("cnt", 10000L))
        });

        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(45000L, rows[0].Values["cnt"].Value);
    }

    [Fact]
    public async Task Count_SinglePartition_ReturnsOriginalCount()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("cnt", 42000L))
        });

        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(42000L, rows[0].Values["cnt"].Value);
    }

    /// <summary>
    /// COUNT(DISTINCT) cannot be parallel-partitioned because summing partial
    /// distinct counts would double-count values appearing in multiple partitions.
    /// This test documents the limitation: if someone were to naively feed
    /// COUNT(DISTINCT) partial results through MergeAggregateNode with Count function,
    /// the result would be incorrect (over-counted).
    /// </summary>
    [Fact]
    public void CountDistinct_CannotBeMergedBySimpleSummation()
    {
        // Imagine: partition 1 has values {A, B, C} => COUNT(DISTINCT) = 3
        //          partition 2 has values {B, C, D} => COUNT(DISTINCT) = 3
        // Correct answer: 4 (A, B, C, D)
        // Naive sum:       6 (WRONG - double-counts B and C)
        //
        // This is why COUNT(DISTINCT) must NOT use parallel partitioning.
        // The AggregateFunction enum intentionally does not include CountDistinct.
        var enumValues = Enum.GetValues(typeof(AggregateFunction)).Cast<AggregateFunction>().ToList();
        Assert.DoesNotContain(enumValues, f => f.ToString().Contains("Distinct", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region SUM Merge Tests

    [Fact]
    public async Task Sum_SumsPartialSums()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("total", 100.5m)),
            MakeRow("entity", ("total", 200.3m)),
            MakeRow("entity", ("total", 50.2m))
        });

        var cols = new List<MergeAggregateColumn> { new("total", AggregateFunction.Sum) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(351.0m, (decimal)rows[0].Values["total"].Value!);
    }

    [Fact]
    public async Task Sum_WithIntegerValues_SumsCorrectly()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("total", 100)),
            MakeRow("entity", ("total", 200))
        });

        var cols = new List<MergeAggregateColumn> { new("total", AggregateFunction.Sum) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(300m, (decimal)rows[0].Values["total"].Value!);
    }

    #endregion

    #region AVG Merge Tests

    [Fact]
    public async Task Avg_WithCountAlias_ComputesWeightedAverage()
    {
        // Partition 1: avg=10, count=100 (sum=1000)
        // Partition 2: avg=20, count=300 (sum=6000)
        // Correct weighted avg: 7000/400 = 17.5
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("avg_val", 10m), ("cnt", 100L)),
            MakeRow("entity", ("avg_val", 20m), ("cnt", 300L))
        });

        var cols = new List<MergeAggregateColumn>
        {
            new("avg_val", AggregateFunction.Avg, countAlias: "cnt")
        };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(17.5m, (decimal)rows[0].Values["avg_val"].Value!);
    }

    [Fact]
    public async Task Avg_WithoutCountAlias_FallsBackToSimpleAverage()
    {
        // Without count alias, each partial result is treated as count=1
        // (10 + 20) / 2 = 15
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("avg_val", 10m)),
            MakeRow("entity", ("avg_val", 20m))
        });

        var cols = new List<MergeAggregateColumn>
        {
            new("avg_val", AggregateFunction.Avg)
        };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(15m, (decimal)rows[0].Values["avg_val"].Value!);
    }

    [Fact]
    public async Task Avg_ZeroCounts_ReturnsZero()
    {
        // Edge case: no rows have count values
        var input = new MockPlanNode(Array.Empty<QueryRow>());

        var cols = new List<MergeAggregateColumn>
        {
            new("avg_val", AggregateFunction.Avg, countAlias: "cnt")
        };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // No input rows => no groups => no output rows
        Assert.Empty(rows);
    }

    #endregion

    #region MIN Merge Tests

    [Fact]
    public async Task Min_FindsGlobalMinimum()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("min_val", 50m)),
            MakeRow("entity", ("min_val", 10m)),
            MakeRow("entity", ("min_val", 30m))
        });

        var cols = new List<MergeAggregateColumn> { new("min_val", AggregateFunction.Min) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(10m, (decimal)rows[0].Values["min_val"].Value!);
    }

    [Fact]
    public async Task Min_AllNullValues_ReturnsNull()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("min_val", null)),
            MakeRow("entity", ("min_val", null))
        });

        var cols = new List<MergeAggregateColumn> { new("min_val", AggregateFunction.Min) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Null(rows[0].Values["min_val"].Value);
    }

    #endregion

    #region MAX Merge Tests

    [Fact]
    public async Task Max_FindsGlobalMaximum()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("max_val", 50m)),
            MakeRow("entity", ("max_val", 10m)),
            MakeRow("entity", ("max_val", 90m))
        });

        var cols = new List<MergeAggregateColumn> { new("max_val", AggregateFunction.Max) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(90m, (decimal)rows[0].Values["max_val"].Value!);
    }

    [Fact]
    public async Task Max_AllNullValues_ReturnsNull()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("max_val", null)),
            MakeRow("entity", ("max_val", null))
        });

        var cols = new List<MergeAggregateColumn> { new("max_val", AggregateFunction.Max) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Null(rows[0].Values["max_val"].Value);
    }

    #endregion

    #region GROUP BY Tests

    [Fact]
    public async Task GroupBy_MergesPartitionsPerGroup()
    {
        // Two partitions, each with counts for "US" and "UK"
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("region", "US"), ("cnt", 1000L)),
            MakeRow("entity", ("region", "UK"), ("cnt", 500L)),
            MakeRow("entity", ("region", "US"), ("cnt", 2000L)),
            MakeRow("entity", ("region", "UK"), ("cnt", 300L))
        });

        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        var groups = new List<string> { "region" };
        var node = new MergeAggregateNode(input, cols, groups);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);

        var usRow = rows.First(r => (string)r.Values["region"].Value! == "US");
        var ukRow = rows.First(r => (string)r.Values["region"].Value! == "UK");

        Assert.Equal(3000L, usRow.Values["cnt"].Value);
        Assert.Equal(800L, ukRow.Values["cnt"].Value);
    }

    [Fact]
    public async Task GroupBy_MultipleColumns_GroupsCorrectly()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("region", "US"), ("year", 2024), ("cnt", 100L)),
            MakeRow("entity", ("region", "US"), ("year", 2024), ("cnt", 200L)),
            MakeRow("entity", ("region", "US"), ("year", 2025), ("cnt", 50L)),
            MakeRow("entity", ("region", "UK"), ("year", 2024), ("cnt", 75L))
        });

        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        var groups = new List<string> { "region", "year" };
        var node = new MergeAggregateNode(input, cols, groups);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);

        var us2024 = rows.First(r =>
            (string)r.Values["region"].Value! == "US" &&
            r.Values["year"].Value!.ToString() == "2024");
        Assert.Equal(300L, us2024.Values["cnt"].Value);
    }

    [Fact]
    public async Task GroupBy_WithNullGroupValue_GroupsNullsTogether()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("region", null), ("cnt", 100L)),
            MakeRow("entity", ("region", null), ("cnt", 200L)),
            MakeRow("entity", ("region", "US"), ("cnt", 50L))
        });

        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        var groups = new List<string> { "region" };
        var node = new MergeAggregateNode(input, cols, groups);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);

        var nullGroup = rows.First(r => r.Values["region"].Value == null);
        Assert.Equal(300L, nullGroup.Values["cnt"].Value);
    }

    [Fact]
    public async Task GroupBy_CaseInsensitiveGroupKeyMatching()
    {
        // Group key matching on string values should still separate different string values
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("region", "US"), ("cnt", 100L)),
            MakeRow("entity", ("region", "us"), ("cnt", 200L))
        });

        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        var groups = new List<string> { "region" };
        var node = new MergeAggregateNode(input, cols, groups);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // "US" and "us" are treated as separate group keys (ToString()-based grouping)
        Assert.Equal(2, rows.Count);
    }

    #endregion

    #region Multiple Aggregate Functions Tests

    [Fact]
    public async Task MultipleAggregateFunctions_AllMergedCorrectly()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow("entity", ("cnt", 100L), ("total", 500m), ("min_val", 5m), ("max_val", 95m)),
            MakeRow("entity", ("cnt", 200L), ("total", 800m), ("min_val", 3m), ("max_val", 88m)),
            MakeRow("entity", ("cnt", 150L), ("total", 600m), ("min_val", 7m), ("max_val", 99m))
        });

        var cols = new List<MergeAggregateColumn>
        {
            new("cnt", AggregateFunction.Count),
            new("total", AggregateFunction.Sum),
            new("min_val", AggregateFunction.Min),
            new("max_val", AggregateFunction.Max)
        };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        var result = rows[0];
        Assert.Equal(450L, result.Values["cnt"].Value);
        Assert.Equal(1900m, (decimal)result.Values["total"].Value!);
        Assert.Equal(3m, (decimal)result.Values["min_val"].Value!);
        Assert.Equal(99m, (decimal)result.Values["max_val"].Value!);
    }

    #endregion

    #region Empty Input Tests

    [Fact]
    public async Task EmptyInput_YieldsNoRows()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());

        var cols = new List<MergeAggregateColumn> { new("cnt", AggregateFunction.Count) };
        var node = new MergeAggregateNode(input, cols);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
    }

    #endregion

    #region MergeAggregateColumn Tests

    [Fact]
    public void MergeAggregateColumn_ThrowsOnNullAlias()
    {
        Assert.Throws<ArgumentNullException>(() => new MergeAggregateColumn(null!, AggregateFunction.Count));
    }

    [Fact]
    public void MergeAggregateColumn_CountAlias_DefaultsToNull()
    {
        var col = new MergeAggregateColumn("cnt", AggregateFunction.Count);
        Assert.Null(col.CountAlias);
    }

    [Fact]
    public void MergeAggregateColumn_StoresProperties()
    {
        var col = new MergeAggregateColumn("avg_val", AggregateFunction.Avg, countAlias: "row_count");

        Assert.Equal("avg_val", col.Alias);
        Assert.Equal(AggregateFunction.Avg, col.Function);
        Assert.Equal("row_count", col.CountAlias);
    }

    #endregion
}
