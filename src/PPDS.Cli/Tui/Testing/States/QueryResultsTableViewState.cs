namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the QueryResultsTableView for testing.
/// </summary>
/// <param name="ColumnHeaders">List of column header names.</param>
/// <param name="RowCount">Total number of rows in the data source.</param>
/// <param name="VisibleRowCount">Number of rows currently visible.</param>
/// <param name="SelectedRowIndex">Currently selected row index (-1 if none).</param>
/// <param name="CurrentPage">Current page (1-based).</param>
/// <param name="TotalPages">Total number of pages.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="HasData">Whether the table has any data.</param>
public sealed record QueryResultsTableViewState(
    IReadOnlyList<string> ColumnHeaders,
    int RowCount,
    int VisibleRowCount,
    int SelectedRowIndex,
    int CurrentPage,
    int TotalPages,
    int PageSize,
    bool HasData);
