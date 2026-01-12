namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the ExportDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="AvailableFormats">List of available export formats.</param>
/// <param name="SelectedFormat">Currently selected format.</param>
/// <param name="FilePath">The export file path.</param>
/// <param name="RowCount">Number of rows to export.</param>
/// <param name="IncludeHeaders">Whether to include headers in export.</param>
/// <param name="IsExporting">Whether export is in progress.</param>
/// <param name="ErrorMessage">Error message if export failed (null if no error).</param>
public sealed record ExportDialogState(
    string Title,
    IReadOnlyList<string> AvailableFormats,
    string SelectedFormat,
    string FilePath,
    int RowCount,
    bool IncludeHeaders,
    bool IsExporting,
    string? ErrorMessage);
