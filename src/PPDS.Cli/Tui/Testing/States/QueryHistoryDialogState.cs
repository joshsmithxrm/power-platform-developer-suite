namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Information about a query in the history list.
/// </summary>
/// <param name="QueryText">The query text (may be truncated for display).</param>
/// <param name="ExecutedAt">When the query was executed.</param>
/// <param name="RowCount">Number of rows returned (null if unknown).</param>
public sealed record QueryHistoryItem(
    string QueryText,
    DateTimeOffset ExecutedAt,
    int? RowCount);

/// <summary>
/// Captures the state of the QueryHistoryDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="HistoryItems">List of history items.</param>
/// <param name="SelectedIndex">Currently selected index (-1 if none).</param>
/// <param name="SelectedQueryText">Full query text of selected item (null if none).</param>
/// <param name="IsEmpty">Whether the history is empty.</param>
public sealed record QueryHistoryDialogState(
    string Title,
    IReadOnlyList<QueryHistoryItem> HistoryItems,
    int SelectedIndex,
    string? SelectedQueryText,
    bool IsEmpty);
