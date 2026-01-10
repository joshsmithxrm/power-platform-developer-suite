using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for displaying device code authentication information.
/// Shows the verification URL and user code for device code flow.
/// </summary>
internal sealed class DeviceCodeAuthDialog : Dialog
{
    private readonly DeviceCodeInfo _deviceCodeInfo;
    private readonly Label _spinnerLabel;
    private int _spinnerIndex;
    private readonly string[] _spinnerFrames = { "/", "-", "\\", "|" };

    /// <summary>
    /// Creates a new device code authentication dialog.
    /// </summary>
    /// <param name="deviceCodeInfo">The device code information to display.</param>
    public DeviceCodeAuthDialog(DeviceCodeInfo deviceCodeInfo) : base("Device Code Authentication")
    {
        _deviceCodeInfo = deviceCodeInfo ?? throw new ArgumentNullException(nameof(deviceCodeInfo));

        Width = 65;
        Height = 14;
        ColorScheme = TuiColorPalette.Default;

        // Instructions
        var instructionLabel = new Label("To sign in, visit:")
        {
            X = Pos.Center(),
            Y = 1,
            TextAlignment = TextAlignment.Centered
        };

        // URL (highlighted)
        var urlLabel = new Label(_deviceCodeInfo.VerificationUrl)
        {
            X = Pos.Center(),
            Y = 3,
            ColorScheme = TuiColorPalette.Focused
        };

        // Code label
        var codeInstructionLabel = new Label("Enter code:")
        {
            X = Pos.Center(),
            Y = 5,
            TextAlignment = TextAlignment.Centered
        };

        // Code value (large and prominent)
        var codeLabel = new Label(_deviceCodeInfo.UserCode)
        {
            X = Pos.Center(),
            Y = 6,
            ColorScheme = TuiColorPalette.TableHeader
        };

        // Spinner/waiting indicator
        _spinnerLabel = new Label("  Waiting for authentication...")
        {
            X = Pos.Center(),
            Y = 8,
            Width = 35
        };

        // Buttons
        var copyCodeButton = new Button("Copy _Code")
        {
            X = Pos.Center() - 22,
            Y = Pos.AnchorEnd(1)
        };
        copyCodeButton.Clicked += OnCopyCodeClicked;

        var openBrowserButton = new Button("Open _Browser")
        {
            X = Pos.Center() - 7,
            Y = Pos.AnchorEnd(1)
        };
        openBrowserButton.Clicked += OnOpenBrowserClicked;

        var closeButton = new Button("_Close")
        {
            X = Pos.Center() + 10,
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(instructionLabel, urlLabel, codeInstructionLabel, codeLabel,
            _spinnerLabel, copyCodeButton, openBrowserButton, closeButton);

        // Start spinner animation
        StartSpinner();
    }

    private void StartSpinner()
    {
        Application.MainLoop?.AddTimeout(TimeSpan.FromMilliseconds(250), _ =>
        {
            _spinnerIndex = (_spinnerIndex + 1) % _spinnerFrames.Length;
            _spinnerLabel.Text = $"{_spinnerFrames[_spinnerIndex]} Waiting for authentication...";
            return true; // Continue the timer
        });
    }

    private void OnCopyCodeClicked()
    {
        if (ClipboardHelper.CopyToClipboard(_deviceCodeInfo.UserCode))
        {
            MessageBox.Query("Copied", "Code copied to clipboard!", "OK");
        }
        else
        {
            MessageBox.ErrorQuery("Error", "Failed to copy code to clipboard.", "OK");
        }
    }

    private void OnOpenBrowserClicked()
    {
        if (!BrowserHelper.OpenUrl(_deviceCodeInfo.VerificationUrl))
        {
            MessageBox.ErrorQuery("Error", "Failed to open browser. Please navigate manually to the URL shown above.", "OK");
        }
    }
}
