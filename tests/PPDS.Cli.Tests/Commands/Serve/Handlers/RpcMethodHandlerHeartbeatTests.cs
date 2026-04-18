using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.DependencyInjection;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Diagnostics;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Tests for the <c>_heartbeat</c> RPC method (H1). Heartbeat lets the Extension
/// detect a dead daemon by polling; the response must contain uptime, a correlation
/// id, and be incrementally counted so the client can notice reconnects.
/// </summary>
[Trait("Category", "Unit")]
public class RpcMethodHandlerHeartbeatTests
{
    private static RpcMethodHandler CreateHandler()
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var authServices = new ServiceCollection().AddAuthServices().BuildServiceProvider();
        return new RpcMethodHandler(mockPoolManager.Object, authServices);
    }

    [Fact]
    public async Task HeartbeatAsync_ReturnsOkWithUptimeAndFreshCorrelationId()
    {
        var handler = CreateHandler();

        // Even with an outer correlation scope in flight (e.g., CLI bootstrap or another
        // call's lingering scope), HeartbeatAsync runs through SafeExecuteAsync which
        // mints a FRESH id at the RPC boundary. The response must not leak the outer id.
        using var scope = CorrelationIdScope.Push("hb-outer-leak-guard");

        var response = await handler.HeartbeatAsync();

        Assert.True(response.Ok);
        Assert.True(response.UptimeSeconds >= 0);
        Assert.False(string.IsNullOrWhiteSpace(response.CorrelationId));
        Assert.NotEqual("hb-outer-leak-guard", response.CorrelationId);
        Assert.Equal(1, response.HeartbeatCount);
        Assert.False(string.IsNullOrWhiteSpace(response.DaemonVersion));
    }

    [Fact]
    public async Task HeartbeatAsync_IncrementsHeartbeatCount()
    {
        var handler = CreateHandler();

        var first = await handler.HeartbeatAsync();
        var second = await handler.HeartbeatAsync();
        var third = await handler.HeartbeatAsync();

        Assert.Equal(1, first.HeartbeatCount);
        Assert.Equal(2, second.HeartbeatCount);
        Assert.Equal(3, third.HeartbeatCount);
    }

    [Fact]
    public async Task HeartbeatAsync_WithoutAmbientScope_GeneratesCorrelationId()
    {
        var handler = CreateHandler();

        // Clear any ambient scope the test runner may have installed.
        using var scope = CorrelationIdScope.Push(null);

        var response = await handler.HeartbeatAsync();

        // SafeExecuteAsync mints a new correlation id when none is present, and
        // the heartbeat echoes CorrelationIdScope.Current which is set during
        // SafeExecuteAsync. Either way, the response correlation id must be non-empty.
        Assert.True(response.Ok);
        Assert.False(string.IsNullOrWhiteSpace(response.CorrelationId));
    }
}
