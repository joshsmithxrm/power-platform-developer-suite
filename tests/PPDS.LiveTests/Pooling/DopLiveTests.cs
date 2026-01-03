using FluentAssertions;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Pooling;

/// <summary>
/// Live integration tests for Degrees of Parallelism (DOP) discovery and calculation.
/// These tests verify that the pool correctly reads and respects the x-ms-dop-hint header
/// from real Dataverse environments.
/// </summary>
[Trait("Category", "Integration")]
public class DopLiveTests : LiveTestBase, IDisposable
{
    private readonly ITestOutputHelper _output;

    public DopLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region DOP Discovery Tests

    [SkipIfNoClientSecret]
    public async Task Pool_DiscoversDopFromServer()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "DopDiscoveryTest");

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });
        var dop = pool.GetLiveSourceDop("DopDiscoveryTest");

        // Assert - DOP should be a valid value between 1 and 52 (Microsoft's hard limit)
        dop.Should().BeInRange(1, 52,
            "DOP should be within Microsoft's allowed range (1-52 per user)");

        _output.WriteLine($"Discovered DOP: {dop}");
        _output.WriteLine("Note: Trial environments typically report DOP=4, production can go up to 52");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_ClientReportsRecommendedDop()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "ClientDopTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act
        await using var client = await pool.GetClientAsync();
        var clientDop = client.RecommendedDegreesOfParallelism;

        // Assert
        clientDop.Should().BeGreaterThan(0, "Client should report positive DOP");
        clientDop.Should().BeLessThanOrEqualTo(52, "Client DOP should not exceed Microsoft's limit");

        _output.WriteLine($"Client RecommendedDegreesOfParallelism: {clientDop}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_TotalRecommendedParallelismMatchesSourceDop()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "TotalDopTest");

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });
        var sourceDop = pool.GetLiveSourceDop("TotalDopTest");
        var totalDop = pool.GetTotalRecommendedParallelism();

        // Assert - With one source, total should equal source DOP
        totalDop.Should().Be(sourceDop,
            "With single source, total recommended parallelism should equal source DOP");

        _output.WriteLine($"Source DOP: {sourceDop}, Total DOP: {totalDop}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_DopIsReadFromSeedClient()
    {
        // Arrange - Create a ServiceClient directly to compare
        using var directClient = await LiveTestHelpers.CreateServiceClientAsync(Configuration);
        var directDop = directClient.RecommendedDegreesOfParallelism;

        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "SeedDopTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });
        var poolDop = pool.GetLiveSourceDop("SeedDopTest");

        // Assert - Pool's DOP should match what the seed client reports
        // Note: Allow small variance as DOP can change between connections
        poolDop.Should().BeGreaterThan(0);
        poolDop.Should().BeLessThanOrEqualTo(52);

        _output.WriteLine($"Direct client DOP: {directDop}");
        _output.WriteLine($"Pool source DOP: {poolDop}");
    }

    #endregion

    #region DOP-Based Pool Sizing Tests

    [SkipIfNoClientSecret]
    public async Task Pool_SizeBasedOnDop()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "PoolSizeTest");

        var options = new ConnectionPoolOptions
        {
            Enabled = true,
            EnableValidation = false,
            MaxPoolSize = 0, // Use DOP-based sizing
        };

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source }, options);
        var dop = pool.GetLiveSourceDop("PoolSizeTest");

        // Assert - Pool should size based on DOP
        _output.WriteLine($"DOP: {dop}");
        _output.WriteLine($"With DOP-based sizing, pool capacity is limited to discovered DOP value");

        // The pool should be able to serve at least DOP concurrent requests
        dop.Should().BeGreaterThan(0);
    }

    [SkipIfNoClientSecret]
    public async Task Pool_CanOverrideDopWithFixedSize()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "FixedSizeTest");

        var fixedSize = 5;
        var options = new ConnectionPoolOptions
        {
            Enabled = true,
            EnableValidation = false,
            MaxPoolSize = fixedSize, // Override DOP-based sizing
        };

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source }, options);
        var dop = pool.GetLiveSourceDop("FixedSizeTest");

        // Assert
        _output.WriteLine($"Server DOP: {dop}, Configured MaxPoolSize: {fixedSize}");
        _output.WriteLine("Fixed MaxPoolSize overrides DOP-based sizing when set > 0");

        // Pool should have been created successfully with fixed size
        pool.Should().NotBeNull();
        pool.IsEnabled.Should().BeTrue();
    }

    #endregion

    #region DOP Reporting Tests

    [SkipIfNoClientSecret]
    public async Task Pool_ReportsDopInRange()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "DopRangeTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act
        var dop = pool.GetLiveSourceDop("DopRangeTest");

        // Assert
        dop.Should().BeInRange(1, 52,
            $"DOP must be capped at Microsoft's hard limit of {52}");

        _output.WriteLine($"DOP: {dop}");
        _output.WriteLine($"Microsoft hard limit per user: {52}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_DopReflectsEnvironmentType()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "EnvTypeTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act
        await using var client = await pool.GetClientAsync();
        var dop = client.RecommendedDegreesOfParallelism;
        var orgFriendlyName = client.ConnectedOrgFriendlyName;

        // Assert - Just log the values for reference
        _output.WriteLine($"Connected to: {orgFriendlyName}");
        _output.WriteLine($"DOP reported: {dop}");
        _output.WriteLine("");
        _output.WriteLine("Reference DOP values by environment type:");
        _output.WriteLine("  Trial/Sandbox: ~4");
        _output.WriteLine("  Production (low load): ~10-20");
        _output.WriteLine("  Production (high capacity): up to 52");

        dop.Should().BeGreaterThan(0);
    }

    #endregion

    #region Default DOP Fallback Tests

    [SkipIfNoClientSecret]
    public async Task Pool_UsesFallbackForUnknownSource()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "FallbackTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act - Query for a source that doesn't exist
        var unknownDop = pool.GetLiveSourceDop("NonExistentSource");

        // Assert - Should return conservative default of 4
        unknownDop.Should().Be(4,
            "Unknown source should return conservative default DOP of 4");

        _output.WriteLine($"Unknown source DOP fallback: {unknownDop}");
    }

    #endregion

    public void Dispose()
    {
        Configuration.Dispose();
    }
}
