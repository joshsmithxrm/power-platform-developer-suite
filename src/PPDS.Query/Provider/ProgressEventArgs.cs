using System;

namespace PPDS.Query.Provider;

/// <summary>
/// Event arguments for progress reporting during query execution.
/// </summary>
public sealed class ProgressEventArgs : EventArgs
{
    /// <summary>
    /// A human-readable message describing the current progress.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// The number of rows processed so far.
    /// </summary>
    public long RowsProcessed { get; }

    /// <summary>
    /// The total number of rows expected, or -1 if unknown.
    /// </summary>
    public long TotalRows { get; }

    /// <summary>
    /// The percentage of completion (0-100), or -1 if unknown.
    /// </summary>
    public int PercentComplete { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressEventArgs"/> class.
    /// </summary>
    /// <param name="message">The progress message.</param>
    /// <param name="rowsProcessed">The number of rows processed.</param>
    /// <param name="totalRows">The total rows expected, or -1 if unknown.</param>
    public ProgressEventArgs(string message, long rowsProcessed, long totalRows = -1)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        RowsProcessed = rowsProcessed;
        TotalRows = totalRows;
        PercentComplete = totalRows > 0 ? (int)(rowsProcessed * 100 / totalRows) : -1;
    }
}
