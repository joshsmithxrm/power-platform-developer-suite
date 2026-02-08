using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class ClientFilterNodeTests
{
    private static QueryPlanContext CreateContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockExecutor.Object, new ExpressionEvaluator());
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
    public async Task FiltersRows_BasedOnComparisonCondition()
    {
        // Arrange: 3 rows with cnt values 3, 7, 10; filter cnt > 5
        var input = new MockPlanNode(new[]
        {
            MakeRow(("ownerid", "user1"), ("cnt", 3)),
            MakeRow(("ownerid", "user2"), ("cnt", 7)),
            MakeRow(("ownerid", "user3"), ("cnt", 10))
        });

        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("5"));

        var filterNode = new ClientFilterNode(input, condition);
        var ctx = CreateContext();

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in filterNode.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Equal(2, rows.Count);
        Assert.Equal(7, rows[0].Values["cnt"].Value);
        Assert.Equal(10, rows[1].Values["cnt"].Value);
    }

    [Fact]
    public async Task PassesAllRows_WhenConditionAlwaysTrue()
    {
        // Arrange: all rows have cnt > 0, filter cnt > 0
        var input = new MockPlanNode(new[]
        {
            MakeRow(("cnt", 5)),
            MakeRow(("cnt", 10)),
            MakeRow(("cnt", 15))
        });

        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("0"));

        var filterNode = new ClientFilterNode(input, condition);
        var ctx = CreateContext();

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in filterNode.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task FiltersAllRows_WhenConditionAlwaysFalse()
    {
        // Arrange: all rows have cnt < 100, filter cnt > 100
        var input = new MockPlanNode(new[]
        {
            MakeRow(("cnt", 1)),
            MakeRow(("cnt", 2)),
            MakeRow(("cnt", 3))
        });

        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("100"));

        var filterNode = new ClientFilterNode(input, condition);
        var ctx = CreateContext();

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in filterNode.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Empty(rows);
    }

    [Fact]
    public async Task FiltersWithLogicalAndCondition()
    {
        // Arrange: filter cnt > 2 AND cnt < 10
        var input = new MockPlanNode(new[]
        {
            MakeRow(("cnt", 1)),
            MakeRow(("cnt", 5)),
            MakeRow(("cnt", 10)),
            MakeRow(("cnt", 15))
        });

        var condition = SqlLogicalCondition.And(
            new SqlComparisonCondition(
                SqlColumnRef.Simple("cnt"),
                SqlComparisonOperator.GreaterThan,
                SqlLiteral.Number("2")),
            new SqlComparisonCondition(
                SqlColumnRef.Simple("cnt"),
                SqlComparisonOperator.LessThan,
                SqlLiteral.Number("10")));

        var filterNode = new ClientFilterNode(input, condition);
        var ctx = CreateContext();

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in filterNode.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Single(rows);
        Assert.Equal(5, rows[0].Values["cnt"].Value);
    }

    [Fact]
    public async Task FiltersWithLogicalOrCondition()
    {
        // Arrange: filter cnt = 1 OR cnt = 15
        var input = new MockPlanNode(new[]
        {
            MakeRow(("cnt", 1)),
            MakeRow(("cnt", 5)),
            MakeRow(("cnt", 10)),
            MakeRow(("cnt", 15))
        });

        var condition = SqlLogicalCondition.Or(
            new SqlComparisonCondition(
                SqlColumnRef.Simple("cnt"),
                SqlComparisonOperator.Equal,
                SqlLiteral.Number("1")),
            new SqlComparisonCondition(
                SqlColumnRef.Simple("cnt"),
                SqlComparisonOperator.Equal,
                SqlLiteral.Number("15")));

        var filterNode = new ClientFilterNode(input, condition);
        var ctx = CreateContext();

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in filterNode.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].Values["cnt"].Value);
        Assert.Equal(15, rows[1].Values["cnt"].Value);
    }

    [Fact]
    public async Task EmptyInput_YieldsNoRows()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());

        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("0"));

        var filterNode = new ClientFilterNode(input, condition);
        var ctx = CreateContext();

        var rows = new List<QueryRow>();
        await foreach (var row in filterNode.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
    }

    [Fact]
    public void Description_IncludesConditionInfo()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("5"));

        var filterNode = new ClientFilterNode(input, condition);

        Assert.Contains("ClientFilter", filterNode.Description);
        Assert.Contains("cnt", filterNode.Description);
    }

    [Fact]
    public void Children_ContainsInputNode()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());
        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("5"));

        var filterNode = new ClientFilterNode(input, condition);

        Assert.Single(filterNode.Children);
        Assert.Same(input, filterNode.Children[0]);
    }

    [Fact]
    public void Constructor_ThrowsOnNullInput()
    {
        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("5"));

        Assert.Throws<ArgumentNullException>(() => new ClientFilterNode(null!, condition));
    }

    [Fact]
    public void Constructor_ThrowsOnNullCondition()
    {
        var input = new MockPlanNode(Array.Empty<QueryRow>());

        Assert.Throws<ArgumentNullException>(() => new ClientFilterNode(input, null!));
    }
}
