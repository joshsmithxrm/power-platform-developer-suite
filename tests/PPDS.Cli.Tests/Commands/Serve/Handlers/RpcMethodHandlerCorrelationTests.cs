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
/// Verifies that handler bodies see an ambient <see cref="CorrelationIdScope"/>
/// and that the wrapper reuses a caller-supplied id (from CLI entry) when present.
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
    public async Task SafeExecuteAsync_ReusesOuterCorrelationId()
    {
        var handler = CreateHandler();
        string? observed = null;

        using (CorrelationIdScope.Push("from-cli-entry"))
        {
            await handler.SafeExecuteAsync("test/method", () =>
            {
                observed = CorrelationIdScope.Current;
                return Task.FromResult(true);
            });
        }

        // Handler should see the outer scope value, not a fresh GUID.
        Assert.Equal("from-cli-entry", observed);
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
