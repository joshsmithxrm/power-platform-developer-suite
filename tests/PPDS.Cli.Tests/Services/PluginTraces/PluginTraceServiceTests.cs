using System.ServiceModel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
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

    // ─── H5: D4 fault-wrapping tests ────────────────────────────────────────────

    private static PluginTraceService CreateService(IDataverseConnectionPool pool)
        => new(pool, new InactiveFakeShakedownGuard(), new NullLogger<PluginTraceService>());

    private static (Mock<IDataverseConnectionPool> pool, Mock<IPooledClient> client) CreateFaultingPool(Exception fault)
    {
        var mockClient = new Mock<IPooledClient>(MockBehavior.Loose);
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(fault);
        mockClient
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(fault);

        var mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        mockPool
            .Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);
        mockPool
            .Setup(p => p.GetTotalRecommendedParallelism())
            .Returns(4);
        return (mockPool, mockClient);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WrapsFaultExceptionInPpdsException()
    {
        // Arrange
        var innerFault = new FaultException("Dataverse unavailable");
        var (pool, _) = CreateFaultingPool(innerFault);
        var service = CreateService(pool.Object);

        // Act
        var act = () => service.ListAsync();

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.PluginTrace.ListFailed);
        ex.Which.InnerException.Should().BeSameAs(innerFault);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WrapsGenericExceptionInPpdsException()
    {
        // Arrange
        var innerException = new InvalidOperationException("network error");
        var (pool, _) = CreateFaultingPool(innerException);
        var service = CreateService(pool.Object);

        // Act
        var act = () => service.ListAsync();

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.PluginTrace.ListFailed);
        ex.Which.InnerException.Should().BeSameAs(innerException);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountAsync_WrapsFaultExceptionInPpdsException()
    {
        // Arrange
        var innerFault = new FaultException("Count query failed");
        var (pool, _) = CreateFaultingPool(innerFault);
        var service = CreateService(pool.Object);

        // Act
        var act = () => service.CountAsync();

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.PluginTrace.CountFailed);
        ex.Which.InnerException.Should().BeSameAs(innerFault);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSettingsAsync_WrapsFaultExceptionInPpdsException()
    {
        // Arrange
        var innerFault = new FaultException("Organization table unavailable");
        var (pool, _) = CreateFaultingPool(innerFault);
        var service = CreateService(pool.Object);

        // Act
        var act = () => service.GetSettingsAsync();

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.PluginTrace.GetSettingsFailed);
        ex.Which.InnerException.Should().BeSameAs(innerFault);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_DoesNotWrapOperationCanceledException()
    {
        // Arrange — OperationCanceledException must propagate unwrapped
        var (pool, _) = CreateFaultingPool(new OperationCanceledException());
        var service = CreateService(pool.Object);

        // Act & Assert
        await ((Func<Task>)(() => service.ListAsync()))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
