using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Interactive status bar with clickable profile and environment sections.
/// </summary>
/// <remarks>
/// <para>
/// The status bar displays the current profile and environment with environment-aware
/// coloring (red for production, yellow for sandbox, green for development).
/// </para>
/// <para>
/// Clicking the profile section opens the profile selector dialog.
/// Clicking the environment section opens the environment selector dialog.
/// </para>
/// </remarks>
internal sealed class TuiStatusBar : View
{
    private readonly Button _profileButton;
    private readonly Button _environmentButton;
    private readonly InteractiveSession _session;
    private readonly ITuiThemeService _themeService;

    private string? _statusMessage;

    /// <summary>
    /// Event raised when the profile section is clicked.
    /// </summary>
    public event Action? ProfileClicked;

    /// <summary>
    /// Event raised when the environment section is clicked.
    /// </summary>
    public event Action? EnvironmentClicked;

    /// <summary>
    /// Creates a new interactive status bar.
    /// </summary>
    /// <param name="session">The interactive session for state and events.</param>
    public TuiStatusBar(InteractiveSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _themeService = session.GetThemeService();

        Height = 1;
        Width = Dim.Fill();
        Y = Pos.AnchorEnd(1);

        // Profile button (left portion)
        _profileButton = new Button
        {
            X = 0,
            Y = 0,
            Height = 1,
            Width = Dim.Percent(40)
        };
        _profileButton.Clicked += () => ProfileClicked?.Invoke();

        // Environment button (right portion - takes remaining space)
        _environmentButton = new Button
        {
            X = Pos.Right(_profileButton),
            Y = 0,
            Height = 1,
            Width = Dim.Fill()
        };
        _environmentButton.Clicked += () => EnvironmentClicked?.Invoke();

        Add(_profileButton, _environmentButton);

        // Subscribe to session events
        _session.EnvironmentChanged += OnEnvironmentChanged;
        _session.ProfileChanged += OnProfileChanged;

        // Initial display
        UpdateDisplay();
    }

    /// <summary>
    /// Sets an optional status message to display after the environment info.
    /// </summary>
    /// <param name="message">The status message, or null to clear.</param>
    public void SetStatusMessage(string? message)
    {
        _statusMessage = message;
        UpdateDisplay();
    }

    /// <summary>
    /// Clears the status message.
    /// </summary>
    public void ClearStatusMessage()
    {
        _statusMessage = null;
        UpdateDisplay();
    }

    /// <summary>
    /// Forces a refresh of the status bar display.
    /// </summary>
    public void Refresh()
    {
        UpdateDisplay();
    }

    private void OnEnvironmentChanged(string? url, string? displayName)
    {
        Application.MainLoop?.Invoke(UpdateDisplay);
    }

    private void OnProfileChanged(string? profileName)
    {
        Application.MainLoop?.Invoke(UpdateDisplay);
    }

    private void UpdateDisplay()
    {
        // Get environment-aware color scheme
        var envType = _themeService.DetectEnvironmentType(_session.CurrentEnvironmentUrl);
        var colorScheme = _themeService.GetStatusBarScheme(envType);

        // Get environment type label
        var envLabel = _themeService.GetEnvironmentLabel(envType);
        var labelSuffix = !string.IsNullOrEmpty(envLabel) ? $" [{envLabel}]" : "";

        // Profile section - show name and identity (e.g., "Josh (josh@contoso.com)")
        var profileName = _session.CurrentProfileName ?? "None";
        var profileIdentity = _session.CurrentProfileIdentity;
        var profileDisplay = !string.IsNullOrEmpty(profileIdentity)
            ? $"{profileName} ({profileIdentity})"
            : profileName;
        _profileButton.Text = $" Profile: {profileDisplay} \u25bc";
        _profileButton.ColorScheme = colorScheme;

        // Environment section with optional status message
        var envName = _session.CurrentEnvironmentDisplayName ?? "None";
        var envText = $"Environment: {envName}{labelSuffix}";

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            envText = $"{envText} | {_statusMessage}";
        }

        _environmentButton.Text = $" {envText} \u25bc";
        _environmentButton.ColorScheme = colorScheme;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.EnvironmentChanged -= OnEnvironmentChanged;
            _session.ProfileChanged -= OnProfileChanged;
        }
        base.Dispose(disposing);
    }
}
