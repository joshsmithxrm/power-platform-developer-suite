namespace PPDS.Auth.Profiles;

/// <summary>
/// Named colors for environment theming.
/// Maps to 16-color terminal palette (works in TUI and VS Code).
/// </summary>
public enum EnvironmentColor
{
    /// <summary>Red — typically used for Production environments.</summary>
    Red,
    /// <summary>Green — typically used for Development environments.</summary>
    Green,
    /// <summary>Yellow — typically used for Test environments.</summary>
    Yellow,
    /// <summary>Cyan — typically used for Trial environments.</summary>
    Cyan,
    /// <summary>Blue.</summary>
    Blue,
    /// <summary>Gray — the default fallback color.</summary>
    Gray,
    /// <summary>Brown — typically used for Sandbox environments.</summary>
    Brown,
    /// <summary>White.</summary>
    White,
    /// <summary>Bright red.</summary>
    BrightRed,
    /// <summary>Bright green.</summary>
    BrightGreen,
    /// <summary>Bright yellow.</summary>
    BrightYellow,
    /// <summary>Bright cyan.</summary>
    BrightCyan,
    /// <summary>Bright blue.</summary>
    BrightBlue
}
