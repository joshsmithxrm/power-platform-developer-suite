namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Implementation of <see cref="ITuiErrorService"/> for centralized error handling.
/// </summary>
/// <remarks>
/// Thread-safe implementation that stores recent errors in memory and logs to TuiDebugLog.
/// </remarks>
internal sealed class TuiErrorService : ITuiErrorService
{
    private readonly List<TuiError> _errors = new();
    private readonly object _lock = new();
    private readonly int _maxErrorCount;

    /// <summary>
    /// Creates a new TuiErrorService with the specified maximum error count.
    /// </summary>
    /// <param name="maxErrorCount">Maximum number of errors to retain (default 20).</param>
    public TuiErrorService(int maxErrorCount = 20)
    {
        _maxErrorCount = maxErrorCount;
    }

    /// <inheritdoc />
    public event Action<TuiError>? ErrorOccurred;

    /// <inheritdoc />
    public void ReportError(string message, Exception? ex = null, string? context = null)
    {
        var error = ex != null
            ? TuiError.FromException(message, ex, context)
            : TuiError.FromMessage(message, context);

        // Log to TuiDebugLog
        var logMessage = ex != null
            ? $"ERROR [{context ?? "Unknown"}]: {message} - {ex.GetType().Name}: {ex.Message}"
            : $"ERROR [{context ?? "Unknown"}]: {message}";
        TuiDebugLog.Log(logMessage);

        lock (_lock)
        {
            // Add to front of list (newest first)
            _errors.Insert(0, error);

            // Trim to max count
            while (_errors.Count > _maxErrorCount)
            {
                _errors.RemoveAt(_errors.Count - 1);
            }
        }

        // Fire event outside lock to avoid deadlocks
        ErrorOccurred?.Invoke(error);
    }

    /// <inheritdoc />
    public IReadOnlyList<TuiError> RecentErrors
    {
        get
        {
            lock (_lock)
            {
                // Return a copy to avoid concurrent modification issues
                return _errors.ToList().AsReadOnly();
            }
        }
    }

    /// <inheritdoc />
    public TuiError? LatestError
    {
        get
        {
            lock (_lock)
            {
                return _errors.Count > 0 ? _errors[0] : null;
            }
        }
    }

    /// <inheritdoc />
    public void ClearErrors()
    {
        lock (_lock)
        {
            _errors.Clear();
        }
    }

    /// <inheritdoc />
    public string GetLogFilePath() => TuiDebugLog.LogPath;
}
