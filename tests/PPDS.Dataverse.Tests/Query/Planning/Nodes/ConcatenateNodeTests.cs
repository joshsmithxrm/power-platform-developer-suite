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
public class ConcatenateNodeTests
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
    public async Task ConcatenatesTwoInputs_InOrder()
    {
        var input1 = new MockPlanNode(new[]
        {
            MakeRow(("name", "Alpha")),
            MakeRow(("name", "Bravo"))
        });
        var input2 = new MockPlanNode(new[]
        {
            MakeRow(("name", "Charlie")),
            MakeRow(("name", "Delta"))
        });

        var node = new ConcatenateNode(new IQueryPlanNode[] { input1, input2 });
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(4, rows.Count);
        Assert.Equal("Alpha", rows[0].Values["name"].Value);
        Assert.Equal("Bravo", rows[1].Values["name"].Value);
        Assert.Equal("Charlie", rows[2].Values["name"].Value);
        Assert.Equal("Delta", rows[3].Values["name"].Value);
    }

    [Fact]
    public async Task ConcatenatesThreeInputs()
    {
        var input1 = new MockPlanNode(new[] { MakeRow(("id", 1)) });
        var input2 = new MockPlanNode(new[] { MakeRow(("id", 2)) });
        var input3 = new MockPlanNode(new[] { MakeRow(("id", 3)) });

        var node = new ConcatenateNode(new IQueryPlanNode[] { input1, input2, input3 });
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0].Values["id"].Value);
        Assert.Equal(2, rows[1].Values["id"].Value);
        Assert.Equal(3, rows[2].Values["id"].Value);
    }

    [Fact]
    public async Task EmptyInputs_YieldNoRows()
    {
        var input1 = new MockPlanNode(Array.Empty<QueryRow>());
        var input2 = new MockPlanNode(Array.Empty<QueryRow>());

        var node = new ConcatenateNode(new IQueryPlanNode[] { input1, input2 });
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
    }

    [Fact]
    public async Task OneEmptyOneNonEmpty_YieldsNonEmptyRows()
    {
        var input1 = new MockPlanNode(Array.Empty<QueryRow>());
        var input2 = new MockPlanNode(new[] { MakeRow(("name", "Only")) });

        var node = new ConcatenateNode(new IQueryPlanNode[] { input1, input2 });
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal("Only", rows[0].Values["name"].Value);
    }

    [Fact]
    public void Description_ShowsInputCount()
    {
        var input1 = new MockPlanNode(Array.Empty<QueryRow>());
        var input2 = new MockPlanNode(Array.Empty<QueryRow>());

        var node = new ConcatenateNode(new IQueryPlanNode[] { input1, input2 });

        Assert.Contains("2 inputs", node.Description);
    }

    [Fact]
    public void Children_ContainsAllInputs()
    {
        var input1 = new MockPlanNode(Array.Empty<QueryRow>());
        var input2 = new MockPlanNode(Array.Empty<QueryRow>());

        var node = new ConcatenateNode(new IQueryPlanNode[] { input1, input2 });

        Assert.Equal(2, node.Children.Count);
        Assert.Same(input1, node.Children[0]);
        Assert.Same(input2, node.Children[1]);
    }

    [Fact]
    public void EstimatedRows_SumsChildren()
    {
        var input1 = new MockPlanNode(new[] { MakeRow(("a", 1)), MakeRow(("a", 2)) });
        var input2 = new MockPlanNode(new[] { MakeRow(("a", 3)) });

        var node = new ConcatenateNode(new IQueryPlanNode[] { input1, input2 });

        Assert.Equal(3, node.EstimatedRows);
    }

    [Fact]
    public void Constructor_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ConcatenateNode(null!));
    }

    [Fact]
    public void Constructor_ThrowsOnSingleInput()
    {
        var input1 = new MockPlanNode(Array.Empty<QueryRow>());
        Assert.Throws<ArgumentException>(() => new ConcatenateNode(new IQueryPlanNode[] { input1 }));
    }

    [Fact]
    public async Task PreservesDuplicateRows()
    {
        // ConcatenateNode should NOT deduplicate — that's DistinctNode's job
        var input1 = new MockPlanNode(new[] { MakeRow(("name", "Same")) });
        var input2 = new MockPlanNode(new[] { MakeRow(("name", "Same")) });

        var node = new ConcatenateNode(new IQueryPlanNode[] { input1, input2 });
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
    }
}
