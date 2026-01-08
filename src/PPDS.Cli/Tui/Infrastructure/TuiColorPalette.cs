using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Centralized color palette for the TUI application.
/// Provides a modern dark theme with environment-aware status bar colors.
/// </summary>
/// <remarks>
/// Design direction:
/// - Base: Dark background (Black) - easier on eyes, modern dev aesthetic
/// - Accents: Cyan as primary accent (stands out on dark, not harsh)
/// - Text: White/Gray on dark backgrounds for readability
/// - Status bar: Context-colored based on environment risk level
/// </remarks>
public static class TuiColorPalette
{
    #region Base Theme Colors

    /// <summary>
    /// Default color scheme for general UI elements.
    /// White/Gray text on black background.
    /// </summary>
    public static ColorScheme Default => new()
    {
        Normal = MakeAttr(Color.White, Color.Black),
        Focus = MakeAttr(Color.Black, Color.Cyan),
        HotNormal = MakeAttr(Color.Cyan, Color.Black),
        HotFocus = MakeAttr(Color.Black, Color.Cyan),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
    };

    /// <summary>
    /// Color scheme for focused/active elements.
    /// Cyan accent on dark background.
    /// </summary>
    public static ColorScheme Focused => new()
    {
        Normal = MakeAttr(Color.Cyan, Color.Black),
        Focus = MakeAttr(Color.Black, Color.Cyan),
        HotNormal = MakeAttr(Color.BrightCyan, Color.Black),
        HotFocus = MakeAttr(Color.Black, Color.BrightCyan),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
    };

    #endregion

    #region Status Bar - Environment-Aware

    /// <summary>
    /// Status bar for PRODUCTION environments.
    /// High contrast danger theme - white on red.
    /// </summary>
    public static ColorScheme StatusBar_Production => new()
    {
        Normal = MakeAttr(Color.White, Color.Red),
        Focus = MakeAttr(Color.White, Color.BrightRed),
        HotNormal = MakeAttr(Color.BrightYellow, Color.Red),
        HotFocus = MakeAttr(Color.BrightYellow, Color.BrightRed),
        Disabled = MakeAttr(Color.Gray, Color.Red)
    };

    /// <summary>
    /// Status bar for SANDBOX/STAGING environments.
    /// Warning theme - black on yellow (Brown is dark yellow in 16-color console).
    /// </summary>
    public static ColorScheme StatusBar_Sandbox => new()
    {
        Normal = MakeAttr(Color.Black, Color.Brown),
        Focus = MakeAttr(Color.Black, Color.BrightYellow),
        HotNormal = MakeAttr(Color.Red, Color.Brown),
        HotFocus = MakeAttr(Color.Red, Color.BrightYellow),
        Disabled = MakeAttr(Color.DarkGray, Color.Brown)
    };

    /// <summary>
    /// Status bar for DEVELOPMENT environments.
    /// Safe theme - white on green.
    /// </summary>
    public static ColorScheme StatusBar_Development => new()
    {
        Normal = MakeAttr(Color.White, Color.Green),
        Focus = MakeAttr(Color.White, Color.BrightGreen),
        HotNormal = MakeAttr(Color.Black, Color.Green),
        HotFocus = MakeAttr(Color.Black, Color.BrightGreen),
        Disabled = MakeAttr(Color.DarkGray, Color.Green)
    };

    /// <summary>
    /// Status bar for TRIAL environments.
    /// Info theme - white on cyan.
    /// </summary>
    public static ColorScheme StatusBar_Trial => new()
    {
        Normal = MakeAttr(Color.White, Color.Cyan),
        Focus = MakeAttr(Color.White, Color.BrightCyan),
        HotNormal = MakeAttr(Color.Black, Color.Cyan),
        HotFocus = MakeAttr(Color.Black, Color.BrightCyan),
        Disabled = MakeAttr(Color.DarkGray, Color.Cyan)
    };

    /// <summary>
    /// Status bar for UNKNOWN environments.
    /// Neutral theme - white on dark gray.
    /// </summary>
    public static ColorScheme StatusBar_Default => new()
    {
        Normal = MakeAttr(Color.White, Color.DarkGray),
        Focus = MakeAttr(Color.White, Color.Gray),
        HotNormal = MakeAttr(Color.Cyan, Color.DarkGray),
        HotFocus = MakeAttr(Color.Cyan, Color.Gray),
        Disabled = MakeAttr(Color.Gray, Color.DarkGray)
    };

    #endregion

    #region Accent Colors

    /// <summary>
    /// Color scheme for table headers.
    /// Cyan text on black background.
    /// </summary>
    public static ColorScheme TableHeader => new()
    {
        Normal = MakeAttr(Color.Cyan, Color.Black),
        Focus = MakeAttr(Color.Black, Color.Cyan),
        HotNormal = MakeAttr(Color.BrightCyan, Color.Black),
        HotFocus = MakeAttr(Color.Black, Color.BrightCyan),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
    };

    /// <summary>
    /// Color scheme for selected/highlighted items.
    /// Black text on cyan background.
    /// </summary>
    public static ColorScheme Selected => new()
    {
        Normal = MakeAttr(Color.Black, Color.Cyan),
        Focus = MakeAttr(Color.Black, Color.BrightCyan),
        HotNormal = MakeAttr(Color.White, Color.Cyan),
        HotFocus = MakeAttr(Color.White, Color.BrightCyan),
        Disabled = MakeAttr(Color.DarkGray, Color.Cyan)
    };

    /// <summary>
    /// Color scheme for error messages.
    /// Red text on black background.
    /// </summary>
    public static ColorScheme Error => new()
    {
        Normal = MakeAttr(Color.Red, Color.Black),
        Focus = MakeAttr(Color.BrightRed, Color.Black),
        HotNormal = MakeAttr(Color.BrightRed, Color.Black),
        HotFocus = MakeAttr(Color.White, Color.Red),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
    };

    /// <summary>
    /// Color scheme for success messages.
    /// Green text on black background.
    /// </summary>
    public static ColorScheme Success => new()
    {
        Normal = MakeAttr(Color.Green, Color.Black),
        Focus = MakeAttr(Color.BrightGreen, Color.Black),
        HotNormal = MakeAttr(Color.BrightGreen, Color.Black),
        HotFocus = MakeAttr(Color.White, Color.Green),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
    };

    #endregion

    #region Helpers

    /// <summary>
    /// Gets the status bar color scheme for the specified environment type.
    /// </summary>
    public static ColorScheme GetStatusBarScheme(EnvironmentType envType) => envType switch
    {
        EnvironmentType.Production => StatusBar_Production,
        EnvironmentType.Sandbox => StatusBar_Sandbox,
        EnvironmentType.Development => StatusBar_Development,
        EnvironmentType.Trial => StatusBar_Trial,
        _ => StatusBar_Default
    };

    /// <summary>
    /// Helper to create color attributes safely.
    /// Handles Terminal.Gui driver initialization.
    /// </summary>
    private static Terminal.Gui.Attribute MakeAttr(Color foreground, Color background)
    {
        // Application.Driver may be null during tests or before Application.Init()
        if (Application.Driver == null)
        {
            return new Terminal.Gui.Attribute(foreground, background);
        }
        return Application.Driver.MakeAttribute(foreground, background);
    }

    #endregion
}
