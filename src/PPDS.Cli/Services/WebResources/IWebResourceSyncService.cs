using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Services.WebResources;

/// <summary>
/// Application service that orchestrates pull and push of web resources between
/// Dataverse and a local folder. Owns tracking-file management, parallel I/O,
/// and conflict detection (Constitution A1, A2).
/// </summary>
public interface IWebResourceSyncService
{
    /// <summary>
    /// Lists web resources, applies filters, downloads text content in parallel,
    /// writes files to disk, and persists the tracking file.
    /// </summary>
    Task<PullResult> PullAsync(PullOptions options, IOperationProgress? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the tracking file, detects local edits, checks for server conflicts,
    /// uploads modified text resources in parallel, and updates the tracking file.
    /// </summary>
    Task<PushResult> PushAsync(PushOptions options, IOperationProgress? progress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Inputs for <see cref="IWebResourceSyncService.PullAsync"/>.
/// </summary>
/// <param name="Folder">Target directory (created if missing).</param>
/// <param name="EnvironmentUrl">Environment URL of the current connection (recorded in tracking file).</param>
/// <param name="SolutionId">Optional solution component filter.</param>
/// <param name="SolutionUniqueName">Optional solution unique name (for tracking-file display).</param>
/// <param name="TypeCodes">Optional client-side type-code filter (1-12).</param>
/// <param name="NamePattern">Optional partial-name filter (substring match).</param>
/// <param name="StripPrefix">If true, removes the publisher prefix from local paths.</param>
/// <param name="Force">If true, overwrites locally modified files.</param>
public sealed record PullOptions(
    string Folder,
    string EnvironmentUrl,
    Guid? SolutionId,
    string? SolutionUniqueName,
    int[]? TypeCodes,
    string? NamePattern,
    bool StripPrefix,
    bool Force);

/// <summary>
/// Inputs for <see cref="IWebResourceSyncService.PushAsync"/>.
/// </summary>
/// <param name="Folder">Folder containing pulled web resources.</param>
/// <param name="CurrentEnvironmentUrl">Environment URL of the current connection.</param>
/// <param name="Force">If true, skips conflict detection and environment URL validation.</param>
/// <param name="DryRun">If true, reports what would be pushed without making changes.</param>
/// <param name="Publish">If true, publishes successfully uploaded resources after upload.</param>
public sealed record PushOptions(
    string Folder,
    string CurrentEnvironmentUrl,
    bool Force,
    bool DryRun,
    bool Publish);

/// <summary>Outcome of a pull operation.</summary>
/// <param name="TotalServerCount">Total resources returned by the server (pre-filter) — for I4 visibility.</param>
/// <param name="Pulled">Resources successfully written to disk.</param>
/// <param name="Skipped">Resources skipped (locally modified, binary, etc.).</param>
/// <param name="Errors">Resources that failed (e.g., path traversal).</param>
public sealed record PullResult(
    int TotalServerCount,
    IReadOnlyList<PulledResource> Pulled,
    IReadOnlyList<SkippedResource> Skipped,
    IReadOnlyList<ErrorResource> Errors);

/// <summary>A resource that was downloaded and written.</summary>
/// <param name="Name">The Dataverse web resource name.</param>
/// <param name="LocalPath">The local file path written.</param>
/// <param name="IsNew">True if the file did not exist before this pull.</param>
public sealed record PulledResource(string Name, string LocalPath, bool IsNew);

/// <summary>A resource that was skipped for a non-error reason.</summary>
public sealed record SkippedResource(string Name, string Reason);

/// <summary>A resource that errored during processing.</summary>
public sealed record ErrorResource(string Name, string Error);

/// <summary>Outcome of a push operation.</summary>
/// <param name="Pushed">Resources successfully uploaded (or that would be in dry-run).</param>
/// <param name="Conflicts">Conflicts detected: server modifiedOn differs from tracked.</param>
/// <param name="Skipped">Resources skipped (unchanged, binary, missing, etc.).</param>
/// <param name="Errors">Resources that errored during the push (per-item upload, refresh, or publish failures).</param>
/// <param name="DryRun">True if this was a dry-run (no mutations applied).</param>
/// <param name="PublishedCount">Number of resources published (only when Publish=true and not DryRun).</param>
public sealed record PushResult(
    IReadOnlyList<PushedResource> Pushed,
    IReadOnlyList<ConflictResource> Conflicts,
    IReadOnlyList<SkippedResource> Skipped,
    IReadOnlyList<ErrorResource> Errors,
    bool DryRun,
    int PublishedCount);

/// <summary>A resource that was uploaded.</summary>
public sealed record PushedResource(string Name, string LocalPath);

/// <summary>A resource where the server has changed since the tracked baseline.</summary>
public sealed record ConflictResource(string Name, DateTime? TrackedModifiedOn, DateTime? ServerModifiedOn);
