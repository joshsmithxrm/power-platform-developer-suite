using System.Data;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Dataverse.Query;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for exporting query results to various formats.
/// </summary>
internal sealed class ExportDialog : TuiDialog, ITuiStateCapture<ExportDialogState>
{
    private static readonly IReadOnlyList<string> FormatNames = new[] { "CSV", "TSV", "JSON", "Clipboard" };
    private readonly IExportService _exportService;
    private readonly DataTable _dataTable;
    private readonly IReadOnlyDictionary<string, QueryColumnType>? _columnTypes;
    private readonly RadioGroup _formatGroup;
    private readonly CheckBox _includeHeadersCheck;
    private readonly Label _statusLabel;

    private bool _exportCompleted;

    // Format indices
    private const int FormatCsv = 0;
    private const int FormatTsv = 1;
    private const int FormatJson = 2;
    private const int FormatClipboard = 3;

    /// <summary>
    /// Gets whether export was completed successfully.
    /// </summary>
    public bool ExportCompleted => _exportCompleted;

    /// <summary>
    /// Creates a new export dialog.
    /// </summary>
    /// <param name="exportService">The export service.</param>
    /// <param name="dataTable">The data to export.</param>
    /// <param name="columnTypes">Optional column type metadata for JSON type preservation.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public ExportDialog(
        IExportService exportService,
        DataTable dataTable,
        IReadOnlyDictionary<string, QueryColumnType>? columnTypes = null,
        InteractiveSession? session = null) : base("Export Results", session)
    {
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _dataTable = dataTable ?? throw new ArgumentNullException(nameof(dataTable));
        _columnTypes = columnTypes;

        Width = 50;
        Height = 15;

        // Format selection
        var formatLabel = new Label("Format:")
        {
            X = 1,
            Y = 1
        };

        _formatGroup = new RadioGroup(new NStack.ustring[]
        {
            "CSV (Comma-separated)",
            "TSV (Tab-separated)",
            "JSON (with types)",
            "Clipboard"
        })
        {
            X = 1,
            Y = 2
        };
        _formatGroup.SelectedItem = 0;

        // Enter on RadioGroup selects the item (Terminal.Gui only binds Space by default)
        _formatGroup.KeyPress += (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter)
            {
                // Simulate Space key to trigger selection
                _formatGroup.ProcessKey(new KeyEvent(Key.Space, new KeyModifiers()));
                args.Handled = true;
            }
        };

        // Options
        _includeHeadersCheck = new CheckBox("Include column headers")
        {
            X = 1,
            Y = 7, // Moved down for extra format option
            Checked = true
        };

        // Status
        _statusLabel = new Label
        {
            X = 1,
            Y = 9, // Moved down for extra format option
            Width = Dim.Fill() - 2,
            Height = 1,
            Text = $"{_dataTable.Rows.Count} rows to export"
        };

        // Buttons
        var exportButton = new Button("E_xport")
        {
            X = Pos.Center() - 12,
            Y = Pos.AnchorEnd(1)
        };
        exportButton.Clicked += OnExportClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => { Application.RequestStop(); };

        Add(formatLabel, _formatGroup, _includeHeadersCheck, _statusLabel, exportButton, cancelButton);
    }

    private void OnExportClicked()
    {
        var format = _formatGroup.SelectedItem;
        var includeHeaders = _includeHeadersCheck.Checked;

        if (format == FormatClipboard)
        {
            ExportToClipboard(includeHeaders);
        }
        else
        {
            ExportToFile(format, includeHeaders);
        }
    }

    private void ExportToClipboard(bool includeHeaders)
    {
        try
        {
            var text = _exportService.FormatForClipboard(_dataTable, includeHeaders: includeHeaders);

            if (ClipboardHelper.CopyToClipboard(text))
            {
                _statusLabel.Text = $"Copied {_dataTable.Rows.Count} rows to clipboard";
                _exportCompleted = true;
                MessageBox.Query("Export", "Data copied to clipboard.", "OK");
                Application.RequestStop();
            }
            else
            {
                _statusLabel.Text = "Failed to copy to clipboard";
            }
        }
        catch (PpdsException ex)
        {
            _statusLabel.Text = $"Error: {ex.UserMessage}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void ExportToFile(int format, bool includeHeaders)
    {
        var (extension, filter) = format switch
        {
            FormatCsv => ("csv", "CSV files (*.csv)"),
            FormatTsv => ("tsv", "TSV files (*.tsv)"),
            FormatJson => ("json", "JSON files (*.json)"),
            _ => ("csv", "CSV files (*.csv)")
        };

        string filePath;
        using (var saveDialog = new SaveDialog("Export to File", filter))
        {
            saveDialog.AllowedFileTypes = new[] { $".{extension}" };

            // Apply colors to dialog views
            ApplyColorSchemeRecursive(saveDialog, TuiColorPalette.FileDialog);

            // ComboBox dropdowns are separate popup windows that use Colors.Menu globally.
            // Save and restore to avoid affecting other parts of the app.
            var originalMenu = Colors.Menu;
            Colors.Menu = TuiColorPalette.FileDialog;

            saveDialog.Loaded += () =>
            {
                ApplyColorSchemeRecursive(saveDialog, TuiColorPalette.FileDialog);

                // Focus the filename field for immediate typing
                var textField = FindFirstTextField(saveDialog);
                textField?.SetFocus();
            };

            try
            {
                Application.Run(saveDialog);
            }
            finally
            {
                Colors.Menu = originalMenu;
            }

            if (saveDialog.Canceled || saveDialog.FilePath == null)
            {
                return;
            }

            filePath = saveDialog.FilePath.ToString() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        // Ensure correct extension
        if (!filePath.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase))
        {
            filePath += $".{extension}";
        }

        _statusLabel.Text = "Exporting...";
        Application.Refresh();

#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
        _ = ExportToFileAsync(filePath, format, includeHeaders).ContinueWith(t =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                if (t.IsFaulted)
                {
                    _statusLabel.Text = $"Error: {t.Exception?.InnerException?.Message ?? "Export failed"}";
                }
                else
                {
                    _exportCompleted = true;
                    MessageBox.Query("Export", $"Exported to:\n{filePath}", "OK");
                    Application.RequestStop();
                }
            });
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }

    private async Task ExportToFileAsync(string filePath, int format, bool includeHeaders)
    {
        var options = new ExportOptions { IncludeHeaders = includeHeaders };

        await using var stream = File.Create(filePath);

        switch (format)
        {
            case FormatCsv:
                await _exportService.ExportCsvAsync(_dataTable, stream, options);
                break;
            case FormatTsv:
                await _exportService.ExportTsvAsync(_dataTable, stream, options);
                break;
            case FormatJson:
                await _exportService.ExportJsonAsync(_dataTable, stream, _columnTypes, options);
                break;
        }
    }

    /// <summary>
    /// Recursively applies a color scheme to a view and all its subviews.
    /// </summary>
    private static void ApplyColorSchemeRecursive(View view, ColorScheme scheme)
    {
        view.ColorScheme = scheme;
        foreach (var subview in view.Subviews)
        {
            ApplyColorSchemeRecursive(subview, scheme);
        }
    }

    /// <summary>
    /// Finds the first TextField in a view hierarchy (for focusing the filename field).
    /// </summary>
    private static TextField? FindFirstTextField(View view)
    {
        if (view is TextField textField)
            return textField;

        foreach (var subview in view.Subviews)
        {
            var found = FindFirstTextField(subview);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <inheritdoc />
    public ExportDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        AvailableFormats: FormatNames,
        SelectedFormat: FormatNames[_formatGroup.SelectedItem],
        FilePath: string.Empty,
        RowCount: _dataTable.Rows.Count,
        IncludeHeaders: _includeHeadersCheck.Checked,
        IsExporting: false,
        ErrorMessage: null);
}
