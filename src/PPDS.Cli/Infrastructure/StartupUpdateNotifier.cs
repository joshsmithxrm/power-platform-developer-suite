using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Pure presentation logic for startup update notifications.
/// No file I/O, no cache knowledge — all data access is owned by <see cref="IUpdateCheckService"/>.
/// Track-based filtering for startup notifications happens here.
/// </summary>
public static class StartupUpdateNotifier
{
    /// <summary>
    /// Formats a human-readable update notification from a cached result.
    /// Applies track-based filtering: stable users see only stable updates at startup;
    /// pre-release users see both stable and pre-release.
    /// Returns <see langword="null"/> if no update is available or the result is null.
    /// </summary>
    public static string? FormatNotification(UpdateCheckResult? result)
    {
        if (result is null || !result.UpdateAvailable)
            return null;

        // Determine user's track from current version
        var isPreReleaseTrack = NuGetVersion.TryParse(result.CurrentVersion, out var current)
            && current!.IsOddMinor;

        // Pre-release track user with both updates — two lines
        if (isPreReleaseTrack
            && result.StableUpdateAvailable && result.PreReleaseUpdateAvailable
            && result.LatestStableVersion is not null
            && result.LatestPreReleaseVersion is not null
            && result.UpdateCommand is not null
            && result.PreReleaseUpdateCommand is not null)
        {
            return $"Update available: {result.LatestStableVersion} (run: {result.UpdateCommand})\n"
                 + $"Pre-release available: {result.LatestPreReleaseVersion} (run: {result.PreReleaseUpdateCommand})";
        }

        // Stable track user: only show stable update (even if pre-release exists in data model)
        if (!isPreReleaseTrack && result.StableUpdateAvailable
            && result.LatestStableVersion is not null && result.UpdateCommand is not null)
        {
            return $"Update available: {result.LatestStableVersion} (run: {result.UpdateCommand})";
        }

        // Pre-release track, single update available
        if (result.StableUpdateAvailable && result.LatestStableVersion is not null
            && result.UpdateCommand is not null)
        {
            return $"Update available: {result.LatestStableVersion} (run: {result.UpdateCommand})";
        }

        if (result.PreReleaseUpdateAvailable && result.LatestPreReleaseVersion is not null)
        {
            var cmd = result.PreReleaseUpdateCommand ?? result.UpdateCommand;
            if (cmd is not null)
                return $"Update available: {result.LatestPreReleaseVersion} (run: {cmd})";
        }

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the update notification should be shown.
    /// Returns <see langword="false"/> when <c>--quiet</c> or <c>-q</c> is present.
    /// Other suppression (--help, --version, version subcommand) is handled by
    /// Program.cs SkipVersionHeaderArgs — no duplication needed.
    /// </summary>
    public static bool ShouldShow(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-q", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
