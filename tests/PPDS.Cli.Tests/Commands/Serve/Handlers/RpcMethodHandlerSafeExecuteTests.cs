using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.DependencyInjection;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Tests for <see cref="RpcMethodHandler"/>'s unhandled-exception wrapper.
/// Covers H4 (daemon must not crash on unhandled handler exception; must return a structured response).
/// </summary>
[Trait("Category", "Unit")]
public class RpcMethodHandlerSafeExecuteTests
{
    private static RpcMethodHandler CreateHandler()
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var authServices = new ServiceCollection().AddAuthServices().BuildServiceProvider();
        return new RpcMethodHandler(mockPoolManager.Object, authServices);
    }

    [Fact]
    public async Task SafeExecuteAsync_Success_ReturnsResult()
    {
        var handler = CreateHandler();

        var result = await handler.SafeExecuteAsync("test/method", () => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task SafeExecuteAsync_RpcException_PassesThrough()
    {
        var handler = CreateHandler();
        var original = new RpcException(ErrorCodes.Validation.RequiredField, "missing field X");

        var thrown = await Assert.ThrowsAsync<RpcException>(() =>
            handler.SafeExecuteAsync<int>("test/method", () => throw original));

        // Same instance — the wrapper must not re-wrap structured errors.
        Assert.Same(original, thrown);
        Assert.Equal(ErrorCodes.Validation.RequiredField, thrown.StructuredErrorCode);
    }

    [Fact]
    public async Task SafeExecuteAsync_OperationCanceled_PassesThrough()
    {
        var handler = CreateHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.SafeExecuteAsync<int>(
                "test/method",
                () => Task.FromCanceled<int>(cts.Token)));
    }

    [Fact]
    public async Task SafeExecuteAsync_UnhandledException_WrapsInInternalRpcException()
    {
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<RpcException>(() =>
            handler.SafeExecuteAsync<int>(
                "test/method",
                () => throw new InvalidOperationException("secret/path/to/config")));

        Assert.Equal(ErrorCodes.Operation.Internal, thrown.StructuredErrorCode);
        // Redacted: the original exception message must not appear in the client-facing message.
        Assert.DoesNotContain("secret/path/to/config", thrown.Message);
        // The method name must be included so the extension surface can classify errors.
        Assert.Contains("test/method", thrown.Message);
        // A correlation id lets operators tie the client-side report to the daemon stderr log.
        Assert.Contains("CorrelationId", thrown.Message);
    }

    [Fact]
    public async Task SafeExecuteAsync_NullReference_DoesNotCrash()
    {
        // A raw NullReferenceException from a buggy handler must not take down the daemon.
        var handler = CreateHandler();

        var thrown = await Assert.ThrowsAsync<RpcException>(() =>
            handler.SafeExecuteAsync<int>(
                "test/method",
                () => throw new NullReferenceException("dereferenced null at X")));

        Assert.Equal(ErrorCodes.Operation.Internal, thrown.StructuredErrorCode);
    }
}
