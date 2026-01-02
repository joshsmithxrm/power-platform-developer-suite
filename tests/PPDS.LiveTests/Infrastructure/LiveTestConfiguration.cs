namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Configuration for live Dataverse integration tests.
/// Reads credentials from environment variables.
/// </summary>
public sealed class LiveTestConfiguration
{
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
    /// Gets a value indicating whether any live test credentials are available.
    /// </summary>
    public bool HasAnyCredentials => HasClientSecretCredentials || HasCertificateCredentials;

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

        if (!HasClientSecretCredentials && !HasCertificateCredentials)
        {
            if (string.IsNullOrWhiteSpace(ClientSecret))
                missing.Add("PPDS_TEST_CLIENT_SECRET");
            if (string.IsNullOrWhiteSpace(CertificateBase64))
                missing.Add("PPDS_TEST_CERT_BASE64");
        }

        return missing.Count > 0
            ? $"Missing environment variables: {string.Join(", ", missing)}"
            : "Unknown reason";
    }
}
