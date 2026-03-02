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
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution;

[Trait("Category", "PlanUnit")]
public class PlanExecutorTests
{
    private readonly PlanExecutor _executor = new();

    private static QueryPlanContext CreateContext()
    {
        var mockQueryExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(mockQueryExecutor.Object);
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

    [Fact]
    public async Task Execute_CollectsAllRows()
    {
        var mockNode = new MockPlanNode(new[]
        {
            MakeRow(("name", "A")),
            MakeRow(("name", "B")),
            MakeRow(("name", "C"))
        });

        var planResult = new QueryPlanResult
        {
            RootNode = mockNode,
            FetchXml = "<fetch />",
            VirtualColumns = new Dictionary<string, PPDS.Dataverse.Sql.Transpilation.VirtualColumnInfo>(),
            EntityLogicalName = "account"
        };

        var ctx = CreateContext();
        var result = await _executor.ExecuteAsync(planResult, ctx);

        Assert.Equal(3, result.Count);
        Assert.Equal(3, result.Records.Count);
        Assert.Equal("account", result.EntityLogicalName);
    }

    [Fact]
    public async Task Execute_InfersColumnsFromFirstRow()
    {
        var mockNode = new MockPlanNode(new[]
        {
            MakeRow(("name", "A"), ("revenue", 100))
        });

        var planResult = new QueryPlanResult
        {
            RootNode = mockNode,
            FetchXml = "<fetch />",
            VirtualColumns = new Dictionary<string, PPDS.Dataverse.Sql.Transpilation.VirtualColumnInfo>(),
            EntityLogicalName = "account"
        };

        var ctx = CreateContext();
        var result = await _executor.ExecuteAsync(planResult, ctx);

        Assert.Equal(2, result.Columns.Count);
    }

    [Fact]
    public async Task Execute_EmptyResult_HasEmptyColumns()
    {
        var mockNode = new MockPlanNode(Array.Empty<QueryRow>());

        var planResult = new QueryPlanResult
        {
            RootNode = mockNode,
            FetchXml = "<fetch />",
            VirtualColumns = new Dictionary<string, PPDS.Dataverse.Sql.Transpilation.VirtualColumnInfo>(),
            EntityLogicalName = "account"
        };

        var ctx = CreateContext();
        var result = await _executor.ExecuteAsync(planResult, ctx);

        Assert.Equal(0, result.Count);
        Assert.Empty(result.Columns);
    }

    [Fact]
    public async Task Execute_RecordsStatistics()
    {
        var mockNode = new MockPlanNode(new[]
        {
            MakeRow(("name", "A")),
            MakeRow(("name", "B"))
        });

        var planResult = new QueryPlanResult
        {
            RootNode = mockNode,
            FetchXml = "<fetch />",
            VirtualColumns = new Dictionary<string, PPDS.Dataverse.Sql.Transpilation.VirtualColumnInfo>(),
            EntityLogicalName = "account"
        };

        var ctx = CreateContext();
        await _executor.ExecuteAsync(planResult, ctx);

        Assert.Equal(2, ctx.Statistics.RowsOutput);
        Assert.True(ctx.Statistics.ExecutionTimeMs >= 0);
    }

    [Fact]
    public async Task Execute_PreservesFetchXml()
    {
        var mockNode = new MockPlanNode(Array.Empty<QueryRow>());
        var fetchXml = "<fetch><entity name='account'><attribute name='name' /></entity></fetch>";

        var planResult = new QueryPlanResult
        {
            RootNode = mockNode,
            FetchXml = fetchXml,
            VirtualColumns = new Dictionary<string, PPDS.Dataverse.Sql.Transpilation.VirtualColumnInfo>(),
            EntityLogicalName = "account"
        };

        var ctx = CreateContext();
        var result = await _executor.ExecuteAsync(planResult, ctx);

        Assert.Equal(fetchXml, result.ExecutedFetchXml);
    }
}
