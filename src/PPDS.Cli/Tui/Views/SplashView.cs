using PPDS.Cli.Commands;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Branded splash screen shown during TUI startup.
/// Displays PPDS logo, version, and initialization status.
/// </summary>
internal sealed class SplashView : View, ITuiStateCapture<SplashViewState>
{
    // ASCII art logo — compact version for terminal
    private const string Logo = @"
 ██████  ██████  ██████  ███████
 ██   ██ ██   ██ ██   ██ ██
 ██████  ██████  ██   ██ ███████
 ██      ██      ██   ██      ██
 ██      ██      ██████  ███████";

    private const string Tagline = "Power Platform Developer Suite";

    private readonly string _version;
    private readonly Label _statusLabel;
    private readonly TuiSpinner _spinner;
    private string _statusMessage = "Initializing...";
    private bool _isReady;
    private bool _spinnerActive;

    public SplashView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        ColorScheme = TuiColorPalette.Default;

        _version = ErrorOutput.Version;

        // Logo (centered)
        var logoLabel = new Label(Logo)
        {
            X = Pos.Center(),
            Y = Pos.Center() - 5,
            TextAlignment = TextAlignment.Centered
        };

        // Tagline
        var taglineLabel = new Label(Tagline)
        {
            X = Pos.Center(),
            Y = Pos.Center() + 2,
            TextAlignment = TextAlignment.Centered
        };

        // Status spinner + message
        _spinner = new TuiSpinner
        {
            X = Pos.Center() - 15,
            Y = Pos.Center() + 4,
            Width = 30,
            Height = 1
        };

        _statusLabel = new Label(_statusMessage)
        {
            X = Pos.Center(),
            Y = Pos.Center() + 4,
            TextAlignment = TextAlignment.Centered
        };

        // Version
        var versionLabel = new Label($"v{_version}")
        {
            X = Pos.Center(),
            Y = Pos.Center() + 6,
            TextAlignment = TextAlignment.Centered
        };

        Add(logoLabel, taglineLabel, _spinner, _statusLabel, versionLabel);
        _spinnerActive = true;
    }

    /// <summary>
    /// Starts the spinner animation. Safe to call only when Terminal.Gui is initialized.
    /// </summary>
    public void StartSpinner()
    {
        _spinner.Start(_statusMessage);
        _statusLabel.Visible = false;
    }

    /// <summary>
    /// Updates the status message shown during initialization.
    /// </summary>
    public void SetStatus(string message)
    {
        _statusMessage = message;
        // Only touch UI if Application is initialized
        if (Application.Driver != null)
        {
            _spinner.StopWithMessage(message);
            _spinner.Start(message);
        }
    }

    /// <summary>
    /// Marks initialization as complete. Stops the spinner.
    /// </summary>
    public void SetReady()
    {
        _isReady = true;
        _spinnerActive = false;
        _statusMessage = "Ready";
        // Only touch UI if Application is initialized
        if (Application.Driver != null)
        {
            _spinner.Stop();
            _spinner.Visible = false;
            _statusLabel.Text = "Ready — press Enter or select from the menu";
            _statusLabel.Visible = true;
        }
    }

    /// <inheritdoc />
    public SplashViewState CaptureState() => new(
        StatusMessage: _statusMessage,
        IsReady: _isReady,
        Version: _version,
        SpinnerActive: _spinnerActive && !_isReady);
}
