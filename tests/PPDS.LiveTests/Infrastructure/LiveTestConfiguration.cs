using System.Security.Cryptography.X509Certificates;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Configuration for live Dataverse integration tests.
/// Reads credentials from environment variables.
/// </summary>
public sealed class LiveTestConfiguration : IDisposable
{
    private string? _tempCertificatePath;
    private bool _disposed;

    /// <summary>
    /// The Dataverse environment URL (e.g., https://org.crm.dynamics.com).
    /// </summary>
    public string? DataverseUrl { get; }

    /// <summary>
    /// The Entra ID Application (Client) ID.
    /// </summary>
    public string? ApplicationId { get; }

    /// <summary>
    /// The client secret for client credential authentication.
    /// </summary>
    public string? ClientSecret { get; }

    /// <summary>
    /// The Entra ID Tenant ID.
    /// </summary>
    public string? TenantId { get; }

    /// <summary>
    /// Base64-encoded certificate for certificate-based authentication.
    /// </summary>
    public string? CertificateBase64 { get; }

    /// <summary>
    /// Password for the certificate.
    /// </summary>
    public string? CertificatePassword { get; }

    /// <summary>
    /// Path to certificate file (used for local testing).
    /// If not set but CertificateBase64 is available, a temp file will be created.
    /// </summary>
    public string? CertificatePath { get; }

    /// <summary>
    /// GitHub Actions OIDC token request URL (set automatically by GitHub).
    /// </summary>
    public string? GitHubOidcTokenUrl { get; }

    /// <summary>
    /// GitHub Actions OIDC token request token (set automatically by GitHub).
    /// </summary>
    public string? GitHubOidcRequestToken { get; }

    /// <summary>
    /// Azure DevOps OIDC request URI (set automatically by Azure DevOps).
    /// </summary>
    public string? AzureDevOpsOidcRequestUri { get; }

    /// <summary>
    /// Azure DevOps system access token (set automatically by Azure DevOps).
    /// </summary>
    public string? AzureDevOpsAccessToken { get; }

    /// <summary>
    /// Azure DevOps service connection ID for workload identity federation.
    /// </summary>
    public string? AzureDevOpsServiceConnectionId { get; }

    /// <summary>
    /// Gets a value indicating whether client secret credentials are available.
    /// </summary>
    public bool HasClientSecretCredentials =>
        !string.IsNullOrWhiteSpace(DataverseUrl) &&
        !string.IsNullOrWhiteSpace(ApplicationId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(TenantId);

    /// <summary>
    /// Gets a value indicating whether certificate credentials are available.
    /// </summary>
    public bool HasCertificateCredentials =>
        !string.IsNullOrWhiteSpace(DataverseUrl) &&
        !string.IsNullOrWhiteSpace(ApplicationId) &&
        !string.IsNullOrWhiteSpace(CertificateBase64) &&
        !string.IsNullOrWhiteSpace(TenantId);

    /// <summary>
    /// Gets a value indicating whether GitHub OIDC federated credentials are available.
    /// This is true when running inside GitHub Actions with id-token permission.
    /// </summary>
    public bool HasGitHubOidcCredentials =>
        !string.IsNullOrWhiteSpace(DataverseUrl) &&
        !string.IsNullOrWhiteSpace(ApplicationId) &&
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(GitHubOidcTokenUrl) &&
        !string.IsNullOrWhiteSpace(GitHubOidcRequestToken);

    /// <summary>
    /// Gets a value indicating whether Azure DevOps OIDC federated credentials are available.
    /// This is true when running inside Azure Pipelines with workload identity federation.
    /// </summary>
    public bool HasAzureDevOpsOidcCredentials =>
        !string.IsNullOrWhiteSpace(DataverseUrl) &&
        !string.IsNullOrWhiteSpace(ApplicationId) &&
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(AzureDevOpsOidcRequestUri) &&
        !string.IsNullOrWhiteSpace(AzureDevOpsAccessToken) &&
        !string.IsNullOrWhiteSpace(AzureDevOpsServiceConnectionId);

    /// <summary>
    /// Gets a value indicating whether any live test credentials are available.
    /// </summary>
    public bool HasAnyCredentials => HasClientSecretCredentials || HasCertificateCredentials || HasGitHubOidcCredentials || HasAzureDevOpsOidcCredentials;

    /// <summary>
    /// Initializes a new instance reading from environment variables.
    /// </summary>
    public LiveTestConfiguration()
    {
        DataverseUrl = Environment.GetEnvironmentVariable("DATAVERSE_URL");
        ApplicationId = Environment.GetEnvironmentVariable("PPDS_TEST_APP_ID");
        ClientSecret = Environment.GetEnvironmentVariable("PPDS_TEST_CLIENT_SECRET");
        TenantId = Environment.GetEnvironmentVariable("PPDS_TEST_TENANT_ID");
        CertificateBase64 = Environment.GetEnvironmentVariable("PPDS_TEST_CERT_BASE64");
        CertificatePassword = Environment.GetEnvironmentVariable("PPDS_TEST_CERT_PASSWORD");
        CertificatePath = Environment.GetEnvironmentVariable("PPDS_TEST_CERT_PATH");
        GitHubOidcTokenUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        GitHubOidcRequestToken = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
        AzureDevOpsOidcRequestUri = Environment.GetEnvironmentVariable("SYSTEM_OIDCREQUESTURI");
        AzureDevOpsAccessToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
        AzureDevOpsServiceConnectionId = Environment.GetEnvironmentVariable("AZURESUBSCRIPTION_SERVICE_CONNECTION_ID");
    }

    /// <summary>
    /// Gets the path to the certificate file, creating a temp file from base64 if needed.
    /// </summary>
    /// <returns>Path to the certificate file.</returns>
    /// <exception cref="InvalidOperationException">Thrown when certificate is not available.</exception>
    public string GetCertificatePath()
    {
        // Use explicit path if provided
        if (!string.IsNullOrWhiteSpace(CertificatePath) && File.Exists(CertificatePath))
        {
            return CertificatePath;
        }

        // Decode from base64 if available
        if (!string.IsNullOrWhiteSpace(CertificateBase64))
        {
            if (_tempCertificatePath == null || !File.Exists(_tempCertificatePath))
            {
                _tempCertificatePath = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}.pfx");
                var bytes = Convert.FromBase64String(CertificateBase64);
                File.WriteAllBytes(_tempCertificatePath, bytes);
            }

            return _tempCertificatePath;
        }

        throw new InvalidOperationException("No certificate available. Set PPDS_TEST_CERT_PATH or PPDS_TEST_CERT_BASE64.");
    }

    /// <summary>
    /// Loads the certificate from the configured source.
    /// </summary>
    /// <returns>The loaded certificate. Caller is responsible for disposing.</returns>
    /// <remarks>
    /// The returned <see cref="X509Certificate2"/> implements <see cref="IDisposable"/>.
    /// Callers should use a <c>using</c> statement or call <see cref="X509Certificate2.Dispose"/>
    /// when the certificate is no longer needed.
    /// </remarks>
    public X509Certificate2 LoadCertificate()
    {
        var path = GetCertificatePath();
        var flags = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet;

#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12FromFile(path, CertificatePassword, flags);
#else
        return new X509Certificate2(path, CertificatePassword, flags);
#endif
    }

    /// <summary>
    /// Gets the reason why credentials are not available, for skip messages.
    /// </summary>
    public string GetMissingCredentialsReason()
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(DataverseUrl))
            missing.Add("DATAVERSE_URL");
        if (string.IsNullOrWhiteSpace(ApplicationId))
            missing.Add("PPDS_TEST_APP_ID");
        if (string.IsNullOrWhiteSpace(TenantId))
            missing.Add("PPDS_TEST_TENANT_ID");

        if (!HasClientSecretCredentials && !HasCertificateCredentials && !HasGitHubOidcCredentials)
        {
            if (string.IsNullOrWhiteSpace(ClientSecret))
                missing.Add("PPDS_TEST_CLIENT_SECRET");
            if (string.IsNullOrWhiteSpace(CertificateBase64))
                missing.Add("PPDS_TEST_CERT_BASE64");
            missing.Add("(or GitHub OIDC: ACTIONS_ID_TOKEN_REQUEST_URL)");
        }

        return missing.Count > 0
            ? $"Missing environment variables: {string.Join(", ", missing)}"
            : "Unknown reason";
    }

    /// <summary>
    /// Cleans up temporary files.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_tempCertificatePath != null && File.Exists(_tempCertificatePath))
        {
            try
            {
                File.Delete(_tempCertificatePath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        _disposed = true;
    }
}
