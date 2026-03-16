namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Specifies which release track to target for self-update.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Stay on current track (stable→stable, pre-release→pre-release).</summary>
    Current,

    /// <summary>Force update to latest stable version.</summary>
    Stable,

    /// <summary>Force update to latest pre-release version.</summary>
    PreRelease
}
