using System.Text.Json;
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Reads the cached update-check result at startup and produces a one-liner notification message.
/// Also fires a background cache refresh so the next startup has fresh data.
/// </summary>
/// <remarks>
/// All methods are static and synchronous (except the fire-and-forget background refresh).
/// The cache file is a small JSON document — reads are sub-millisecond and impose no
/// perceptible startup latency.
/// </remarks>
public static class StartupUpdateNotifier
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Default path to the update-check cache file: <c>~/.ppds/update-check.json</c>.
    /// </summary>
    private static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ppds",
        "update-check.json");

    /// <summary>
    /// Reads the cached update-check result and returns a formatted notification message,
    /// or <see langword="null"/> if no update is available, the cache is missing, expired, or corrupt.
    /// </summary>
    /// <param name="cachePath">
    /// Optional override for the cache file path. Pass <see langword="null"/> to use the default
    /// <c>~/.ppds/update-check.json</c>.
    /// </param>
    /// <returns>
    /// A message such as
    /// <c>"Update available: 0.6.0 (run: dotnet tool update PPDS.Cli -g)"</c>,
    /// or <see langword="null"/>.
    /// </returns>
    public static string? GetNotificationMessage(string? cachePath = null)
    {
        try
        {
            var path = cachePath ?? DefaultCachePath;

            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOptions);

            if (result is null)
                return null;

            // Honour the same 24-hour TTL as UpdateCheckService
            if (DateTimeOffset.UtcNow - result.CheckedAt > CacheTtl)
                return null;

            if (!result.UpdateAvailable)
                return null;

            // Prefer stable version in the message; fall back to pre-release
            var latestVersion = result.StableUpdateAvailable
                ? result.LatestStableVersion
                : result.LatestPreReleaseVersion;

            if (latestVersion is null || result.UpdateCommand is null)
                return null;

            return $"Update available: {latestVersion} (run: {result.UpdateCommand})";
        }
        catch
        {
            // Never propagate exceptions — broken paths, permissions, corrupt JSON, etc.
            return null;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the update notification should be shown.
    /// Returns <see langword="false"/> when <c>--quiet</c> or <c>-q</c> is present in <paramref name="args"/>.
    /// </summary>
    /// <param name="args">The raw command-line arguments (before System.CommandLine parsing).</param>
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

    /// <summary>
    /// Fires a best-effort background <see cref="Task"/> to refresh the update cache when
    /// the existing cache is stale (older than 24 hours) or missing.
    /// All exceptions are swallowed — this must never crash the CLI.
    /// </summary>
    /// <param name="currentVersion">The currently installed CLI version.</param>
    /// <param name="cachePath">
    /// Optional override for the cache file path (used in tests).
    /// Pass <see langword="null"/> to use the default <c>~/.ppds/update-check.json</c>.
    /// </param>
    public static void RefreshCacheInBackground(string currentVersion, string? cachePath = null)
    {
        try
        {
            var path = cachePath ?? DefaultCachePath;

            // Check cache age before firing the background task — skip if fresh
            if (IsCacheFresh(path))
                return;

            // Fire-and-forget: no await, exceptions swallowed inside the task
            _ = Task.Run(async () =>
            {
                try
                {
                    var svc = new UpdateCheckService(cachePath: path);
                    await svc.CheckAsync(currentVersion).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort — never surface to the caller
                }
            });
        }
        catch
        {
            // Swallow filesystem errors in cache-age check
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the cache file exists and was written within the last 24 hours.
    /// </summary>
    private static bool IsCacheFresh(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOptions);

            if (result is null)
                return false;

            return DateTimeOffset.UtcNow - result.CheckedAt <= CacheTtl;
        }
        catch
        {
            return false;
        }
    }
}
