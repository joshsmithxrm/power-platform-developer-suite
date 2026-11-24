using System;
using System.Collections.Generic;
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
    /// <remarks>
    /// The cache can be older than the file when another process (or another store instance) has saved
    /// since. Reads tolerate that; every change (<see cref="SaveConfigAsync(string, string?, EnvironmentType?, EnvironmentColor?, bool, string?, string?, CancellationToken)"/>,
    /// <see cref="RemoveConfigAsync"/>, <see cref="UpdateAsync"/>) re-reads the file first, so a stale cache is
    /// never written back.
    /// </remarks>
    public async Task<EnvironmentConfigCollection> LoadAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cached ??= await ReadFromDiskAsync(ct).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reads and normalises the file, ignoring the cache. The caller holds <see cref="_lock"/>.
    /// </summary>
    private async Task<EnvironmentConfigCollection> ReadFromDiskAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new EnvironmentConfigCollection();

        var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
        var collection = JsonSerializer.Deserialize<EnvironmentConfigCollection>(json, JsonOptions)
            ?? new EnvironmentConfigCollection();

        // Dedup environments by normalized URL (keep first occurrence, merge profiles)
        var seen = new Dictionary<string, EnvironmentConfig>();
        foreach (var env in collection.Environments)
        {
            var key = EnvironmentConfig.NormalizeUrl(env.Url);
            if (seen.TryGetValue(key, out var existing))
            {
                // Merge profiles from duplicate into the kept entry
                foreach (var p in env.Profiles)
                {
                    if (!existing.Profiles.Contains(p, StringComparer.OrdinalIgnoreCase))
                        existing.Profiles.Add(p);
                }
            }
            else
            {
                env.Url = key; // Ensure stored URL is normalized
                seen[key] = env;
            }
        }
        if (seen.Count != collection.Environments.Count)
        {
            collection.Environments = seen.Values.ToList();
            // Persist the deduped version (within the lock)
            await WriteToDiskAsync(collection, ct).ConfigureAwait(false);
        }

        return collection;
    }

    /// <summary>Writes the collection. The caller holds <see cref="_lock"/>.</summary>
    private async Task WriteToDiskAsync(EnvironmentConfigCollection collection, CancellationToken ct)
    {
        ProfilePaths.EnsureDirectoryExists();
        collection.Version = 1;
        var json = JsonSerializer.Serialize(collection, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct).ConfigureAwait(false);
    }

    /// <summary>Persists the environment config collection to disk.</summary>
    public async Task SaveAsync(EnvironmentConfigCollection collection, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(collection);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteToDiskAsync(collection, ct).ConfigureAwait(false);
            _cached = collection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Re-reads the file, lets <paramref name="update"/> change the fresh collection, and writes it when
    /// <paramref name="update"/> returns true, all under this store's lock. Use it instead of
    /// <see cref="LoadAsync"/> followed by <see cref="SaveAsync"/>, which writes back whatever the cache held.
    /// </summary>
    /// <returns>The value <paramref name="update"/> returned: whether the collection was written.</returns>
    public async Task<bool> UpdateAsync(Func<EnvironmentConfigCollection, bool> update, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(update);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collection = await ReadFromDiskAsync(ct).ConfigureAwait(false);
            _cached = null; // update may throw after changing the fresh collection
            var changed = update(collection);
            if (changed)
                await WriteToDiskAsync(collection, ct).ConfigureAwait(false);
            _cached = collection;
            return changed;
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
    /// Optionally links the environment to a profile name.
    /// </summary>
    /// <remarks>
    /// Re-reads the file under this store's lock and changes only the fields given here on the freshly
    /// read entry, so a change another process saved meanwhile is kept, not overwritten from the cache.
    /// </remarks>
    public Task<EnvironmentConfig> SaveConfigAsync(
        string url, string? label = null, EnvironmentType? type = null, EnvironmentColor? color = null,
        bool clearColor = false, string? discoveredType = null, string? profileName = null,
        CancellationToken ct = default)
        => SaveConfigAsync(url, label, type, color, clearColor, discoveredType, profileName,
            validate: static (_, _) => { }, ct);

    /// <summary>
    /// Saves or updates config for a specific environment like the other overload, and calls
    /// <paramref name="validate"/> with the entry as freshly read from the file (null when there is none)
    /// and the entry about to be written, before writing. If <paramref name="validate"/> throws, nothing is
    /// written.
    /// </summary>
    /// <remarks>
    /// The read, the check and the write happen under this store's lock, so no other caller of this store
    /// instance can change the entry in between. The lock is in-process only: another process that saves
    /// the same file in the same moment is not excluded, and the later write wins.
    /// </remarks>
    public async Task<EnvironmentConfig> SaveConfigAsync(
        string url, string? label, EnvironmentType? type, EnvironmentColor? color,
        bool clearColor, string? discoveredType, string? profileName,
        Action<EnvironmentConfig?, EnvironmentConfig> validate,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(validate);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collection = await ReadFromDiskAsync(ct).ConfigureAwait(false);
            _cached = null; // validate may throw after the fresh collection was changed below
            var normalized = EnvironmentConfig.NormalizeUrl(url);

            var existing = collection.Environments.FirstOrDefault(
                e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);
            var before = existing is null ? null : Copy(existing);

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

            // Link to profile if specified
            if (!string.IsNullOrWhiteSpace(profileName) &&
                !existing.Profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                existing.Profiles.Add(profileName);
            }

            validate(before, Copy(existing));

            await WriteToDiskAsync(collection, ct).ConfigureAwait(false);
            _cached = collection;
            return existing;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes config for a specific environment URL. Re-reads the file under this store's lock first, so
    /// the rest of the file is written back as it is on disk, not from the cache.
    /// </summary>
    public async Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collection = await ReadFromDiskAsync(ct).ConfigureAwait(false);
            var normalized = EnvironmentConfig.NormalizeUrl(url);
            var removed = collection.Environments.RemoveAll(
                e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);

            if (removed > 0)
                await WriteToDiskAsync(collection, ct).ConfigureAwait(false);
            _cached = collection;
            return removed > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static EnvironmentConfig Copy(EnvironmentConfig source) => new()
    {
        Url = source.Url,
        Label = source.Label,
        Type = source.Type,
        DiscoveredType = source.DiscoveredType,
        Color = source.Color,
        SafetySettings = source.SafetySettings,
        Protection = source.Protection,
        Profiles = new List<string>(source.Profiles)
    };

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
