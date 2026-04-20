namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Snapshot of the guard's current activation status, cached between
/// resolutions. Value type keeps the cache slot copy-friendly and
/// serialization-free.
/// </summary>
/// <param name="IsActive">
/// <c>true</c> when a shakedown signal (env var or fresh sentinel) is
/// currently present.
/// </param>
/// <param name="Source">
/// Human-readable activation source. <c>"env:PPDS_SHAKEDOWN"</c> when the
/// env var activated the guard, <c>"sentinel:&lt;rel-path&gt;"</c> when a
/// fresh sentinel file did. Empty string when <paramref name="IsActive"/>
/// is <c>false</c>.
/// </param>
/// <param name="SentinelRelativePath">
/// Project-root-relative path to the sentinel (forward slashes) when the
/// sentinel drove activation; <c>null</c> otherwise.
/// </param>
/// <param name="SentinelAge">
/// Age of the sentinel's <c>started_at</c> timestamp when sentinel-driven;
/// <c>null</c> otherwise.
/// </param>
internal readonly record struct ActivationState(
    bool IsActive,
    string Source,
    string? SentinelRelativePath,
    TimeSpan? SentinelAge);
