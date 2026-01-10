using PPDS.Cli.Services.History;

namespace PPDS.Cli.Tests.Mocks;

/// <summary>
/// Fake implementation of <see cref="IQueryHistoryService"/> for testing.
/// </summary>
public sealed class FakeQueryHistoryService : IQueryHistoryService
{
    private readonly List<QueryHistoryEntry> _entries = new();
    private int _nextId = 1;

    /// <summary>
    /// Gets all entries added to history.
    /// </summary>
    public IReadOnlyList<QueryHistoryEntry> Entries => _entries;

    /// <inheritdoc />
    public Task<QueryHistoryEntry> AddQueryAsync(
        string environmentUrl,
        string sql,
        int? rowCount = null,
        long? executionTimeMs = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new QueryHistoryEntry
        {
            Id = (_nextId++).ToString(),
            Sql = sql,
            RowCount = rowCount,
            ExecutionTimeMs = executionTimeMs,
            ExecutedAt = DateTimeOffset.UtcNow
        };
        _entries.Add(entry);
        return Task.FromResult(entry);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueryHistoryEntry>> GetHistoryAsync(
        string environmentUrl,
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        var result = _entries
            .OrderByDescending(e => e.ExecutedAt)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<QueryHistoryEntry>>(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueryHistoryEntry>> SearchHistoryAsync(
        string environmentUrl,
        string pattern,
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        var result = _entries
            .Where(e => e.Sql.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ExecutedAt)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<QueryHistoryEntry>>(result);
    }

    /// <inheritdoc />
    public Task<QueryHistoryEntry?> GetEntryByIdAsync(
        string environmentUrl,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == entryId);
        return Task.FromResult(entry);
    }

    /// <inheritdoc />
    public Task<bool> DeleteEntryAsync(
        string environmentUrl,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == entryId);
        if (entry != null)
        {
            _entries.Remove(entry);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task ClearHistoryAsync(string environmentUrl, CancellationToken cancellationToken = default)
    {
        _entries.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resets the fake service state.
    /// </summary>
    public void Reset()
    {
        _entries.Clear();
        _nextId = 1;
    }
}
