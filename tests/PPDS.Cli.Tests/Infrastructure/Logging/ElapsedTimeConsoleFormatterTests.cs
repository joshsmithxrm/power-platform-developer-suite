using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Logging;

public class ElapsedTimeConsoleFormatterTests
{
    [Fact]
    public void FormatterName_IsElapsed()
    {
        Assert.Equal("elapsed", ElapsedTimeConsoleFormatter.FormatterName);
    }

    [Fact]
    public void Write_IncludesElapsedTimePrefix()
    {
        // Arrange
        OperationClock.Start();
        var formatter = new ElapsedTimeConsoleFormatter();
        using var writer = new StringWriter();

        var logEntry = new LogEntry<string>(
            LogLevel.Information,
            "TestCategory",
            new EventId(0),
            "Test message",
            exception: null,
            formatter: (state, _) => state);

        // Act
        formatter.Write(logEntry, null, writer);
        var output = writer.ToString();

        // Assert
        Assert.StartsWith("[+", output);
        Assert.Contains("] info: TestCategory[0] Test message", output);
    }

    [Fact]
    public void Write_FormatsLogLevelCorrectly()
    {
        OperationClock.Start();
        var formatter = new ElapsedTimeConsoleFormatter();

        var testCases = new[]
        {
            (LogLevel.Trace, "trce"),
            (LogLevel.Debug, "dbug"),
            (LogLevel.Information, "info"),
            (LogLevel.Warning, "warn"),
            (LogLevel.Error, "fail"),
            (LogLevel.Critical, "crit")
        };

        foreach (var (level, expected) in testCases)
        {
            using var writer = new StringWriter();
            var logEntry = new LogEntry<string>(
                level,
                "Test",
                new EventId(0),
                "Message",
                exception: null,
                formatter: (state, _) => state);

            formatter.Write(logEntry, null, writer);
            var output = writer.ToString();

            Assert.Contains($"] {expected}: Test[0]", output);
        }
    }

    [Fact]
    public void Write_IncludesException_WhenProvided()
    {
        OperationClock.Start();
        var formatter = new ElapsedTimeConsoleFormatter();
        using var writer = new StringWriter();

        var exception = new InvalidOperationException("Test exception");
        var logEntry = new LogEntry<string>(
            LogLevel.Error,
            "TestCategory",
            new EventId(0),
            "Error occurred",
            exception: exception,
            formatter: (state, _) => state);

        formatter.Write(logEntry, null, writer);
        var output = writer.ToString();

        Assert.Contains("Error occurred", output);
        Assert.Contains("InvalidOperationException", output);
        Assert.Contains("Test exception", output);
    }

    [Fact]
    public void Write_DoesNothing_WhenFormatterReturnsNull()
    {
        OperationClock.Start();
        var formatter = new ElapsedTimeConsoleFormatter();
        using var writer = new StringWriter();

        var logEntry = new LogEntry<string>(
            LogLevel.Information,
            "TestCategory",
            new EventId(0),
            "Message",
            exception: null,
            formatter: (_, _) => null!);

        formatter.Write(logEntry, null, writer);
        var output = writer.ToString();

        Assert.Empty(output);
    }

    [Fact]
    public void Write_IncludesEventId()
    {
        OperationClock.Start();
        var formatter = new ElapsedTimeConsoleFormatter();
        using var writer = new StringWriter();

        var logEntry = new LogEntry<string>(
            LogLevel.Information,
            "TestCategory",
            new EventId(42),
            "Test message",
            exception: null,
            formatter: (state, _) => state);

        formatter.Write(logEntry, null, writer);
        var output = writer.ToString();

        Assert.Contains("TestCategory[42]", output);
    }

    [Fact]
    public void Write_UsesOperationClockElapsed()
    {
        // Start clock and wait a bit to ensure non-zero elapsed
        OperationClock.Start();
        Thread.Sleep(100);

        var formatter = new ElapsedTimeConsoleFormatter();
        using var writer = new StringWriter();

        var logEntry = new LogEntry<string>(
            LogLevel.Information,
            "Test",
            new EventId(0),
            "Message",
            exception: null,
            formatter: (state, _) => state);

        formatter.Write(logEntry, null, writer);
        var output = writer.ToString();

        // Should have a timestamp that's not [+00:00:00.000]
        // The pattern should be [+HH:mm:ss.fff]
        Assert.Matches(@"\[\+\d{2}:\d{2}:\d{2}\.\d{3}\]", output);
    }
}
