using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Manages persistent storage of authentication profiles.
/// </summary>
public sealed class ProfileStore : IDisposable
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
    private ProfileCollection? _cachedCollection;
    private bool _disposed;

    /// <summary>
    /// Creates a new profile store using the default path.
    /// </summary>
    public ProfileStore() : this(ProfilePaths.ProfilesFile)
    {
    }

    /// <summary>
    /// Creates a new profile store using a custom path.
    /// </summary>
    /// <param name="filePath">The path to the profiles file.</param>
    public ProfileStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>
    /// Loads the profile collection from disk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile collection.</returns>
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
            _cachedCollection = JsonSerializer.Deserialize<ProfileCollection>(json, JsonOptions)
                ?? new ProfileCollection();

            // Decrypt sensitive fields
            foreach (var profile in _cachedCollection.All)
            {
                DecryptProfile(profile);
            }

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
            _cachedCollection = JsonSerializer.Deserialize<ProfileCollection>(json, JsonOptions)
                ?? new ProfileCollection();

            // Decrypt sensitive fields
            foreach (var profile in _cachedCollection.All)
            {
                DecryptProfile(profile);
            }

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

            // Create a copy with encrypted sensitive fields
            var toSave = CloneWithEncryption(collection);

            var json = JsonSerializer.Serialize(toSave, JsonOptions);
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

            // Create a copy with encrypted sensitive fields
            var toSave = CloneWithEncryption(collection);

            var json = JsonSerializer.Serialize(toSave, JsonOptions);
            File.WriteAllText(_filePath, json);

            _cachedCollection = collection;
        }
        finally
        {
            _lock.Release();
        }
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
    /// Creates a deep copy of the collection with encrypted sensitive fields.
    /// </summary>
    private static ProfileCollection CloneWithEncryption(ProfileCollection source)
    {
        // Serialize and deserialize to create a deep copy
        var json = JsonSerializer.Serialize(source, JsonOptions);
        var copy = JsonSerializer.Deserialize<ProfileCollection>(json, JsonOptions)!;

        // Encrypt sensitive fields
        foreach (var profile in copy.All)
        {
            EncryptProfile(profile);
        }

        return copy;
    }

    /// <summary>
    /// Encrypts sensitive fields in a profile.
    /// </summary>
    private static void EncryptProfile(AuthProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.ClientSecret) && !ProfileEncryption.IsEncrypted(profile.ClientSecret))
        {
            profile.ClientSecret = ProfileEncryption.Encrypt(profile.ClientSecret);
        }

        if (!string.IsNullOrEmpty(profile.CertificatePassword) && !ProfileEncryption.IsEncrypted(profile.CertificatePassword))
        {
            profile.CertificatePassword = ProfileEncryption.Encrypt(profile.CertificatePassword);
        }

        if (!string.IsNullOrEmpty(profile.Password) && !ProfileEncryption.IsEncrypted(profile.Password))
        {
            profile.Password = ProfileEncryption.Encrypt(profile.Password);
        }
    }

    /// <summary>
    /// Decrypts sensitive fields in a profile.
    /// </summary>
    private static void DecryptProfile(AuthProfile profile)
    {
        if (ProfileEncryption.IsEncrypted(profile.ClientSecret))
        {
            profile.ClientSecret = ProfileEncryption.Decrypt(profile.ClientSecret);
        }

        if (ProfileEncryption.IsEncrypted(profile.CertificatePassword))
        {
            profile.CertificatePassword = ProfileEncryption.Decrypt(profile.CertificatePassword);
        }

        if (ProfileEncryption.IsEncrypted(profile.Password))
        {
            profile.Password = ProfileEncryption.Decrypt(profile.Password);
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
