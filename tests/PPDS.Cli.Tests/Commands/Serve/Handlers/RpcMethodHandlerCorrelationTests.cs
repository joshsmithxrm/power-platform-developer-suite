using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.DependencyInjection;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Diagnostics;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Tests for H2 — correlation id threading through <c>SafeExecuteAsync</c>.
/// Verifies that the RPC boundary always mints a fresh id and the handler body
/// sees that fresh id ambiently, even when an outer scope (e.g., the CLI bootstrap
/// scope from Program.Main) is already in flight. Outer scopes are restored after
/// the handler returns.
/// </summary>
[Trait("Category", "Unit")]
public class RpcMethodHandlerCorrelationTests
{
    private static RpcMethodHandler CreateHandler()
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var authServices = new ServiceCollection().AddAuthServices().BuildServiceProvider();
        return new RpcMethodHandler(mockPoolManager.Object, authServices);
    }

    [Fact]
    public async Task SafeExecuteAsync_SetsAmbientCorrelationIdForHandler()
    {
        var handler = CreateHandler();
        string? observed = null;

        await handler.SafeExecuteAsync("test/method", () =>
        {
            observed = CorrelationIdScope.Current;
            return Task.FromResult(true);
        });

        Assert.False(string.IsNullOrWhiteSpace(observed));
    }

    [Fact]
    public async Task SafeExecuteAsync_MintsFreshCorrelationIdAtBoundaryDespiteOuterScope()
    {
        var handler = CreateHandler();
        string? observed = null;

        // Program.Main pushes a process-lifetime bootstrap scope before any RPC arrives.
        // SafeExecuteAsync must NOT honor it — otherwise every RPC in daemon mode would
        // share one correlation id, defeating per-call telemetry.
        using (CorrelationIdScope.Push("from-cli-entry"))
        {
            await handler.SafeExecuteAsync("test/method", () =>
            {
                observed = CorrelationIdScope.Current;
                return Task.FromResult(true);
            });
        }

        Assert.False(string.IsNullOrWhiteSpace(observed));
        Assert.NotEqual("from-cli-entry", observed);
    }

    [Fact]
    public async Task SafeExecuteAsync_DistinctCallsGetDistinctCorrelationIds()
    {
        // Regression guard for the daemon-mode leak: every call through SafeExecuteAsync
        // must mint a fresh id, even when invoked back-to-back under the same outer scope.
        var handler = CreateHandler();
        string? first = null;
        string? second = null;

        using (CorrelationIdScope.Push("bootstrap"))
        {
            await handler.SafeExecuteAsync("a", () =>
            {
                first = CorrelationIdScope.Current;
                return Task.FromResult(true);
            });
            await handler.SafeExecuteAsync("b", () =>
            {
                second = CorrelationIdScope.Current;
                return Task.FromResult(true);
            });
        }

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.False(string.IsNullOrWhiteSpace(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task SafeExecuteAsync_RestoresOuterCorrelationIdAfterReturn()
    {
        var handler = CreateHandler();

        using (CorrelationIdScope.Push("outer"))
        {
            await handler.SafeExecuteAsync("test/method", () => Task.FromResult(true));
            Assert.Equal("outer", CorrelationIdScope.Current);
        }
    }
}
