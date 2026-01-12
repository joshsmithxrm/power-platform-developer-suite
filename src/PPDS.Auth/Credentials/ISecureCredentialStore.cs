using System;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Represents a credential stored in secure platform storage.
/// </summary>
public sealed class StoredCredential
{
    /// <summary>
    /// Gets or sets the application (client) ID that owns this credential.
    /// </summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client secret for ClientSecret authentication.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the certificate path for CertificateFile authentication.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Gets or sets the certificate password for CertificateFile authentication.
    /// </summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Gets or sets the password for UsernamePassword authentication.
    /// </summary>
    public string? Password { get; set; }
}

/// <summary>
/// Provides secure, platform-native credential storage for service principal secrets.
/// </summary>
/// <remarks>
/// <para>
/// Credentials are keyed by applicationId (not profile name) because:
/// - Multiple profiles may use the same service principal
/// - Credential lifecycle is independent of profile lifecycle
/// - Prevents secret duplication across profiles
/// </para>
/// <para>
/// Platform storage mechanisms:
/// - Windows: DPAPI (Data Protection API)
/// - macOS: Keychain
/// - Linux: libsecret (with cleartext fallback when libsecret unavailable)
/// </para>
/// </remarks>
public interface ISecureCredentialStore
{
    /// <summary>
    /// Gets whether cleartext caching is enabled (Linux fallback).
    /// </summary>
    bool IsCleartextCachingEnabled { get; }

    /// <summary>
    /// Stores credentials for a service principal.
    /// </summary>
    /// <param name="credential">The credential to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">If credential or ApplicationId is null/empty.</exception>
    Task StoreAsync(StoredCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves credentials for a service principal by application ID.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored credential, or null if not found.</returns>
    Task<StoredCredential?> GetAsync(string applicationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes credentials for a service principal.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if removed, false if not found.</returns>
    Task<bool> RemoveAsync(string applicationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all stored credentials.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if credentials exist for a service principal.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if credentials exist.</returns>
    Task<bool> ExistsAsync(string applicationId, CancellationToken cancellationToken = default);
}
