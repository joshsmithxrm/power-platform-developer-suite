using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GitCredentialManager;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides secure, platform-native credential storage using OS credential managers.
/// </summary>
/// <remarks>
/// <para>
/// Uses platform-native security mechanisms via Git Credential Manager's credential store:
/// - Windows: Windows Credential Manager (DPAPI with CurrentUser scope)
/// - macOS: Keychain Services
/// - Linux: libsecret (GNOME Keyring/KWallet), with optional plaintext fallback for CI/CD
/// </para>
/// <para>
/// Credentials are stored as individual entries keyed by applicationId.
/// A manifest entry tracks all stored applicationIds to support enumeration.
/// </para>
/// </remarks>
public sealed class NativeCredentialStore : ISecureCredentialStore, IDisposable
{
    /// <summary>
    /// Service name used for credential storage namespace.
    /// </summary>
    private const string ServiceName = "ppds.credentials";

    /// <summary>
    /// Special key used to store the manifest of all applicationIds.
    /// </summary>
    private const string ManifestKey = "_manifest";

    /// <summary>
    /// Separator for encoding certificate path and password as a single string.
    /// Uses a sequence unlikely to appear in file paths or passwords.
    /// </summary>
    private const string CertificateSeparator = "||||";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ICredentialStore _store;
    private readonly string _legacyFilePath;
    private readonly bool _allowCleartextFallback;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private bool _migrationAttempted;

    /// <summary>
    /// Creates a new native credential store using the default settings.
    /// </summary>
    /// <param name="allowCleartextFallback">
    /// On Linux, if true and libsecret is unavailable, uses plaintext file storage.
    /// Has no effect on Windows or macOS where secure storage is always available.
    /// This is intended for CI/CD environments without a keyring.
    /// </param>
    public NativeCredentialStore(bool allowCleartextFallback = false)
        : this(allowCleartextFallback, null)
    {
    }

    /// <summary>
    /// Creates a new native credential store with an optional custom credential store.
    /// </summary>
    /// <param name="allowCleartextFallback">
    /// On Linux, if true and libsecret is unavailable, uses plaintext file storage.
    /// </param>
    /// <param name="store">
    /// Optional credential store for testing. If null, creates appropriate OS-native store.
    /// </param>
    internal NativeCredentialStore(bool allowCleartextFallback, ICredentialStore? store)
    {
        _allowCleartextFallback = allowCleartextFallback;
        _legacyFilePath = Path.Combine(ProfilePaths.DataDirectory, "ppds.credentials.dat");

        if (store != null)
        {
            _store = store;
        }
        else
        {
            // Configure credential store backend based on platform
            ConfigureCredentialStoreBackend(allowCleartextFallback);
            _store = CredentialManager.Create(ServiceName);
        }
    }

    /// <summary>
    /// Configures the GCM credential store backend via environment variable.
    /// </summary>
    private static void ConfigureCredentialStoreBackend(bool allowCleartextFallback)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && allowCleartextFallback)
        {
            // CI/CD mode: use plaintext store when libsecret unavailable
            // This matches PAC CLI behavior for headless environments
            Environment.SetEnvironmentVariable("GCM_CREDENTIAL_STORE", "plaintext");
        }
        // Windows and macOS use their native stores by default (DPAPI, Keychain)
        // Linux without fallback uses libsecret by default
    }

    /// <inheritdoc />
    public string CacheFilePath => _legacyFilePath;

    /// <inheritdoc />
    public bool IsCleartextCachingEnabled =>
        _allowCleartextFallback && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <inheritdoc />
    public Task StoreAsync(StoredCredential credential, CancellationToken cancellationToken = default)
    {
        if (credential == null)
            throw new ArgumentNullException(nameof(credential));
        if (string.IsNullOrWhiteSpace(credential.ApplicationId))
            throw new ArgumentException("ApplicationId is required.", nameof(credential));

        var key = credential.ApplicationId.ToLowerInvariant();
        var json = SerializeCredential(credential);

        _store.AddOrUpdate(ServiceName, key, json);
        AddToManifest(key);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<StoredCredential?> GetAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return null;

        await MigrateLegacyIfNeededAsync().ConfigureAwait(false);

        var key = applicationId.ToLowerInvariant();
        var cred = _store.Get(ServiceName, key);

        if (cred == null)
            return null;

        return DeserializeCredential(applicationId, cred.Password);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return Task.FromResult(false);

        var key = applicationId.ToLowerInvariant();
        var removed = _store.Remove(ServiceName, key);

        if (removed)
        {
            RemoveFromManifest(key);
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var manifest = GetManifest();
        foreach (var key in manifest)
        {
            _store.Remove(ServiceName, key);
        }

        // Clear the manifest itself
        _store.Remove(ServiceName, ManifestKey);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return false;

        await MigrateLegacyIfNeededAsync().ConfigureAwait(false);

        var key = applicationId.ToLowerInvariant();
        return _store.Get(ServiceName, key) != null;
    }

    /// <summary>
    /// Migrates credentials from the legacy plaintext file to the OS credential store.
    /// </summary>
    private async Task MigrateLegacyIfNeededAsync()
    {
        if (_migrationAttempted)
            return;

        await _migrationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_migrationAttempted)
                return;

            _migrationAttempted = true;

            if (!File.Exists(_legacyFilePath))
                return;

            try
            {
                // Read the legacy file
                var bytes = await File.ReadAllBytesAsync(_legacyFilePath).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    SecureDeleteFile(_legacyFilePath);
                    return;
                }

                var json = Encoding.UTF8.GetString(bytes);
                var legacyCache = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);

                if (legacyCache == null || legacyCache.Count == 0)
                {
                    SecureDeleteFile(_legacyFilePath);
                    return;
                }

                // Migrate each credential to the OS store
                var migratedCount = 0;
                foreach (var kvp in legacyCache)
                {
                    var key = kvp.Key.ToLowerInvariant();
                    _store.AddOrUpdate(ServiceName, key, kvp.Value);
                    AddToManifest(key);
                    migratedCount++;
                }

                // Securely delete the legacy file
                SecureDeleteFile(_legacyFilePath);

                // Log migration
                Console.Error.WriteLine($"Migrated {migratedCount} credential(s) from legacy file to OS credential store.");
            }
            catch (JsonException)
            {
                // Corrupted legacy file - just delete it
                SecureDeleteFile(_legacyFilePath);
            }
            catch (Exception ex)
            {
                // Log but don't fail - user can still authenticate
                Console.Error.WriteLine($"Warning: Failed to migrate legacy credentials: {ex.Message}");
            }
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    /// <summary>
    /// Securely deletes a file by overwriting its contents before deletion.
    /// </summary>
    private static void SecureDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            // Overwrite with zeros before deletion
            var fileInfo = new FileInfo(path);
            var length = fileInfo.Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[Math.Min(length, 4096)];
                var remaining = length;
                while (remaining > 0)
                {
                    var toWrite = (int)Math.Min(remaining, zeros.Length);
                    fs.Write(zeros, 0, toWrite);
                    remaining -= toWrite;
                }
                fs.Flush();
            }

            File.Delete(path);
        }
        catch
        {
            // Best effort - try normal delete as fallback
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Gets the list of all stored applicationIds from the manifest.
    /// </summary>
    private List<string> GetManifest()
    {
        var cred = _store.Get(ServiceName, ManifestKey);
        if (cred == null)
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(cred.Password, JsonOptions)
                ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Adds an applicationId to the manifest.
    /// </summary>
    private void AddToManifest(string key)
    {
        var manifest = GetManifest();
        if (!manifest.Contains(key))
        {
            manifest.Add(key);
            _store.AddOrUpdate(ServiceName, ManifestKey, JsonSerializer.Serialize(manifest, JsonOptions));
        }
    }

    /// <summary>
    /// Removes an applicationId from the manifest.
    /// </summary>
    private void RemoveFromManifest(string key)
    {
        var manifest = GetManifest();
        if (manifest.Remove(key))
        {
            if (manifest.Count > 0)
            {
                _store.AddOrUpdate(ServiceName, ManifestKey, JsonSerializer.Serialize(manifest, JsonOptions));
            }
            else
            {
                _store.Remove(ServiceName, ManifestKey);
            }
        }
    }

    /// <summary>
    /// Serializes a credential to a compact JSON string.
    /// </summary>
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
    /// Deserializes a credential from its stored JSON format.
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
    /// Disposes resources used by this credential store.
    /// </summary>
    public void Dispose()
    {
        _migrationLock.Dispose();
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
