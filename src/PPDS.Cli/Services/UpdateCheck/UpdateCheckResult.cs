using System.Text.Json.Serialization;

namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Represents the result of checking for available PPDS CLI updates.
/// </summary>
public sealed record UpdateCheckResult
{
    /// <summary>
    /// Gets the currently installed version of the PPDS CLI.
    /// </summary>
    public required string CurrentVersion { get; init; }

    /// <summary>
    /// Gets the latest stable version available on NuGet, or null if unable to determine.
    /// </summary>
    public string? LatestStableVersion { get; init; }

    /// <summary>
    /// Gets the latest pre-release version available on NuGet, or null if unable to determine.
    /// </summary>
    public string? LatestPreReleaseVersion { get; init; }

    /// <summary>
    /// Gets a value indicating whether a stable update is available.
    /// </summary>
    public bool StableUpdateAvailable { get; init; }

    /// <summary>
    /// Gets a value indicating whether a pre-release update is available.
    /// </summary>
    public bool PreReleaseUpdateAvailable { get; init; }

    /// <summary>
    /// Gets the primary update command (stable preferred), or null if already up-to-date.
    /// </summary>
    public string? UpdateCommand { get; init; }

    /// <summary>
    /// Gets the pre-release update command when a pre-release update is available, or null when no pre-release update exists.
    /// </summary>
    public string? PreReleaseUpdateCommand { get; init; }

    /// <summary>
    /// Gets the timestamp when this check was performed.
    /// </summary>
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>
    /// Gets a value indicating whether any update (stable or pre-release) is available.
    /// </summary>
    [JsonIgnore]
    public bool UpdateAvailable => StableUpdateAvailable || PreReleaseUpdateAvailable;
}
