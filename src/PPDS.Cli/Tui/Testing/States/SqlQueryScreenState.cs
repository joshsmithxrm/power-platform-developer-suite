namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the SqlQueryScreen for testing.
/// </summary>
/// <param name="QueryText">The current text in the query input area.</param>
/// <param name="IsExecuting">Whether a query is currently executing.</param>
/// <param name="StatusText">The status label text (e.g., "Ready", "Executing...").</param>
/// <param name="ResultCount">Total number of result rows (null if no results).</param>
/// <param name="CurrentPage">Current page number (1-based, null if no pagination).</param>
/// <param name="TotalPages">Total number of pages (null if no pagination).</param>
/// <param name="PageSize">Number of rows per page.</param>
/// <param name="ColumnHeaders">List of column header names in the results table.</param>
/// <param name="VisibleRowCount">Number of rows currently visible in the table.</param>
/// <param name="FilterText">Current filter text (empty if no filter).</param>
/// <param name="FilterVisible">Whether the filter input is visible.</param>
/// <param name="CanExport">Whether export is available (results exist).</param>
/// <param name="ErrorMessage">Error message if query failed (null if no error).</param>
public sealed record SqlQueryScreenState(
    string QueryText,
    bool IsExecuting,
    string StatusText,
    int? ResultCount,
    int? CurrentPage,
    int? TotalPages,
    int PageSize,
    IReadOnlyList<string> ColumnHeaders,
    int VisibleRowCount,
    string FilterText,
    bool FilterVisible,
    bool CanExport,
    string? ErrorMessage);
