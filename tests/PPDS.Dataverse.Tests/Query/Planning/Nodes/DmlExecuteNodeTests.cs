using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "TuiUnit")]
public class DmlExecuteNodeTests
{
    [Fact]
    public void InsertValues_HasCorrectDescription()
    {
        var node = DmlExecuteNode.InsertValues(
            "account",
            new[] { "name", "revenue" },
            new ISqlExpression[][]
            {
                new ISqlExpression[]
                {
                    new SqlLiteralExpression(SqlLiteral.String("Contoso")),
                    new SqlLiteralExpression(SqlLiteral.Number("1000"))
                }
            });

        Assert.Contains("INSERT", node.Description);
        Assert.Contains("account", node.Description);
        Assert.Contains("1 rows", node.Description);
        Assert.Equal(DmlOperation.Insert, node.Operation);
        Assert.Equal("account", node.EntityLogicalName);
        Assert.Equal(1, node.EstimatedRows);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void Delete_HasCorrectDescription()
    {
        var mockSource = new MockPlanNode();
        var node = DmlExecuteNode.Delete("account", mockSource);

        Assert.Contains("DELETE", node.Description);
        Assert.Contains("account", node.Description);
        Assert.Equal(DmlOperation.Delete, node.Operation);
        Assert.Single(node.Children);
    }

    [Fact]
    public void Update_HasCorrectDescription()
    {
        var mockSource = new MockPlanNode();
        var setClauses = new[]
        {
            new SqlSetClause("name", new SqlLiteralExpression(SqlLiteral.String("Updated")))
        };
        var node = DmlExecuteNode.Update("account", mockSource, setClauses);

        Assert.Contains("UPDATE", node.Description);
        Assert.Contains("account", node.Description);
        Assert.Equal(DmlOperation.Update, node.Operation);
        Assert.Single(node.Children);
        Assert.NotNull(node.SetClauses);
        Assert.Single(node.SetClauses!);
    }

    [Fact]
    public void InsertSelect_HasSourceNode()
    {
        var mockSource = new MockPlanNode();
        var node = DmlExecuteNode.InsertSelect(
            "account",
            new[] { "name" },
            mockSource);

        Assert.Contains("INSERT", node.Description);
        Assert.Contains("SELECT", node.Description);
        Assert.Single(node.Children);
        Assert.Same(mockSource, node.Children[0]);
    }

    [Fact]
    public void InsertValues_RowCapDefaultIsMaxValue()
    {
        var node = DmlExecuteNode.InsertValues(
            "account",
            new[] { "name" },
            new ISqlExpression[][]
            {
                new ISqlExpression[] { new SqlLiteralExpression(SqlLiteral.String("Test")) }
            });

        Assert.Equal(int.MaxValue, node.RowCap);
    }

    [Fact]
    public void Delete_RowCapCanBeSet()
    {
        var mockSource = new MockPlanNode();
        var node = DmlExecuteNode.Delete("account", mockSource, rowCap: 100);

        Assert.Equal(100, node.RowCap);
    }

    [Fact]
    public void InsertSelect_WithSourceColumns_StoresOrdinalMapping()
    {
        var mockSource = new MockPlanNode();
        var insertColumns = new[] { "name", "revenue" };
        var sourceColumns = new[] { "fullname", "totalrevenue" };

        var node = DmlExecuteNode.InsertSelect(
            "account",
            insertColumns,
            mockSource,
            sourceColumns: sourceColumns);

        Assert.NotNull(node.SourceColumns);
        Assert.Equal(2, node.SourceColumns!.Count);
        Assert.Equal("fullname", node.SourceColumns[0]);
        Assert.Equal("totalrevenue", node.SourceColumns[1]);
        Assert.Equal(2, node.InsertColumns!.Count);
        Assert.Equal("name", node.InsertColumns[0]);
        Assert.Equal("revenue", node.InsertColumns[1]);
    }

    [Fact]
    public void InsertSelect_WithoutSourceColumns_HasNullSourceColumns()
    {
        var mockSource = new MockPlanNode();
        var node = DmlExecuteNode.InsertSelect(
            "account",
            new[] { "name" },
            mockSource);

        Assert.Null(node.SourceColumns);
    }

    /// <summary>
    /// Minimal mock plan node for testing DmlExecuteNode structure.
    /// </summary>
    private sealed class MockPlanNode : IQueryPlanNode
    {
        public string Description => "Mock";
        public long EstimatedRows => -1;
        public System.Collections.Generic.IReadOnlyList<IQueryPlanNode> Children =>
            System.Array.Empty<IQueryPlanNode>();

        public System.Collections.Generic.IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            System.Threading.CancellationToken cancellationToken = default)
        {
            return AsyncEnumerable.Empty<QueryRow>();
        }
    }

    /// <summary>Helper to create empty async enumerables.</summary>
    private static class AsyncEnumerable
    {
        public static System.Collections.Generic.IAsyncEnumerable<T> Empty<T>()
        {
            return new EmptyAsyncEnumerable<T>();
        }

        private sealed class EmptyAsyncEnumerable<T> : System.Collections.Generic.IAsyncEnumerable<T>
        {
            public System.Collections.Generic.IAsyncEnumerator<T> GetAsyncEnumerator(
                System.Threading.CancellationToken cancellationToken = default)
            {
                return new EmptyAsyncEnumerator<T>();
            }
        }

        private sealed class EmptyAsyncEnumerator<T> : System.Collections.Generic.IAsyncEnumerator<T>
        {
            public T Current => default!;
            public System.Threading.Tasks.ValueTask<bool> MoveNextAsync() =>
                new System.Threading.Tasks.ValueTask<bool>(false);
            public System.Threading.Tasks.ValueTask DisposeAsync() =>
                new System.Threading.Tasks.ValueTask();
        }
    }
}
