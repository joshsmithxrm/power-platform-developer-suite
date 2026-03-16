namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Represents the outcome of a self-update attempt.
/// </summary>
/// <remarks>
/// Expected outcomes (non-global install, already current) are returned as results.
/// Unexpected failures (dotnet not found, spawn failed) throw <see cref="Infrastructure.Errors.PpdsException"/>.
/// </remarks>
public sealed record UpdateResult
{
    /// <summary>Whether the update was successfully initiated.</summary>
    public bool Success { get; init; }

    /// <summary>The version being installed, if known.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Error message for non-fatal failures.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when PPDS is installed as a local tool (not global).</summary>
    public bool IsNonGlobalInstall { get; init; }

    /// <summary>Manual update command to show when automation isn't possible.</summary>
    public string? ManualCommand { get; init; }
}
