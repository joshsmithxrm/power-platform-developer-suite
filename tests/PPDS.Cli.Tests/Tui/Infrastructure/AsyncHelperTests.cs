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

        // Allow ContinueWith to execute
        await Task.Delay(100);

        Assert.Single(errorService.RecentErrors);
        Assert.Contains("TestContext", errorService.RecentErrors[0].Context);
    }

    [Fact]
    public async Task FireAndForget_DoesNotReportError_OnSuccessfulTask()
    {
        var errorService = new TuiErrorService();

        errorService.FireAndForget(Task.CompletedTask, "TestContext");

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

        await Task.Delay(100);

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
}
