using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

/// <summary>
/// Unit tests for <see cref="TuiErrorService"/>.
/// </summary>
/// <remarks>
/// These tests verify error handling logic including storage, limits, and events.
/// Tests can run without Terminal.Gui context since error handling is pure logic.
/// </remarks>
[Trait("Category", "TuiUnit")]
public class TuiErrorServiceTests
{
    #region ReportError Tests

    [Fact]
    public void ReportError_WithMessage_AddsToRecentErrors()
    {
        var service = new TuiErrorService();

        service.ReportError("Test error message");

        Assert.Single(service.RecentErrors);
        Assert.Equal("Test error message", service.RecentErrors[0].Message);
    }

    [Fact]
    public void ReportError_WithException_CapturesExceptionDetails()
    {
        var service = new TuiErrorService();

        // Throw and catch to get a real stack trace
        Exception? ex = null;
        try
        {
            throw new InvalidOperationException("Inner exception message");
        }
        catch (InvalidOperationException e)
        {
            ex = e;
        }

        service.ReportError("User message", ex!, "TestContext");

        Assert.Single(service.RecentErrors);
        var error = service.RecentErrors[0];
        Assert.Equal("User message", error.Message);
        Assert.Equal("TestContext", error.Context);
        Assert.Equal("InvalidOperationException", error.ExceptionType);
        Assert.Equal("Inner exception message", error.ExceptionMessage);
        Assert.NotNull(error.StackTrace);
    }

    [Fact]
    public void ReportError_WithAggregateException_UnwrapsInnerException()
    {
        var service = new TuiErrorService();
        var inner = new ArgumentException("Inner message");
        var aggregate = new AggregateException(inner);

        service.ReportError("User message", aggregate);

        Assert.Single(service.RecentErrors);
        var error = service.RecentErrors[0];
        Assert.Equal("ArgumentException", error.ExceptionType);
    }

    [Fact]
    public void ReportError_WithContext_IncludesContext()
    {
        var service = new TuiErrorService();

        service.ReportError("Error", context: "LoadProfile");

        Assert.Equal("LoadProfile", service.RecentErrors[0].Context);
    }

    [Fact]
    public void ReportError_MultipleErrors_OrdersNewestFirst()
    {
        var service = new TuiErrorService();

        service.ReportError("First error");
        service.ReportError("Second error");
        service.ReportError("Third error");

        Assert.Equal(3, service.RecentErrors.Count);
        Assert.Equal("Third error", service.RecentErrors[0].Message);
        Assert.Equal("Second error", service.RecentErrors[1].Message);
        Assert.Equal("First error", service.RecentErrors[2].Message);
    }

    #endregion

    #region RecentErrors Limit Tests

    [Fact]
    public void RecentErrors_RespectsMaxLimit()
    {
        var service = new TuiErrorService(maxErrorCount: 3);

        service.ReportError("Error 1");
        service.ReportError("Error 2");
        service.ReportError("Error 3");
        service.ReportError("Error 4");
        service.ReportError("Error 5");

        Assert.Equal(3, service.RecentErrors.Count);
        // Oldest errors should be removed
        Assert.Equal("Error 5", service.RecentErrors[0].Message);
        Assert.Equal("Error 4", service.RecentErrors[1].Message);
        Assert.Equal("Error 3", service.RecentErrors[2].Message);
    }

    [Fact]
    public void RecentErrors_DefaultMaxIs20()
    {
        var service = new TuiErrorService();

        for (int i = 1; i <= 25; i++)
        {
            service.ReportError($"Error {i}");
        }

        Assert.Equal(20, service.RecentErrors.Count);
    }

    #endregion

    #region LatestError Tests

    [Fact]
    public void LatestError_ReturnsNull_WhenNoErrors()
    {
        var service = new TuiErrorService();

        Assert.Null(service.LatestError);
    }

    [Fact]
    public void LatestError_ReturnsMostRecentError()
    {
        var service = new TuiErrorService();

        service.ReportError("First");
        service.ReportError("Second");
        service.ReportError("Third");

        Assert.NotNull(service.LatestError);
        Assert.Equal("Third", service.LatestError.Message);
    }

    #endregion

    #region ClearErrors Tests

    [Fact]
    public void ClearErrors_RemovesAllErrors()
    {
        var service = new TuiErrorService();
        service.ReportError("Error 1");
        service.ReportError("Error 2");

        service.ClearErrors();

        Assert.Empty(service.RecentErrors);
        Assert.Null(service.LatestError);
    }

    #endregion

    #region ErrorOccurred Event Tests

    [Fact]
    public void ErrorOccurred_FiresWhenErrorReported()
    {
        var service = new TuiErrorService();
        TuiError? receivedError = null;
        service.ErrorOccurred += error => receivedError = error;

        service.ReportError("Test error");

        Assert.NotNull(receivedError);
        Assert.Equal("Test error", receivedError.Message);
    }

    [Fact]
    public void ErrorOccurred_FiresForEachError()
    {
        var service = new TuiErrorService();
        var errorCount = 0;
        service.ErrorOccurred += _ => errorCount++;

        service.ReportError("Error 1");
        service.ReportError("Error 2");
        service.ReportError("Error 3");

        Assert.Equal(3, errorCount);
    }

    #endregion

    #region GetLogFilePath Tests

    [Fact]
    public void GetLogFilePath_ReturnsExpectedPath()
    {
        var service = new TuiErrorService();

        var path = service.GetLogFilePath();

        Assert.NotNull(path);
        Assert.EndsWith("tui-debug.log", path);
        Assert.Contains(".ppds", path);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ReportError_ThreadSafe_UnderConcurrentAccess()
    {
        var service = new TuiErrorService(maxErrorCount: 100);
        var errors = new List<Exception>();
        var tasks = new List<Task>();

        // Spawn multiple tasks to report errors concurrently
        for (int i = 0; i < 10; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int j = 0; j < 10; j++)
                    {
                        service.ReportError($"Error from task {index}-{j}");
                    }
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Empty(errors);
        Assert.Equal(100, service.RecentErrors.Count);
    }

    #endregion
}

/// <summary>
/// Unit tests for <see cref="TuiError"/>.
/// </summary>
[Trait("Category", "TuiUnit")]
public class TuiErrorTests
{
    [Fact]
    public void FromException_CreatesErrorWithDetails()
    {
        var ex = new InvalidOperationException("Test exception");

        var error = TuiError.FromException("User message", ex, "TestContext");

        Assert.Equal("User message", error.Message);
        Assert.Equal("TestContext", error.Context);
        Assert.Equal("InvalidOperationException", error.ExceptionType);
        Assert.Equal("Test exception", error.ExceptionMessage);
        Assert.True(error.Timestamp > DateTimeOffset.MinValue);
    }

    [Fact]
    public void FromMessage_CreatesErrorWithoutException()
    {
        var error = TuiError.FromMessage("Simple error", "Context");

        Assert.Equal("Simple error", error.Message);
        Assert.Equal("Context", error.Context);
        Assert.Null(error.ExceptionType);
        Assert.Null(error.StackTrace);
    }

    [Fact]
    public void BriefSummary_TruncatesLongMessages()
    {
        var longMessage = new string('x', 100);
        var error = TuiError.FromMessage(longMessage);

        Assert.True(error.BriefSummary.Length <= 60);
        Assert.EndsWith("...", error.BriefSummary);
    }

    [Fact]
    public void BriefSummary_KeepsShortMessages()
    {
        var error = TuiError.FromMessage("Short message");

        Assert.Equal("Short message", error.BriefSummary);
    }

    [Fact]
    public void GetFullDetails_IncludesAllFields()
    {
        var ex = new InvalidOperationException("Test");
        var error = TuiError.FromException("User message", ex, "Context");

        var details = error.GetFullDetails();

        Assert.Contains("Timestamp:", details);
        Assert.Contains("User message", details);
        Assert.Contains("Context", details);
        Assert.Contains("InvalidOperationException", details);
    }
}
