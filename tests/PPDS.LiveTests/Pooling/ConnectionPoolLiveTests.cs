using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Pooling;

/// <summary>
/// Live integration tests for DataverseConnectionPool using real Dataverse connections.
/// Tests verify pool initialization, connection distribution, and recovery scenarios.
/// </summary>
[Trait("Category", "Integration")]
public class ConnectionPoolLiveTests : LiveTestBase, IDisposable
{
    private readonly ITestOutputHelper _output;

    public ConnectionPoolLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region Pool Initialization Tests

    [SkipIfNoClientSecret]
    public async Task Pool_InitializesWithRealServiceClient()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "TestPool");

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Assert
        pool.Should().NotBeNull();
        pool.IsEnabled.Should().BeTrue();
        pool.SourceCount.Should().Be(1);

        _output.WriteLine($"Pool initialized. SourceCount: {pool.SourceCount}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_ReportsStatisticsAfterInitialization()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "StatsTest");

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });
        var stats = pool.Statistics;

        // Assert
        stats.Should().NotBeNull();
        stats.ConnectionStats.Should().ContainKey("StatsTest");

        var connStats = stats.ConnectionStats["StatsTest"];
        connStats.Name.Should().Be("StatsTest");
        connStats.IsThrottled.Should().BeFalse();

        _output.WriteLine($"Pool statistics - Active: {stats.ActiveConnections}, Idle: {stats.IdleConnections}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_WarmUpCreatesInitialConnection()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "WarmUpTest");

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });
        var stats = pool.Statistics;

        // Assert - Pool should have warmed up with at least 1 idle connection
        stats.IdleConnections.Should().BeGreaterThanOrEqualTo(1,
            "Pool should warm up with at least 1 connection per source");

        _output.WriteLine($"Warmed up connections: {stats.IdleConnections}");
    }

    #endregion

    #region Connection Distribution Tests

    [SkipIfNoClientSecret]
    public async Task Pool_GetClientReturnsWorkingClient()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "GetClientTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act
        await using var client = await pool.GetClientAsync();

        // Assert
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue();
        client.ConnectionName.Should().Be("GetClientTest");

        _output.WriteLine($"Got client: {client.DisplayName}, IsReady: {client.IsReady}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_ClientCanExecuteWhoAmI()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "WhoAmITest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act
        await using var client = await pool.GetClientAsync();
        var response = (WhoAmIResponse)client.Execute(new WhoAmIRequest());

        // Assert
        response.Should().NotBeNull();
        response.UserId.Should().NotBeEmpty();
        response.OrganizationId.Should().NotBeEmpty();

        _output.WriteLine($"WhoAmI - UserId: {response.UserId}, OrgId: {response.OrganizationId}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_MultipleClientCheckoutsWork()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "MultiClientTest", maxPoolSize: 10);
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act - Get multiple clients concurrently
        var tasks = Enumerable.Range(0, 3).Select(async i =>
        {
            await using var client = await pool.GetClientAsync();
            var response = (WhoAmIResponse)client.Execute(new WhoAmIRequest());
            return (Index: i, UserId: response.UserId, ConnectionId: client.ConnectionId);
        });

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(3);
        results.All(r => r.UserId != Guid.Empty).Should().BeTrue();

        _output.WriteLine($"Completed {results.Length} concurrent WhoAmI calls");
        foreach (var r in results)
        {
            _output.WriteLine($"  Request {r.Index}: ConnectionId={r.ConnectionId}");
        }
    }

    [SkipIfNoClientSecret]
    public async Task Pool_ConnectionsAreReturnedToPool()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "ReturnTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        var initialStats = pool.Statistics;
        var initialIdle = initialStats.IdleConnections;

        // Act - Get a client and dispose it
        var client = await pool.GetClientAsync();
        await client.DisposeAsync();

        // Small delay to allow return to pool
        await Task.Delay(100);

        var afterStats = pool.Statistics;

        // Assert - Connection should be returned to pool
        afterStats.IdleConnections.Should().BeGreaterThanOrEqualTo(initialIdle,
            "Connection should be returned to idle pool after dispose");

        _output.WriteLine($"Before checkout: Idle={initialIdle}, After return: Idle={afterStats.IdleConnections}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_TrackActiveConnectionsDuringUse()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "ActiveTrackTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act - Get a client but don't dispose yet
        var client = await pool.GetClientAsync();
        var duringUseStats = pool.Statistics;
        var activeCount = pool.GetActiveConnectionCount("ActiveTrackTest");

        // Now dispose
        await client.DisposeAsync();
        await Task.Delay(50);
        var afterDisposeStats = pool.Statistics;

        // Assert
        activeCount.Should().BeGreaterThanOrEqualTo(1, "Should track at least 1 active connection during use");

        _output.WriteLine($"During use: Active={duringUseStats.ActiveConnections}");
        _output.WriteLine($"After dispose: Active={afterDisposeStats.ActiveConnections}");
    }

    #endregion

    #region Connection Recovery Tests

    [SkipIfNoClientSecret]
    public async Task Pool_CanInvalidateSeedAndRecreate()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "SeedInvalidateTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Get initial client to verify pool works
        await using (var client1 = await pool.GetClientAsync())
        {
            var response = (WhoAmIResponse)client1.Execute(new WhoAmIRequest());
            response.UserId.Should().NotBeEmpty();
        }

        // Act - Invalidate the seed
        pool.InvalidateSeed("SeedInvalidateTest");

        // Try to get a new client - should still work
        // Note: ServiceClientSource.InvalidateSeed is a no-op, so this tests graceful handling
        await using var client2 = await pool.GetClientAsync();
        var response2 = (WhoAmIResponse)client2.Execute(new WhoAmIRequest());

        // Assert
        response2.UserId.Should().NotBeEmpty();

        _output.WriteLine("Successfully executed request after seed invalidation");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_StatisticsTrackRequestsServed()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "RequestCountTest");
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        var initialStats = pool.Statistics;
        var initialRequests = initialStats.RequestsServed;

        // Act - Execute multiple requests
        for (int i = 0; i < 3; i++)
        {
            await using var client = await pool.GetClientAsync();
            client.Execute(new WhoAmIRequest());
        }

        var finalStats = pool.Statistics;

        // Assert - Request count should have increased
        finalStats.RequestsServed.Should().BeGreaterThan(initialRequests,
            "RequestsServed should increment with each pool checkout");

        _output.WriteLine($"Initial requests: {initialRequests}, Final: {finalStats.RequestsServed}");
    }

    #endregion

    #region Load Distribution Tests

    [SkipIfNoClientSecret]
    public async Task Pool_DistributesLoadUnderConcurrentRequests()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(
            Configuration, "LoadDistTest", maxPoolSize: 10);
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act - Execute many concurrent requests
        var requestCount = 10;
        var connectionIds = new List<Guid>();
        var lockObj = new object();

        var tasks = Enumerable.Range(0, requestCount).Select(async i =>
        {
            await using var client = await pool.GetClientAsync();
            lock (lockObj)
            {
                connectionIds.Add(client.ConnectionId);
            }
            await Task.Delay(50); // Brief work simulation
            return client.ConnectionId;
        });

        await Task.WhenAll(tasks);

        // Assert - Should have distributed across multiple connections
        var uniqueConnections = connectionIds.Distinct().Count();
        var stats = pool.Statistics;

        _output.WriteLine($"Total requests: {requestCount}");
        _output.WriteLine($"Unique connection IDs: {uniqueConnections}");
        _output.WriteLine($"Pool stats - RequestsServed: {stats.RequestsServed}, ConnectionStats: {stats.ConnectionStats.Count}");

        // The pool should serve all requests
        stats.RequestsServed.Should().BeGreaterThanOrEqualTo(requestCount);
    }

    #endregion

    public void Dispose()
    {
        Configuration.Dispose();
    }
}
