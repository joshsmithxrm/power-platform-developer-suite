using System.Diagnostics;
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

        errorService.FireAndForget(faultedTask, "TestContext");

        await WaitForErrors(errorService, count: 1);

        Assert.Single(errorService.RecentErrors);
        Assert.Contains("TestContext", errorService.RecentErrors[0].Context);
    }

    [Fact]
    public async Task FireAndForget_DoesNotReportError_OnSuccessfulTask()
    {
        var errorService = new TuiErrorService();

        errorService.FireAndForget(Task.CompletedTask, "TestContext");

        // No error expected — short delay is sufficient for the successful path
        await Task.Delay(50);

        Assert.Empty(errorService.RecentErrors);
    }

    [Fact]
    public async Task FireAndForget_UnwrapsAggregateException()
    {
        var errorService = new TuiErrorService();
        var inner = new InvalidOperationException("inner");
        var faultedTask = Task.FromException(new AggregateException(inner));

        errorService.FireAndForget(faultedTask, "AggregateTest");

        await WaitForErrors(errorService, count: 1);

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

    // The poll returns as soon as the condition holds, so the deadline is only a backstop against a hang.
    // It must be generous: a loaded CI runner can delay the thread-pool continuation that records the error.
    private static async Task WaitForErrors(TuiErrorService errorService, int count, int timeoutMs = 30_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (errorService.RecentErrors.Count < count && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(25);
        }

        if (errorService.RecentErrors.Count < count)
        {
            Assert.Fail($"Timed out after {timeoutMs}ms waiting for {count} error(s), but only found {errorService.RecentErrors.Count}.");
        }
    }
}
