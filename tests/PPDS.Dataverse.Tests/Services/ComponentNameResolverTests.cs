using System;
using System.Collections.Generic;
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
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly ILogger<ComponentNameResolver> _logger = NullLogger<ComponentNameResolver>.Instance;

    private ComponentNameResolver CreateResolver() =>
        new(_metadataProvider.Object, _pool.Object, _logger);

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
}
