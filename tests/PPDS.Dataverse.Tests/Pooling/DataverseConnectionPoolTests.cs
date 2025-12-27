using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Dataverse.Tests.Pooling;

/// <summary>
/// Tests for DataverseConnectionPool, specifically the new IConnectionSource-based constructor.
/// </summary>
public class DataverseConnectionPoolTests
{
    [Fact]
    public void Constructor_WithNullSources_ThrowsArgumentNullException()
    {
        // Arrange
        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                null!,
                throttleTracker,
                adaptiveRateController,
                poolOptions,
                logger));
    }

    [Fact]
    public void Constructor_WithEmptySources_ThrowsArgumentException()
    {
        // Arrange
        var sources = Array.Empty<IConnectionSource>();
        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            new DataverseConnectionPool(
                sources,
                throttleTracker,
                adaptiveRateController,
                poolOptions,
                logger));

        ex.Message.Should().Contain("source");
    }

    [Fact]
    public void Constructor_WithNullThrottleTracker_ThrowsArgumentNullException()
    {
        // Arrange
        var sources = new[] { Mock.Of<IConnectionSource>(s => s.Name == "Test" && s.MaxPoolSize == 10) };
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                sources,
                null!,
                adaptiveRateController,
                poolOptions,
                logger));
    }

    [Fact]
    public void Constructor_WithNullAdaptiveRateController_ThrowsArgumentNullException()
    {
        // Arrange
        var sources = new[] { Mock.Of<IConnectionSource>(s => s.Name == "Test" && s.MaxPoolSize == 10) };
        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                sources,
                throttleTracker,
                null!,
                poolOptions,
                logger));
    }

    [Fact]
    public void Constructor_WithNullPoolOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var sources = new[] { Mock.Of<IConnectionSource>(s => s.Name == "Test" && s.MaxPoolSize == 10) };
        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                sources,
                throttleTracker,
                adaptiveRateController,
                null!,
                logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        var sources = new[] { Mock.Of<IConnectionSource>(s => s.Name == "Test" && s.MaxPoolSize == 10) };
        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                sources,
                throttleTracker,
                adaptiveRateController,
                poolOptions,
                null!));
    }

    [Fact]
    public void Constructor_WithValidSources_PoolNotEnabled_DoesNotCallGetSeedClient()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = false,
            MinPoolSize = 0 // Don't pre-warm
        };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            adaptiveRateController,
            poolOptions,
            logger);

        // Assert - GetSeedClient should not be called when pool is disabled and MinPoolSize is 0
        sourceMock.Verify(s => s.GetSeedClient(), Times.Never);
    }

    [Fact]
    public void IsEnabled_ReturnsPoolOptionsEnabled()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var poolOptions = new ConnectionPoolOptions { Enabled = true, MinPoolSize = 0 };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            adaptiveRateController,
            poolOptions,
            logger);

        // Assert
        pool.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Dispose_DisposesSources()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false, MinPoolSize = 0 };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            adaptiveRateController,
            poolOptions,
            logger);

        // Act
        pool.Dispose();

        // Assert
        sourceMock.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public void Statistics_ReturnsValidStatistics()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false, MinPoolSize = 0 };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            adaptiveRateController,
            poolOptions,
            logger);

        var stats = pool.Statistics;

        // Assert
        stats.Should().NotBeNull();
        stats.ConnectionStats.Should().ContainKey("Test");
    }

    [Fact]
    public void LegacyConstructor_WithValidOptions_CreatesPool()
    {
        // Arrange
        var options = Options.Create(new DataverseOptions
        {
            Pool = new ConnectionPoolOptions { Enabled = false, MinPoolSize = 0 },
            Connections = new List<DataverseConnection>
            {
                new("Test")
                {
                    Url = "https://test.crm.dynamics.com",
                    ClientId = "test-client-id",
                    ClientSecret = "test-secret",
                    AuthType = DataverseAuthType.ClientSecret
                }
            }
        });

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
#pragma warning disable CS0618 // Type or member is obsolete
        using var pool = new DataverseConnectionPool(
            options,
            throttleTracker,
            adaptiveRateController,
            logger);
#pragma warning restore CS0618

        // Assert
        pool.IsEnabled.Should().BeFalse();
        pool.Statistics.ConnectionStats.Should().ContainKey("Test");
    }

    [Fact]
    public void LegacyConstructor_WithNoConnections_ThrowsConfigurationException()
    {
        // Arrange
        var options = Options.Create(new DataverseOptions
        {
            Pool = new ConnectionPoolOptions { Enabled = false }
            // No connections configured
        });

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var adaptiveRateController = Mock.Of<IAdaptiveRateController>();
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Throws<ConfigurationException>(() =>
            new DataverseConnectionPool(
                options,
                throttleTracker,
                adaptiveRateController,
                logger));
#pragma warning restore CS0618
    }
}
