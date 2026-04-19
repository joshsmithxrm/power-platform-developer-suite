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
/// <param name="HasReportIssueButton">Whether the report-issue button is available.</param>
/// <param name="ReportIssueUrl">The URL the report-issue button opens.</param>
/// <param name="FooterText">Non-interactive footer showing the issues URL.</param>
public sealed record ErrorDetailsDialogState(
    string Title,
    IReadOnlyList<ErrorListItem> Errors,
    int SelectedIndex,
    string? SelectedErrorDetails,
    int ErrorCount,
    bool HasClearButton,
    bool HasReportIssueButton = false,
    string? ReportIssueUrl = null,
    string? FooterText = null);
