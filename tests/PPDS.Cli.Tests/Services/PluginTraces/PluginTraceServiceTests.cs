using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.PluginTraces;

public class PluginTraceServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Arrange
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<PluginTraceService>();

        // Act
        var act = () => new PluginTraceService(null!, guard, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("pool");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new InactiveFakeShakedownGuard();

        // Act
        var act = () => new PluginTraceService(pool, guard, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<PluginTraceService>();

        // Act
        var service = new PluginTraceService(pool, guard, logger);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void AddCliApplicationServices_RegistersIPluginTraceService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IShakedownGuard, InactiveFakeShakedownGuard>();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var traceService = provider.GetService<IPluginTraceService>();

        // Assert
        traceService.Should().NotBeNull();
        traceService.Should().BeOfType<PluginTraceService>();
    }

    [Fact]
    public void AddCliApplicationServices_PluginTraceServiceIsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IShakedownGuard, InactiveFakeShakedownGuard>();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var service1 = provider.GetService<IPluginTraceService>();
        var service2 = provider.GetService<IPluginTraceService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }
}
