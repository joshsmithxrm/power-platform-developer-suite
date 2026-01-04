using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.Extensions.Msal;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides secure, platform-native credential storage using MSAL.Extensions.
/// </summary>
/// <remarks>
/// <para>
/// Uses platform-native security mechanisms:
/// - Windows: DPAPI (Data Protection API) with CurrentUser scope
/// - macOS: Keychain Services
/// - Linux: libsecret (GNOME Keyring/KWallet), with optional cleartext fallback
/// </para>
/// <para>
/// Credentials are stored in a JSON dictionary structure, encrypted at rest.
/// The dictionary is keyed by applicationId for efficient lookup.
/// </para>
/// </remarks>
public sealed class SecureCredentialStore : ISecureCredentialStore, IDisposable
{
    /// <summary>
    /// Separator for encoding certificate path and password as a single string.
    /// Uses a sequence unlikely to appear in file paths or passwords.
    /// </summary>
    private const string CertificateSeparator = "||||";

    /// <summary>
    /// Service name for macOS Keychain and Linux libsecret.
    /// </summary>
    private const string KeychainServiceName = "ppds.credentials";

    /// <summary>
    /// Account name for macOS Keychain and Linux libsecret.
    /// </summary>
    private const string KeychainAccountName = "ppds";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _cacheFilePath;
    private readonly bool _allowCleartextFallback;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MsalCacheHelper? _cacheHelper;
    private bool _disposed;

    /// <summary>
    /// Creates a new secure credential store using the default path.
    /// </summary>
    /// <param name="allowCleartextFallback">
    /// On Linux, if true and libsecret is unavailable, falls back to cleartext storage.
    /// Has no effect on Windows or macOS where secure storage is always available.
    /// </param>
    public SecureCredentialStore(bool allowCleartextFallback = false)
        : this(Path.Combine(ProfilePaths.DataDirectory, "ppds.credentials.dat"), allowCleartextFallback)
    {
    }

    /// <summary>
    /// Creates a new secure credential store using a custom path.
    /// </summary>
    /// <param name="cacheFilePath">Path to the credential cache file.</param>
    /// <param name="allowCleartextFallback">
    /// On Linux, if true and libsecret is unavailable, falls back to cleartext storage.
    /// </param>
    public SecureCredentialStore(string cacheFilePath, bool allowCleartextFallback = false)
    {
        _cacheFilePath = cacheFilePath ?? throw new ArgumentNullException(nameof(cacheFilePath));
        _allowCleartextFallback = allowCleartextFallback;
    }

    /// <inheritdoc />
    public string CacheFilePath => _cacheFilePath;

    /// <inheritdoc />
    public bool IsCleartextCachingEnabled => _allowCleartextFallback && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <inheritdoc />
    public async Task StoreAsync(StoredCredential credential, CancellationToken cancellationToken = default)
    {
        if (credential == null)
            throw new ArgumentNullException(nameof(credential));
        if (string.IsNullOrWhiteSpace(credential.ApplicationId))
            throw new ArgumentException("ApplicationId is required.", nameof(credential));

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            cache[credential.ApplicationId.ToLowerInvariant()] = SerializeCredential(credential);
            await SaveCacheAsync(cache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<StoredCredential?> GetAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return null;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            if (cache.TryGetValue(applicationId.ToLowerInvariant(), out var serialized))
            {
                return DeserializeCredential(applicationId, serialized);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return false;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            if (cache.Remove(applicationId.ToLowerInvariant()))
            {
                await SaveCacheAsync(cache, cancellationToken).ConfigureAwait(false);
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return false;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            return cache.ContainsKey(applicationId.ToLowerInvariant());
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Serializes a credential to a compact string format.
    /// </summary>
    /// <remarks>
    /// Format: JSON object with only non-null credential fields.
    /// Certificate path and password are combined with separator.
    /// </remarks>
    private static string SerializeCredential(StoredCredential credential)
    {
        var data = new CredentialData();

        if (!string.IsNullOrEmpty(credential.ClientSecret))
        {
            data.Secret = credential.ClientSecret;
        }

        if (!string.IsNullOrEmpty(credential.CertificatePath))
        {
            // Combine path and optional password: "path||||password" or just "path"
            data.Certificate = string.IsNullOrEmpty(credential.CertificatePassword)
                ? credential.CertificatePath
                : $"{credential.CertificatePath}{CertificateSeparator}{credential.CertificatePassword}";
        }

        if (!string.IsNullOrEmpty(credential.Password))
        {
            data.Password = credential.Password;
        }

        return JsonSerializer.Serialize(data, JsonOptions);
    }

    /// <summary>
    /// Deserializes a credential from its stored format.
    /// </summary>
    private static StoredCredential DeserializeCredential(string applicationId, string serialized)
    {
        var data = JsonSerializer.Deserialize<CredentialData>(serialized, JsonOptions)
            ?? new CredentialData();

        var credential = new StoredCredential { ApplicationId = applicationId };

        if (!string.IsNullOrEmpty(data.Secret))
        {
            credential.ClientSecret = data.Secret;
        }

        if (!string.IsNullOrEmpty(data.Certificate))
        {
            var separatorIndex = data.Certificate.IndexOf(CertificateSeparator, StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                credential.CertificatePath = data.Certificate[..separatorIndex];
                credential.CertificatePassword = data.Certificate[(separatorIndex + CertificateSeparator.Length)..];
            }
            else
            {
                credential.CertificatePath = data.Certificate;
            }
        }

        if (!string.IsNullOrEmpty(data.Password))
        {
            credential.Password = data.Password;
        }

        return credential;
    }

    /// <summary>
    /// Loads the credential cache from secure storage.
    /// </summary>
    private async Task<Dictionary<string, string>> LoadCacheAsync(CancellationToken cancellationToken)
    {
        await EnsureCacheHelperAsync().ConfigureAwait(false);

        if (!File.Exists(_cacheFilePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(_cacheFilePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Corrupted cache, start fresh
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Saves the credential cache to secure storage.
    /// </summary>
    private async Task SaveCacheAsync(Dictionary<string, string> cache, CancellationToken cancellationToken)
    {
        await EnsureCacheHelperAsync().ConfigureAwait(false);

        ProfilePaths.EnsureDirectoryExists();
        var json = JsonSerializer.Serialize(cache, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        // Write atomically to prevent corruption
        var tempPath = _cacheFilePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _cacheFilePath, overwrite: true);
    }

    /// <summary>
    /// Timeout for MsalCacheHelper initialization.
    /// DPAPI/Keychain operations should complete quickly; hanging indicates system issues.
    /// </summary>
    private static readonly TimeSpan CacheHelperTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Initializes the MSAL cache helper for platform-native encryption.
    /// </summary>
    private async Task EnsureCacheHelperAsync()
    {
        if (_cacheHelper != null)
            return;

        var storageProperties = CreateStorageProperties();

        // Add timeout to fail fast if DPAPI/Keychain is unresponsive
        try
        {
            using var cts = new CancellationTokenSource(CacheHelperTimeout);
            _cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties)
                .WaitAsync(cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Credential store initialization timed out after {CacheHelperTimeout.TotalSeconds}s. " +
                "This may indicate DPAPI issues on Windows or Keychain issues on macOS. " +
                "Set PPDS_SPN_SECRET or PPDS_TEST_CLIENT_SECRET environment variable to bypass credential store lookup.");
        }

        // Register the cache helper to protect the file
        // The cache helper will encrypt/decrypt data when reading/writing
        try
        {
            _cacheHelper.VerifyPersistence();
        }
        catch (MsalCachePersistenceException ex)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new InvalidOperationException(
                    "Secure credential storage (libsecret) is unavailable on this system. " +
                    "Use --accept-cleartext-caching to allow cleartext storage, or install libsecret with a keyring.",
                    ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Creates storage properties for the platform-specific cache.
    /// </summary>
    private StorageCreationProperties CreateStorageProperties()
    {
        var directory = Path.GetDirectoryName(_cacheFilePath) ?? ProfilePaths.DataDirectory;
        var fileName = Path.GetFileName(_cacheFilePath);

        var builder = new StorageCreationPropertiesBuilder(fileName, directory)
            .WithMacKeyChain(KeychainServiceName, KeychainAccountName);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (_allowCleartextFallback)
            {
                // User explicitly opted into cleartext storage
                builder.WithLinuxUnprotectedFile();
            }
            else
            {
                // Use libsecret (will fail if not available)
                builder.WithLinuxKeyring(
                    schemaName: "ppds.credentials",
                    collection: "default",
                    secretLabel: "PPDS Credentials",
                    attribute1: new KeyValuePair<string, string>("application", "ppds"),
                    attribute2: new KeyValuePair<string, string>("version", "2"));
            }
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _lock.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Internal DTO for credential serialization.
    /// </summary>
    private sealed class CredentialData
    {
        [JsonPropertyName("s")]
        public string? Secret { get; set; }

        [JsonPropertyName("c")]
        public string? Certificate { get; set; }

        [JsonPropertyName("p")]
        public string? Password { get; set; }
    }
}
