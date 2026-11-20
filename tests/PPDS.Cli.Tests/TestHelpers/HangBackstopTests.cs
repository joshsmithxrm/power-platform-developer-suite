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
    public async Task NonGenericHangThatIgnoresTheToken_FailsAtTheBoundNamingWhatWasAwaited()
    {
        var never = new TaskCompletionSource();

        var error = await Assert.ThrowsAsync<FailException>(() =>
            HangBackstop.WaitAsync(_ => never.Task, "the non-generic signal", ShortBound));

        Assert.StartsWith("No completion after 50ms waiting for the non-generic signal.", error.Message);
    }

    [Fact]
    public async Task NonGenericHangThatObservesTheToken_FailsAtTheBound()
    {
        var error = await Assert.ThrowsAsync<FailException>(() =>
            HangBackstop.WaitAsync(token => Task.Delay(Timeout.Infinite, token), "the non-generic cooperative signal", ShortBound));

        Assert.StartsWith("No completion after 50ms waiting for the non-generic cooperative signal.", error.Message);
    }

    [Fact]
    public async Task NonGenericSignal_CompletesAndSurfacesItsFaultUnchanged()
    {
        await HangBackstop.WaitAsync(Task.CompletedTask, "an already completed non-generic signal");
        var fault = new InvalidOperationException("the signal's own failure");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HangBackstop.WaitAsync(Task.FromException(fault), "a faulting non-generic signal"));

        Assert.Same(fault, error);
    }

    [Fact]
    public async Task AtTheBound_TheOperationsTokenIsCancelled()
    {
        var observed = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<int>();

        await Assert.ThrowsAsync<FailException>(() => HangBackstop.WaitAsync(token =>
        {
            observed.TrySetResult(token);
            return never.Task; // Ignores the token, so only the guard can cancel it.
        }, "a signal that never completes", ShortBound));

        Assert.True((await observed.Task).IsCancellationRequested);
    }

    public static TheoryData<string> TwoArgumentOverloads => new() { "signal of T", "operation of T", "signal", "operation" };

    // The two-argument overloads are the ones every other test calls; each must apply a bound, not hand back
    // the bare signal. The override shortens the bound for this async flow only.
    [Theory]
    [MemberData(nameof(TwoArgumentOverloads))]
    public async Task TwoArgumentOverload_FailsAtTheDefaultBound(string overload)
    {
        var never = new TaskCompletionSource<int>();
        Task call;
        HangBackstop.BoundOverrideForHelperTests.Value = ShortBound;
        try
        {
            call = overload switch
            {
                "signal of T" => HangBackstop.WaitAsync(never.Task, "the signal of T"),
                "operation of T" => HangBackstop.WaitAsync(_ => never.Task, "the operation of T"),
                "signal" => HangBackstop.WaitAsync((Task)never.Task, "the signal"),
                _ => HangBackstop.WaitAsync(_ => (Task)never.Task, "the operation"),
            };
        }
        finally { HangBackstop.BoundOverrideForHelperTests.Value = null; }

        // An unguarded overload never completes; the full bound turns that into a clean failure, not a hang.
        Assert.Same(call, await Task.WhenAny(call, Task.Delay(HangBackstop.Bound)));
        var error = await Assert.ThrowsAsync<FailException>(() => call);
        Assert.StartsWith($"No completion after 50ms waiting for the {overload}.", error.Message);
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
