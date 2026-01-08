namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Classification of Dataverse environment types for theming purposes.
/// </summary>
public enum EnvironmentType
{
    /// <summary>Production environment - requires caution (red theme).</summary>
    Production,

    /// <summary>Sandbox/staging environment - moderate caution (yellow theme).</summary>
    Sandbox,

    /// <summary>Development environment - safe for experimentation (green theme).</summary>
    Development,

    /// <summary>Trial environment - temporary, limited (cyan theme).</summary>
    Trial,

    /// <summary>Unknown environment type - use neutral theme (dark gray).</summary>
    Unknown
}
