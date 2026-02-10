using System;

namespace PPDS.Query.Provider;

/// <summary>
/// Event arguments for DML confirmation events (PreInsert, PreUpdate, PreDelete).
/// Allows subscribers to inspect the operation and optionally cancel it.
/// </summary>
public sealed class DmlConfirmationEventArgs : EventArgs
{
    /// <summary>
    /// The SQL command text that triggered the DML operation.
    /// </summary>
    public string CommandText { get; }

    /// <summary>
    /// The target entity logical name for the DML operation.
    /// </summary>
    public string EntityLogicalName { get; }

    /// <summary>
    /// The estimated number of rows that will be affected.
    /// May be -1 if the count is unknown.
    /// </summary>
    public int EstimatedRowCount { get; }

    /// <summary>
    /// Set to true to cancel the DML operation. Default is false.
    /// </summary>
    public bool Cancel { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DmlConfirmationEventArgs"/> class.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="entityLogicalName">The target entity logical name.</param>
    /// <param name="estimatedRowCount">The estimated affected row count.</param>
    public DmlConfirmationEventArgs(string commandText, string entityLogicalName, int estimatedRowCount)
    {
        CommandText = commandText ?? throw new ArgumentNullException(nameof(commandText));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        EstimatedRowCount = estimatedRowCount;
    }
}
