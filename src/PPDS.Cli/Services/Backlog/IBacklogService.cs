namespace PPDS.Cli.Services.Backlog;

/// <summary>
/// Application service for fetching and categorizing GitHub project backlog.
/// </summary>
/// <remarks>
/// <para>
/// This service fetches issues from GitHub Projects v2 and categorizes them
/// by type, status, priority, and readiness. Results are cached to avoid
/// repeated API calls.
/// </para>
/// <para>
/// The service integrates with <see cref="Session.ISessionService"/> to show
/// which issues have active worker sessions.
/// </para>
/// </remarks>
public interface IBacklogService
{
    /// <summary>
    /// Gets the backlog for a specific repository.
    /// </summary>
    /// <param name="owner">Repository owner (optional, detected from git remote).</param>
    /// <param name="repo">Repository name (optional, detected from git remote).</param>
    /// <param name="noCache">If true, bypass cache and fetch fresh data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Categorized backlog data.</returns>
    /// <remarks>
    /// If owner/repo are not specified, they are detected from the current git remote.
    /// Data is cached for 5 minutes unless noCache is true.
    /// </remarks>
    Task<BacklogData> GetBacklogAsync(
        string? owner = null,
        string? repo = null,
        bool noCache = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the backlog aggregated across all PPDS ecosystem repositories.
    /// </summary>
    /// <param name="noCache">If true, bypass cache and fetch fresh data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregated backlog across all repos.</returns>
    /// <remarks>
    /// Since the GitHub Project tracks all repos, this returns data from a single
    /// project query, filtered and grouped by repository.
    /// </remarks>
    Task<EcosystemBacklog> GetEcosystemBacklogAsync(
        bool noCache = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the backlog cache.
    /// </summary>
    void InvalidateCache();
}
