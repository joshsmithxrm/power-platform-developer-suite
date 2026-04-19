using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Auth.Internal.CredentialStore;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides secure, platform-native credential storage using OS credential managers.
/// </summary>
/// <remarks>
/// <para>
/// Uses platform-native security mechanisms via a vendored subset of Microsoft's
/// git-credential-manager (see <c>src/PPDS.Auth/Internal/CredentialStore/</c>):
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
    private const string ServiceName = "https://ppds.credentials";

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
    private readonly bool _allowCleartextFallback;

    /// <summary>
    /// Creates a new native credential store using the default settings.
    /// </summary>
    /// <param name="allowCleartextFallback">
    /// On Linux, opts the caller in to plaintext file storage when libsecret is unavailable.
    /// Plaintext activation is double-gated: it requires BOTH this flag set to <c>true</c>
    /// AND the <c>GCM_CREDENTIAL_STORE=plaintext</c> environment variable, preventing
    /// accidental activation. Has no effect on Windows or macOS, where secure storage is
    /// always available. This is intended for CI/CD environments without a keyring.
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

        if (store != null)
        {
            _store = store;
        }
        else
        {
            // Configure credential store backend based on platform
            ConfigureCredentialStoreBackend(allowCleartextFallback);
            _store = CredentialManager.Create(ServiceName, allowCleartextFallback);
        }
    }

    /// <summary>
    /// Configures the vendored credential store backend (Linux plaintext fallback) via
    /// environment variable. Name <c>GCM_CREDENTIAL_STORE</c> preserved for compatibility
    /// with any ops runbook that already sets it.
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

        WrapStoreCall(() => _store.AddOrUpdate(ServiceName, key, json), "store");
        AddToManifest(key);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<StoredCredential?> GetAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return Task.FromResult<StoredCredential?>(null);

        var key = applicationId.ToLowerInvariant();
        var cred = WrapStoreCall(() => _store.Get(ServiceName, key), "read");

        if (cred == null)
            return Task.FromResult<StoredCredential?>(null);

        return Task.FromResult<StoredCredential?>(DeserializeCredential(applicationId, cred.Password));
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return Task.FromResult(false);

        var key = applicationId.ToLowerInvariant();
        var removed = WrapStoreCall(() => _store.Remove(ServiceName, key), "remove");

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
            WrapStoreCall(() => _store.Remove(ServiceName, key), "remove");
        }

        // Clear the manifest itself
        WrapStoreCall(() => _store.Remove(ServiceName, ManifestKey), "remove");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            return Task.FromResult(false);

        var key = applicationId.ToLowerInvariant();
        return Task.FromResult(WrapStoreCall(() => _store.Get(ServiceName, key), "read") != null);
    }

    /// <summary>
    /// Gets the list of all stored applicationIds from the manifest.
    /// </summary>
    private List<string> GetManifest()
    {
        var cred = WrapStoreCall(() => _store.Get(ServiceName, ManifestKey), "read");
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
            WrapStoreCall(
                () => _store.AddOrUpdate(ServiceName, ManifestKey, JsonSerializer.Serialize(manifest, JsonOptions)),
                "store");
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
                WrapStoreCall(
                    () => _store.AddOrUpdate(ServiceName, ManifestKey, JsonSerializer.Serialize(manifest, JsonOptions)),
                    "store");
            }
            else
            {
                WrapStoreCall(() => _store.Remove(ServiceName, ManifestKey), "remove");
            }
        }
    }

    /// <summary>
    /// Wraps a call to the underlying vendored <see cref="ICredentialStore"/> and converts the
    /// internal <c>InteropException</c> into a PPDS-layer <see cref="AuthenticationException"/>
    /// carrying an <c>Auth.CredentialStoreFailure</c> error code.
    /// </summary>
    /// <remarks>
    /// Per Constitution D4, application-service exceptions must carry a PPDS error code.
    /// <c>InteropException</c> is an <c>internal</c> type inside this assembly; external
    /// callers only ever observe the wrapped <see cref="AuthenticationException"/>. We do
    /// not swallow <see cref="ArgumentException"/>, <see cref="ArgumentNullException"/>, or
    /// <see cref="PlatformNotSupportedException"/>; those propagate as-is (they indicate a
    /// caller bug, not an OS store failure).
    /// </remarks>
    private static void WrapStoreCall(Action call, string operation)
    {
        try
        {
            call();
        }
        catch (InteropException ex)
        {
            throw new AuthenticationException(
                $"Credential store {operation} failed: {SensitiveValueRedactor.Redact(ex.Message)}",
                CredentialStoreFailureErrorCode,
                ex);
        }
    }

    /// <summary>
    /// Typed variant of <see cref="WrapStoreCall(Action, string)"/> for calls that return a value.
    /// </summary>
    private static T WrapStoreCall<T>(Func<T> call, string operation)
    {
        try
        {
            return call();
        }
        catch (InteropException ex)
        {
            throw new AuthenticationException(
                $"Credential store {operation} failed: {SensitiveValueRedactor.Redact(ex.Message)}",
                CredentialStoreFailureErrorCode,
                ex);
        }
    }

    /// <summary>
    /// Error code emitted when the underlying OS credential store (Windows Credential
    /// Manager, macOS Keychain, libsecret) returns a native failure. Callers that need to
    /// differentiate OS-store failures from other authentication problems can match on this.
    /// </summary>
    private const string CredentialStoreFailureErrorCode = "Auth.CredentialStoreFailure";

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
    /// <remarks>
    /// No-op. The vendored <c>ICredentialStore</c> implementations (Windows Credential
    /// Manager, macOS Keychain, libsecret, plaintext fallback) hold no managed
    /// resources requiring disposal. IDisposable is kept on the public surface for
    /// call sites that use <c>using</c> statements.
    /// </remarks>
    public void Dispose()
    {
        // No-op: vendored ICredentialStore backends hold no disposable state.
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
