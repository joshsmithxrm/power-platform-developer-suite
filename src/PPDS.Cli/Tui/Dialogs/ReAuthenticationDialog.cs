using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog prompting the user to re-authenticate after a session expiry.
/// </summary>
/// <remarks>
/// <para>
/// Shown when a Dataverse operation fails due to token expiry (401 Unauthorized).
/// The user can choose to re-authenticate and retry the operation, or cancel.
/// </para>
/// <para>
/// Use the <see cref="ShouldReauthenticate"/> property to determine the user's choice
/// after the dialog closes.
/// </para>
/// </remarks>
internal sealed class ReAuthenticationDialog : TuiDialog, ITuiStateCapture<ReAuthenticationDialogState>
{
    private readonly string _errorMessage;

    /// <summary>
    /// Gets whether the user chose to re-authenticate.
    /// </summary>
    public bool ShouldReauthenticate { get; private set; }

    /// <summary>
    /// Creates a new re-authentication dialog with default message.
    /// </summary>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public ReAuthenticationDialog(InteractiveSession? session = null)
        : this("Your session has expired. Please re-authenticate to continue.", session)
    {
    }

    /// <summary>
    /// Creates a new re-authentication dialog with custom message.
    /// </summary>
    /// <param name="errorMessage">The error message to display.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public ReAuthenticationDialog(string errorMessage, InteractiveSession? session = null)
        : base("Session Expired", session)
    {
        _errorMessage = errorMessage;

        Width = 55;
        Height = 11;

        // Warning icon label
        var iconLabel = new Label("!")
        {
            X = 2,
            Y = 1,
            ColorScheme = TuiColorPalette.Error
        };

        // Main message
        var messageLabel = new Label(_errorMessage)
        {
            X = 5,
            Y = 1,
            Width = Dim.Fill(2)
        };

        // Separator
        var separator = new Label(new string('─', 51))
        {
            X = 1,
            Y = 3
        };

        // Prompt
        var promptLabel = new Label("Would you like to re-authenticate now?")
        {
            X = Pos.Center(),
            Y = 5
        };

        // Re-authenticate button (primary action)
        var reAuthButton = new Button("_Re-authenticate")
        {
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1),
            ColorScheme = TuiColorPalette.Focused
        };
        reAuthButton.Clicked += () =>
        {
            ShouldReauthenticate = true;
            Application.RequestStop();
        };

        // Cancel button
        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 5,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () =>
        {
            ShouldReauthenticate = false;
            Application.RequestStop();
        };

        Add(
            iconLabel,
            messageLabel,
            separator,
            promptLabel,
            reAuthButton,
            cancelButton
        );

        // Focus on Re-authenticate button by default
        reAuthButton.SetFocus();
    }

    /// <inheritdoc />
    public ReAuthenticationDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        ErrorMessage: _errorMessage,
        ShouldReauthenticate: ShouldReauthenticate);
}
