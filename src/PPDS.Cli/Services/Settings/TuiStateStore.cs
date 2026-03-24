using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Settings;

/// <summary>
/// Manages persistent storage of per-environment TUI screen state.
/// </summary>
internal sealed class TuiStateStore : IDisposable
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
    private TuiStateCollection? _cached;
    private bool _disposed;

    /// <summary>Creates a store using the default TUI state file path.</summary>
    public TuiStateStore() : this(ProfilePaths.TuiStateFile) { }

    /// <summary>Creates a store using a custom file path.</summary>
    public TuiStateStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>Gets the file path used for persistent storage.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Loads the state for a specific screen and environment.
    /// Returns null if no state is persisted or the file is missing/corrupt.
    /// </summary>
    public async Task<T?> LoadScreenStateAsync<T>(
        string screenKey, string environmentUrl, CancellationToken ct = default) where T : class
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collection = await LoadCollectionAsync(ct).ConfigureAwait(false);
            if (collection == null)
                return null;

            var normalizedUrl = NormalizeUrl(environmentUrl);
            if (!collection.Screens.TryGetValue(normalizedUrl, out var screenDict))
                return null;

            if (!screenDict.TryGetValue(screenKey, out var element))
                return null;

            return JsonSerializer.Deserialize<T>(element, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Synchronously loads the state for a specific screen and environment.
    /// Intended for use in constructors where async is not available.
    /// Returns null if no state is persisted or the file is missing/corrupt.
    /// </summary>
    public T? LoadScreenState<T>(string screenKey, string environmentUrl) where T : class
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);

        _lock.Wait();
        try
        {
            var collection = LoadCollection();
            if (collection == null)
                return null;

            var normalizedUrl = NormalizeUrl(environmentUrl);
            if (!collection.Screens.TryGetValue(normalizedUrl, out var screenDict))
                return null;

            if (!screenDict.TryGetValue(screenKey, out var element))
                return null;

            return JsonSerializer.Deserialize<T>(element, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves the state for a specific screen and environment, merging into existing data.
    /// </summary>
    public async Task SaveScreenStateAsync<T>(
        string screenKey, string environmentUrl, T state, CancellationToken ct = default) where T : class
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);
        ArgumentNullException.ThrowIfNull(state);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collection = await LoadCollectionAsync(ct).ConfigureAwait(false)
                ?? new TuiStateCollection();

            var normalizedUrl = NormalizeUrl(environmentUrl);
            if (!collection.Screens.TryGetValue(normalizedUrl, out var screenDict))
            {
                screenDict = new Dictionary<string, JsonElement>();
                collection.Screens[normalizedUrl] = screenDict;
            }

            // Serialize to JsonElement for storage
            var element = JsonSerializer.SerializeToElement(state, JsonOptions);
            screenDict[screenKey] = element;

            await PersistAsync(collection, ct).ConfigureAwait(false);
            _cached = collection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes the state for a specific screen and environment.
    /// </summary>
    public async Task ClearScreenStateAsync(
        string screenKey, string environmentUrl, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(screenKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collection = await LoadCollectionAsync(ct).ConfigureAwait(false);
            if (collection == null)
                return;

            var normalizedUrl = NormalizeUrl(environmentUrl);
            if (!collection.Screens.TryGetValue(normalizedUrl, out var screenDict))
                return;

            if (!screenDict.Remove(screenKey))
                return;

            // Clean up empty environment entries
            if (screenDict.Count == 0)
                collection.Screens.Remove(normalizedUrl);

            await PersistAsync(collection, ct).ConfigureAwait(false);
            _cached = collection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _lock.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Loads the collection from cache or disk. Returns null if file is missing or corrupt.
    /// Must be called under the lock.
    /// </summary>
    private async Task<TuiStateCollection?> LoadCollectionAsync(CancellationToken ct)
    {
        if (_cached != null)
            return _cached;

        if (!File.Exists(_filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            _cached = JsonSerializer.Deserialize<TuiStateCollection>(json, JsonOptions);
            return _cached;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Synchronously loads the collection from cache or disk. Returns null if file is missing or corrupt.
    /// Must be called under the lock.
    /// </summary>
    private TuiStateCollection? LoadCollection()
    {
        if (_cached != null)
            return _cached;

        if (!File.Exists(_filePath))
            return null;

        try
        {
            var json = File.ReadAllText(_filePath);
            _cached = JsonSerializer.Deserialize<TuiStateCollection>(json, JsonOptions);
            return _cached;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Persists the collection to disk atomically (write to temp, then move).
    /// Must be called under the lock.
    /// </summary>
    private async Task PersistAsync(TuiStateCollection collection, CancellationToken ct)
    {
        ProfilePaths.EnsureDirectoryExists();
        collection.Version = 1;

        var json = JsonSerializer.Serialize(collection, JsonOptions);
        var tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Normalizes an environment URL: lowercase, trimmed, with trailing slash.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        var normalized = url.Trim().ToLowerInvariant();
        if (!normalized.EndsWith('/'))
            normalized += '/';
        return normalized;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Internal JSON root structure for the TUI state file.
    /// </summary>
    private sealed class TuiStateCollection
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("screens")]
        public Dictionary<string, Dictionary<string, JsonElement>> Screens { get; set; } = new();
    }
}
