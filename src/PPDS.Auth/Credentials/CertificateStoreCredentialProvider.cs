using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using a certificate from the Windows certificate store.
/// </summary>
/// <remarks>
/// This provider only works on Windows. On other platforms, use CertificateFileCredentialProvider.
/// </remarks>
public sealed class CertificateStoreCredentialProvider : ICredentialProvider
{
    private readonly string _applicationId;
    private readonly string _thumbprint;
    private readonly StoreName _storeName;
    private readonly StoreLocation _storeLocation;
    private readonly string _tenantId;
    private readonly CloudEnvironment _cloud;

    private X509Certificate2? _certificate;
    private DateTimeOffset? _tokenExpiresAt;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.CertificateStore;

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
    /// Creates a new certificate store credential provider.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="thumbprint">The certificate thumbprint.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="storeName">The certificate store name (default: My).</param>
    /// <param name="storeLocation">The certificate store location (default: CurrentUser).</param>
    /// <param name="cloud">The cloud environment.</param>
    public CertificateStoreCredentialProvider(
        string applicationId,
        string thumbprint,
        string tenantId,
        StoreName storeName = StoreName.My,
        StoreLocation storeLocation = StoreLocation.CurrentUser,
        CloudEnvironment cloud = CloudEnvironment.Public)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "Certificate store authentication is only supported on Windows. " +
                "Use certificate file authentication (--certificate-path) on other platforms.");
        }

        _applicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        _thumbprint = thumbprint ?? throw new ArgumentNullException(nameof(thumbprint));
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        _storeName = storeName;
        _storeLocation = storeLocation;
        _cloud = cloud;
    }

    /// <summary>
    /// Creates a provider from an auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <returns>A new provider instance.</returns>
    public static CertificateStoreCredentialProvider FromProfile(AuthProfile profile)
    {
        if (profile.AuthMethod != AuthMethod.CertificateStore)
            throw new ArgumentException($"Profile auth method must be CertificateStore, got {profile.AuthMethod}", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.ApplicationId))
            throw new ArgumentException("Profile ApplicationId is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.CertificateThumbprint))
            throw new ArgumentException("Profile CertificateThumbprint is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.TenantId))
            throw new ArgumentException("Profile TenantId is required", nameof(profile));

        // Parse store name
        var storeName = StoreName.My;
        if (!string.IsNullOrWhiteSpace(profile.CertificateStoreName) &&
            Enum.TryParse<StoreName>(profile.CertificateStoreName, true, out var parsedStoreName))
        {
            storeName = parsedStoreName;
        }

        // Parse store location
        var storeLocation = StoreLocation.CurrentUser;
        if (!string.IsNullOrWhiteSpace(profile.CertificateStoreLocation) &&
            Enum.TryParse<StoreLocation>(profile.CertificateStoreLocation, true, out var parsedStoreLocation))
        {
            storeLocation = parsedStoreLocation;
        }

        return new CertificateStoreCredentialProvider(
            profile.ApplicationId,
            profile.CertificateThumbprint,
            profile.TenantId,
            storeName,
            storeLocation,
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

        // Find certificate in store
        FindCertificate();

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
    /// Finds the certificate in the Windows certificate store.
    /// </summary>
    private void FindCertificate()
    {
        if (_certificate != null)
            return;

        try
        {
            using var store = new X509Store(_storeName, _storeLocation);
            store.Open(OpenFlags.ReadOnly);

            var certificates = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                _thumbprint,
                validOnly: false);

            if (certificates.Count == 0)
            {
                throw new AuthenticationException(
                    $"Certificate with thumbprint '{_thumbprint}' not found in " +
                    $"{_storeLocation}\\{_storeName} store.");
            }

            _certificate = certificates[0];

            if (!_certificate.HasPrivateKey)
            {
                throw new AuthenticationException(
                    "Certificate does not have a private key or the private key is not accessible.");
            }
        }
        catch (Exception ex) when (ex is not AuthenticationException)
        {
            throw new AuthenticationException($"Failed to access certificate store: {ex.Message}", ex);
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
        builder.Append($"CertificateThumbprint={_thumbprint};");
        builder.Append($"CertificateStoreName={_storeName};");
        builder.Append($"TenantId={_tenantId};");

        // Add store location if not default
        if (_storeLocation != StoreLocation.CurrentUser)
        {
            builder.Append($"StoreName={_storeLocation};");
        }

        // Add authority for non-public clouds
        if (_cloud != CloudEnvironment.Public)
        {
            var authority = CloudEndpoints.GetAuthorityUrl(_cloud, _tenantId);
            builder.Append($"Authority={authority};");
        }

        return builder.ToString().TrimEnd(';');
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        // Don't dispose the certificate - it's from the store
        _disposed = true;
    }
}
