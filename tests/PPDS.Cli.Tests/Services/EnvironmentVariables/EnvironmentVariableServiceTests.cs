using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.EnvironmentVariables;

public class EnvironmentVariableServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Arrange
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<EnvironmentVariableService>();

        // Act
        var act = () => new EnvironmentVariableService(null!, guard, logger);

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
        var act = () => new EnvironmentVariableService(pool, guard, null!);

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
        var logger = new NullLogger<EnvironmentVariableService>();

        // Act
        var service = new EnvironmentVariableService(pool, guard, logger);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void AddCliApplicationServices_RegistersIEnvironmentVariableService()
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
        var envVarService = provider.GetService<IEnvironmentVariableService>();

        // Assert
        envVarService.Should().NotBeNull();
        envVarService.Should().BeOfType<EnvironmentVariableService>();
    }

    [Fact]
    public void AddCliApplicationServices_EnvironmentVariableServiceIsTransient()
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
        var service1 = provider.GetService<IEnvironmentVariableService>();
        var service2 = provider.GetService<IEnvironmentVariableService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }
}
