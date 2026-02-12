using FluentAssertions;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class RemoteScanNodeTests
{
    [Fact]
    public async Task ExecuteAsync_UsesRemoteExecutor_NotContextExecutor()
    {
        // Arrange: set up a mock remote executor with test records
        var remoteExecutor = new Mock<IQueryExecutor>();
        var records = new List<IReadOnlyDictionary<string, QueryValue>>
        {
            new Dictionary<string, QueryValue>
            {
                ["name"] = QueryValue.Simple("Contoso"),
                ["accountid"] = QueryValue.Simple(Guid.NewGuid())
            },
            new Dictionary<string, QueryValue>
            {
                ["name"] = QueryValue.Simple("Fabrikam"),
                ["accountid"] = QueryValue.Simple(Guid.NewGuid())
            }
        };

        var queryResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>(),
            Records = records,
            Count = 2,
            MoreRecords = false
        };

        const string fetchXml = "<fetch><entity name='account'><all-attributes /></entity></fetch>";

        remoteExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                fetchXml,
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryResult);

        var node = new RemoteScanNode(
            fetchXml: fetchXml,
            entityLogicalName: "account",
            remoteLabel: "UAT",
            remoteExecutor: remoteExecutor.Object);

        // Act: execute with a context whose QueryExecutor is a different mock
        var rows = await TestHelpers.CollectRowsAsync(node);

        // Assert
        rows.Should().HaveCount(2);
        rows[0].Values["name"].Value.Should().Be("Contoso");
        rows[1].Values["name"].Value.Should().Be("Fabrikam");
        rows[0].EntityLogicalName.Should().Be("account");

        // Verify the remote executor was called, NOT the context executor
        remoteExecutor.Verify(
            e => e.ExecuteFetchXmlAsync(
                fetchXml,
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResult_YieldsNoRows()
    {
        var remoteExecutor = new Mock<IQueryExecutor>();

        remoteExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(QueryResult.Empty("contact"));

        var node = new RemoteScanNode(
            fetchXml: "<fetch><entity name='contact' /></fetch>",
            entityLogicalName: "contact",
            remoteLabel: "PROD",
            remoteExecutor: remoteExecutor.Object);

        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UsesEntityLogicalNameFromResult()
    {
        var remoteExecutor = new Mock<IQueryExecutor>();
        var records = new List<IReadOnlyDictionary<string, QueryValue>>
        {
            new Dictionary<string, QueryValue>
            {
                ["fullname"] = QueryValue.Simple("John Doe")
            }
        };

        var queryResult = new QueryResult
        {
            EntityLogicalName = "contact",
            Columns = new List<QueryColumn>(),
            Records = records,
            Count = 1,
            MoreRecords = false
        };

        remoteExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryResult);

        var node = new RemoteScanNode(
            fetchXml: "<fetch><entity name='contact' /></fetch>",
            entityLogicalName: "contact",
            remoteLabel: "DEV",
            remoteExecutor: remoteExecutor.Object);

        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(1);
        rows[0].EntityLogicalName.Should().Be("contact");
    }

    [Fact]
    public void Description_IncludesRemoteLabelAndEntity()
    {
        var node = new RemoteScanNode(
            fetchXml: "<fetch/>",
            entityLogicalName: "account",
            remoteLabel: "UAT",
            remoteExecutor: Mock.Of<IQueryExecutor>());

        node.Description.Should().Contain("[UAT]");
        node.Description.Should().Contain("account");
        node.Description.Should().Be("RemoteScan: [UAT].account");
    }

    [Fact]
    public void EstimatedRows_Returns1000()
    {
        var node = new RemoteScanNode(
            fetchXml: "<fetch/>",
            entityLogicalName: "account",
            remoteLabel: "UAT",
            remoteExecutor: Mock.Of<IQueryExecutor>());

        node.EstimatedRows.Should().Be(1000);
    }

    [Fact]
    public void Children_IsEmpty()
    {
        var node = new RemoteScanNode(
            fetchXml: "<fetch/>",
            entityLogicalName: "account",
            remoteLabel: "UAT",
            remoteExecutor: Mock.Of<IQueryExecutor>());

        node.Children.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ThrowsOnNullArguments()
    {
        var executor = Mock.Of<IQueryExecutor>();

        var act1 = () => new RemoteScanNode(null!, "account", "UAT", executor);
        var act2 = () => new RemoteScanNode("<fetch/>", null!, "UAT", executor);
        var act3 = () => new RemoteScanNode("<fetch/>", "account", null!, executor);
        var act4 = () => new RemoteScanNode("<fetch/>", "account", "UAT", null!);

        act1.Should().Throw<ArgumentNullException>().WithParameterName("fetchXml");
        act2.Should().Throw<ArgumentNullException>().WithParameterName("entityLogicalName");
        act3.Should().Throw<ArgumentNullException>().WithParameterName("remoteLabel");
        act4.Should().Throw<ArgumentNullException>().WithParameterName("remoteExecutor");
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotCallContextQueryExecutor()
    {
        // Arrange: set up both a remote executor and a context executor
        var remoteExecutor = new Mock<IQueryExecutor>();
        var contextExecutor = new Mock<IQueryExecutor>();

        remoteExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(QueryResult.Empty("account"));

        var node = new RemoteScanNode(
            fetchXml: "<fetch><entity name='account' /></fetch>",
            entityLogicalName: "account",
            remoteLabel: "UAT",
            remoteExecutor: remoteExecutor.Object);

        var context = new QueryPlanContext(contextExecutor.Object);

        // Act
        var rows = await TestHelpers.CollectRowsAsync(node, context);

        // Assert: remote was called, context was NOT called
        remoteExecutor.Verify(
            e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        contextExecutor.Verify(
            e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
