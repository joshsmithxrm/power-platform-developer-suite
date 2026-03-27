using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class EntityReferenceMapperTests
{
    private readonly Mock<IDataverseConnectionPool> _pool;
    private readonly Mock<IPooledClient> _client;
    private readonly IdMappingCollection _idMappings;
    private readonly ImportOptions _options;

    public EntityReferenceMapperTests()
    {
        _pool = new Mock<IDataverseConnectionPool>();
        _client = new Mock<IPooledClient>();
        _idMappings = new IdMappingCollection();
        _options = new ImportOptions { ResolveExternalLookups = true, SkipUnresolvedLookups = true };

        _pool.Setup(p => p.GetClientAsync(
                It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
    }

    private EntityReferenceMapper CreateSut() =>
        new(_idMappings, _pool.Object, _options);

    [Fact]
    public async Task ResolvesFromIdMappingFirst()
    {
        var sourceId = Guid.NewGuid();
        var mappedId = Guid.NewGuid();
        _idMappings.AddMapping("account", sourceId, mappedId);

        var sut = CreateSut();
        var result = await sut.ResolveAsync("account", sourceId, CancellationToken.None);

        result.Should().Be(mappedId);
        _pool.Verify(
            p => p.GetClientAsync(It.IsAny<Dataverse.Client.DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FallsBackToDirectIdCheck()
    {
        var sourceId = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is RetrieveRequest), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetrieveResponse());

        var sut = CreateSut();
        var result = await sut.ResolveAsync("account", sourceId, CancellationToken.None);

        result.Should().Be(sourceId);
    }

    [Fact]
    public async Task FallsBackToNameBasedQuery()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is RetrieveRequest), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Record not found"));

        var results = new EntityCollection();
        results.Entities.Add(new Entity("transactioncurrency", targetId));
        _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);

        var sut = CreateSut();
        var result = await sut.ResolveWithNameAsync("transactioncurrency", sourceId, "USD", CancellationToken.None);

        result.Should().Be(targetId);
    }

    [Fact]
    public async Task CachesResolvedLookups()
    {
        var sourceId = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is RetrieveRequest), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetrieveResponse());

        var sut = CreateSut();
        var result1 = await sut.ResolveAsync("account", sourceId, CancellationToken.None);
        var result2 = await sut.ResolveAsync("account", sourceId, CancellationToken.None);

        result1.Should().Be(sourceId);
        result2.Should().Be(sourceId);
        _pool.Verify(
            p => p.GetClientAsync(It.IsAny<Dataverse.Client.DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void MatchesCurrencyByIsoCode()
    {
        var matchField = EntityReferenceMapper.GetMatchField("transactioncurrency");
        matchField.Should().Be("isocurrencycode");
    }
}
