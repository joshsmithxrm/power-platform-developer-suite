using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Manages persistent storage of environment configurations.
/// </summary>
public sealed class EnvironmentConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private EnvironmentConfigCollection? _cached;
    private bool _disposed;

    public EnvironmentConfigStore() : this(ProfilePaths.EnvironmentsFile) { }

    public EnvironmentConfigStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string FilePath => _filePath;

    public async Task<EnvironmentConfigCollection> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached != null) return _cached;

            if (!File.Exists(_filePath))
            {
                _cached = new EnvironmentConfigCollection();
                return _cached;
            }

            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            _cached = JsonSerializer.Deserialize<EnvironmentConfigCollection>(json, JsonOptions)
                ?? new EnvironmentConfigCollection();
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(EnvironmentConfigCollection collection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ProfilePaths.EnsureDirectoryExists();
            collection.Version = 1;
            var json = JsonSerializer.Serialize(collection, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, ct).ConfigureAwait(false);
            _cached = collection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets the config for a specific environment URL, or null if not configured.
    /// </summary>
    public async Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default)
    {
        var collection = await LoadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);
        return collection.Environments.FirstOrDefault(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);
    }

    /// <summary>
    /// Saves or updates config for a specific environment. Merges non-null fields.
    /// </summary>
    public async Task<EnvironmentConfig> SaveConfigAsync(
        string url, string? label = null, string? type = null, EnvironmentColor? color = null,
        CancellationToken ct = default)
    {
        var collection = await LoadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);

        var existing = collection.Environments.FirstOrDefault(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);

        if (existing != null)
        {
            if (label != null) existing.Label = label;
            if (type != null) existing.Type = type;
            if (color != null) existing.Color = color;
        }
        else
        {
            existing = new EnvironmentConfig
            {
                Url = normalized,
                Label = label,
                Type = type,
                Color = color
            };
            collection.Environments.Add(existing);
        }

        await SaveAsync(collection, ct).ConfigureAwait(false);
        return existing;
    }

    /// <summary>
    /// Removes config for a specific environment URL.
    /// </summary>
    public async Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default)
    {
        var collection = await LoadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);
        var removed = collection.Environments.RemoveAll(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);

        if (removed > 0)
        {
            await SaveAsync(collection, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public void ClearCache()
    {
        _lock.Wait();
        try { _cached = null; }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _lock.Dispose();
        _disposed = true;
    }
}
