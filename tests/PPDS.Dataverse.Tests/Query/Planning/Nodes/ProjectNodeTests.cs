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
public class ProjectNodeTests
{
    private static QueryPlanContext CreateContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockExecutor.Object);
    }

    /// <summary>
    /// A mock plan node that yields predefined rows.
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
            await Task.CompletedTask; // satisfy async requirement
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

    [Fact]
    public async Task PassThrough_CopiesColumns()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Contoso"), ("revenue", 1000000m))
        });

        var project = new ProjectNode(input, new[]
        {
            ProjectColumn.PassThrough("name"),
            ProjectColumn.PassThrough("revenue")
        });

        var ctx = CreateContext();
        var rows = new List<QueryRow>();
        await foreach (var row in project.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal("Contoso", rows[0].Values["name"].Value);
        Assert.Equal(1000000m, rows[0].Values["revenue"].Value);
    }

    [Fact]
    public async Task Rename_ChangesColumnName()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("fullname", "John Doe"))
        });

        var project = new ProjectNode(input, new[]
        {
            ProjectColumn.Rename("fullname", "contact_name")
        });

        var ctx = CreateContext();
        var rows = new List<QueryRow>();
        await foreach (var row in project.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.True(rows[0].Values.TryGetValue("contact_name", out var contactNameVal));
        Assert.Equal("John Doe", contactNameVal.Value);
    }

    [Fact]
    public async Task Computed_EvaluatesExpression()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("revenue", 1000m))
        });

        // revenue * 0.1 AS tax — compiled as a delegate
        CompiledScalarExpression taxExpr = row =>
        {
            var revenue = row.TryGetValue("revenue", out var rv) ? rv.Value : null;
            if (revenue is decimal d) return d * 0.1m;
            return null;
        };

        var project = new ProjectNode(input, new[]
        {
            ProjectColumn.PassThrough("revenue"),
            ProjectColumn.Computed("tax", taxExpr)
        });

        var ctx = CreateContext();
        var rows = new List<QueryRow>();
        await foreach (var row in project.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(100.0m, rows[0].Values["tax"].Value);
    }

    [Fact]
    public async Task MissingSource_ReturnsNull()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "Contoso"))
        });

        var project = new ProjectNode(input, new[]
        {
            ProjectColumn.PassThrough("name"),
            ProjectColumn.PassThrough("nonexistent")
        });

        var ctx = CreateContext();
        var rows = new List<QueryRow>();
        await foreach (var row in project.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Null(rows[0].Values["nonexistent"].Value);
    }

    [Fact]
    public async Task CaseInsensitive_SourceLookup()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("Name", "Contoso"))
        });

        var project = new ProjectNode(input, new[]
        {
            ProjectColumn.Rename("name", "display_name")
        });

        var ctx = CreateContext();
        var rows = new List<QueryRow>();
        await foreach (var row in project.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal("Contoso", rows[0].Values["display_name"].Value);
    }

    [Fact]
    public async Task MultipleRows_AllProjected()
    {
        var input = new MockPlanNode(new[]
        {
            MakeRow(("name", "A"), ("value", 1)),
            MakeRow(("name", "B"), ("value", 2)),
            MakeRow(("name", "C"), ("value", 3))
        });

        var project = new ProjectNode(input, new[]
        {
            ProjectColumn.PassThrough("name")
        });

        var ctx = CreateContext();
        var rows = new List<QueryRow>();
        await foreach (var row in project.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal("A", rows[0].Values["name"].Value);
        Assert.Equal("B", rows[1].Values["name"].Value);
        Assert.Equal("C", rows[2].Values["name"].Value);
    }

    [Fact]
    public void Description_ListsOutputColumns()
    {
        var mockInput = new MockPlanNode(Array.Empty<QueryRow>());
        var project = new ProjectNode(mockInput, new[]
        {
            ProjectColumn.PassThrough("name"),
            ProjectColumn.PassThrough("revenue")
        });

        Assert.Contains("name", project.Description);
        Assert.Contains("revenue", project.Description);
    }
}
