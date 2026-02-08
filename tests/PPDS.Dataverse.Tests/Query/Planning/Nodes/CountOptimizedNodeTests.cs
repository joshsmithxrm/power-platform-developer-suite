using System;
using System.Collections.Generic;
using System.Linq;
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
public class CountOptimizedNodeTests
{
    private static QueryPlanContext CreateContext(IQueryExecutor executor)
    {
        return new QueryPlanContext(executor, new ExpressionEvaluator());
    }

    [Fact]
    public async Task WhenCountSucceeds_ReturnsSingleRow()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.GetTotalRecordCountAsync("account", It.IsAny<CancellationToken>()))
            .ReturnsAsync(42L);

        var node = new CountOptimizedNode("account", "count");
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(42L, rows[0].Values["count"].Value);
        Assert.Equal("account", rows[0].EntityLogicalName);
        Assert.Equal(1, ctx.Statistics.RowsRead);
    }

    [Fact]
    public async Task WhenCountSucceeds_UsesSpecifiedAlias()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.GetTotalRecordCountAsync("contact", It.IsAny<CancellationToken>()))
            .ReturnsAsync(100L);

        var node = new CountOptimizedNode("contact", "total_records");
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.True(rows[0].Values.ContainsKey("total_records"));
        Assert.Equal(100L, rows[0].Values["total_records"].Value);
    }

    [Fact]
    public async Task WhenCountFails_FallsBackToFetchXml()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.GetTotalRecordCountAsync("account", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Not supported"));

        // Set up fallback FetchXML result
        var fallbackResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "count" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["count"] = QueryValue.Simple(99)
                }
            },
            Count = 1,
            MoreRecords = false,
            PageNumber = 1
        };
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fallbackResult);

        var fallbackNode = new FetchXmlScanNode("<fetch aggregate='true'><entity name='account'><attribute name='accountid' aggregate='count' alias='count' /></entity></fetch>", "account", autoPage: false);
        var node = new CountOptimizedNode("account", "count", fallbackNode);
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(99, rows[0].Values["count"].Value);
    }

    [Fact]
    public async Task WhenCountReturnsNull_FallsBackToFetchXml()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.GetTotalRecordCountAsync("account", It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var fallbackResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "count" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["count"] = QueryValue.Simple(55)
                }
            },
            Count = 1,
            MoreRecords = false,
            PageNumber = 1
        };
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fallbackResult);

        var fallbackNode = new FetchXmlScanNode("<fetch />", "account", autoPage: false);
        var node = new CountOptimizedNode("account", "count", fallbackNode);
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(55, rows[0].Values["count"].Value);
    }

    [Fact]
    public async Task WhenCountFails_AndNoFallback_YieldsEmpty()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.GetTotalRecordCountAsync("account", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Not supported"));

        var node = new CountOptimizedNode("account", "count", fallbackNode: null);
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
    }

    [Fact]
    public void Description_ContainsEntityName()
    {
        var node = new CountOptimizedNode("account", "count");
        Assert.Contains("account", node.Description);
        Assert.Contains("CountOptimized", node.Description);
    }

    [Fact]
    public void EstimatedRows_IsOne()
    {
        var node = new CountOptimizedNode("account", "count");
        Assert.Equal(1, node.EstimatedRows);
    }

    [Fact]
    public void Children_WithFallback_ContainsFallbackNode()
    {
        var fallback = new FetchXmlScanNode("<fetch />", "account");
        var node = new CountOptimizedNode("account", "count", fallback);
        Assert.Single(node.Children);
        Assert.Same(fallback, node.Children[0]);
    }

    [Fact]
    public void Children_WithoutFallback_IsEmpty()
    {
        var node = new CountOptimizedNode("account", "count");
        Assert.Empty(node.Children);
    }

    [Fact]
    public void Constructor_NullEntityName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CountOptimizedNode(null!, "count"));
    }

    [Fact]
    public void Constructor_NullCountAlias_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CountOptimizedNode("account", null!));
    }
}
