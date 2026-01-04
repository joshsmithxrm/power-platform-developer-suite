using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Dataverse.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="ServiceCollectionExtensions.RegisterDataverseServices"/>.
/// These tests verify the shared DI registration method that prevents CLI/library divergence.
/// </summary>
public class RegisterDataverseServicesTests
{
    [Fact]
    public void RegisterDataverseServices_RegistersIThrottleTracker()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterDataverseServices();

        // Assert
        var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(IThrottleTracker));
        descriptor.Should().NotBeNull("IThrottleTracker should be registered");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(ThrottleTracker));
    }

    [Fact]
    public void RegisterDataverseServices_RegistersIBulkOperationExecutor()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterDataverseServices();

        // Assert
        var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(IBulkOperationExecutor));
        descriptor.Should().NotBeNull("IBulkOperationExecutor should be registered");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Transient);
        descriptor.ImplementationType.Should().Be(typeof(BulkOperationExecutor));
    }

    [Fact]
    public void RegisterDataverseServices_RegistersIMetadataService()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterDataverseServices();

        // Assert
        var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(IMetadataService));
        descriptor.Should().NotBeNull("IMetadataService should be registered");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Transient);
        descriptor.ImplementationType.Should().Be(typeof(DataverseMetadataService));
    }

    [Fact]
    public void RegisterDataverseServices_DoesNotRegisterIDataverseConnectionPool()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.RegisterDataverseServices();

        // Assert - Pool is NOT registered here (registered separately by library and CLI with different patterns)
        var descriptor = services.FirstOrDefault(sd => sd.ServiceType == typeof(PPDS.Dataverse.Pooling.IDataverseConnectionPool));
        descriptor.Should().BeNull("IDataverseConnectionPool should be registered separately by callers");
    }

    [Fact]
    public void RegisterDataverseServices_ReturnsServiceCollectionForChaining()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.RegisterDataverseServices();

        // Assert
        result.Should().BeSameAs(services);
    }

    [Fact]
    public void RegisterDataverseServices_CalledMultipleTimes_AllowsDuplicateRegistrations()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act - Call twice (shouldn't happen but verify it's safe)
        services.RegisterDataverseServices();
        services.RegisterDataverseServices();

        // Assert - Should have duplicates (standard DI behavior) but both should work
        // This documents the behavior, not necessarily the desired behavior
        var throttleDescriptors = services.Where(sd => sd.ServiceType == typeof(IThrottleTracker)).ToList();
        throttleDescriptors.Should().HaveCount(2, "DI allows duplicate registrations (last wins for singletons)");
    }
}
