using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Services.Users;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Users;

public class UserServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Arrange
        var logger = new NullLogger<UserService>();

        // Act
        var act = () => new UserService(null!, logger);

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
        var act = () => new UserService(pool, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var logger = new NullLogger<UserService>();

        // Act
        var service = new UserService(pool, logger);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void AddCliApplicationServices_RegistersIUserService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var userService = provider.GetService<IUserService>();

        // Assert
        userService.Should().NotBeNull();
        userService.Should().BeOfType<UserService>();
    }

    [Fact]
    public void AddCliApplicationServices_UserServiceIsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var service1 = provider.GetService<IUserService>();
        var service2 = provider.GetService<IUserService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }
}
