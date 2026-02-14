using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for displaying device code authentication info.
/// Shows the code prominently with a Copy button and auto-closes when auth completes.
/// </summary>
internal sealed class DeviceCodeDialog : TuiDialog, ITuiStateCapture<DeviceCodeDialogState>
{
    private readonly string _userCode;
    private readonly string _verificationUrl;
    private readonly bool _clipboardCopied;
    private readonly Label _statusLabel;
    private CancellationTokenRegistration? _autoCloseRegistration;

    /// <summary>
    /// Creates a device code dialog with prominent code display and copy button.
    /// </summary>
    /// <param name="userCode">The device code to display.</param>
    /// <param name="verificationUrl">The URL where the user enters the code.</param>
    /// <param name="clipboardCopied">Whether the code was auto-copied to clipboard on dialog creation.</param>
    /// <param name="authComplete">Optional token that fires when auth succeeds — auto-closes the dialog.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public DeviceCodeDialog(
        string userCode,
        string verificationUrl,
        bool clipboardCopied = false,
        CancellationToken authComplete = default,
        InteractiveSession? session = null)
        : base("Authentication Required", session)
    {
        _userCode = userCode;
        _verificationUrl = verificationUrl;
        _clipboardCopied = clipboardCopied;

        Width = 60;
        Height = 14;

        var urlLabel = new Label($"Visit: {verificationUrl}")
        {
            X = Pos.Center(),
            Y = 1,
            TextAlignment = TextAlignment.Centered
        };

        var codeLabel = new Label("Enter this code:")
        {
            X = Pos.Center(),
            Y = 3,
            TextAlignment = TextAlignment.Centered
        };

        // Code displayed as a prominent label (not editable TextField)
        var codeDisplay = new Label($"  {userCode}  ")
        {
            X = Pos.Center(),
            Y = 5,
            TextAlignment = TextAlignment.Centered,
            ColorScheme = TuiColorPalette.Selected
        };

        // Copy Code button
        var copyCodeButton = new Button("Copy _Code")
        {
            X = Pos.Center() - 14,
            Y = 7
        };
        copyCodeButton.Clicked += () => CopyToClipboard(_userCode, "Code copied!");

        // Copy URL button
        var copyUrlButton = new Button("Copy _URL")
        {
            X = Pos.Center() + 2,
            Y = 7
        };
        copyUrlButton.Clicked += () => CopyToClipboard(_verificationUrl, "URL copied!");

        // Status label for clipboard feedback
        _statusLabel = new Label(clipboardCopied ? "(code copied to clipboard!)" : "")
        {
            X = Pos.Center(),
            Y = 9,
            Width = 50,
            TextAlignment = TextAlignment.Centered,
            ColorScheme = TuiColorPalette.Success
        };

        var okButton = new Button("_OK")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        okButton.Clicked += () => Application.RequestStop();

        Add(urlLabel, codeLabel, codeDisplay, copyCodeButton, copyUrlButton, _statusLabel, okButton);

        // Auto-close when authentication completes
        if (authComplete.CanBeCanceled)
        {
            _autoCloseRegistration = authComplete.Register(() =>
            {
                Application.MainLoop?.Invoke(() => Application.RequestStop());
            });
        }
    }

    private void CopyToClipboard(string text, string successMessage)
    {
        if (Clipboard.TrySetClipboardData(text))
        {
            _statusLabel.Text = $"({successMessage})";
            _statusLabel.ColorScheme = TuiColorPalette.Success;
        }
        else
        {
            _statusLabel.Text = $"Copy failed - code: {_userCode}";
            _statusLabel.ColorScheme = TuiColorPalette.Error;
        }
        _statusLabel.SetNeedsDisplay();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoCloseRegistration?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public DeviceCodeDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        UserCode: _userCode,
        VerificationUrl: _verificationUrl,
        ClipboardCopied: _clipboardCopied,
        IsVisible: Visible);
}
