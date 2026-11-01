using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Services.Flows.Utilities;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Flows;

public sealed class FlowActivationReaderTests
{
    [Fact]
    public async Task List_FollowsPagingCookieDeduplicatesAndReleasesEachLease()
    {
        var first = new Entity("workflow", Guid.NewGuid())
        {
            ["uniquename"] = "test_first", ["type"] = new OptionSetValue(1),
            ["category"] = new OptionSetValue(5), ["clientdata"] = """{"properties":{"connectionReferences":{}}}"""
        };
        var second = new Entity("workflow", Guid.NewGuid())
        {
            ["uniquename"] = "test_second", ["type"] = new OptionSetValue(2), ["category"] = new OptionSetValue(5)
        };
        var pages = new List<(int Number, string? Cookie)>();
        var client = new Mock<IPooledClient>();
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase raw, CancellationToken _) =>
            {
                var query = Assert.IsType<QueryExpression>(raw);
                pages.Add((query.PageInfo.PageNumber, query.PageInfo.PagingCookie));
                var component = Assert.Single(query.LinkEntities);
                Assert.Contains(component.LinkCriteria.Conditions, c => c.AttributeName == "componenttype" && (int)c.Values[0] == 29);
                return query.PageInfo.PageNumber == 1
                    ? new EntityCollection([first]) { MoreRecords = true, PagingCookie = "next-page" }
                    : new EntityCollection([first, second]);
            });
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        var service = new FlowService(pool.Object, NullLogger<FlowService>.Instance);
        var result = await service.ListAsync("test_solution", includeClassic: true);
        (int, string?)[] expectedPages = [(1, null), (2, "next-page")];
        Assert.Equal(expectedPages, pages);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.Items.Single(f => f.Id == first.Id).DefinitionType);
        Assert.True(result.Items.Single(f => f.Id == first.Id).ConnectionReferencesVerified);
        client.Verify(c => c.DisposeAsync(), Times.Exactly(2));
        pool.Verify(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Get_AmbiguousUniqueNameDoesNotChooseFirst()
    {
        var client = new Mock<IPooledClient>();
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection([new Entity("workflow", Guid.NewGuid()), new Entity("workflow", Guid.NewGuid())]));
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        var service = new FlowService(pool.Object, NullLogger<FlowService>.Instance);
        Assert.Contains("ambiguous", (await Assert.ThrowsAsync<PpdsException>(() => service.GetAsync("same"))).Message);
    }

    [Theory]
    [InlineData("""{"properties":{"connectionReferences":{"shared_connector":{"connection":{"connectionReferenceLogicalName":"test_actual"}}}}}""", "test_actual")]
    [InlineData("""{"connectionReferences":{"alias":{"connectionReferenceLogicalName":"test_actual"}}}""", "test_actual")]
    [InlineData("""{"definition":{"connectionReferences":{"alias":{"connectionReferenceLogicalName":"test_actual"}}}}""", "test_actual")]
    public void Parser_UsesLogicalNameInsteadOfConnectorAlias(string json, string expected)
    {
        var parsed = FlowClientDataParser.ParseForActivation(json);
        Assert.True(parsed.Verified);
        Assert.Equal(expected, Assert.Single(parsed.Names));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"properties":{"connectionReferences":[]}}""")]
    [InlineData("""{"connectionReferences":{"alias":{}}}""")]
    public void Parser_MissingOrAmbiguousMappingsFailClosed(string? json)
    {
        var parsed = FlowClientDataParser.ParseForActivation(json);
        Assert.False(parsed.Verified);
        Assert.Empty(parsed.Names);
        Assert.NotNull(parsed.Error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"properties":[]}""")]
    [InlineData("""{"definition":[]}""")]
    [InlineData("""{"properties":{"connectionReferences":[]}}""")]
    public async Task Reader_MalformedDependencyShapeRemainsReadableForDisable(string json)
    {
        var id = Guid.NewGuid();
        var entity = new Entity("workflow", id)
        {
            ["uniquename"] = "test_bad_dependencies", ["type"] = new OptionSetValue(1),
            ["category"] = new OptionSetValue(5), ["statecode"] = new OptionSetValue(1),
            ["clientdata"] = json
        };
        var client = new Mock<IPooledClient>();
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.RetrieveAsync("workflow", id, It.IsAny<ColumnSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection([entity]));
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        var service = new FlowService(pool.Object, NullLogger<FlowService>.Instance);
        var single = await service.GetByIdAsync(id);
        Assert.NotNull(single);
        Assert.False(single.ConnectionReferencesVerified);
        Assert.Equal(FlowState.Activated, single.State);
        var list = await service.ListAsync();
        Assert.False(Assert.Single(list.Items).ConnectionReferencesVerified);
    }
}
