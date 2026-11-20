using PPDS.Tests.Shared;
using Xunit;
using Xunit.Sdk;

namespace PPDS.Cli.Tests.TestHelpers;

// The firing path uses a short injected bound; the default bound is only ever reached by a real hang.
public sealed class HangBackstopTests
{
    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task HangThatObservesTheToken_FailsAtTheBoundNamingWhatWasAwaited()
    {
        var error = await Assert.ThrowsAsync<FailException>(() => HangBackstop.WaitAsync<int>(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, "the cooperative signal", ShortBound));

        Assert.StartsWith("No completion after 50ms waiting for the cooperative signal.", error.Message);
    }

    [Fact]
    public async Task HangThatIgnoresTheToken_FailsAtTheBoundNamingWhatWasAwaited()
    {
        var never = new TaskCompletionSource<int>();

        var error = await Assert.ThrowsAsync<FailException>(() =>
            HangBackstop.WaitAsync(_ => never.Task, "the non-cooperative signal", ShortBound));

        Assert.StartsWith("No completion after 50ms waiting for the non-cooperative signal.", error.Message);
    }

    [Fact]
    public async Task HangThatBlocksItsThread_FailsAtTheBound()
    {
        using var release = new ManualResetEventSlim();
        try
        {
            var error = await Assert.ThrowsAsync<FailException>(() => HangBackstop.WaitAsync<int>(_ =>
            {
                release.Wait(); // Blocks before returning a task; released in finally.
                return Task.FromResult(0);
            }, "the blocking signal", ShortBound));

            Assert.StartsWith("No completion after 50ms waiting for the blocking signal.", error.Message);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CompletedOperation_ReturnsItsResult()
    {
        Assert.Equal(42, await HangBackstop.WaitAsync(Task.FromResult(42), "an already completed signal"));
    }

    [Fact]
    public async Task FaultedOperation_SurfacesItsExceptionUnchanged()
    {
        var fault = new InvalidOperationException("the operation's own failure");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HangBackstop.WaitAsync<int>(_ => Task.FromException<int>(fault), "a faulting signal"));

        Assert.Same(fault, error);
    }

    [Fact]
    public async Task OperationCancelledByItsOwnToken_SurfacesTheCancellationUnchanged()
    {
        using var own = new CancellationTokenSource();
        own.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HangBackstop.WaitAsync<int>(_ => Task.FromCanceled<int>(own.Token), "a signal cancelled by its owner"));

        Assert.Equal(own.Token, error.CancellationToken);
    }
}
