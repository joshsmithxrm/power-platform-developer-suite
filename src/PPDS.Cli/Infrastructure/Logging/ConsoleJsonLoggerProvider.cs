using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PPDS.Dataverse.Diagnostics;
using PPDS.Dataverse.Security;

namespace PPDS.Cli.Infrastructure.Logging;

/// <summary>
/// Logger provider that writes structured JSON log entries to stderr.
/// </summary>
public sealed class ConsoleJsonLoggerProvider : ILoggerProvider
{
    private readonly CliLoggerOptions _options;
    private readonly LogContext _context;
    private readonly ConcurrentDictionary<string, ConsoleJsonLogger> _loggers = new();

    /// <summary>
    /// Creates a new ConsoleJsonLoggerProvider.
    /// </summary>
    /// <param name="options">Logger configuration options.</param>
    /// <param name="context">Log context with correlation ID.</param>
    public ConsoleJsonLoggerProvider(CliLoggerOptions options, LogContext context)
    {
        _options = options;
        _context = context;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new ConsoleJsonLogger(name, _options, _context));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loggers.Clear();
    }
}

/// <summary>
/// Logger that writes structured JSON log entries to stderr (one JSON object per line).
/// </summary>
internal sealed class ConsoleJsonLogger : ILogger
{
    private static readonly object ConsoleLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _categoryName;
    private readonly CliLoggerOptions _options;
    private readonly LogContext _context;

    public ConsoleJsonLogger(string categoryName, CliLoggerOptions options, LogContext context)
    {
        _categoryName = categoryName;
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
        var safeMessage = ConnectionStringRedactor.RedactExceptionMessage(message);

        // Prefer the ambient scope so correlation ids propagate across await points and
        // into Dataverse services that don't know about the CLI LogContext. Fall back to
        // the initial command-level id so every log line still carries a correlation id.
        var correlationId = CorrelationIdScope.Current ?? _context.CorrelationId;

        var entry = new JsonLogEntry
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            Level = GetLevelString(logLevel),
            Category = _categoryName,
            Message = safeMessage,
            CorrelationId = correlationId,
            Command = _context.CommandName,
            EventId = eventId.Id != 0 ? eventId.Id : null,
            Exception = exception != null
                ? ConnectionStringRedactor.RedactExceptionMessage(exception.Message)
                : null
        };

        var json = JsonSerializer.Serialize(entry, JsonOptions);

        lock (ConsoleLock)
        {
            Console.Error.WriteLine(json);
        }
    }

    private static string GetLevelString(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "information",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "unknown"
    };

    private sealed class JsonLogEntry
    {
        public string? Timestamp { get; set; }
        public string? Level { get; set; }
        public string? Category { get; set; }
        public string? Message { get; set; }
        public string? CorrelationId { get; set; }
        public string? Command { get; set; }
        public int? EventId { get; set; }
        public string? Exception { get; set; }
    }
}
