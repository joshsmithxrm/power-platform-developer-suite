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

    [Fact]
    public async Task LargeRowCount_AllRowsPassThroughCorrectly()
    {
        // Arrange: 10,000 rows with default buffer size
        const int rowCount = 10_000;
        var sourceRows = MakeRows(rowCount);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source, bufferSize: 500);
        var ctx = CreateContext();

        // Act
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
        }

        // Assert: all 10,000 rows yielded in order
        Assert.Equal(rowCount, results.Count);
        for (var i = 0; i < rowCount; i++)
        {
            Assert.Equal(i, results[i].Values["id"].Value);
        }
    }

    [Fact]
    public async Task MinimumBufferSize_OneRow_AllRowsYielded()
    {
        // Arrange: buffer of 1 forces maximum backpressure (producer blocks after every row)
        var sourceRows = MakeRows(100);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source, bufferSize: 1);
        var ctx = CreateContext();

        // Act
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
        }

        // Assert: all rows yielded in order despite minimal buffer
        Assert.Equal(100, results.Count);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(i, results[i].Values["id"].Value);
        }
    }

    [Fact]
    public async Task Backpressure_ProducerBlocksWhenBufferFull()
    {
        // Arrange: a tracking source that records how many rows have been produced.
        // With a buffer of 5 and a slow consumer, the producer should not get
        // too far ahead of the consumer.
        var produced = new TrackingSourceNode(200);
        var node = new PrefetchScanNode(produced, bufferSize: 5);
        var ctx = CreateContext();

        // Act: consume slowly and track the max producer lead
        var consumed = 0;
        var maxLead = 0;
        await foreach (var _ in node.ExecuteAsync(ctx))
        {
            consumed++;
            var currentProduced = produced.ProducedCount;
            var lead = currentProduced - consumed;
            if (lead > maxLead)
                maxLead = lead;

            // Slow down consumer every 10 rows to let producer potentially fill buffer
            if (consumed % 10 == 0)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        // Assert: all rows consumed
        Assert.Equal(200, consumed);
        // The producer should never get more than bufferSize + small margin ahead
        // (margin accounts for in-flight items and timing)
        Assert.True(maxLead <= 10, $"Producer lead was {maxLead}, expected <= ~bufferSize (5) + margin");
    }

    /// <summary>
    /// A source node that tracks how many rows have been produced via an atomic counter.
    /// </summary>
    private sealed class TrackingSourceNode : IQueryPlanNode
    {
        private readonly int _rowCount;
        private int _producedCount;

        public int ProducedCount => Volatile.Read(ref _producedCount);

        public string Description => "TrackingSource";
        public long EstimatedRows => _rowCount;
        public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

        public TrackingSourceNode(int rowCount) => _rowCount = rowCount;

        public async IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < _rowCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                Interlocked.Increment(ref _producedCount);
                yield return MakeRow(i);
            }
        }
    }

    [Fact]
    public async Task SourceException_ImmediateFailure_PropagatedToConsumer()
    {
        // Arrange: source throws immediately before yielding any rows
        var expectedException = new InvalidOperationException("Immediate failure");
        var source = new FailingSourceNode(failAfterRows: 0, expectedException);
        var node = new PrefetchScanNode(source, bufferSize: 50);
        var ctx = CreateContext();

        // Act & Assert: exception propagated even with zero rows
        var results = new List<QueryRow>();
        var caughtException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var row in node.ExecuteAsync(ctx))
            {
                results.Add(row);
            }
        });

        Assert.Equal("Immediate failure", caughtException.Message);
        Assert.Empty(results);
    }

    [Fact]
    public async Task MultipleExecutions_EachYieldsAllRows()
    {
        // Arrange: same PrefetchScanNode can be executed multiple times
        var sourceRows = MakeRows(50);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source, bufferSize: 20);
        var ctx = CreateContext();

        // Act: execute twice
        var results1 = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results1.Add(row);
        }

        var results2 = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results2.Add(row);
        }

        // Assert: both executions yield all rows in order
        Assert.Equal(50, results1.Count);
        Assert.Equal(50, results2.Count);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(i, results1[i].Values["id"].Value);
            Assert.Equal(i, results2[i].Values["id"].Value);
        }
    }

    [Fact]
    public async Task BufferSizeMatchesSourceSize_AllRowsYielded()
    {
        // Arrange: buffer exactly equals source row count (boundary case)
        var sourceRows = MakeRows(25);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source, bufferSize: 25);
        var ctx = CreateContext();

        // Act
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
        }

        // Assert
        Assert.Equal(25, results.Count);
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(i, results[i].Values["id"].Value);
        }
    }

    [Fact]
    public async Task BufferLargerThanSource_AllRowsYielded()
    {
        // Arrange: buffer is much larger than source (no backpressure needed)
        var sourceRows = MakeRows(10);
        var source = new MockSourceNode(sourceRows);
        var node = new PrefetchScanNode(source, bufferSize: 5000);
        var ctx = CreateContext();

        // Act
        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            results.Add(row);
        }

        // Assert
        Assert.Equal(10, results.Count);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(i, results[i].Values["id"].Value);
        }
    }
}
