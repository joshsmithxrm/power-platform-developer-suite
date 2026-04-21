using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Services.Flows;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Flows;

public class FlowServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Arrange
        var logger = new NullLogger<FlowService>();

        // Act
        var act = () => new FlowService(null!, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("pool");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;

        // Act
        var act = () => new FlowService(pool, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var logger = new NullLogger<FlowService>();

        // Act
        var service = new FlowService(pool, logger);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void AddCliApplicationServices_RegistersIFlowService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var flowService = provider.GetService<IFlowService>();

        // Assert
        flowService.Should().NotBeNull();
        flowService.Should().BeOfType<FlowService>();
    }

    [Fact]
    public void AddCliApplicationServices_FlowServiceIsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var service1 = provider.GetService<IFlowService>();
        var service2 = provider.GetService<IFlowService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }
}
