using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for displaying transpiled FetchXML from a SQL query.
/// </summary>
internal sealed class FetchXmlPreviewDialog : TuiDialog, ITuiStateCapture<FetchXmlPreviewDialogState>
{
    private readonly TextView _fetchXmlView;
    private readonly Label _statusLabel;
    private readonly string? _fetchXml;

    /// <summary>
    /// Creates a new FetchXML preview dialog.
    /// </summary>
    /// <param name="fetchXml">The FetchXML content to display.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public FetchXmlPreviewDialog(string? fetchXml, InteractiveSession? session = null)
        : base("FetchXML Preview", session)
    {
        _fetchXml = fetchXml;

        Width = 80;
        Height = 25;

        // FetchXML content view (read-only, scrollable)
        var frameView = new FrameView("Transpiled FetchXML")
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 4
        };

        _fetchXmlView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = false,
            Text = fetchXml ?? "(No FetchXML available)",
            ColorScheme = TuiColorPalette.ReadOnlyText
        };

        frameView.Add(_fetchXmlView);

        // Status label for copy feedback
        _statusLabel = new Label(string.Empty)
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill() - 20
        };

        // Copy button
        var copyButton = new Button("_Copy")
        {
            X = Pos.Center() - 10,
            Y = Pos.AnchorEnd(1)
        };
        copyButton.Clicked += OnCopyClicked;

        // Close button
        var closeButton = new Button("C_lose")
        {
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(frameView, _statusLabel, copyButton, closeButton);

        // Focus the close button by default
        closeButton.SetFocus();
    }

    private void OnCopyClicked()
    {
        if (string.IsNullOrEmpty(_fetchXml))
        {
            _statusLabel.Text = "Nothing to copy";
            return;
        }

        if (Clipboard.TrySetClipboardData(_fetchXml))
        {
            _statusLabel.Text = "Copied to clipboard!";
        }
        else
        {
            _statusLabel.Text = "Failed to copy to clipboard";
        }
    }

    /// <inheritdoc />
    public FetchXmlPreviewDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        FetchXml: _fetchXml,
        HasContent: !string.IsNullOrEmpty(_fetchXml));
}
