using PPDS.Auth.Profiles;
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
/// DESIGN RULE - CYAN BACKGROUNDS: When background is Cyan or BrightCyan,
/// foreground MUST be Black. White/grey text is unreadable on cyan backgrounds.
/// Blue/BrightBlue backgrounds use White foreground for readability.
/// Use <see cref="ValidateCyanBackgroundRule"/> in unit tests to enforce this rule.
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
    /// Safe theme - black on green.
    /// </summary>
    public static ColorScheme StatusBar_Development => new()
    {
        Normal = MakeAttr(Color.Black, Color.Green),
        Focus = MakeAttr(Color.Black, Color.BrightGreen),
        HotNormal = MakeAttr(Color.White, Color.Green),
        HotFocus = MakeAttr(Color.White, Color.BrightGreen),
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

    /// <summary>
    /// Gets the tab color scheme for the given environment type and active state.
    /// Active tabs use white text on dark gray. Inactive tabs use environment-tinted
    /// foreground on black to visually distinguish environments.
    /// </summary>
    public static ColorScheme GetTabScheme(EnvironmentType envType, bool isActive)
    {
        if (isActive) return TabActive;

        var fg = envType switch
        {
            EnvironmentType.Production => Color.Red,
            EnvironmentType.Sandbox => Color.Brown,
            EnvironmentType.Development => Color.Green,
            EnvironmentType.Trial => Color.Cyan,
            _ => Color.Gray
        };

        // For Trial (Cyan foreground), use Black background per blue rule
        // But Cyan foreground on Black background is fine (rule is about Cyan *background*)
        return new ColorScheme
        {
            Normal = MakeAttr(fg, Color.Black),
            Focus = MakeAttr(Color.White, Color.Black),
            HotNormal = MakeAttr(fg, Color.Black),
            HotFocus = MakeAttr(Color.White, Color.Black),
            Disabled = MakeAttr(Color.DarkGray, Color.Black)
        };
    }

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
    /// Gets the status bar color scheme for a user-configured environment color.
    /// </summary>
    public static ColorScheme GetStatusBarScheme(EnvironmentColor envColor) => envColor switch
    {
        EnvironmentColor.Red => StatusBar_Production,
        EnvironmentColor.Brown => StatusBar_Sandbox,
        EnvironmentColor.Green => StatusBar_Development,
        EnvironmentColor.Cyan => StatusBar_Trial,
        EnvironmentColor.Yellow => new ColorScheme
        {
            Normal = MakeAttr(Color.Black, Color.BrightYellow),
            Focus = MakeAttr(Color.Black, Color.BrightYellow),
            HotNormal = MakeAttr(Color.Red, Color.BrightYellow),
            HotFocus = MakeAttr(Color.Red, Color.BrightYellow),
            Disabled = MakeAttr(Color.DarkGray, Color.BrightYellow)
        },
        EnvironmentColor.Blue => new ColorScheme
        {
            Normal = MakeAttr(Color.White, Color.Blue),
            Focus = MakeAttr(Color.White, Color.BrightBlue),
            HotNormal = MakeAttr(Color.BrightCyan, Color.Blue),
            HotFocus = MakeAttr(Color.BrightCyan, Color.BrightBlue),
            Disabled = MakeAttr(Color.Gray, Color.Blue)
        },
        EnvironmentColor.Gray => StatusBar_Default,
        EnvironmentColor.BrightRed => new ColorScheme
        {
            Normal = MakeAttr(Color.White, Color.BrightRed),
            Focus = MakeAttr(Color.White, Color.BrightRed),
            HotNormal = MakeAttr(Color.BrightYellow, Color.BrightRed),
            HotFocus = MakeAttr(Color.BrightYellow, Color.BrightRed),
            Disabled = MakeAttr(Color.Gray, Color.BrightRed)
        },
        EnvironmentColor.BrightGreen => new ColorScheme
        {
            Normal = MakeAttr(Color.Black, Color.BrightGreen),
            Focus = MakeAttr(Color.Black, Color.BrightGreen),
            HotNormal = MakeAttr(Color.Black, Color.BrightGreen),
            HotFocus = MakeAttr(Color.Black, Color.BrightGreen),
            Disabled = MakeAttr(Color.DarkGray, Color.BrightGreen)
        },
        EnvironmentColor.BrightYellow => new ColorScheme
        {
            Normal = MakeAttr(Color.Black, Color.BrightYellow),
            Focus = MakeAttr(Color.Black, Color.BrightYellow),
            HotNormal = MakeAttr(Color.Red, Color.BrightYellow),
            HotFocus = MakeAttr(Color.Red, Color.BrightYellow),
            Disabled = MakeAttr(Color.DarkGray, Color.BrightYellow)
        },
        EnvironmentColor.BrightCyan => new ColorScheme
        {
            Normal = MakeAttr(Color.Black, Color.BrightCyan),
            Focus = MakeAttr(Color.Black, Color.BrightCyan),
            HotNormal = MakeAttr(Color.Black, Color.BrightCyan),
            HotFocus = MakeAttr(Color.Black, Color.BrightCyan),
            Disabled = MakeAttr(Color.Black, Color.BrightCyan)
        },
        EnvironmentColor.BrightBlue => new ColorScheme
        {
            Normal = MakeAttr(Color.White, Color.BrightBlue),
            Focus = MakeAttr(Color.White, Color.BrightBlue),
            HotNormal = MakeAttr(Color.BrightCyan, Color.BrightBlue),
            HotFocus = MakeAttr(Color.BrightCyan, Color.BrightBlue),
            Disabled = MakeAttr(Color.Gray, Color.BrightBlue)
        },
        EnvironmentColor.White => new ColorScheme
        {
            Normal = MakeAttr(Color.Black, Color.White),
            Focus = MakeAttr(Color.Black, Color.White),
            HotNormal = MakeAttr(Color.Blue, Color.White),
            HotFocus = MakeAttr(Color.Blue, Color.White),
            Disabled = MakeAttr(Color.DarkGray, Color.White)
        },
        _ => StatusBar_Default
    };

    /// <summary>
    /// Maps EnvironmentColor to Terminal.Gui foreground Color (for tab tinting).
    /// </summary>
    public static Color GetForegroundColor(EnvironmentColor envColor) => envColor switch
    {
        EnvironmentColor.Red => Color.Red,
        EnvironmentColor.Green => Color.Green,
        EnvironmentColor.Yellow => Color.BrightYellow,
        EnvironmentColor.Cyan => Color.Cyan,
        EnvironmentColor.Blue => Color.Blue,
        EnvironmentColor.Gray => Color.Gray,
        EnvironmentColor.Brown => Color.Brown,
        EnvironmentColor.BrightRed => Color.BrightRed,
        EnvironmentColor.BrightGreen => Color.BrightGreen,
        EnvironmentColor.BrightYellow => Color.BrightYellow,
        EnvironmentColor.BrightCyan => Color.BrightCyan,
        EnvironmentColor.BrightBlue => Color.BrightBlue,
        EnvironmentColor.White => Color.White,
        _ => Color.Gray
    };

    /// <summary>
    /// Gets the tab color scheme using EnvironmentColor.
    /// </summary>
    public static ColorScheme GetTabScheme(EnvironmentColor envColor, bool isActive)
    {
        if (isActive) return TabActive;

        var fg = GetForegroundColor(envColor);
        return new ColorScheme
        {
            Normal = MakeAttr(fg, Color.Black),
            Focus = MakeAttr(Color.White, Color.Black),
            HotNormal = MakeAttr(fg, Color.Black),
            HotFocus = MakeAttr(Color.White, Color.Black),
            Disabled = MakeAttr(Color.DarkGray, Color.Black)
        };
    }

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
    /// Cyan background colors that require black foreground.
    /// Blue/BrightBlue use white foreground instead.
    /// </summary>
    private static readonly Color[] CyanBackgrounds = { Color.Cyan, Color.BrightCyan };

    /// <summary>
    /// Validates all color schemes follow the cyan background rule.
    /// Returns a list of violations (scheme name, attribute name, foreground, background).
    /// Used in unit tests to prevent regressions.
    /// </summary>
    /// <remarks>
    /// DESIGN RULE: When background is Cyan or BrightCyan,
    /// foreground MUST be Black. Blue/BrightBlue use White foreground.
    /// </remarks>
    public static IEnumerable<(string Scheme, string Attribute, Color Foreground, Color Background)> ValidateCyanBackgroundRule()
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

            if (CyanBackgrounds.Contains(bg) && fg != Color.Black)
            {
                yield return (schemeName, attrName, fg, bg);
            }
        }
    }

    #endregion
}
