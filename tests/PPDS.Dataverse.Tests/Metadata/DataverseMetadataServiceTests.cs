using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata;

public class DataverseMetadataServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Act
        var act = () => new DataverseMetadataService(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("connectionPool");
    }

    [Fact]
    public void AddDataverseConnectionPool_RegistersIMetadataService()
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
        var metadataService = provider.GetService<IMetadataService>();

        // Assert
        metadataService.Should().NotBeNull();
        metadataService.Should().BeOfType<DataverseMetadataService>();
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
        var service1 = provider.GetService<IMetadataService>();
        var service2 = provider.GetService<IMetadataService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }
}
