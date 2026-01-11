using PPDS.Auth.Credentials;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog shown before interactive browser authentication.
/// Gives user control to proceed with browser auth, switch to device code, or cancel.
/// </summary>
internal sealed class PreAuthenticationDialog : Dialog
{
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;

    /// <summary>
    /// Gets the user's choice from the dialog.
    /// </summary>
    public PreAuthDialogResult Result { get; private set; } = PreAuthDialogResult.Cancel;

    /// <summary>
    /// Creates a new pre-authentication dialog.
    /// </summary>
    /// <param name="deviceCodeCallback">Optional callback for device code display (enables fallback option).</param>
    public PreAuthenticationDialog(Action<DeviceCodeInfo>? deviceCodeCallback = null)
        : base("Authentication Required")
    {
        _deviceCodeCallback = deviceCodeCallback;

        Width = 60;
        Height = 12;
        ColorScheme = TuiColorPalette.Default;

        // Main message
        var messageLabel = new Label("A browser window will open for authentication.")
        {
            X = Pos.Center(),
            Y = 1,
            TextAlignment = TextAlignment.Centered
        };

        var instructionLabel = new Label("Choose how to proceed:")
        {
            X = Pos.Center(),
            Y = 3,
            TextAlignment = TextAlignment.Centered
        };

        // Open Browser button (primary action)
        var openBrowserButton = new Button("Open _Browser")
        {
            X = Pos.Center() - 24,
            Y = 6,
            IsDefault = true
        };
        openBrowserButton.Clicked += OnOpenBrowserClicked;

        // Device Code button (fallback option) - only enabled if callback provided
        var deviceCodeButton = new Button("Use _Device Code")
        {
            X = Pos.Center() - 8,
            Y = 6
        };
        if (_deviceCodeCallback == null)
        {
            // Disable if no callback - device code fallback not available
            deviceCodeButton.Enabled = false;
        }
        deviceCodeButton.Clicked += OnDeviceCodeClicked;

        // Cancel button
        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 12,
            Y = 6
        };
        cancelButton.Clicked += OnCancelClicked;

        // Help text at bottom
        var helpLabel = new Label("Press Escape to cancel")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };

        Add(messageLabel, instructionLabel, openBrowserButton, deviceCodeButton, cancelButton, helpLabel);

        // Handle Escape key as Cancel
        KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == Key.Esc)
            {
                Result = PreAuthDialogResult.Cancel;
                Application.RequestStop();
                e.Handled = true;
            }
        };
    }

    private void OnOpenBrowserClicked()
    {
        Result = PreAuthDialogResult.OpenBrowser;
        Application.RequestStop();
    }

    private void OnDeviceCodeClicked()
    {
        Result = PreAuthDialogResult.UseDeviceCode;
        Application.RequestStop();
    }

    private void OnCancelClicked()
    {
        Result = PreAuthDialogResult.Cancel;
        Application.RequestStop();
    }
}
