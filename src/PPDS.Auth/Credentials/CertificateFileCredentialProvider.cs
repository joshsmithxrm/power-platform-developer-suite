using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using a certificate file (PFX/P12).
/// </summary>
public sealed class CertificateFileCredentialProvider : ICredentialProvider
{
    private readonly string _applicationId;
    private readonly string _certificatePath;
    private readonly string? _certificatePassword;
    private readonly string _tenantId;
    private readonly CloudEnvironment _cloud;

    private X509Certificate2? _certificate;
    private DateTimeOffset? _tokenExpiresAt;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.CertificateFile;

    /// <inheritdoc />
    public string? Identity => $"app:{_applicationId[..Math.Min(8, _applicationId.Length)]}...";

    /// <inheritdoc />
    public DateTimeOffset? TokenExpiresAt => _tokenExpiresAt;

    /// <inheritdoc />
    public string? TenantId => _tenantId;

    /// <inheritdoc />
    public string? ObjectId => null; // Service principals don't have a user OID

    /// <inheritdoc />
    public string? HomeAccountId => null; // Service principals don't use MSAL user cache

    /// <inheritdoc />
    public string? AccessToken => null; // Connection string auth doesn't expose the token

    /// <inheritdoc />
    public System.Security.Claims.ClaimsPrincipal? IdTokenClaims => null;

    /// <summary>
    /// Creates a new certificate file credential provider.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="certificatePath">Path to the certificate file (PFX/P12).</param>
    /// <param name="certificatePassword">Password for the certificate file (optional).</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cloud">The cloud environment.</param>
    public CertificateFileCredentialProvider(
        string applicationId,
        string certificatePath,
        string? certificatePassword,
        string tenantId,
        CloudEnvironment cloud = CloudEnvironment.Public)
    {
        _applicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        _certificatePath = certificatePath ?? throw new ArgumentNullException(nameof(certificatePath));
        _certificatePassword = certificatePassword;
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        _cloud = cloud;
    }

    /// <summary>
    /// Creates a provider from an auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <returns>A new provider instance.</returns>
    public static CertificateFileCredentialProvider FromProfile(AuthProfile profile)
    {
        if (profile.AuthMethod != AuthMethod.CertificateFile)
            throw new ArgumentException($"Profile auth method must be CertificateFile, got {profile.AuthMethod}", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.ApplicationId))
            throw new ArgumentException("Profile ApplicationId is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.CertificatePath))
            throw new ArgumentException("Profile CertificatePath is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.TenantId))
            throw new ArgumentException("Profile TenantId is required", nameof(profile));

        return new CertificateFileCredentialProvider(
            profile.ApplicationId,
            profile.CertificatePath,
            profile.CertificatePassword,
            profile.TenantId,
            profile.Cloud);
    }

    /// <inheritdoc />
    public Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default,
        bool forceInteractive = false) // Ignored for service principals
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentNullException(nameof(environmentUrl));

        // Normalize URL
        environmentUrl = environmentUrl.TrimEnd('/');

        // Load certificate
        LoadCertificate();

        // Build connection string
        var connectionString = BuildConnectionString(environmentUrl);

        // Create ServiceClient
        ServiceClient client;
        try
        {
            client = new ServiceClient(connectionString);
        }
        catch (Exception ex)
        {
            throw new AuthenticationException($"Failed to create ServiceClient: {ex.Message}", ex);
        }

        if (!client.IsReady)
        {
            var error = client.LastError ?? "Unknown error";
            client.Dispose();
            throw new AuthenticationException($"Failed to connect to Dataverse: {error}");
        }

        // Estimate token expiration (typically 1 hour for client credentials)
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1);

        return Task.FromResult(client);
    }

    /// <summary>
    /// Loads the certificate from the file.
    /// </summary>
    private void LoadCertificate()
    {
        if (_certificate != null)
            return;

        if (!File.Exists(_certificatePath))
        {
            throw new AuthenticationException($"Certificate file not found: {_certificatePath}");
        }

        try
        {
            var flags = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet;

            _certificate = string.IsNullOrEmpty(_certificatePassword)
                ? new X509Certificate2(_certificatePath, (string?)null, flags)
                : new X509Certificate2(_certificatePath, _certificatePassword, flags);

            if (!_certificate.HasPrivateKey)
            {
                throw new AuthenticationException("Certificate does not contain a private key.");
            }
        }
        catch (Exception ex) when (ex is not AuthenticationException)
        {
            throw new AuthenticationException($"Failed to load certificate: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds a connection string for the ServiceClient.
    /// </summary>
    private string BuildConnectionString(string environmentUrl)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("AuthType=Certificate;");
        builder.Append($"Url={environmentUrl};");
        builder.Append($"ClientId={_applicationId};");
        builder.Append($"CertificateThumbprint={_certificate!.Thumbprint};");
        builder.Append($"TenantId={_tenantId};");

        // Add authority for non-public clouds
        if (_cloud != CloudEnvironment.Public)
        {
            var authority = CloudEndpoints.GetAuthorityUrl(_cloud, _tenantId);
            builder.Append($"Authority={authority};");
        }

        // Store certificate in current user store temporarily for ServiceClient to find
        StoreCertificateTemporarily();

        return builder.ToString().TrimEnd(';');
    }

    /// <summary>
    /// Temporarily stores the certificate in the current user certificate store
    /// so ServiceClient can find it by thumbprint.
    /// </summary>
    private void StoreCertificateTemporarily()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            // Check if already in store
            var existing = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                _certificate!.Thumbprint,
                false);

            if (existing.Count == 0)
            {
                store.Add(_certificate);
            }
        }
        catch (Exception ex)
        {
            // Log warning but don't fail - ServiceClient may still work with in-memory cert
            Console.Error.WriteLine($"Warning: Could not store certificate in cert store ({ex.Message}). Authentication may still succeed.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _certificate?.Dispose();
        _disposed = true;
    }
}
