using System;
using System.Collections.Generic;
using System.Net.Http;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Moq;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Security;
using Xunit;

namespace PPDS.Dataverse.Tests.BulkOperations;

/// <summary>
/// Unit tests for <see cref="BulkOperationExecutor"/> retry-policy behavior.
/// </summary>
/// <remarks>
/// These tests verify C1 (v1 pre-launch audit): the retry counters are split per error
/// category so that one category does not starve the others. Prior implementation used
/// a single shared counter — throttle retries would exhaust the budget and then the
/// first transient auth/connection/deadlock blip would fail immediately.
/// </remarks>
[Trait("Category", "Unit")]
public class BulkOperationExecutorRetryCounterTests
{
    /// <summary>
    /// Builds a mock throttle tracker that never reports a connection as throttled,
    /// so the pre-flight loop short-circuits and each call proceeds to ExecuteAsync.
    /// </summary>
    private static Mock<IThrottleTracker> CreateNoopThrottleTracker()
    {
        var mock = new Mock<IThrottleTracker>();
        mock.Setup(t => t.IsThrottled(It.IsAny<string>())).Returns(false);
        return mock;
    }

    /// <summary>
    /// Builds a mock pool that returns a new mock client on each GetClientAsync call.
    /// Each issued client's ExecuteAsync throws the exception supplied by
    /// <paramref name="executeFailureFactory"/> (keyed by the overall call index).
    /// </summary>
    private static (Mock<IDataverseConnectionPool> pool, List<Mock<IPooledClient>> issuedClients)
        CreateThrowingPool(Func<int, Exception> executeFailureFactory)
    {
        var issued = new List<Mock<IPooledClient>>();
        var mockPool = new Mock<IDataverseConnectionPool>();
        var callCount = 0;

        mockPool
            .Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var mockClient = new Mock<IPooledClient>();
                mockClient.SetupGet(c => c.ConnectionName).Returns($"conn-{callCount}");
                mockClient.SetupGet(c => c.DisplayName).Returns($"conn-{callCount}@test-org");
                mockClient.SetupGet(c => c.ConnectionId).Returns(Guid.NewGuid());
                mockClient.SetupGet(c => c.RecommendedDegreesOfParallelism).Returns(4);

                var thisCall = callCount;
                mockClient
                    .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(executeFailureFactory(thisCall));
                mockClient
                    .Setup(c => c.DisposeAsync())
                    .Returns(new ValueTask());

                issued.Add(mockClient);
                callCount++;
                return mockClient.Object;
            });

        mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(1);

        return (mockPool, issued);
    }

    private static BulkOperationExecutor CreateExecutor(IDataverseConnectionPool pool, int maxConnectionRetries)
    {
        var options = new DataverseOptions
        {
            BulkOperations = new BulkOperationOptions
            {
                BatchSize = 100,
                MaxParallelBatches = 1,
            },
            Pool = new ConnectionPoolOptions
            {
                MaxConnectionRetries = maxConnectionRetries
            }
        };

        return new BulkOperationExecutor(
            pool,
            CreateNoopThrottleTracker().Object,
            Options.Create(options),
            NullLogger<BulkOperationExecutor>.Instance);
    }

    // ═══════════════════════════════════════════════════════════════
    //  C1: Per-category retry counter independence
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="BulkOperationExecutor.CreateMultipleAsync"/> acquires one pooled client
    /// up-front to read <c>RecommendedDegreesOfParallelism</c> before entering the retry
    /// loop. That "info client" call does not trigger ExecuteAsync, but it does consume
    /// one slot in our <c>issuedClients</c> list. All per-retry assertions below subtract
    /// this offset.
    /// </summary>
    private const int InfoClientCallOffset = 1;

    [Fact]
    public async Task ConnectionFailure_RetriesUpToMaxConnectionRetries()
    {
        // Arrange: every ExecuteAsync raises an HttpRequestException, which the executor
        // classifies as a connection failure. With MaxConnectionRetries=3 the retry loop
        // must issue 3 attempts and then surface a DataverseConnectionException.
        const int maxRetries = 3;
        var (mockPool, issuedClients) = CreateThrowingPool(_ =>
            new HttpRequestException("simulated transient network failure"));
        var executor = CreateExecutor(mockPool.Object, maxConnectionRetries: maxRetries);

        var entities = new List<Entity>
        {
            new("account") { ["name"] = "Acme" }
        };

        // Act & Assert: retries exhausted — final throw after attempt 3
        await Assert.ThrowsAsync<DataverseConnectionException>(
            () => executor.CreateMultipleAsync("account", entities));

        // maxRetries clients in the retry loop, plus one info-client acquire before the loop.
        Assert.Equal(maxRetries + InfoClientCallOffset, issuedClients.Count);
    }

    [Fact]
    public async Task ThrottleFollowedByConnectionFailure_DoesNotShareBudget()
    {
        // Arrange: first 5 retry-loop calls raise service-protection (throttle) faults,
        // then subsequent calls raise HttpRequestException connection failures.
        // With MaxConnectionRetries=3, if the counters were shared (pre-fix bug) the first
        // HttpRequestException (overall retry-loop attempt #6) would already exceed the
        // retry budget and the executor would throw after a single connection-failure
        // attempt. With per-category counters, the connection-failure branch must still
        // get its full budget of 3 attempts before giving up.
        const int maxRetries = 3;
        const int throttleCount = 5;

        var (mockPool, issuedClients) = CreateThrowingPool(callIndex =>
        {
            // callIndex 0 is the info-client acquisition and never reaches ExecuteAsync.
            // Retry-loop calls start at callIndex 1.
            var retryIndex = callIndex - InfoClientCallOffset;

            if (retryIndex < throttleCount)
            {
                var fault = new OrganizationServiceFault
                {
                    // ErrorCodeRequestsExceeded triggers TryGetThrottleInfo → throttle branch
                    ErrorCode = ServiceProtectionException.ErrorCodeRequestsExceeded,
                    Message = "service protection limit"
                };
                fault.ErrorDetails["Retry-After"] = TimeSpan.FromMilliseconds(1);
                return new FaultException<OrganizationServiceFault>(fault, new FaultReason("throttled"));
            }

            return new HttpRequestException("simulated transient network failure");
        });

        var executor = CreateExecutor(mockPool.Object, maxConnectionRetries: maxRetries);
        var entities = new List<Entity>
        {
            new("account") { ["name"] = "Acme" }
        };

        // Act & Assert
        await Assert.ThrowsAsync<DataverseConnectionException>(
            () => executor.CreateMultipleAsync("account", entities));

        // Retry-loop attempts = throttleCount (5) + maxRetries (3) = 8, plus one info-client call.
        // Prior bug (shared counter): only ~4 retry-loop clients would be issued before premature failure.
        Assert.Equal(throttleCount + maxRetries + InfoClientCallOffset, issuedClients.Count);
    }
}
