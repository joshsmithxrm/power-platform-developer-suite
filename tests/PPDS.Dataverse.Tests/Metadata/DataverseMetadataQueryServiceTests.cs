using System;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata;

public class DataverseMetadataQueryServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Act
        var act = () => new DataverseMetadataQueryService(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("connectionPool");
    }

    [Fact]
    public void AddDataverseConnectionPool_RegistersIMetadataQueryService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("Primary")
            {
                Url = "https://test.crm.dynamics.com",
                ClientId = "test-client-id",
                ClientSecret = "test-secret",
                AuthType = DataverseAuthType.ClientSecret
            });
        });

        // Act
        var provider = services.BuildServiceProvider();
        var metadataService = provider.GetService<IMetadataQueryService>();

        // Assert
        metadataService.Should().NotBeNull();
        metadataService.Should().BeOfType<DataverseMetadataQueryService>();
    }

    [Fact]
    public void EntitySummary_HasMetadataIdProperty()
    {
        var summary = new EntitySummary
        {
            MetadataId = Guid.NewGuid(),
            LogicalName = "account",
            DisplayName = "Account",
            SchemaName = "Account",
            ObjectTypeCode = 1
        };
        summary.MetadataId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void AddDataverseConnectionPool_MetadataServiceIsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("Primary")
            {
                Url = "https://test.crm.dynamics.com",
                ClientId = "test-client-id",
                ClientSecret = "test-secret",
                AuthType = DataverseAuthType.ClientSecret
            });
        });

        // Act
        var provider = services.BuildServiceProvider();
        var service1 = provider.GetService<IMetadataQueryService>();
        var service2 = provider.GetService<IMetadataQueryService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }
}
