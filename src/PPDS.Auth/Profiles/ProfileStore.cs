using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Manages persistent storage of authentication profiles.
/// </summary>
/// <remarks>
/// <para>Schema v2 notes:</para>
/// <list type="bullet">
/// <item><description>Profiles stored as array, not dictionary</description></item>
/// <item><description>Active profile tracked by name, not index</description></item>
/// <item><description>Secrets stored separately in SecureCredentialStore</description></item>
/// <item><description>v1 profiles are detected and deleted (breaking change)</description></item>
/// </list>
/// </remarks>
public sealed class ProfileStore : IDisposable
{
    /// <summary>
    /// Current schema version.
    /// </summary>
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ProfileCollection? _cachedCollection;
    private bool _disposed;

    /// <summary>
    /// Creates a new profile store using the default path.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public ProfileStore(ILogger? logger = null) : this(ProfilePaths.ProfilesFile, logger)
    {
    }

    /// <summary>
    /// Creates a new profile store using a custom path.
    /// </summary>
    /// <param name="filePath">The path to the profiles file.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public ProfileStore(string filePath, ILogger? logger = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger;
    }

    /// <summary>
    /// Gets the path to the profiles file.
    /// </summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Loads the profile collection from disk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile collection.</returns>
    /// <remarks>
    /// If a v1 profile file is detected, it is deleted and an empty collection is returned.
    /// </remarks>
    public async Task<ProfileCollection> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedCollection != null)
            {
                return _cachedCollection;
            }

            if (!File.Exists(_filePath))
            {
                _cachedCollection = new ProfileCollection();
                return _cachedCollection;
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);

            // Check for v1 schema and handle migration
            if (IsV1Schema(json))
            {
                HandleV1Migration();
                _cachedCollection = new ProfileCollection();
                return _cachedCollection;
            }

            _cachedCollection = JsonSerializer.Deserialize<ProfileCollection>(json, JsonOptions)
                ?? new ProfileCollection();

            return _cachedCollection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Loads the profile collection from disk (synchronous).
    /// </summary>
    /// <returns>The profile collection.</returns>
    /// <remarks>
    /// If a v1 profile file is detected, it is deleted and an empty collection is returned.
    /// </remarks>
    public ProfileCollection Load()
    {
        _lock.Wait();
        try
        {
            if (_cachedCollection != null)
            {
                return _cachedCollection;
            }

            if (!File.Exists(_filePath))
            {
                _cachedCollection = new ProfileCollection();
                return _cachedCollection;
            }

            var json = File.ReadAllText(_filePath);

            // Check for v1 schema and handle migration
            if (IsV1Schema(json))
            {
                HandleV1Migration();
                _cachedCollection = new ProfileCollection();
                return _cachedCollection;
            }

            _cachedCollection = JsonSerializer.Deserialize<ProfileCollection>(json, JsonOptions)
                ?? new ProfileCollection();

            return _cachedCollection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves the profile collection to disk.
    /// </summary>
    /// <param name="collection">The collection to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(ProfileCollection collection, CancellationToken cancellationToken = default)
    {
        if (collection == null)
            throw new ArgumentNullException(nameof(collection));

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProfilePaths.EnsureDirectoryExists();

            // Ensure version is set correctly
            collection.Version = CurrentVersion;

            var json = JsonSerializer.Serialize(collection, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);

            _cachedCollection = collection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves the profile collection to disk (synchronous).
    /// </summary>
    /// <param name="collection">The collection to save.</param>
    public void Save(ProfileCollection collection)
    {
        if (collection == null)
            throw new ArgumentNullException(nameof(collection));

        _lock.Wait();
        try
        {
            ProfilePaths.EnsureDirectoryExists();

            // Ensure version is set correctly
            collection.Version = CurrentVersion;

            var json = JsonSerializer.Serialize(collection, JsonOptions);
            File.WriteAllText(_filePath, json);

            _cachedCollection = collection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Updates a specific profile using an update action.
    /// </summary>
    /// <param name="profileName">The name of the profile to update.</param>
    /// <param name="updateAction">Action to apply to the profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the profile was found and updated, false otherwise.</returns>
    public async Task<bool> UpdateProfileAsync(
        string profileName,
        Action<AuthProfile> updateAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateAction);

        var collection = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = collection.GetByName(profileName);

        if (profile == null)
        {
            return false;
        }

        updateAction(profile);
        await SaveAsync(collection, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes the profile storage file and clears the cache.
    /// </summary>
    public void Delete()
    {
        _lock.Wait();
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }

            _cachedCollection = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Clears the cached collection, forcing a reload on next access.
    /// </summary>
    public void ClearCache()
    {
        _lock.Wait();
        try
        {
            _cachedCollection = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Checks if the JSON represents a v1 schema (dictionary-based profiles).
    /// </summary>
    private static bool IsV1Schema(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // v1 has "activeIndex" property, v2 has "activeProfile"
            if (root.TryGetProperty("activeIndex", out _))
            {
                return true;
            }

            // v1 has version = 1
            if (root.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.Number &&
                versionElement.GetInt32() == 1)
            {
                return true;
            }

            // v1 profiles is an object (dictionary), v2 profiles is an array
            if (root.TryGetProperty("profiles", out var profilesElement) &&
                profilesElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            // If we can't parse, assume it's corrupted and treat as v1 to delete
            return true;
        }
    }

    /// <summary>
    /// Handles migration from v1 by deleting the old file.
    /// </summary>
    /// <remarks>
    /// Pre-release breaking change: v1 profiles are simply deleted.
    /// Users must re-authenticate with v2.
    /// </remarks>
    private void HandleV1Migration()
    {
        _logger?.LogWarning(
            "Detected v1 profile schema at '{FilePath}'. " +
            "This version requires schema v2. The old profile file will be deleted. " +
            "Please re-authenticate with 'ppds auth create'.",
            _filePath);

        try
        {
            File.Delete(_filePath);
            _logger?.LogInformation("Deleted v1 profile file: {FilePath}", _filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete v1 profile file: {FilePath}", _filePath);
            throw new InvalidOperationException(
                $"Failed to migrate from v1 profile schema. Please manually delete '{_filePath}' and re-authenticate.",
                ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _lock.Dispose();
        _disposed = true;
    }
}
