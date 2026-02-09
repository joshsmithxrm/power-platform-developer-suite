using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
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
/// <para>
/// This view draws its content directly without child views to ensure reliable
/// mouse event handling. Terminal.Gui child views consume mouse events before
/// the parent can handle them, so we bypass that by not using child views.
/// </para>
/// </remarks>
internal sealed class TuiStatusBar : View, ITuiStateCapture<TuiStatusBarState>
{
    private readonly InteractiveSession _session;
    private readonly ITuiThemeService _themeService;

    // Text content for rendering
    private string _profileText = string.Empty;
    private string _environmentText = string.Empty;

    /// <summary>
    /// Event raised when the profile section is clicked.
    /// </summary>
    public event Action? ProfileClicked;

    /// <summary>
    /// Event raised when the environment section is clicked.
    /// </summary>
    public event Action? EnvironmentClicked;

    /// <summary>
    /// Event raised when the environment section is right-clicked (configure).
    /// </summary>
    public event Action? EnvironmentConfigureRequested;

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
        Y = Pos.AnchorEnd(1); // Bottom of screen

        // Enable mouse event reception
        WantMousePositionReports = true;

        // Subscribe to session events
        _session.EnvironmentChanged += OnEnvironmentChanged;
        _session.ProfileChanged += OnProfileChanged;

        // Initial display
        UpdateDisplay();
    }

    /// <summary>
    /// Forces a refresh of the status bar display.
    /// </summary>
    public void Refresh()
    {
        UpdateDisplay();
        SetNeedsDisplay();
    }

    /// <summary>
    /// Handles mouse events for click detection.
    /// </summary>
    public override bool MouseEvent(MouseEvent mouseEvent)
    {
        // Profile is on left, environment on right
        // Boundary is where environment text starts (right-aligned)
        var envStartX = Bounds.Width - _environmentText.Length;
        if (envStartX < 0) envStartX = Bounds.Width / 2; // Fallback if text too long

        if (mouseEvent.Flags.HasFlag(MouseFlags.Button1Clicked))
        {
            if (mouseEvent.X < envStartX)
            {
                ProfileClicked?.Invoke();
                return true;
            }
            else
            {
                EnvironmentClicked?.Invoke();
                return true;
            }
        }

        if (mouseEvent.Flags.HasFlag(MouseFlags.Button3Clicked))
        {
            if (mouseEvent.X >= envStartX)
            {
                EnvironmentConfigureRequested?.Invoke();
                return true;
            }
        }

        return base.MouseEvent(mouseEvent);
    }

    /// <summary>
    /// Draws the status bar content directly.
    /// </summary>
    public override void Redraw(Rect bounds)
    {
        // Get environment-aware color scheme
        var colorScheme = _themeService.GetStatusBarSchemeForUrl(_session.CurrentEnvironmentUrl);

        // Apply the color scheme
        Driver.SetAttribute(colorScheme.Normal);

        // Clear the entire bar
        for (int x = 0; x < bounds.Width; x++)
        {
            Move(x, 0);
            Driver.AddRune(' ');
        }

        // Calculate available space for each section
        // Profile on left, Environment on right, with minimum gap of 2 chars
        var totalTextLength = _profileText.Length + _environmentText.Length;
        var availableWidth = bounds.Width;
        var minGap = 2;

        string profileToRender;
        string envToRender;

        if (totalTextLength + minGap <= availableWidth)
        {
            // Both fit - no truncation needed
            profileToRender = _profileText;
            envToRender = _environmentText;
        }
        else
        {
            // Need to truncate - give each section roughly half the space
            var maxPerSection = (availableWidth - minGap) / 2;

            profileToRender = _profileText.Length > maxPerSection
                ? _profileText[..(maxPerSection - 1)] + "\u2026"
                : _profileText;

            envToRender = _environmentText.Length > maxPerSection
                ? _environmentText[..(maxPerSection - 1)] + "\u2026"
                : _environmentText;
        }

        // Draw profile on the left
        Move(0, 0);
        Driver.AddStr(profileToRender);

        // Draw environment on the right
        var envStartX = bounds.Width - envToRender.Length;
        if (envStartX < profileToRender.Length + minGap)
        {
            envStartX = profileToRender.Length + minGap;
        }
        Move(envStartX, 0);
        Driver.AddStr(envToRender);
    }

    private void OnEnvironmentChanged(string? url, string? displayName)
    {
        Application.MainLoop?.Invoke(() =>
        {
            UpdateDisplay();
            SetNeedsDisplay();
        });
    }

    private void OnProfileChanged(string? profileName)
    {
        Application.MainLoop?.Invoke(() =>
        {
            UpdateDisplay();
            SetNeedsDisplay();
        });
    }

    private void UpdateDisplay()
    {
        // Get environment type label
        var envLabel = _themeService.GetEnvironmentLabelForUrl(_session.CurrentEnvironmentUrl);
        var labelSuffix = !string.IsNullOrEmpty(envLabel) ? $" [{envLabel}]" : "";

        // Profile section - show name and identity (e.g., "Josh (josh@contoso.com)")
        // No truncation here - Redraw() will truncate based on actual terminal width
        var profileName = _session.CurrentProfileName ?? "None";
        var profileIdentity = _session.CurrentProfileIdentity;
        var profileDisplay = !string.IsNullOrEmpty(profileIdentity)
            ? $"{profileName} ({profileIdentity})"
            : profileName;
        _profileText = $" Profile: {profileDisplay} \u25bc";

        // Environment section - no truncation, Redraw() handles it dynamically
        var envName = _session.CurrentEnvironmentDisplayName ?? "None";
        _environmentText = $" Environment: {envName}{labelSuffix} \u25bc";
    }

    /// <summary>
    /// Truncates text to max length with ellipsis if needed.
    /// </summary>
    private static string TruncateWithEllipsis(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 1)] + "\u2026"; // Unicode ellipsis
    }

    /// <inheritdoc />
    public TuiStatusBarState CaptureState()
    {
        var envType = _themeService.DetectEnvironmentType(_session.CurrentEnvironmentUrl);
        return new TuiStatusBarState(
            ProfileButtonText: _profileText,
            EnvironmentButtonText: _environmentText,
            EnvironmentType: envType,
            HasProfile: _session.CurrentProfileName != null,
            HasEnvironment: _session.CurrentEnvironmentUrl != null,
            HelpText: null // Status bar doesn't have help text in current implementation
        );
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
