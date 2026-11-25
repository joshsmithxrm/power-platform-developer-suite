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
/// <remarks>
/// <para>
/// Several processes share the file (the CLI, the daemon behind the VS Code extension, the TUI, the MCP server),
/// and a long-lived one may hold a store for hours. Every read of the file and every read-modify-write of it
/// (save, remove, update, and the de-duplication write while reading) happens under a cross-process lock: an
/// exclusive lock on a sibling file, <c>environments.json.lock</c>, opened with <see cref="FileShare.None"/>.
/// On Windows that is a sharing lock; on Linux and macOS .NET takes an advisory <c>flock</c> on it, which every
/// PPDS process on the same machine takes too. So a change made by one process cannot be overwritten by another
/// that read the file before it (#1829). The lock is held only for one read or one read-modify-write (milliseconds); a caller
/// that cannot get it within <see cref="DefaultLockTimeout"/> gets a <see cref="TimeoutException"/> and nothing
/// is written. When a process dies holding the lock, the operating system closes its handle and the lock is
/// released at once; the lock file itself stays (it is never deleted, so two processes can never lock two
/// different files of that name).
/// </para>
/// <para>
/// Where the lock does not hold, nothing reports it and two processes can again overwrite each other's change:
/// when file locking is turned off for the process with the .NET environment switch
/// <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>; on a network share (SMB/CIFS, and some NFS set-ups) where the
/// advisory lock is not enforced, as .NET on Linux ignores <c>flock</c> errors other than "would block"; and
/// across machines when the configuration folder is synced between them, since each machine locks only its own
/// copy.
/// </para>
/// <para>
/// The file is written to a temporary file in the same folder and then moved over the old one, so a reader
/// sees the old or the new content, never part of it.
/// </para>
/// </remarks>
public sealed class EnvironmentConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>How long a read or a change waits for another process's lock on the file before giving up.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(10);

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

    /// <summary>The cross-process lock file: the environments file's path with <c>.lock</c> appended.</summary>
    internal string LockFilePath => _filePath + ".lock";

    /// <summary>How long to wait for the cross-process lock (tests shorten it).</summary>
    internal TimeSpan LockTimeout { get; set; } = DefaultLockTimeout;

    /// <summary>Loads the environment config collection, using cache if available.</summary>
    /// <remarks>
    /// The cache can be older than the file when another process (or another store instance) has saved
    /// since. Use it for display and listing only. A protection decision reads the file:
    /// <see cref="GetConfigAsync"/> always does, and <see cref="ReloadAsync"/> returns the whole collection as
    /// it is on disk. Every change (<see cref="SaveConfigAsync(string, string?, EnvironmentType?, EnvironmentColor?, bool, string?, string?, CancellationToken)"/>,
    /// <see cref="RemoveConfigAsync"/>, <see cref="UpdateAsync"/>) re-reads the file under the cross-process lock,
    /// so a stale cache is never written back.
    /// </remarks>
    public async Task<EnvironmentConfigCollection> LoadAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is null)
            {
                using var fileLock = await AcquireFileLockAsync(ct).ConfigureAwait(false);
                _cached = await ReadFromDiskAsync(ct).ConfigureAwait(false);
            }
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Reads the file now, ignoring and replacing the cache, and returns the collection as it is on disk.
    /// Long-lived hosts (the TUI, the daemon) call it before a protection decision, so a change another
    /// process saved is seen.
    /// </summary>
    public async Task<EnvironmentConfigCollection> ReloadAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fileLock = await AcquireFileLockAsync(ct).ConfigureAwait(false);
            _cached = await ReadFromDiskAsync(ct).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Takes the cross-process lock: opens <see cref="LockFilePath"/> exclusively, retrying with a short back-off
    /// until <see cref="LockTimeout"/>. Disposing the returned stream releases it. The caller holds
    /// <see cref="_lock"/> (always taken first, so the two locks are never taken in the other order).
    /// </summary>
    private async Task<FileStream> AcquireFileLockAsync(CancellationToken ct)
    {
        EnsureDirectory();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.None);
            }
            catch (IOException ex) when (ex is not (DirectoryNotFoundException or FileNotFoundException or PathTooLongException))
            {
                // Another process (or another store instance) holds the lock.
                if (started.Elapsed >= LockTimeout)
                {
                    throw new TimeoutException(
                        $"Another PPDS process has held the environment configuration lock ({LockFilePath}) for more than " +
                        $"{LockTimeout.TotalSeconds:0.#} seconds, so the environment configuration was not read or changed. " +
                        "Try again when the other command has finished.", ex);
                }
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 100));
            }
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Reads and normalises the file, ignoring the cache. The caller holds <see cref="_lock"/> and the
    /// cross-process lock (<see cref="AcquireFileLockAsync"/>).
    /// </summary>
    private async Task<EnvironmentConfigCollection> ReadFromDiskAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new EnvironmentConfigCollection();

        var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
        EnvironmentConfigCollection collection;
        try
        {
            collection = JsonSerializer.Deserialize<EnvironmentConfigCollection>(json, JsonOptions)
                ?? new EnvironmentConfigCollection();
        }
        catch (JsonException ex)
        {
            // Names the file, the entry and the field, and says how to repair it; nothing is written (#1829).
            throw EnvironmentConfigFileException.From(_filePath, json, ex);
        }

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
            // Persist the deduped version (the caller holds both locks)
            await WriteToDiskAsync(collection, ct).ConfigureAwait(false);
        }

        return collection;
    }

    /// <summary>
    /// Writes the collection to a temporary file in the same folder, then moves it over the file, so a reader
    /// never sees part of a write. The caller holds <see cref="_lock"/> and the cross-process lock.
    /// </summary>
    private async Task WriteToDiskAsync(EnvironmentConfigCollection collection, CancellationToken ct)
    {
        EnsureDirectory();
        collection.Version = 1;
        var json = JsonSerializer.Serialize(collection, JsonOptions);
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_filePath))!,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            await ReplaceAsync(temp, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Moves <paramref name="temp"/> over the file (an atomic rename on Linux and macOS, MoveFileEx with replace
    /// on Windows). On Windows a program outside PPDS that has the file open (an editor, a virus scanner) can make
    /// the move fail for a moment, so it is retried within the lock timeout.
    /// </summary>
    private async Task ReplaceAsync(string temp, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(temp, _filePath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && started.Elapsed < LockTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Persists the environment config collection to disk.</summary>
    /// <remarks>
    /// Replaces the whole file with <paramref name="collection"/>, so a change another process saved since
    /// <paramref name="collection"/> was read is lost. To change part of the file, use <see cref="UpdateAsync"/>,
    /// <see cref="SaveConfigAsync(string, string?, EnvironmentType?, EnvironmentColor?, bool, string?, string?, CancellationToken)"/>
    /// or <see cref="RemoveConfigAsync"/>, which re-read the file under the cross-process lock.
    /// </remarks>
    public async Task SaveAsync(EnvironmentConfigCollection collection, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(collection);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fileLock = await AcquireFileLockAsync(ct).ConfigureAwait(false);
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
    /// <paramref name="update"/> returns true, all under the cross-process lock. Use it instead of
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
            using var fileLock = await AcquireFileLockAsync(ct).ConfigureAwait(false);
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
    /// <remarks>
    /// Reads the file every time (and refreshes the cache), never the cache alone: every protection decision
    /// looks an environment up here, and a long-lived host must see a change another process saved (#1829).
    /// </remarks>
    public async Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var collection = await ReloadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);
        return collection.Environments.FirstOrDefault(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);
    }

    /// <summary>
    /// Saves or updates config for a specific environment. Merges non-null fields.
    /// Optionally links the environment to a profile name.
    /// </summary>
    /// <remarks>
    /// Re-reads the file under the cross-process lock and changes only the fields given here on the freshly
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
    /// The read, the check and the write happen under the cross-process lock, so no other caller of this
    /// store, no other store instance and no other PPDS process can change the file in between.
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
            using var fileLock = await AcquireFileLockAsync(ct).ConfigureAwait(false);
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
    /// Removes config for a specific environment URL. Re-reads the file under the cross-process lock first,
    /// so the rest of the file is written back as it is on disk, not from the cache.
    /// </summary>
    public async Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fileLock = await AcquireFileLockAsync(ct).ConfigureAwait(false);
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
