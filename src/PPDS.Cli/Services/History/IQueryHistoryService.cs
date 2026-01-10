namespace PPDS.Cli.Services.History;

/// <summary>
/// Application service for managing SQL query history.
/// </summary>
/// <remarks>
/// This service provides per-environment query history persistence.
/// History is stored in ~/.ppds/history/{environment-hash}.json.
/// See ADR-0015 and ADR-0016 for architectural context.
/// </remarks>
public interface IQueryHistoryService
{
    /// <summary>
    /// Gets recent queries for an environment.
    /// </summary>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="count">Maximum number of entries to return (default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of history entries, most recent first.</returns>
    Task<IReadOnlyList<QueryHistoryEntry>> GetHistoryAsync(
        string environmentUrl,
        int count = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a query to history.
    /// </summary>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="sql">The SQL query.</param>
    /// <param name="rowCount">Number of rows returned (optional).</param>
    /// <param name="executionTimeMs">Execution time in milliseconds (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created history entry.</returns>
    Task<QueryHistoryEntry> AddQueryAsync(
        string environmentUrl,
        string sql,
        int? rowCount = null,
        long? executionTimeMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches history for matching queries.
    /// </summary>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="pattern">Search pattern (case-insensitive substring match).</param>
    /// <param name="count">Maximum number of entries to return (default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching history entries, most recent first.</returns>
    Task<IReadOnlyList<QueryHistoryEntry>> SearchHistoryAsync(
        string environmentUrl,
        string pattern,
        int count = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific history entry by ID.
    /// </summary>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="entryId">The entry ID to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The history entry, or null if not found.</returns>
    Task<QueryHistoryEntry?> GetEntryByIdAsync(
        string environmentUrl,
        string entryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a specific history entry.
    /// </summary>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="entryId">The entry ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if entry was deleted, false if not found.</returns>
    Task<bool> DeleteEntryAsync(
        string environmentUrl,
        string entryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all history for an environment.
    /// </summary>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearHistoryAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single query history entry.
/// </summary>
public sealed record QueryHistoryEntry
{
    /// <summary>
    /// Unique identifier for this entry.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The SQL query text.
    /// </summary>
    public required string Sql { get; init; }

    /// <summary>
    /// When the query was executed.
    /// </summary>
    public required DateTimeOffset ExecutedAt { get; init; }

    /// <summary>
    /// Number of rows returned (null if unknown).
    /// </summary>
    public int? RowCount { get; init; }

    /// <summary>
    /// Execution time in milliseconds (null if unknown).
    /// </summary>
    public long? ExecutionTimeMs { get; init; }

    /// <summary>
    /// Whether this was a successful query.
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// Error message if query failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
