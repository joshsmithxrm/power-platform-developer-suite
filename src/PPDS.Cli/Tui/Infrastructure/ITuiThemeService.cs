using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Service for TUI theming operations including environment detection and color scheme selection.
/// </summary>
public interface ITuiThemeService
{
    /// <summary>
    /// Detects the environment type from a Dataverse URL.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to analyze.</param>
    /// <returns>The detected environment type.</returns>
    EnvironmentType DetectEnvironmentType(string? environmentUrl);

    /// <summary>
    /// Gets the appropriate status bar color scheme for the specified environment type.
    /// </summary>
    /// <param name="envType">The environment type.</param>
    /// <returns>The color scheme for the status bar.</returns>
    ColorScheme GetStatusBarScheme(EnvironmentType envType);

    /// <summary>
    /// Gets the default color scheme for general UI elements.
    /// </summary>
    /// <returns>The default color scheme.</returns>
    ColorScheme GetDefaultScheme();

    /// <summary>
    /// Gets the color scheme for error states.
    /// </summary>
    /// <returns>The error color scheme.</returns>
    ColorScheme GetErrorScheme();

    /// <summary>
    /// Gets the color scheme for success states.
    /// </summary>
    /// <returns>The success color scheme.</returns>
    ColorScheme GetSuccessScheme();

    /// <summary>
    /// Gets a human-readable label for the environment type.
    /// </summary>
    /// <param name="envType">The environment type.</param>
    /// <returns>A display label (e.g., "PROD", "DEV", "SANDBOX").</returns>
    string GetEnvironmentLabel(EnvironmentType envType);
}
