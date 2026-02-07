using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Centralized color palette for the TUI application.
/// Provides a modern dark theme with environment-aware status bar colors.
/// </summary>
/// <remarks>
/// <para>
/// Design direction:
/// - Base: Dark background (Black) - easier on eyes, modern dev aesthetic
/// - Accents: Cyan as primary accent (stands out on dark, not harsh)
/// - Text: White/Gray on dark backgrounds for readability
/// - Status bar: Context-colored based on environment risk level
/// </para>
/// <para>
/// DESIGN RULE - BLUE BACKGROUNDS: When background is Cyan, BrightCyan, Blue, or BrightBlue,
/// foreground MUST be Black. No exceptions. White/grey text is unreadable on blue backgrounds.
/// Use <see cref="ValidateBlueBackgroundRule"/> in unit tests to enforce this rule.
/// </para>
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

    /// <summary>
    /// Color scheme for text input fields (TextField).
    /// No background change on focus - block cursor provides visibility.
    /// </summary>
    public static ColorScheme TextInput => new()
    {
        Normal = MakeAttr(Color.White, Color.Black),
        Focus = MakeAttr(Color.White, Color.Black),
        HotNormal = MakeAttr(Color.Cyan, Color.Black),
        HotFocus = MakeAttr(Color.White, Color.Black),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
    };

    /// <summary>
    /// Color scheme for read-only text display (TextViews showing non-editable content).
    /// Maintains black background even when focused for readability.
    /// Uses White for Disabled because Terminal.Gui treats ReadOnly as Disabled.
    /// </summary>
    public static ColorScheme ReadOnlyText => new()
    {
        Normal = MakeAttr(Color.White, Color.Black),
        Focus = MakeAttr(Color.White, Color.Black),
        HotNormal = MakeAttr(Color.Cyan, Color.Black),
        HotFocus = MakeAttr(Color.White, Color.Black),
        Disabled = MakeAttr(Color.White, Color.Black)
    };

    /// <summary>
    /// Color scheme for file dialogs (SaveDialog, OpenDialog).
    /// Uses black text on cyan for Focus per blue background rule.
    /// Disabled uses Black because Terminal.Gui may not respect the background color.
    /// </summary>
    public static ColorScheme FileDialog => new()
    {
        Normal = MakeAttr(Color.White, Color.Black),
        Focus = MakeAttr(Color.Black, Color.Cyan),
        HotNormal = MakeAttr(Color.Cyan, Color.Black),
        HotFocus = MakeAttr(Color.Black, Color.Cyan),
        Disabled = MakeAttr(Color.Black, Color.Cyan)
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
    /// Info theme - black on cyan per blue background rule.
    /// </summary>
    public static ColorScheme StatusBar_Trial => new()
    {
        Normal = MakeAttr(Color.Black, Color.Cyan),
        Focus = MakeAttr(Color.Black, Color.BrightCyan),
        HotNormal = MakeAttr(Color.Black, Color.Cyan),
        HotFocus = MakeAttr(Color.Black, Color.BrightCyan),
        Disabled = MakeAttr(Color.Black, Color.Cyan)
    };

    /// <summary>
    /// Status bar for UNKNOWN environments.
    /// Neutral theme - black on gray for maximum readability.
    /// </summary>
    public static ColorScheme StatusBar_Default => new()
    {
        Normal = MakeAttr(Color.Black, Color.Gray),
        Focus = MakeAttr(Color.Black, Color.BrightYellow),
        HotNormal = MakeAttr(Color.Blue, Color.Gray),
        HotFocus = MakeAttr(Color.Blue, Color.BrightYellow),
        Disabled = MakeAttr(Color.DarkGray, Color.Gray)
    };

    #endregion

    #region Menu Bar

    /// <summary>
    /// Color scheme for the menu bar.
    /// Dark background with cyan accents for a modern look.
    /// </summary>
    public static ColorScheme MenuBar => new()
    {
        Normal = MakeAttr(Color.Cyan, Color.Black),
        Focus = MakeAttr(Color.Black, Color.Cyan),
        HotNormal = MakeAttr(Color.BrightCyan, Color.Black),
        HotFocus = MakeAttr(Color.Black, Color.BrightCyan),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
    };

    #endregion

    #region Tab Bar

    /// <summary>
    /// Active tab in tab bar.
    /// White text on dark gray background for clear visibility.
    /// </summary>
    public static ColorScheme TabActive => new()
    {
        Normal = MakeAttr(Color.White, Color.DarkGray),
        Focus = MakeAttr(Color.White, Color.DarkGray),
        HotNormal = MakeAttr(Color.BrightCyan, Color.DarkGray),
        HotFocus = MakeAttr(Color.BrightCyan, Color.DarkGray),
        Disabled = MakeAttr(Color.Gray, Color.DarkGray)
    };

    /// <summary>
    /// Inactive tab in tab bar.
    /// Gray text on black background for muted appearance.
    /// </summary>
    public static ColorScheme TabInactive => new()
    {
        Normal = MakeAttr(Color.Gray, Color.Black),
        Focus = MakeAttr(Color.White, Color.Black),
        HotNormal = MakeAttr(Color.Cyan, Color.Black),
        HotFocus = MakeAttr(Color.White, Color.Black),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
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
    /// Black text on cyan background per blue background rule.
    /// </summary>
    public static ColorScheme Selected => new()
    {
        Normal = MakeAttr(Color.Black, Color.Cyan),
        Focus = MakeAttr(Color.Black, Color.BrightCyan),
        HotNormal = MakeAttr(Color.Black, Color.Cyan),
        HotFocus = MakeAttr(Color.Black, Color.BrightCyan),
        Disabled = MakeAttr(Color.Black, Color.Cyan)
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

    /// <summary>
    /// Blue background colors that require black foreground.
    /// </summary>
    private static readonly Color[] BlueBackgrounds = { Color.Cyan, Color.BrightCyan, Color.Blue, Color.BrightBlue };

    /// <summary>
    /// Validates all color schemes follow the blue background rule.
    /// Returns a list of violations (scheme name, attribute name, foreground, background).
    /// Used in unit tests to prevent regressions.
    /// </summary>
    /// <remarks>
    /// DESIGN RULE: When background is Cyan, BrightCyan, Blue, or BrightBlue,
    /// foreground MUST be Black. No exceptions.
    /// </remarks>
    public static IEnumerable<(string Scheme, string Attribute, Color Foreground, Color Background)> ValidateBlueBackgroundRule()
    {
        var schemes = new (string Name, ColorScheme Scheme)[]
        {
            (nameof(Default), Default),
            (nameof(Focused), Focused),
            (nameof(TextInput), TextInput),
            (nameof(ReadOnlyText), ReadOnlyText),
            (nameof(FileDialog), FileDialog),
            (nameof(StatusBar_Production), StatusBar_Production),
            (nameof(StatusBar_Sandbox), StatusBar_Sandbox),
            (nameof(StatusBar_Development), StatusBar_Development),
            (nameof(StatusBar_Trial), StatusBar_Trial),
            (nameof(StatusBar_Default), StatusBar_Default),
            (nameof(MenuBar), MenuBar),
            (nameof(TabActive), TabActive),
            (nameof(TabInactive), TabInactive),
            (nameof(TableHeader), TableHeader),
            (nameof(Selected), Selected),
            (nameof(Error), Error),
            (nameof(Success), Success)
        };

        foreach (var (name, scheme) in schemes)
        {
            foreach (var violation in CheckScheme(name, scheme))
            {
                yield return violation;
            }
        }
    }

    private static IEnumerable<(string, string, Color, Color)> CheckScheme(string schemeName, ColorScheme scheme)
    {
        var attributes = new (string Name, Terminal.Gui.Attribute Attr)[]
        {
            ("Normal", scheme.Normal),
            ("Focus", scheme.Focus),
            ("HotNormal", scheme.HotNormal),
            ("HotFocus", scheme.HotFocus),
            ("Disabled", scheme.Disabled)
        };

        foreach (var (attrName, attr) in attributes)
        {
            var bg = attr.Background;
            var fg = attr.Foreground;

            if (BlueBackgrounds.Contains(bg) && fg != Color.Black)
            {
                yield return (schemeName, attrName, fg, bg);
            }
        }
    }

    #endregion
}
