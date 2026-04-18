using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PPDS.Dataverse.Diagnostics;

namespace PPDS.Cli.Infrastructure.Logging;

/// <summary>
/// Logger provider that writes human-readable log entries to stderr.
/// </summary>
public sealed class ConsoleTextLoggerProvider : ILoggerProvider
{
    private readonly CliLoggerOptions _options;
    private readonly LogContext _context;
    private readonly ConcurrentDictionary<string, ConsoleTextLogger> _loggers = new();

    /// <summary>
    /// Creates a new ConsoleTextLoggerProvider.
    /// </summary>
    /// <param name="options">Logger configuration options.</param>
    /// <param name="context">Log context with correlation ID.</param>
    public ConsoleTextLoggerProvider(CliLoggerOptions options, LogContext context)
    {
        _options = options;
        _context = context;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new ConsoleTextLogger(name, _options, _context));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loggers.Clear();
    }
}

/// <summary>
/// Logger that writes human-readable log entries to stderr.
/// </summary>
internal sealed class ConsoleTextLogger : ILogger
{
    private static readonly object ConsoleLock = new();

    private readonly string _categoryName;
    private readonly string _shortCategoryName;
    private readonly CliLoggerOptions _options;
    private readonly LogContext _context;

    public ConsoleTextLogger(string categoryName, CliLoggerOptions options, LogContext context)
    {
        _categoryName = categoryName;
        _shortCategoryName = ShortenCategory(categoryName);
        _options = options;
        _context = context;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _options.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var timestamp = DateTime.Now.ToString(_options.TimestampFormat);
        var levelAbbr = GetLevelAbbreviation(logLevel);
        var levelColor = GetLevelColor(logLevel);

        // Prefer the ambient scope so correlation ids propagate across await points and
        // into Dataverse services that don't know about the CLI LogContext.
        var correlationId = CorrelationIdScope.Current ?? _context.CorrelationId;
        // Show a short suffix (first 8 chars) so text output stays scannable while
        // still allowing log-aggregation tools to grep on a prefix match.
        var correlationSuffix = !string.IsNullOrEmpty(correlationId) && correlationId.Length >= 8
            ? correlationId.Substring(0, 8)
            : correlationId ?? string.Empty;

        lock (ConsoleLock)
        {
            var useColor = _options.EnableColors && !Console.IsErrorRedirected &&
                           string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

            // Write timestamp
            Console.Error.Write($"[{timestamp}] ");

            // Write level with color
            if (useColor)
            {
                Console.ForegroundColor = levelColor;
            }
            Console.Error.Write($"[{levelAbbr}]");
            if (useColor)
            {
                Console.ResetColor();
            }

            // Write category, correlation id (short), and message
            if (!string.IsNullOrEmpty(correlationSuffix))
            {
                Console.Error.WriteLine($" [{_shortCategoryName}] [cid={correlationSuffix}] {message}");
            }
            else
            {
                Console.Error.WriteLine($" [{_shortCategoryName}] {message}");
            }

            // Write exception if present
            if (exception != null)
            {
                if (useColor)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                }
                Console.Error.WriteLine($"  Exception: {exception.Message}");
                if (_options.MinimumLevel <= LogLevel.Debug)
                {
                    Console.Error.WriteLine(exception.StackTrace);
                }
                if (useColor)
                {
                    Console.ResetColor();
                }
            }
        }
    }

    private static string GetLevelAbbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };

    private static ConsoleColor GetLevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace => ConsoleColor.DarkGray,
        LogLevel.Debug => ConsoleColor.Gray,
        LogLevel.Information => ConsoleColor.Cyan,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Critical => ConsoleColor.DarkRed,
        _ => ConsoleColor.White
    };

    /// <summary>
    /// Shortens category names for readability.
    /// PPDS.Cli.Commands.Auth.AuthCommandGroup -> PPDS.Cli.Auth
    /// </summary>
    private static string ShortenCategory(string category)
    {
        var parts = category.Split('.');
        if (parts.Length <= 3)
        {
            return category;
        }

        // Take first two parts and the last part (without common suffixes)
        var lastPart = parts[^1]
            .Replace("CommandGroup", "")
            .Replace("Command", "")
            .Replace("Service", "")
            .Replace("Provider", "");

        if (string.IsNullOrEmpty(lastPart))
        {
            lastPart = parts[^1];
        }

        return $"{parts[0]}.{parts[1]}.{lastPart}";
    }
}
