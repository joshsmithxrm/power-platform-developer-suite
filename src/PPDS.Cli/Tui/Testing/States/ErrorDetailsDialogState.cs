namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Information about an error in the error list.
/// </summary>
/// <param name="Message">The error message.</param>
/// <param name="Context">The context where the error occurred.</param>
/// <param name="Timestamp">When the error occurred.</param>
/// <param name="HasException">Whether the error has an associated exception.</param>
public sealed record ErrorListItem(
    string Message,
    string? Context,
    DateTimeOffset Timestamp,
    bool HasException);

/// <summary>
/// Captures the state of the ErrorDetailsDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="Errors">List of errors.</param>
/// <param name="SelectedIndex">Currently selected index (-1 if none).</param>
/// <param name="SelectedErrorDetails">Full details of selected error (null if none).</param>
/// <param name="ErrorCount">Total number of errors.</param>
/// <param name="HasClearButton">Whether the clear button is available.</param>
public sealed record ErrorDetailsDialogState(
    string Title,
    IReadOnlyList<ErrorListItem> Errors,
    int SelectedIndex,
    string? SelectedErrorDetails,
    int ErrorCount,
    bool HasClearButton);
