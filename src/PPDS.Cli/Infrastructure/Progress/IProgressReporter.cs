namespace PPDS.Cli.Infrastructure.Progress;

/// <summary>
/// UI-agnostic interface for reporting operation progress.
/// </summary>
/// <remarks>
/// Services accept this interface for operations expected to take more than ~1 second.
/// Each UI (CLI, TUI, RPC) provides its own adapter implementation.
/// See ADR-0025 for architectural context.
/// </remarks>
public interface IProgressReporter
{
    /// <summary>
    /// Reports progress snapshot with current/total counts.
    /// </summary>
    /// <param name="snapshot">Progress data.</param>
    void ReportProgress(ProgressSnapshot snapshot);

    /// <summary>
    /// Reports phase change (e.g., "Exporting", "Importing").
    /// </summary>
    /// <param name="phase">Phase name.</param>
    /// <param name="detail">Optional detail text.</param>
    void ReportPhase(string phase, string? detail = null);

    /// <summary>
    /// Reports non-fatal warning during operation.
    /// </summary>
    /// <param name="message">Warning message.</param>
    void ReportWarning(string message);

    /// <summary>
    /// Reports informational message.
    /// </summary>
    /// <param name="message">Info message.</param>
    void ReportInfo(string message);
}

/// <summary>
/// Progress data snapshot.
/// </summary>
public sealed record ProgressSnapshot
{
    /// <summary>
    /// Current item index (0-based).
    /// </summary>
    public required int CurrentItem { get; init; }

    /// <summary>
    /// Total number of items.
    /// </summary>
    public required int TotalItems { get; init; }

    /// <summary>
    /// Current entity being processed.
    /// </summary>
    public string? CurrentEntity { get; init; }

    /// <summary>
    /// Processing rate in records per second.
    /// </summary>
    public double? RecordsPerSecond { get; init; }

    /// <summary>
    /// Estimated time remaining.
    /// </summary>
    public TimeSpan? EstimatedRemaining { get; init; }

    /// <summary>
    /// Human-readable status message.
    /// </summary>
    public string? StatusMessage { get; init; }
}

/// <summary>
/// Null implementation for operations that don't need progress reporting.
/// </summary>
public sealed class NullProgressReporter : IProgressReporter
{
    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly IProgressReporter Instance = new NullProgressReporter();

    private NullProgressReporter() { }

    /// <inheritdoc />
    public void ReportProgress(ProgressSnapshot snapshot) { }

    /// <inheritdoc />
    public void ReportPhase(string phase, string? detail = null) { }

    /// <inheritdoc />
    public void ReportWarning(string message) { }

    /// <inheritdoc />
    public void ReportInfo(string message) { }
}
