namespace PPDS.Cli.Infrastructure;

/// <summary>
/// UI-agnostic interface for reporting progress of operations.
/// </summary>
/// <remarks>
/// <para>
/// This interface enables Application Services to report progress without coupling
/// to any specific UI framework (CLI, TUI, RPC). Each UI implements its own adapter.
/// See ADR-0025 for architectural context.
/// </para>
/// <para>
/// Note: This is different from <see cref="PPDS.Migration.Progress.IProgressReporter"/>
/// which is specifically designed for migration operations with MigrationResult.
/// This interface is simpler and designed for general-purpose progress reporting
/// in services like export, query, etc.
/// </para>
/// <para>
/// Usage pattern:
/// <code>
/// async Task ExportAsync(Stream stream, IOperationProgress? progress, CancellationToken ct)
/// {
///     progress?.ReportStatus("Starting export...");
///     for (int i = 0; i &lt; totalRows; i++)
///     {
///         // ... do work ...
///         progress?.ReportProgress(i + 1, totalRows, $"Row {i + 1} of {totalRows}");
///     }
///     progress?.ReportComplete($"Exported {totalRows} rows.");
/// }
/// </code>
/// </para>
/// </remarks>
public interface IOperationProgress
{
    /// <summary>
    /// Reports a status message (no percentage).
    /// Use for indeterminate operations or phase changes.
    /// </summary>
    /// <param name="message">Status message to display.</param>
    void ReportStatus(string message);

    /// <summary>
    /// Reports progress with current/total counts.
    /// </summary>
    /// <param name="current">Current item number (1-based).</param>
    /// <param name="total">Total number of items.</param>
    /// <param name="message">Optional message describing current item.</param>
    void ReportProgress(int current, int total, string? message = null);

    /// <summary>
    /// Reports progress as a percentage (0.0 to 1.0).
    /// </summary>
    /// <param name="fraction">Progress fraction (0.0 to 1.0).</param>
    /// <param name="message">Optional message describing current state.</param>
    void ReportProgress(double fraction, string? message = null);

    /// <summary>
    /// Reports successful completion.
    /// </summary>
    /// <param name="message">Completion message.</param>
    void ReportComplete(string message);

    /// <summary>
    /// Reports an error.
    /// </summary>
    /// <param name="message">Error message.</param>
    void ReportError(string message);
}

/// <summary>
/// A null operation progress reporter that does nothing.
/// Use when caller doesn't need progress updates.
/// </summary>
public sealed class NullOperationProgress : IOperationProgress
{
    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly NullOperationProgress Instance = new();

    private NullOperationProgress() { }

    /// <inheritdoc />
    public void ReportStatus(string message) { }

    /// <inheritdoc />
    public void ReportProgress(int current, int total, string? message = null) { }

    /// <inheritdoc />
    public void ReportProgress(double fraction, string? message = null) { }

    /// <inheritdoc />
    public void ReportComplete(string message) { }

    /// <inheritdoc />
    public void ReportError(string message) { }
}
