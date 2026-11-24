using PPDS.Tests.Shared;
using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

[Trait("Category", "TuiUnit")]
public sealed class AsyncHelperTests
{
    [Fact]
    public async Task FireAndForget_ReportsError_OnFaultedTask()
    {
        var errorService = new TuiErrorService();
        var faultedTask = Task.FromException(new InvalidOperationException("test error"));
        var reported = NextReportedError(errorService);

        errorService.FireAndForget(faultedTask, "TestContext");

        await HangBackstop.WaitAsync(reported, "the error service to report the faulted task");

        Assert.Single(errorService.RecentErrors);
        Assert.Contains("TestContext", errorService.RecentErrors[0].Context);
    }

    [Fact]
    public async Task FireAndForget_DoesNotReportError_OnSuccessfulTask()
    {
        var errorService = new TuiErrorService();

        errorService.FireAndForget(Task.CompletedTask, "TestContext");

        // No signal exists for "the continuation ran and reported nothing", so this delay cannot make the
        // test fail on a slow runner; at worst the assertion runs before the continuation and passes
        // without having checked it. A deterministic check needs FireAndForget to expose its continuation.
        await Task.Delay(50);

        Assert.Empty(errorService.RecentErrors);
    }

    [Fact]
    public async Task FireAndForget_UnwrapsAggregateException()
    {
        var errorService = new TuiErrorService();
        var inner = new InvalidOperationException("inner");
        var faultedTask = Task.FromException(new AggregateException(inner));
        var reported = NextReportedError(errorService);

        errorService.FireAndForget(faultedTask, "AggregateTest");

        await HangBackstop.WaitAsync(reported, "the error service to report the faulted task");

        Assert.Single(errorService.RecentErrors);
    }

    [Fact]
    public void FireAndForget_DoesNotThrow_WhenCalledSynchronously()
    {
        var errorService = new TuiErrorService();
        var exception = Record.Exception(() =>
            errorService.FireAndForget(Task.FromException(new Exception("boom")), "Sync"));

        Assert.Null(exception);
    }

    // Completes when the service raises ErrorOccurred, which it does after recording the error, so
    // RecentErrors is populated by then. Subscribe before starting the work so the event cannot be missed.
    private static Task<TuiError> NextReportedError(TuiErrorService errorService)
    {
        var reported = new TaskCompletionSource<TuiError>(TaskCreationOptions.RunContinuationsAsynchronously);
        errorService.ErrorOccurred += error => reported.TrySetResult(error);
        return reported.Task;
    }
}
