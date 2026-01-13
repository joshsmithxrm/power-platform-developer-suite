using System.Data;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for exporting query results to various formats.
/// </summary>
internal sealed class ExportDialog : Dialog
{
    private readonly IExportService _exportService;
    private readonly DataTable _dataTable;
    private readonly RadioGroup _formatGroup;
    private readonly CheckBox _includeHeadersCheck;
    private readonly Label _statusLabel;

    private bool _exportCompleted;

    /// <summary>
    /// Gets whether export was completed successfully.
    /// </summary>
    public bool ExportCompleted => _exportCompleted;

    /// <summary>
    /// Creates a new export dialog.
    /// </summary>
    public ExportDialog(IExportService exportService, DataTable dataTable) : base("Export Results")
    {
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _dataTable = dataTable ?? throw new ArgumentNullException(nameof(dataTable));

        Width = 50;
        Height = 14;
        ColorScheme = TuiColorPalette.Default;

        // Format selection
        var formatLabel = new Label("Format:")
        {
            X = 1,
            Y = 1
        };

        _formatGroup = new RadioGroup(new NStack.ustring[] { "CSV (Comma-separated)", "TSV (Tab-separated)", "Clipboard" })
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
            Y = 6,
            Checked = true
        };

        // Status
        _statusLabel = new Label
        {
            X = 1,
            Y = 8,
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

        // Escape closes dialog
        KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == Key.Esc)
            {
                Application.RequestStop();
                e.Handled = true;
            }
        };
    }

    private void OnExportClicked()
    {
        var format = _formatGroup.SelectedItem;
        var includeHeaders = _includeHeadersCheck.Checked;

        if (format == 2) // Clipboard
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
        var extension = format == 0 ? "csv" : "tsv";
        var filter = format == 0 ? "CSV files (*.csv)" : "TSV files (*.tsv)";

        string filePath;
        using (var saveDialog = new SaveDialog("Export to File", filter))
        {
            saveDialog.AllowedFileTypes = new[] { $".{extension}" };
            saveDialog.ColorScheme = TuiColorPalette.Default;
            Application.Run(saveDialog);

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

        if (format == 0)
        {
            await _exportService.ExportCsvAsync(_dataTable, stream, options);
        }
        else
        {
            await _exportService.ExportTsvAsync(_dataTable, stream, options);
        }
    }
}
