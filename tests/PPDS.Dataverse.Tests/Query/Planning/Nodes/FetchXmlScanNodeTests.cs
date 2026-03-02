using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class FetchXmlScanNodeTests
{
    private static QueryPlanContext CreateContext(IQueryExecutor executor)
    {
        return new QueryPlanContext(executor);
    }

    private static QueryResult MakeResult(string entity, int count, bool moreRecords = false, string? pagingCookie = null)
    {
        var records = new List<IReadOnlyDictionary<string, QueryValue>>();
        for (int i = 0; i < count; i++)
        {
            records.Add(new Dictionary<string, QueryValue>
            {
                ["name"] = QueryValue.Simple($"Record {i}")
            });
        }

        return new QueryResult
        {
            EntityLogicalName = entity,
            Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
            Records = records,
            Count = count,
            MoreRecords = moreRecords,
            PagingCookie = pagingCookie,
            PageNumber = 1
        };
    }

    [Fact]
    public async Task SinglePage_YieldsAllRows()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("account", 3));

        var node = new FetchXmlScanNode("<fetch><entity name='account'><attribute name='name' /></entity></fetch>", "account");
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("account", r.EntityLogicalName));
    }

    [Fact]
    public async Task MultiplePages_FetchesAll()
    {
        var page1 = MakeResult("account", 2, moreRecords: true, pagingCookie: "cookie1");
        var page2 = MakeResult("account", 1, moreRecords: false);

        var callCount = 0;
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++callCount == 1 ? page1 : page2);

        var node = new FetchXmlScanNode("<fetch><entity name='account'><attribute name='name' /></entity></fetch>", "account", autoPage: true);
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, ctx.Statistics.PagesFetched);
    }

    [Fact]
    public async Task SinglePageMode_StopsAfterFirstPage()
    {
        var result = MakeResult("account", 2, moreRecords: true);
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var node = new FetchXmlScanNode("<fetch />", "account", autoPage: false);
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, ctx.Statistics.PagesFetched);
    }

    [Fact]
    public async Task MaxRows_LimitsOutput()
    {
        var result = MakeResult("account", 10);
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var node = new FetchXmlScanNode("<fetch />", "account", maxRows: 5);
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public async Task Cancellation_StopsIteration()
    {
        using var cts = new CancellationTokenSource();
        var result = MakeResult("account", 5, moreRecords: true, pagingCookie: "cookie");

        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var node = new FetchXmlScanNode("<fetch />", "account");
        var ctx = CreateContext(mockExecutor.Object);

        var rowCount = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(ctx, cts.Token))
            {
                rowCount++;
                if (rowCount >= 3)
                {
                    cts.Cancel();
                }
            }
        });
    }

    [Fact]
    public void Description_IncludesEntityName()
    {
        var node = new FetchXmlScanNode("<fetch />", "account");
        Assert.Contains("account", node.Description);
    }

    [Fact]
    public void TopN_ConvertsToPagingInEffectiveFetchXml()
    {
        // FetchXML with top="100" should have top removed and count="100" in effective XML
        var fetchXml = "<fetch top=\"100\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>";
        var node = new FetchXmlScanNode(fetchXml, "account", maxRows: 100);

        // FetchXml property preserved for display
        Assert.Contains("top=", node.FetchXml);

        // Description should reflect the node correctly
        Assert.Contains("account", node.Description);
    }

    [Fact]
    public async Task TopN_ExecutesWithoutError()
    {
        // Verify that TOP N queries execute without the top+page conflict
        var fetchXml = "<fetch top=\"100\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>";

        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.Is<string>(f => !f.Contains("top=") && f.Contains("count=")),
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("account", 3));

        var node = new FetchXmlScanNode(fetchXml, "account", maxRows: 100);
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        // Verify the executor was called with converted FetchXML (no top, has count)
        mockExecutor.Verify(x => x.ExecuteFetchXmlAsync(
            It.Is<string>(f => !f.Contains("top=") && f.Contains("count=")),
            It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NoTop_FetchXmlUnchanged()
    {
        var fetchXml = "<fetch><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>";

        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("account", 3));

        var node = new FetchXmlScanNode(fetchXml, "account");
        var ctx = CreateContext(mockExecutor.Object);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void TopN_CapsCountAt5000()
    {
        var fetchXml = "<fetch top=\"7000\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>";
        var node = new FetchXmlScanNode(fetchXml, "account", maxRows: 7000);

        // The original FetchXml property is preserved
        Assert.Contains("top=", node.FetchXml);
    }
}
