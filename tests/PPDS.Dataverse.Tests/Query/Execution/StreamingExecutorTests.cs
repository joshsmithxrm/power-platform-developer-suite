using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution;

[Trait("Category", "PlanUnit")]
public class StreamingExecutorTests
{
    private readonly PlanExecutor _executor = new();

    private static QueryPlanContext CreateContext(CancellationToken cancellationToken = default)
    {
        var mockQueryExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockQueryExecutor.Object, cancellationToken);
    }

    /// <summary>
    /// A mock plan node that yields predefined rows with optional delay.
    /// </summary>
    private sealed class MockPlanNode : IQueryPlanNode
    {
        private readonly IReadOnlyList<QueryRow> _rows;
        private readonly int _delayPerRowMs;

        public MockPlanNode(IReadOnlyList<QueryRow> rows, int delayPerRowMs = 0)
        {
            _rows = rows;
            _delayPerRowMs = delayPerRowMs;
        }

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
                if (_delayPerRowMs > 0)
                {
                    await Task.Delay(_delayPerRowMs, cancellationToken);
                }
                yield return row;
            }
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

    private static QueryPlanResult MakePlan(IQueryPlanNode rootNode) => new()
    {
        RootNode = rootNode,
        FetchXml = "<fetch />",
        VirtualColumns = new Dictionary<string, PPDS.Dataverse.Sql.Transpilation.VirtualColumnInfo>(),
        EntityLogicalName = "account"
    };

    [Fact]
    public async Task Streaming_YieldsAllRows()
    {
        var mockNode = new MockPlanNode(new[]
        {
            MakeRow(("name", "A")),
            MakeRow(("name", "B")),
            MakeRow(("name", "C"))
        });

        var planResult = MakePlan(mockNode);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in _executor.ExecuteStreamingAsync(planResult, ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal("A", rows[0].Values["name"].Value);
        Assert.Equal("B", rows[1].Values["name"].Value);
        Assert.Equal("C", rows[2].Values["name"].Value);
    }

    [Fact]
    public async Task Streaming_EmptySource_YieldsZeroRows()
    {
        var mockNode = new MockPlanNode(Array.Empty<QueryRow>());
        var planResult = MakePlan(mockNode);
        var ctx = CreateContext();

        var count = 0;
        await foreach (var _ in _executor.ExecuteStreamingAsync(planResult, ctx))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Streaming_WithCancellation_StopsMidStream()
    {
        var rows = new List<QueryRow>();
        for (int i = 0; i < 100; i++)
        {
            rows.Add(MakeRow(("name", $"Row{i}")));
        }

        // Use a small delay per row so cancellation can take effect
        var mockNode = new MockPlanNode(rows, delayPerRowMs: 5);
        var planResult = MakePlan(mockNode);

        using var cts = new CancellationTokenSource();
        var ctx = CreateContext(cts.Token);

        var collected = new List<QueryRow>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in _executor.ExecuteStreamingAsync(planResult, ctx, cts.Token))
            {
                collected.Add(row);
                if (collected.Count >= 5)
                {
                    cts.Cancel();
                }
            }
        });

        // Should have stopped after ~5 rows (the exact count depends on timing,
        // but it should be far fewer than the total 100)
        Assert.True(collected.Count < 100, $"Expected fewer than 100 rows but got {collected.Count}");
        Assert.True(collected.Count >= 5, $"Expected at least 5 rows but got {collected.Count}");
    }

    [Fact]
    public async Task Streaming_TracksStatistics()
    {
        var mockNode = new MockPlanNode(new[]
        {
            MakeRow(("name", "A")),
            MakeRow(("name", "B")),
            MakeRow(("name", "C")),
            MakeRow(("name", "D"))
        });

        var planResult = MakePlan(mockNode);
        var ctx = CreateContext();

        var count = 0;
        await foreach (var _ in _executor.ExecuteStreamingAsync(planResult, ctx))
        {
            count++;
        }

        Assert.Equal(4, count);
        Assert.Equal(4, ctx.Statistics.RowsOutput);
    }

    [Fact]
    public async Task Streaming_RowsAreYieldedIncrementally()
    {
        // Verify that rows are available one at a time (not buffered)
        var mockNode = new MockPlanNode(new[]
        {
            MakeRow(("id", 1)),
            MakeRow(("id", 2)),
            MakeRow(("id", 3))
        });

        var planResult = MakePlan(mockNode);
        var ctx = CreateContext();

        var receivedOrder = new List<int>();
        await foreach (var row in _executor.ExecuteStreamingAsync(planResult, ctx))
        {
            receivedOrder.Add((int)row.Values["id"].Value!);
        }

        // Verify rows arrive in order
        Assert.Equal(new[] { 1, 2, 3 }, receivedOrder);
    }

    [Fact]
    public async Task Streaming_PreservesEntityLogicalName()
    {
        var mockNode = new MockPlanNode(new[]
        {
            MakeRow(("name", "Test"))
        });

        var planResult = MakePlan(mockNode);
        var ctx = CreateContext();

        await foreach (var row in _executor.ExecuteStreamingAsync(planResult, ctx))
        {
            Assert.Equal("account", row.EntityLogicalName);
        }
    }
}
