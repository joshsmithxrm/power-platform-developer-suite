namespace PPDS.Auth.Profiles;

/// <summary>
/// Classification of Dataverse environment types.
/// Drives protection levels and default color theming.
/// </summary>
public enum EnvironmentType
{
    /// <summary>Unknown or unconfigured — auto-detect from Discovery API or URL heuristics.</summary>
    Unknown,

    /// <summary>Production environment — DML blocked by default, requires confirmation with preview.</summary>
    Production,

    /// <summary>Sandbox/staging environment — unrestricted DML.</summary>
    Sandbox,

    /// <summary>Development environment — unrestricted DML.</summary>
    Development,

    /// <summary>Test/QA/UAT environment — unrestricted DML.</summary>
    Test,

    /// <summary>Trial environment — unrestricted DML.</summary>
    Trial
}
