using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Services;
using Xunit;

namespace PPDS.Dataverse.Tests.Services;

public class ConnectionReferenceServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Arrange
        var flowService = new Mock<IFlowService>().Object;
        var logger = new NullLogger<ConnectionReferenceService>();

        // Act
        var act = () => new ConnectionReferenceService(null!, flowService, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("pool");
    }

    [Fact]
    public void Constructor_ThrowsOnNullFlowService()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var logger = new NullLogger<ConnectionReferenceService>();

        // Act
        var act = () => new ConnectionReferenceService(pool, null!, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("flowService");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var flowService = new Mock<IFlowService>().Object;

        // Act
        var act = () => new ConnectionReferenceService(pool, flowService, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var flowService = new Mock<IFlowService>().Object;
        var logger = new NullLogger<ConnectionReferenceService>();

        // Act
        var service = new ConnectionReferenceService(pool, flowService, logger);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void AddDataverseConnectionPool_RegistersIConnectionReferenceService()
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
        var crService = provider.GetService<IConnectionReferenceService>();

        // Assert
        crService.Should().NotBeNull();
        crService.Should().BeOfType<ConnectionReferenceService>();
    }

    [Fact]
    public void AddDataverseConnectionPool_ConnectionReferenceServiceIsTransient()
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
        var service1 = provider.GetService<IConnectionReferenceService>();
        var service2 = provider.GetService<IConnectionReferenceService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }
}
