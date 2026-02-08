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
public class PrefetchScanNodeTests
{
    private static QueryPlanContext CreateContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockExecutor.Object, new ExpressionEvaluator());
    }

    private static QueryRow MakeRow(int index)
    {
        var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = QueryValue.Simple(index),
            ["name"] = QueryValue.Simple($"row_{index}")
        };
        return new QueryRow(values, "entity");
    }

    private static IReadOnlyList<QueryRow> MakeRows(int count)
    {
        return Enumerable.Range(0, count).Select(MakeRow).ToList();
    }

    /// <summary>
    /// A mock plan node that yields predefined rows with optional async delay.
    /// </summary>
    private sealed class MockSourceNode : IQueryPlanNode
    {
        private readonly IReadOnlyList<QueryRow> _rows;
        private readonly TimeSpan _delayPerRow;

        public string Description => "MockSource";
        public long EstimatedRows => _rows.Count;
        public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

        public MockSourceNode(IReadOnlyList<QueryRow> rows, TimeSpan? delayPerRow = null)
        {
            _rows = rows;
            _delayPerRow = delayPerRow ?? TimeSpan.Zero;
        }

        public async IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_delayPerRow > TimeSpan.Zero)
                {
                    await Task.Delay(_delayPerRow, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield(); // Simulate async
                }
                yield return row;
            }
        }
    }

    /// <summary>
    /// A mock source node that throws an exception after yielding a specified number of rows.
    /// </summary>
    private sealed class FailingSourceNode : IQueryPlanNode
    {
        private readonly int _failAfterRows;
        private readonly Exception _exception;

        public string Description => "FailingSource";
        public long EstimatedRows => -1;
        public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

        public FailingSourceNode(int failAfterRows, Exception exception)
        {
            _failAfterRows = failAfterRows;
            _exception = exception;
        }

        public async IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < _failAfterRows; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return MakeRow(i);
            }

            throw _exception;
        }
    }

    [Fact]
    public async Task BasicFlow_YieldsAllRowsInOrder()
    {
        // Arrange: source produces 100 rows
        var sourceRows = MakeRows(100);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source, bufferSize: 50);
        var ctx = CreateContext();

        // Act
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
        }

        // Assert: all 100 rows yielded in same order
        Assert.Equal(100, results.Count);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(i, results[i].Values["id"].Value);
            Assert.Equal($"row_{i}", results[i].Values["name"].Value);
        }
    }

    [Fact]
    public async Task EmptySource_YieldsNoRows()
    {
        // Arrange: source produces 0 rows
        var source = new MockSourceNode(Array.Empty<QueryRow>());
        var node = new PrefetchScanNode(source);
        var ctx = CreateContext();

        // Act
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
        }

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task Cancellation_DoesNotHang()
    {
        // Arrange: source produces many rows with delay; cancel mid-stream
        var sourceRows = MakeRows(1000);
        var source = new MockSourceNode(sourceRows, delayPerRow: TimeSpan.FromMilliseconds(10));
        var node = new PrefetchScanNode(source, bufferSize: 50);
        var ctx = CreateContext();
        using var cts = new CancellationTokenSource();

        // Act: consume a few rows then cancel
        var results = new List<QueryRow>();
        var exceptionThrown = false;

        try
        {
            await foreach (var row in node.ExecuteAsync(ctx, cts.Token))
            {
                results.Add(row);
                if (results.Count >= 5)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            exceptionThrown = true;
        }

        // Assert: cancellation occurred, no hang
        Assert.True(exceptionThrown, "Expected OperationCanceledException");
        Assert.True(results.Count >= 5, "Expected at least 5 rows before cancellation");
        Assert.True(results.Count < 1000, "Expected fewer than all 1000 rows");
    }

    [Fact]
    public async Task Backpressure_SmallBufferLargeSource_AllRowsYielded()
    {
        // Arrange: buffer of 10, source of 1000 rows
        var sourceRows = MakeRows(1000);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source, bufferSize: 10);
        var ctx = CreateContext();

        // Act
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
        }

        // Assert: all 1000 rows eventually yielded in order
        Assert.Equal(1000, results.Count);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(i, results[i].Values["id"].Value);
        }
    }

    [Fact]
    public async Task ProducerFasterThanConsumer_BufferingWorks()
    {
        // Arrange: fast producer (no delay), slow consumer
        var sourceRows = MakeRows(50);
        var source = new MockSourceNode(sourceRows); // No delay = fast producer
        var node = new PrefetchScanNode(source, bufferSize: 20);
        var ctx = CreateContext();

        // Act: consume slowly to allow producer to fill buffer
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
            // Simulate slow consumer on first few rows
            if (results.Count <= 5)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        // Assert: all rows yielded in order despite speed mismatch
        Assert.Equal(50, results.Count);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(i, results[i].Values["id"].Value);
        }
    }

    [Fact]
    public async Task SourceException_PropagatedToConsumer()
    {
        // Arrange: source throws after 5 rows
        var expectedException = new InvalidOperationException("Source error at row 5");
        var source = new FailingSourceNode(failAfterRows: 5, expectedException);
        var node = new PrefetchScanNode(source, bufferSize: 100);
        var ctx = CreateContext();

        // Act & Assert: exception propagated
        var results = new List<QueryRow>();
        var caughtException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in node.ExecuteAsync(ctx))
            {
                results.Add(row);
            }
        });

        Assert.Equal("Source error at row 5", caughtException.Message);
        // Some rows may have been yielded before the exception
        Assert.True(results.Count <= 5, "Expected at most 5 rows before exception");
    }

    [Fact]
    public void Description_IncludesSourceDescriptionAndBufferSize()
    {
        // Arrange
        var source = new MockSourceNode(Array.Empty<QueryRow>());
        var node = new PrefetchScanNode(source, bufferSize: 5000);

        // Assert
        Assert.Contains("Prefetch", node.Description);
        Assert.Contains("5000", node.Description);
        Assert.Contains("MockSource", node.Description);
    }

    [Fact]
    public void Children_ReturnsSourceNode()
    {
        // Arrange
        var source = new MockSourceNode(Array.Empty<QueryRow>());
        var node = new PrefetchScanNode(source);

        // Assert
        Assert.Single(node.Children);
        Assert.Same(source, node.Children[0]);
    }

    [Fact]
    public void EstimatedRows_DelegatesToSource()
    {
        // Arrange
        var sourceRows = MakeRows(42);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source);

        // Assert
        Assert.Equal(42, node.EstimatedRows);
    }

    [Fact]
    public void Constructor_ThrowsOnNullSource()
    {
        Assert.Throws<ArgumentNullException>(() => new PrefetchScanNode(null!));
    }

    [Fact]
    public void Constructor_ThrowsOnZeroBufferSize()
    {
        var source = new MockSourceNode(Array.Empty<QueryRow>());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrefetchScanNode(source, bufferSize: 0));
    }

    [Fact]
    public void Constructor_ThrowsOnNegativeBufferSize()
    {
        var source = new MockSourceNode(Array.Empty<QueryRow>());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrefetchScanNode(source, bufferSize: -1));
    }

    [Fact]
    public void DefaultBufferSize_Is5000()
    {
        var source = new MockSourceNode(Array.Empty<QueryRow>());
        var node = new PrefetchScanNode(source);
        Assert.Equal(5000, node.BufferSize);
    }

    [Fact]
    public async Task SingleRow_YieldsCorrectly()
    {
        // Arrange: edge case with exactly one row
        var sourceRows = MakeRows(1);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source, bufferSize: 10);
        var ctx = CreateContext();

        // Act
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
        }

        // Assert
        Assert.Single(results);
        Assert.Equal(0, results[0].Values["id"].Value);
    }
}
