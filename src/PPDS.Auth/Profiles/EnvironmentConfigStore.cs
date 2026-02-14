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

    /// <summary>Creates a store using the default environments file path.</summary>
    public EnvironmentConfigStore() : this(ProfilePaths.EnvironmentsFile) { }

    /// <summary>Creates a store using a custom file path.</summary>
    public EnvironmentConfigStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>Gets the file path used for persistent storage.</summary>
    public string FilePath => _filePath;

    /// <summary>Loads the environment config collection, using cache if available.</summary>
    public async Task<EnvironmentConfigCollection> LoadAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
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

    /// <summary>Persists the environment config collection to disk.</summary>
    public async Task SaveAsync(EnvironmentConfigCollection collection, CancellationToken ct = default)
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
        var collection = await LoadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);
        return collection.Environments.FirstOrDefault(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);
    }

    /// <summary>
    /// Saves or updates config for a specific environment. Merges non-null fields.
    /// </summary>
    public async Task<EnvironmentConfig> SaveConfigAsync(
        string url, string? label = null, EnvironmentType? type = null, EnvironmentColor? color = null,
        bool clearColor = false, string? discoveredType = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var collection = await LoadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);

        var existing = collection.Environments.FirstOrDefault(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);

        if (existing != null)
        {
            if (label != null) existing.Label = label == "" ? null : label;
            if (type != null) existing.Type = type;
            if (color != null) existing.Color = color;
            else if (clearColor) existing.Color = null;
            if (discoveredType != null) existing.DiscoveredType = discoveredType;
        }
        else
        {
            existing = new EnvironmentConfig
            {
                Url = normalized,
                Label = label,
                Type = type,
                Color = color,
                DiscoveredType = discoveredType
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
        ThrowIfDisposed();
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

    /// <summary>Clears the in-memory cache, forcing a reload from disk on next access.</summary>
    public void ClearCache()
    {
        ThrowIfDisposed();
        _lock.Wait();
        try { _cached = null; }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _lock.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
