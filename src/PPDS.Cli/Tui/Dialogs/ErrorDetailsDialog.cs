using System.Diagnostics;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for displaying error details and recent error history.
/// </summary>
internal sealed class ErrorDetailsDialog : Dialog
{
    private readonly ITuiErrorService _errorService;
    private readonly ListView _errorListView;
    private readonly TextView _detailsTextView;
    private readonly Label _logPathLabel;
    private TuiError? _selectedError;

    /// <summary>
    /// Creates a new error details dialog.
    /// </summary>
    /// <param name="errorService">The error service to get errors from.</param>
    public ErrorDetailsDialog(ITuiErrorService errorService) : base("Error Details")
    {
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));

        Width = 80;
        Height = 24;
        ColorScheme = TuiColorPalette.Default;

        // Recent errors list (left side)
        var errorsFrame = new FrameView("Recent Errors")
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(40),
            Height = Dim.Fill() - 4,
            ColorScheme = TuiColorPalette.Default
        };

        _errorListView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            AllowsMultipleSelection = false
        };
        _errorListView.SelectedItemChanged += OnErrorSelected;
        errorsFrame.Add(_errorListView);

        // Error details (right side)
        var detailsFrame = new FrameView("Details")
        {
            X = Pos.Right(errorsFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 4,
            ColorScheme = TuiColorPalette.Default
        };

        _detailsTextView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true
        };
        detailsFrame.Add(_detailsTextView);

        // Log file path
        var logPath = _errorService.GetLogFilePath();
        _logPathLabel = new Label($"Log: {logPath}")
        {
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill() - 2,
            Height = 1,
            ColorScheme = TuiColorPalette.Default
        };

        // Buttons
        var copyButton = new Button("_Copy Error")
        {
            X = 1,
            Y = Pos.AnchorEnd(1)
        };
        copyButton.Clicked += OnCopyClicked;

        var openLogButton = new Button("Open _Log")
        {
            X = Pos.Right(copyButton) + 2,
            Y = Pos.AnchorEnd(1)
        };
        openLogButton.Clicked += OnOpenLogClicked;

        var clearButton = new Button("C_lear All")
        {
            X = Pos.Right(openLogButton) + 2,
            Y = Pos.AnchorEnd(1)
        };
        clearButton.Clicked += OnClearClicked;

        var closeButton = new Button("_Close")
        {
            X = Pos.AnchorEnd(12),
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(errorsFrame, detailsFrame, _logPathLabel, copyButton, openLogButton, clearButton, closeButton);

        // Handle Escape to close
        KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == Key.Esc)
            {
                Application.RequestStop();
                e.Handled = true;
            }
        };

        // Load errors
        RefreshErrorList();
    }

    private void RefreshErrorList()
    {
        var errors = _errorService.RecentErrors;

        if (errors.Count == 0)
        {
            _errorListView.SetSource(new[] { "(No errors)" });
            _detailsTextView.Text = "No errors have been recorded.\n\nErrors will appear here when they occur.";
            _selectedError = null;
            return;
        }

        // Create display strings for list
        var displayItems = errors.Select(e =>
        {
            var time = e.Timestamp.LocalDateTime.ToString("HH:mm:ss");
            var brief = e.Message.Length > 25 ? e.Message.Substring(0, 22) + "..." : e.Message;
            return $"[{time}] {brief}";
        }).ToList();

        _errorListView.SetSource(displayItems);

        // Select first error if available
        if (errors.Count > 0)
        {
            _errorListView.SelectedItem = 0;
            ShowErrorDetails(errors[0]);
        }
    }

    private void OnErrorSelected(ListViewItemEventArgs args)
    {
        var errors = _errorService.RecentErrors;
        if (args.Item >= 0 && args.Item < errors.Count)
        {
            ShowErrorDetails(errors[args.Item]);
        }
    }

    private void ShowErrorDetails(TuiError error)
    {
        _selectedError = error;
        _detailsTextView.Text = error.GetFullDetails();
    }

    private void OnCopyClicked()
    {
        if (_selectedError == null)
        {
            MessageBox.Query("Copy Error", "No error selected.", "OK");
            return;
        }

        var details = _selectedError.GetFullDetails();

        try
        {
            Clipboard.TrySetClipboardData(details);
            MessageBox.Query("Copied", "Error details copied to clipboard.", "OK");
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Copy Failed",
                $"Failed to copy to clipboard: {ex.Message}\n\n" +
                "The error details are displayed in the Details panel.",
                "OK");
        }
    }

    private void OnOpenLogClicked()
    {
        var logPath = _errorService.GetLogFilePath();

        if (!File.Exists(logPath))
        {
            MessageBox.Query("Log File",
                $"Log file does not exist yet:\n{logPath}\n\n" +
                "The log file is created when the first message is logged.",
                "OK");
            return;
        }

        try
        {
            // Use shell execute to open with default text editor
            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Open Failed",
                $"Failed to open log file: {ex.Message}\n\n" +
                $"Log path: {logPath}",
                "OK");
        }
    }

    private void OnClearClicked()
    {
        var result = MessageBox.Query("Clear Errors",
            "Clear all recorded errors?\n\n" +
            "This does not clear the log file.",
            "Clear", "Cancel");

        if (result == 0)
        {
            _errorService.ClearErrors();
            RefreshErrorList();
        }
    }
}
