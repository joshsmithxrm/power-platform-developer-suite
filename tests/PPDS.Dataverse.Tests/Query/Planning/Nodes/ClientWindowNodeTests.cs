using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Moq;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class ClientWindowNodeTests
{
    private static QueryPlanContext CreateContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockExecutor.Object, new ExpressionEvaluator());
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

    private static QueryRow MakeRow(params (string key, object? value)[] pairs)
    {
        var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            values[key] = QueryValue.Simple(value);
        }
        return new QueryRow(values, "account");
    }

    private static async Task<List<QueryRow>> ExecuteToListAsync(IQueryPlanNode node, QueryPlanContext ctx)
    {
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Helper: creates a compiled scalar expression that reads a column by name.
    /// </summary>
    private static CompiledScalarExpression ColumnExpr(string columnName)
    {
        return row => row.TryGetValue(columnName, out var qv) ? qv.Value : null;
    }

    /// <summary>
    /// Helper: creates a compiled order-by item for the given column and direction.
    /// </summary>
    private static CompiledOrderByItem OrderBy(string columnName, bool descending = false)
    {
        return new CompiledOrderByItem(columnName, ColumnExpr(columnName), descending);
    }

    #region ROW_NUMBER Tests

    [Fact]
    public async Task RowNumber_WithOrderBy_SequentialNumbers()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Charlie"), ("revenue", 300m)),
            MakeRow(("name", "Alice"), ("revenue", 100m)),
            MakeRow(("name", "Bob"), ("revenue", 200m))
        });

        // ROW_NUMBER() OVER (ORDER BY revenue ASC)
        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("revenue") })
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Equal(3, rows.Count);

        // Rows should be in original order but with rn assigned by sorted order
        // Original order: Charlie(300), Alice(100), Bob(200)
        // Sorted by revenue ASC: Alice(100)=1, Bob(200)=2, Charlie(300)=3
        // rn is assigned based on sort, but rows keep original order with rn values mapped

        // Find each row and check its rn
        var charlie = rows.First(r => (string)r.Values["name"].Value! == "Charlie");
        var alice = rows.First(r => (string)r.Values["name"].Value! == "Alice");
        var bob = rows.First(r => (string)r.Values["name"].Value! == "Bob");

        Assert.Equal(3, charlie.Values["rn"].Value);
        Assert.Equal(1, alice.Values["rn"].Value);
        Assert.Equal(2, bob.Values["rn"].Value);
    }

    [Fact]
    public async Task RowNumber_WithPartitionByAndOrderBy_ResetsPerPartition()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("owner", "A"), ("name", "A1"), ("revenue", 300m)),
            MakeRow(("owner", "A"), ("name", "A2"), ("revenue", 100m)),
            MakeRow(("owner", "B"), ("name", "B1"), ("revenue", 200m)),
            MakeRow(("owner", "B"), ("name", "B2"), ("revenue", 400m))
        });

        // ROW_NUMBER() OVER (PARTITION BY owner ORDER BY revenue ASC)
        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null,
                new CompiledScalarExpression[] { ColumnExpr("owner") },
                new[] { OrderBy("revenue") })
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Equal(4, rows.Count);

        // Partition A sorted by revenue ASC: A2(100)=1, A1(300)=2
        var a1 = rows.First(r => (string)r.Values["name"].Value! == "A1");
        var a2 = rows.First(r => (string)r.Values["name"].Value! == "A2");
        Assert.Equal(2, a1.Values["rn"].Value);
        Assert.Equal(1, a2.Values["rn"].Value);

        // Partition B sorted by revenue ASC: B1(200)=1, B2(400)=2
        var b1 = rows.First(r => (string)r.Values["name"].Value! == "B1");
        var b2 = rows.First(r => (string)r.Values["name"].Value! == "B2");
        Assert.Equal(1, b1.Values["rn"].Value);
        Assert.Equal(2, b2.Values["rn"].Value);
    }

    #endregion

    #region RANK Tests

    [Fact]
    public async Task Rank_WithTies_SameRankWithGaps()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "A"), ("score", 100)),
            MakeRow(("name", "B"), ("score", 200)),
            MakeRow(("name", "C"), ("score", 200)),
            MakeRow(("name", "D"), ("score", 300))
        });

        // RANK() OVER (ORDER BY score ASC)
        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rnk", "RANK", null, null,
                new[] { OrderBy("score") })
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Equal(4, rows.Count);

        var a = rows.First(r => (string)r.Values["name"].Value! == "A");
        var b = rows.First(r => (string)r.Values["name"].Value! == "B");
        var c = rows.First(r => (string)r.Values["name"].Value! == "C");
        var d = rows.First(r => (string)r.Values["name"].Value! == "D");

        // score=100 -> rank 1, score=200 -> rank 2 (tied), score=300 -> rank 4 (gap)
        Assert.Equal(1, a.Values["rnk"].Value);
        Assert.Equal(2, b.Values["rnk"].Value);
        Assert.Equal(2, c.Values["rnk"].Value);
        Assert.Equal(4, d.Values["rnk"].Value);
    }

    #endregion

    #region DENSE_RANK Tests

    [Fact]
    public async Task DenseRank_WithTies_SameRankNoGaps()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "A"), ("score", 100)),
            MakeRow(("name", "B"), ("score", 200)),
            MakeRow(("name", "C"), ("score", 200)),
            MakeRow(("name", "D"), ("score", 300))
        });

        // DENSE_RANK() OVER (ORDER BY score ASC)
        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("dr", "DENSE_RANK", null, null,
                new[] { OrderBy("score") })
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Equal(4, rows.Count);

        var a = rows.First(r => (string)r.Values["name"].Value! == "A");
        var b = rows.First(r => (string)r.Values["name"].Value! == "B");
        var c = rows.First(r => (string)r.Values["name"].Value! == "C");
        var d = rows.First(r => (string)r.Values["name"].Value! == "D");

        // score=100 -> rank 1, score=200 -> rank 2 (tied), score=300 -> rank 3 (no gap)
        Assert.Equal(1, a.Values["dr"].Value);
        Assert.Equal(2, b.Values["dr"].Value);
        Assert.Equal(2, c.Values["dr"].Value);
        Assert.Equal(3, d.Values["dr"].Value);
    }

    #endregion

    #region Aggregate Window Tests

    [Fact]
    public async Task SumOver_PartitionBy_CorrectPartitionSums()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("industry", "Tech"), ("name", "A"), ("revenue", 100m)),
            MakeRow(("industry", "Tech"), ("name", "B"), ("revenue", 200m)),
            MakeRow(("industry", "Finance"), ("name", "C"), ("revenue", 300m)),
            MakeRow(("industry", "Finance"), ("name", "D"), ("revenue", 400m))
        });

        // SUM(revenue) OVER (PARTITION BY industry)
        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("total", "SUM", ColumnExpr("revenue"),
                new CompiledScalarExpression[] { ColumnExpr("industry") }, null)
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Equal(4, rows.Count);

        // Tech partition: 100 + 200 = 300
        var a = rows.First(r => (string)r.Values["name"].Value! == "A");
        var b = rows.First(r => (string)r.Values["name"].Value! == "B");
        Assert.Equal(300m, a.Values["total"].Value);
        Assert.Equal(300m, b.Values["total"].Value);

        // Finance partition: 300 + 400 = 700
        var c = rows.First(r => (string)r.Values["name"].Value! == "C");
        var d = rows.First(r => (string)r.Values["name"].Value! == "D");
        Assert.Equal(700m, c.Values["total"].Value);
        Assert.Equal(700m, d.Values["total"].Value);
    }

    [Fact]
    public async Task CountStarOver_PartitionBy_CountPerPartition()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("status", "Active"), ("name", "A")),
            MakeRow(("status", "Active"), ("name", "B")),
            MakeRow(("status", "Active"), ("name", "C")),
            MakeRow(("status", "Inactive"), ("name", "D")),
            MakeRow(("status", "Inactive"), ("name", "E"))
        });

        // COUNT(*) OVER (PARTITION BY status)
        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("cnt", "COUNT", null,
                new CompiledScalarExpression[] { ColumnExpr("status") }, null,
                isCountStar: true)
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Equal(5, rows.Count);

        // Active partition: 3 rows
        var a = rows.First(r => (string)r.Values["name"].Value! == "A");
        Assert.Equal(3, a.Values["cnt"].Value);

        // Inactive partition: 2 rows
        var d = rows.First(r => (string)r.Values["name"].Value! == "D");
        Assert.Equal(2, d.Values["cnt"].Value);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task EmptyInput_EmptyOutput()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());

        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("name") })
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task RowNumber_PreservesOriginalColumns()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Alice"), ("revenue", 500m))
        });

        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("name") })
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Single(rows);
        // Original columns preserved
        Assert.Equal("Alice", rows[0].Values["name"].Value);
        Assert.Equal(500m, rows[0].Values["revenue"].Value);
        // Window column added
        Assert.Equal(1, rows[0].Values["rn"].Value);
    }

    [Fact]
    public void Description_ListsWindowFunctions()
    {
        var mockInput = new MockPlanNode(Array.Empty<QueryRow>());

        var windowNode = new ClientWindowNode(mockInput, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("name") })
        });

        Assert.Contains("ROW_NUMBER", windowNode.Description);
        Assert.Contains("rn", windowNode.Description);
    }

    [Fact]
    public async Task AvgOver_PartitionBy_CorrectAverages()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("team", "A"), ("name", "p1"), ("score", 10m)),
            MakeRow(("team", "A"), ("name", "p2"), ("score", 20m)),
            MakeRow(("team", "B"), ("name", "p3"), ("score", 30m)),
            MakeRow(("team", "B"), ("name", "p4"), ("score", 50m))
        });

        // AVG(score) OVER (PARTITION BY team)
        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("avg_score", "AVG", ColumnExpr("score"),
                new CompiledScalarExpression[] { ColumnExpr("team") }, null)
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        Assert.Equal(4, rows.Count);

        // Team A average: (10 + 20) / 2 = 15
        var p1 = rows.First(r => (string)r.Values["name"].Value! == "p1");
        Assert.Equal(15m, p1.Values["avg_score"].Value);

        // Team B average: (30 + 50) / 2 = 40
        var p3 = rows.First(r => (string)r.Values["name"].Value! == "p3");
        Assert.Equal(40m, p3.Values["avg_score"].Value);
    }

    [Fact]
    public async Task RowNumber_DescendingOrder_HighestFirst()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "A"), ("revenue", 100m)),
            MakeRow(("name", "B"), ("revenue", 300m)),
            MakeRow(("name", "C"), ("revenue", 200m))
        });

        // ROW_NUMBER() OVER (ORDER BY revenue DESC)
        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("revenue", descending: true) })
        });

        var ctx = CreateContext();
        var rows = await ExecuteToListAsync(windowNode, ctx);

        // Sorted DESC: B(300)=1, C(200)=2, A(100)=3
        var a = rows.First(r => (string)r.Values["name"].Value! == "A");
        var b = rows.First(r => (string)r.Values["name"].Value! == "B");
        var c = rows.First(r => (string)r.Values["name"].Value! == "C");

        Assert.Equal(3, a.Values["rn"].Value);
        Assert.Equal(1, b.Values["rn"].Value);
        Assert.Equal(2, c.Values["rn"].Value);
    }

    #endregion

    #region Memory Bounds Tests

    [Fact]
    public async Task ExecuteAsync_ExceedsRowLimit_Throws()
    {
        // Arrange: create 100 rows, set limit to 50
        var rows = new List<QueryRow>();
        for (var i = 0; i < 100; i++)
        {
            rows.Add(MakeRow(("name", $"Row{i}"), ("value", i)));
        }

        var input = new MockPlanNode(rows);

        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("value") })
        }, maxMaterializationRows: 50);

        var ctx = CreateContext();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(
            () => ExecuteToListAsync(windowNode, ctx));

        Assert.Equal(QueryErrorCode.MemoryLimitExceeded, ex.ErrorCode);
        Assert.Contains("51", ex.Message); // row count in message
        Assert.Contains("50", ex.Message); // limit in message
        Assert.Contains("WHERE or TOP", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_UnderRowLimit_Succeeds()
    {
        // Arrange: create 10 rows, set limit to 50
        var rows = new List<QueryRow>();
        for (var i = 0; i < 10; i++)
        {
            rows.Add(MakeRow(("name", $"Row{i}"), ("value", i)));
        }

        var input = new MockPlanNode(rows);

        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("value") })
        }, maxMaterializationRows: 50);

        var ctx = CreateContext();

        // Act
        var result = await ExecuteToListAsync(windowNode, ctx);

        // Assert
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroLimit_Unlimited()
    {
        // Arrange: create 100 rows, set limit to 0 (unlimited)
        var rows = new List<QueryRow>();
        for (var i = 0; i < 100; i++)
        {
            rows.Add(MakeRow(("name", $"Row{i}"), ("value", i)));
        }

        var input = new MockPlanNode(rows);

        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("value") })
        }, maxMaterializationRows: 0);

        var ctx = CreateContext();

        // Act
        var result = await ExecuteToListAsync(windowNode, ctx);

        // Assert - should succeed with all 100 rows
        Assert.Equal(100, result.Count);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringMaterialization_Throws()
    {
        // Arrange: create rows and cancel after a few
        var rows = new List<QueryRow>();
        for (var i = 0; i < 100; i++)
        {
            rows.Add(MakeRow(("name", $"Row{i}"), ("value", i)));
        }

        var cts = new CancellationTokenSource();
        var cancellingInput = new CancellingMockPlanNode(rows, cts, cancelAfter: 5);

        var windowNode = new ClientWindowNode(cancellingInput, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("value") })
        });

        var ctx = CreateContext();

        // Act & Assert -- driving async enumeration should trigger cancellation
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in windowNode.ExecuteAsync(ctx, cts.Token))
            {
                _ = row;
            }
            Assert.Fail("Expected OperationCanceledException before enumeration completed");
        });
    }

    [Fact]
    public void MaxMaterializationRows_DefaultValue_Is500000()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());

        var windowNode = new ClientWindowNode(input, new[]
        {
            new WindowDefinition("rn", "ROW_NUMBER", null, null,
                new[] { OrderBy("name") })
        });

        Assert.Equal(500_000, windowNode.MaxMaterializationRows);
    }

    /// <summary>
    /// A mock plan node that cancels the token after yielding a specified number of rows.
    /// </summary>
    private sealed class CancellingMockPlanNode : IQueryPlanNode
    {
        private readonly IReadOnlyList<QueryRow> _rows;
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfter;

        public CancellingMockPlanNode(IReadOnlyList<QueryRow> rows, CancellationTokenSource cts, int cancelAfter)
        {
            _rows = rows;
            _cts = cts;
            _cancelAfter = cancelAfter;
        }

        public string Description => "CancellingMockScan";
        public long EstimatedRows => _rows.Count;
        public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

        public async IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var count = 0;
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
                count++;
                if (count >= _cancelAfter)
                {
                    _cts.Cancel();
                }
            }
            await Task.CompletedTask;
        }
    }

    #endregion
}
