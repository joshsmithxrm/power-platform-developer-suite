using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Services;
using Xunit;

namespace PPDS.Dataverse.Tests.Services;

public class ComponentNameResolverTests
{
    private readonly Mock<ICachedMetadataProvider> _metadataProvider = new();
    private readonly Mock<IMetadataQueryService> _metadataService = new();
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly ILogger<ComponentNameResolver> _logger = NullLogger<ComponentNameResolver>.Instance;

    private ComponentNameResolver CreateResolver() =>
        new(_metadataProvider.Object, _metadataService.Object, _pool.Object, _logger);

    [Fact]
    public async Task ResolveAsync_EntityType_UsesMetadataProvider()
    {
        var entityId = Guid.NewGuid();
        var entities = new List<EntitySummary>
        {
            new()
            {
                MetadataId = entityId,
                LogicalName = "account",
                SchemaName = "Account",
                DisplayName = "Account",
                ObjectTypeCode = 1
            }
        };
        _metadataProvider
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(1, new[] { entityId });

        result.Should().ContainKey(entityId);
        result[entityId].LogicalName.Should().Be("account");
        result[entityId].SchemaName.Should().Be("Account");
        result[entityId].DisplayName.Should().Be("Account");

        _pool.Verify(p => p.GetClientAsync(
            It.IsAny<DataverseClientOptions>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_UnmappedType_ReturnsEmptyDictionary()
    {
        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(999, new[] { Guid.NewGuid() });
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_EmptyObjectIds_ReturnsEmptyDictionary()
    {
        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(61, Array.Empty<Guid>());
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_LogsTiming()
    {
        var entityId = Guid.NewGuid();
        _metadataProvider
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new()
                {
                    MetadataId = entityId,
                    LogicalName = "account",
                    SchemaName = "Account",
                    DisplayName = "Account",
                    ObjectTypeCode = 1
                }
            });

        var mockLogger = new Mock<ILogger<ComponentNameResolver>>();
        var resolver = new ComponentNameResolver(_metadataProvider.Object, _metadataService.Object, _pool.Object, mockLogger.Object);

        await resolver.ResolveAsync(1, new[] { entityId });

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Resolved") && v.ToString()!.Contains("ms")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_PartialFailure_ReturnsEmptyAndLogsWarning()
    {
        _metadataProvider
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Metadata unavailable"));

        var mockLogger = new Mock<ILogger<ComponentNameResolver>>();
        var resolver = new ComponentNameResolver(_metadataProvider.Object, _metadataService.Object, _pool.Object, mockLogger.Object);

        var result = await resolver.ResolveAsync(1, new[] { Guid.NewGuid() });

        result.Should().BeEmpty();

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_EntityNotInCache_OmitsFromResult()
    {
        var unknownId = Guid.NewGuid();
        _metadataProvider
            .Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>());

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(1, new[] { unknownId });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_LargeBatch_SplitsIntoMultipleQueries()
    {
        var objectIds = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();

        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        _pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        var resolver = CreateResolver();
        await resolver.ResolveAsync(61, objectIds);

        _pool.Verify(
            p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ResolveAsync_WebResource_QueriesTable()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var entity1 = new Entity("webresource", id1);
        entity1["name"] = "new_scripts/form.js";
        var entity2 = new Entity("webresource", id2);
        entity2["name"] = "new_styles/global.css";

        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { entity1, entity2 }));

        _pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(61, new[] { id1, id2 });

        result.Should().HaveCount(2);
        result[id1].LogicalName.Should().Be("new_scripts/form.js");
        result[id2].LogicalName.Should().Be("new_styles/global.css");
    }
}
