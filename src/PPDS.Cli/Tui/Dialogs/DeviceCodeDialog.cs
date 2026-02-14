using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for displaying device code authentication info.
/// Shows the code in a selectable TextField and auto-closes when auth completes.
/// </summary>
internal sealed class DeviceCodeDialog : TuiDialog, ITuiStateCapture<DeviceCodeDialogState>
{
    private readonly string _userCode;
    private readonly string _verificationUrl;
    private readonly bool _clipboardCopied;
    private CancellationTokenRegistration? _autoCloseRegistration;

    /// <summary>
    /// Creates a device code dialog with selectable code display.
    /// </summary>
    /// <param name="userCode">The device code to display.</param>
    /// <param name="verificationUrl">The URL where the user enters the code.</param>
    /// <param name="clipboardCopied">Whether the code was auto-copied to clipboard.</param>
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
        Height = 12;

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

        // Selectable TextField so user can select + Ctrl+C the code
        var codeField = new TextField(userCode)
        {
            X = Pos.Center(),
            Y = 5,
            Width = userCode.Length + 4,
            ReadOnly = true,
            ColorScheme = TuiColorPalette.Focused
        };

        var clipboardLabel = new Label(clipboardCopied ? "(copied to clipboard!)" : "(select code above and Ctrl+C to copy)")
        {
            X = Pos.Center(),
            Y = 7,
            TextAlignment = TextAlignment.Centered,
            ColorScheme = clipboardCopied ? TuiColorPalette.Success : TuiColorPalette.Default
        };

        var okButton = new Button("_OK")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1),
            IsDefault = true
        };
        okButton.Clicked += () => Application.RequestStop();

        Add(urlLabel, codeLabel, codeField, clipboardLabel, okButton);

        // Auto-close when authentication completes
        if (authComplete.CanBeCanceled)
        {
            _autoCloseRegistration = authComplete.Register(() =>
            {
                Application.MainLoop?.Invoke(() => Application.RequestStop());
            });
        }
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
