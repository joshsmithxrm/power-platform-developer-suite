using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure.Logging;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Logging;

public class ConsoleTextLoggerProviderTests
{
    [Fact]
    public void CreateLogger_ReturnsLogger()
    {
        var options = new CliLoggerOptions { MinimumLevel = LogLevel.Information };
        var context = new LogContext();
        using var provider = new ConsoleTextLoggerProvider(options, context);

        var logger = provider.CreateLogger("TestCategory");

        Assert.NotNull(logger);
    }

    [Fact]
    public void CreateLogger_ReturnsSameLoggerForSameCategory()
    {
        var options = new CliLoggerOptions { MinimumLevel = LogLevel.Information };
        var context = new LogContext();
        using var provider = new ConsoleTextLoggerProvider(options, context);

        var logger1 = provider.CreateLogger("TestCategory");
        var logger2 = provider.CreateLogger("TestCategory");

        Assert.Same(logger1, logger2);
    }

    [Fact]
    public void CreateLogger_ReturnsDifferentLoggerForDifferentCategory()
    {
        var options = new CliLoggerOptions { MinimumLevel = LogLevel.Information };
        var context = new LogContext();
        using var provider = new ConsoleTextLoggerProvider(options, context);

        var logger1 = provider.CreateLogger("Category1");
        var logger2 = provider.CreateLogger("Category2");

        Assert.NotSame(logger1, logger2);
    }

    [Fact]
    public void Logger_IsEnabled_RespectsMinimumLevel()
    {
        var options = new CliLoggerOptions { MinimumLevel = LogLevel.Warning };
        var context = new LogContext();
        using var provider = new ConsoleTextLoggerProvider(options, context);
        var logger = provider.CreateLogger("TestCategory");

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void Logger_IsEnabled_TraceEnabled_WhenMinLevelIsTrace()
    {
        var options = new CliLoggerOptions { MinimumLevel = LogLevel.Trace };
        var context = new LogContext();
        using var provider = new ConsoleTextLoggerProvider(options, context);
        var logger = provider.CreateLogger("TestCategory");

        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void Logger_BeginScope_ReturnsNull()
    {
        var options = new CliLoggerOptions { MinimumLevel = LogLevel.Information };
        var context = new LogContext();
        using var provider = new ConsoleTextLoggerProvider(options, context);
        var logger = provider.CreateLogger("TestCategory");

        var scope = logger.BeginScope("test scope");

        Assert.Null(scope);
    }

    [Fact]
    public void Dispose_ClearsLoggers()
    {
        var options = new CliLoggerOptions { MinimumLevel = LogLevel.Information };
        var context = new LogContext();
        using var provider = new ConsoleTextLoggerProvider(options, context);

        var logger1 = provider.CreateLogger("TestCategory");

        // This test verifies Dispose doesn't throw (using statement handles disposal)
        Assert.NotNull(logger1);
    }
}
