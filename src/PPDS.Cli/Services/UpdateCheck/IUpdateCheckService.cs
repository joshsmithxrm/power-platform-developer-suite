namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Service for checking available updates and performing self-update of the PPDS CLI tool.
/// All cache format knowledge, TTL logic, and file I/O are owned by this service (A1).
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>
    /// Synchronously reads the cached update check result.
    /// Returns <see langword="null"/> if the cache is missing, expired (&gt;24h), or corrupt.
    /// Never throws.
    /// </summary>
    UpdateCheckResult? GetCachedResult();

    /// <summary>
    /// Queries the NuGet flat-container API for available versions and returns the result.
    /// Updates the cache on success. Returns <see langword="null"/> on network failure.
    /// </summary>
    Task<UpdateCheckResult?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires a best-effort background task to refresh the cache when it is stale.
    /// Intentionally takes no <see cref="CancellationToken"/> — fire-and-forget by design (R2).
    /// </summary>
    void RefreshCacheInBackgroundIfStale(string currentVersion);

    /// <summary>
    /// Performs a self-update of the PPDS CLI tool.
    /// Returns <see cref="UpdateResult"/> for expected outcomes (non-global install, already current).
    /// Throws <see cref="Infrastructure.Errors.PpdsException"/> for unexpected failures (dotnet not found).
    /// </summary>
    Task<UpdateResult> UpdateAsync(UpdateChannel channel, CancellationToken cancellationToken = default);
}
