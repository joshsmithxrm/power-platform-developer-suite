using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "TuiUnit")]
public class DistinctNodeTests
{
    private static QueryPlanContext CreateContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockExecutor.Object);
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
        return new QueryRow(values, "entity");
    }

    [Fact]
    public async Task RemovesDuplicateRows()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Alpha")),
            MakeRow(("name", "Alpha")),
            MakeRow(("name", "Bravo"))
        });

        var node = new DistinctNode(input);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alpha", rows[0].Values["name"].Value);
        Assert.Equal("Bravo", rows[1].Values["name"].Value);
    }

    [Fact]
    public async Task PreservesUniqueRows()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Alpha")),
            MakeRow(("name", "Bravo")),
            MakeRow(("name", "Charlie"))
        });

        var node = new DistinctNode(input);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task DeduplicatesOnMultipleColumns()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Alpha"), ("id", 1)),
            MakeRow(("name", "Alpha"), ("id", 2)),  // Different id: unique
            MakeRow(("name", "Alpha"), ("id", 1))   // Same as first: duplicate
        });

        var node = new DistinctNode(input);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].Values["id"].Value);
        Assert.Equal(2, rows[1].Values["id"].Value);
    }

    [Fact]
    public async Task HandlesNullValues()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", null)),
            MakeRow(("name", null)),  // duplicate null
            MakeRow(("name", "Alpha"))
        });

        var node = new DistinctNode(input);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task EmptyInput_YieldsNoRows()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());

        var node = new DistinctNode(input);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
    }

    [Fact]
    public async Task PreservesFirstOccurrenceOrder()
    {
        // When duplicates exist, the first occurrence should be kept
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Bravo")),
            MakeRow(("name", "Alpha")),
            MakeRow(("name", "Bravo")),  // duplicate
            MakeRow(("name", "Charlie")),
            MakeRow(("name", "Alpha"))   // duplicate
        });

        var node = new DistinctNode(input);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal("Bravo", rows[0].Values["name"].Value);
        Assert.Equal("Alpha", rows[1].Values["name"].Value);
        Assert.Equal("Charlie", rows[2].Values["name"].Value);
    }

    [Fact]
    public void Description_ContainsDistinct()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var node = new DistinctNode(input);

        Assert.Contains("Distinct", node.Description);
    }

    [Fact]
    public void Children_ContainsInput()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var node = new DistinctNode(input);

        Assert.Single(node.Children);
        Assert.Same(input, node.Children[0]);
    }

    [Fact]
    public void Constructor_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DistinctNode(null!));
    }

    [Fact]
    public async Task AllDuplicates_YieldsSingleRow()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Same")),
            MakeRow(("name", "Same")),
            MakeRow(("name", "Same"))
        });

        var node = new DistinctNode(input);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal("Same", rows[0].Values["name"].Value);
    }
}
