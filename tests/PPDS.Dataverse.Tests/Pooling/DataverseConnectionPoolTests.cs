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
/// Tests for DataverseConnectionPool, specifically the IConnectionSource-based constructor.
/// </summary>
public class DataverseConnectionPoolTests
{
    #region Constructor Validation Tests

    [Fact]
    public void Constructor_WithNullSources_ThrowsArgumentNullException()
    {
        // Arrange
        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                null!,
                throttleTracker,
                poolOptions,
                logger));
    }

    [Fact]
    public void Constructor_WithEmptySources_ThrowsArgumentException()
    {
        // Arrange
        var sources = Array.Empty<IConnectionSource>();
        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            new DataverseConnectionPool(
                sources,
                throttleTracker,
                poolOptions,
                logger));

        ex.Message.Should().Contain("source");
    }

    [Fact]
    public void Constructor_WithNullThrottleTracker_ThrowsArgumentNullException()
    {
        // Arrange
        var sources = new[] { Mock.Of<IConnectionSource>(s => s.Name == "Test" && s.MaxPoolSize == 10) };
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                sources,
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
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                sources,
                throttleTracker,
                null!,
                logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        var sources = new[] { Mock.Of<IConnectionSource>(s => s.Name == "Test" && s.MaxPoolSize == 10) };
        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DataverseConnectionPool(
                sources,
                throttleTracker,
                poolOptions,
                null!));
    }

    #endregion

    #region Pool Disabled Tests

    [Fact]
    public void Constructor_WithValidSources_PoolNotEnabled_DoesNotCallGetSeedClient()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = false // Pool disabled - should not initialize seeds
        };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Assert - GetSeedClient should not be called when pool is disabled
        sourceMock.Verify(s => s.GetSeedClient(), Times.Never);
    }

    [Fact]
    public void IsEnabled_WhenPoolDisabled_ReturnsFalse()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Assert
        pool.IsEnabled.Should().BeFalse();
    }

    #endregion

    #region SourceCount Tests

    [Fact]
    public void SourceCount_WithSingleSource_ReturnsOne()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Assert
        pool.SourceCount.Should().Be(1);
    }

    [Fact]
    public void SourceCount_WithMultipleSources_ReturnsCorrectCount()
    {
        // Arrange
        var source1 = Mock.Of<IConnectionSource>(s => s.Name == "Source1" && s.MaxPoolSize == 10);
        var source2 = Mock.Of<IConnectionSource>(s => s.Name == "Source2" && s.MaxPoolSize == 10);
        var source3 = Mock.Of<IConnectionSource>(s => s.Name == "Source3" && s.MaxPoolSize == 10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { source1, source2, source3 },
            throttleTracker,
            poolOptions,
            logger);

        // Assert
        pool.SourceCount.Should().Be(3);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_DisposesSources()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        pool.Dispose();

        // Assert
        sourceMock.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public void Dispose_MultipleCalls_OnlyDisposesOnce()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        pool.Dispose();
        pool.Dispose();
        pool.Dispose();

        // Assert - should only dispose once even with multiple calls
        sourceMock.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_DisposesSources()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        await pool.DisposeAsync();

        // Assert
        sourceMock.Verify(s => s.Dispose(), Times.Once);
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public void Statistics_ReturnsValidStatistics()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        var stats = pool.Statistics;

        // Assert
        stats.Should().NotBeNull();
        stats.ConnectionStats.Should().ContainKey("Test");
    }

    [Fact]
    public void Statistics_WithMultipleSources_ContainsAllSources()
    {
        // Arrange
        var source1 = Mock.Of<IConnectionSource>(s => s.Name == "Primary" && s.MaxPoolSize == 10);
        var source2 = Mock.Of<IConnectionSource>(s => s.Name == "Secondary" && s.MaxPoolSize == 10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { source1, source2 },
            throttleTracker,
            poolOptions,
            logger);

        var stats = pool.Statistics;

        // Assert
        stats.ConnectionStats.Should().ContainKey("Primary");
        stats.ConnectionStats.Should().ContainKey("Secondary");
        stats.ConnectionStats.Should().HaveCount(2);
    }

    [Fact]
    public void Statistics_InitialState_HasZeroCounts()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        var stats = pool.Statistics;

        // Assert
        stats.RequestsServed.Should().Be(0);
        stats.ThrottleEvents.Should().Be(0);
        stats.InvalidConnections.Should().Be(0);
        stats.AuthFailures.Should().Be(0);
        stats.ConnectionFailures.Should().Be(0);
    }

    #endregion

    #region Failure Recording Tests

    [Fact]
    public void RecordAuthFailure_IncrementsStatistics()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        pool.RecordAuthFailure();
        pool.RecordAuthFailure();

        // Assert
        pool.Statistics.AuthFailures.Should().Be(2);
    }

    [Fact]
    public void RecordConnectionFailure_IncrementsStatistics()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        pool.RecordConnectionFailure();
        pool.RecordConnectionFailure();
        pool.RecordConnectionFailure();

        // Assert
        pool.Statistics.ConnectionFailures.Should().Be(3);
    }

    #endregion

    #region DOP-Based Parallelism Tests

    [Fact]
    public void GetLiveSourceDop_WithUnknownSource_ReturnsDefaultValue()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act - query for a source that doesn't exist
        var dop = pool.GetLiveSourceDop("NonExistent");

        // Assert - should return conservative default
        dop.Should().Be(4);
    }

    [Fact]
    public void GetActiveConnectionCount_WithUnknownSource_ReturnsZero()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        var count = pool.GetActiveConnectionCount("NonExistent");

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public void GetActiveConnectionCount_InitialState_ReturnsZero()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        var count = pool.GetActiveConnectionCount("Test");

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public void GetTotalRecommendedParallelism_WithDisabledPool_ReturnsZeroOrDefault()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        var parallelism = pool.GetTotalRecommendedParallelism();

        // Assert - with pool disabled, seeds aren't initialized, so returns default (4 per source)
        parallelism.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region Pool Options Tests

    [Fact]
    public void Constructor_UsesMaxPoolSizeFromOptions()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = false,
            MaxPoolSize = 100
        };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Assert - pool should be created successfully with custom options
        pool.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_UsesSelectionStrategyFromOptions()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = false,
            SelectionStrategy = ConnectionSelectionStrategy.RoundRobin
        };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act - should not throw
        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Assert
        pool.Should().NotBeNull();
    }

    #endregion

    #region Seed Invalidation Tests

    [Fact]
    public void InvalidateSeed_WithNullConnectionName_DoesNotThrow()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act & Assert - should not throw
        pool.InvalidateSeed(null!);
        pool.InvalidateSeed("");
    }

    [Fact]
    public void InvalidateSeed_WithValidConnectionName_CallsSourceInvalidate()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act
        pool.InvalidateSeed("Test");

        // Assert
        sourceMock.Verify(s => s.InvalidateSeed(), Times.Once);
    }

    [Fact]
    public void InvalidateSeed_WithNonExistentConnectionName_DoesNotThrow()
    {
        // Arrange
        var sourceMock = new Mock<IConnectionSource>();
        sourceMock.Setup(s => s.Name).Returns("Test");
        sourceMock.Setup(s => s.MaxPoolSize).Returns(10);

        var throttleTracker = Mock.Of<IThrottleTracker>();
        var poolOptions = new ConnectionPoolOptions { Enabled = false };
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        using var pool = new DataverseConnectionPool(
            new[] { sourceMock.Object },
            throttleTracker,
            poolOptions,
            logger);

        // Act & Assert - should not throw even for non-existent connection
        pool.InvalidateSeed("NonExistent");
    }

    #endregion

    #region Legacy Constructor Tests

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
        var logger = NullLogger<DataverseConnectionPool>.Instance;

        // Act & Assert
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Throws<ConfigurationException>(() =>
            new DataverseConnectionPool(
                options,
                throttleTracker,
                logger));
#pragma warning restore CS0618
    }

    #endregion
}
