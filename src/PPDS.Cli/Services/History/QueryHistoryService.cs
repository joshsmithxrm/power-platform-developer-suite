using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PPDS.Cli.Services.History;

/// <summary>
/// Application service for managing SQL query history with file persistence.
/// </summary>
/// <remarks>
/// History is stored per-environment in ~/.ppds/history/{environment-hash}.json.
/// See ADR-0015 and ADR-0016 for architectural context.
/// </remarks>
public sealed class QueryHistoryService : IQueryHistoryService
{
    private const int MaxHistorySize = 200;
    private const string HistoryDirectoryName = "history";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _basePath;
    private readonly ILogger<QueryHistoryService> _logger;

    /// <summary>
    /// Creates a new query history service.
    /// </summary>
    public QueryHistoryService(ILogger<QueryHistoryService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _basePath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".ppds",
            HistoryDirectoryName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryHistoryEntry>> GetHistoryAsync(
        string environmentUrl,
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        var history = await LoadHistoryAsync(environmentUrl, cancellationToken);
        return history.Take(count).ToList();
    }

    /// <inheritdoc />
    public async Task<QueryHistoryEntry> AddQueryAsync(
        string environmentUrl,
        string sql,
        int? rowCount = null,
        long? executionTimeMs = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL query cannot be empty.", nameof(sql));
        }

        var history = await LoadHistoryAsync(environmentUrl, cancellationToken);

        // Check for duplicate (same query normalized)
        var normalized = NormalizeQuery(sql);
        var existingIndex = history.FindIndex(e => NormalizeQuery(e.Sql) == normalized);
        if (existingIndex >= 0)
        {
            // Remove existing to re-add at front with updated metadata
            history.RemoveAt(existingIndex);
        }

        var entry = new QueryHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Sql = sql.Trim(),
            ExecutedAt = DateTimeOffset.UtcNow,
            RowCount = rowCount,
            ExecutionTimeMs = executionTimeMs,
            Success = true
        };

        history.Insert(0, entry);

        // Trim to max size
        while (history.Count > MaxHistorySize)
        {
            history.RemoveAt(history.Count - 1);
        }

        await SaveHistoryAsync(environmentUrl, history, cancellationToken);

        _logger.LogDebug("Added query to history for {Environment}", GetDisplayUrl(environmentUrl));

        return entry;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryHistoryEntry>> SearchHistoryAsync(
        string environmentUrl,
        string pattern,
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return await GetHistoryAsync(environmentUrl, count, cancellationToken);
        }

        var history = await LoadHistoryAsync(environmentUrl, cancellationToken);

        return history
            .Where(e => e.Sql.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .Take(count)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<QueryHistoryEntry?> GetEntryByIdAsync(
        string environmentUrl,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        var history = await LoadHistoryAsync(environmentUrl, cancellationToken);
        return history.FirstOrDefault(e => e.Id == entryId);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteEntryAsync(
        string environmentUrl,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        var history = await LoadHistoryAsync(environmentUrl, cancellationToken);
        var index = history.FindIndex(e => e.Id == entryId);

        if (index < 0)
        {
            return false;
        }

        history.RemoveAt(index);
        await SaveHistoryAsync(environmentUrl, history, cancellationToken);

        _logger.LogDebug("Deleted history entry {EntryId} for {Environment}", entryId, GetDisplayUrl(environmentUrl));

        return true;
    }

    /// <inheritdoc />
    public async Task ClearHistoryAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetHistoryFilePath(environmentUrl);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("Cleared history for {Environment}", GetDisplayUrl(environmentUrl));
        }
    }

    #region Private Helpers

    private async Task<List<QueryHistoryEntry>> LoadHistoryAsync(
        string environmentUrl,
        CancellationToken cancellationToken)
    {
        var filePath = GetHistoryFilePath(environmentUrl);

        if (!File.Exists(filePath))
        {
            return new List<QueryHistoryEntry>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var data = JsonSerializer.Deserialize<HistoryFileData>(json, JsonOptions);
            return data?.Entries?.ToList() ?? new List<QueryHistoryEntry>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse history file {FilePath}, starting fresh", filePath);
            return new List<QueryHistoryEntry>();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read history file {FilePath}", filePath);
            return new List<QueryHistoryEntry>();
        }
    }

    private async Task SaveHistoryAsync(
        string environmentUrl,
        List<QueryHistoryEntry> history,
        CancellationToken cancellationToken)
    {
        var filePath = GetHistoryFilePath(environmentUrl);
        var directory = Path.GetDirectoryName(filePath)!;

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var data = new HistoryFileData
        {
            EnvironmentUrl = environmentUrl,
            LastUpdated = DateTimeOffset.UtcNow,
            Entries = history.ToArray()
        };

        var json = JsonSerializer.Serialize(data, JsonOptions);

        // Write atomically by writing to temp file first
        var tempPath = filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, filePath, overwrite: true);
    }

    private string GetHistoryFilePath(string environmentUrl)
    {
        var hash = ComputeUrlHash(environmentUrl);
        return Path.Combine(_basePath, $"{hash}.json");
    }

    private static string ComputeUrlHash(string url)
    {
        // Normalize URL and compute short hash for filename
        var normalized = url.Trim().ToLowerInvariant().TrimEnd('/');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string NormalizeQuery(string query)
    {
        // Normalize for comparison: lowercase, collapse whitespace
        return string.Join(' ', query.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private static string GetDisplayUrl(string url)
    {
        // Extract just the hostname for logging
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }
        return url;
    }

    #endregion

    #region File Data Model

    private sealed class HistoryFileData
    {
        public string? EnvironmentUrl { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        public QueryHistoryEntry[] Entries { get; set; } = Array.Empty<QueryHistoryEntry>();
    }

    #endregion
}
