using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Console formatter that displays elapsed time from process start instead of wall clock time.
/// Format: [+HH:mm:ss.fff] level: Category[EventId] Message
/// </summary>
public sealed class ElapsedTimeConsoleFormatter : ConsoleFormatter
{
    private static readonly Stopwatch Stopwatch = Stopwatch.StartNew();

    public const string FormatterName = "elapsed";

    public ElapsedTimeConsoleFormatter() : base(FormatterName)
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null)
            return;

        var elapsed = Stopwatch.Elapsed;
        var timestamp = $"[+{elapsed:hh\\:mm\\:ss\\.fff}]";

        var logLevel = GetLogLevelString(logEntry.LogLevel);
        var category = logEntry.Category;
        var eventId = logEntry.EventId.Id;

        textWriter.Write(timestamp);
        textWriter.Write(' ');
        textWriter.Write(logLevel);
        textWriter.Write(": ");
        textWriter.Write(category);
        textWriter.Write('[');
        textWriter.Write(eventId);
        textWriter.Write(']');
        textWriter.Write(' ');
        textWriter.WriteLine(message);

        if (logEntry.Exception != null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }

    private static string GetLogLevelString(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => logLevel.ToString().ToLowerInvariant()
    };
}
