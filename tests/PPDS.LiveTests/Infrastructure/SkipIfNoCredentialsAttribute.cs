using Xunit;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Skips the test if live Dataverse credentials are not configured.
/// Use this attribute on test methods that require a real Dataverse connection.
/// </summary>
public sealed class SkipIfNoCredentialsAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips if credentials are missing.
    /// </summary>
    public SkipIfNoCredentialsAttribute()
    {
        if (!Configuration.HasAnyCredentials)
        {
            Skip = Configuration.GetMissingCredentialsReason();
        }
    }
}

/// <summary>
/// Skips the test if client secret credentials are not configured.
/// </summary>
public sealed class SkipIfNoClientSecretAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips if client secret credentials are missing.
    /// </summary>
    public SkipIfNoClientSecretAttribute()
    {
        if (!Configuration.HasClientSecretCredentials)
        {
            Skip = "Client secret credentials not configured. Set DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, and PPDS_TEST_TENANT_ID.";
        }
    }
}

/// <summary>
/// Skips the test if certificate credentials are not configured.
/// </summary>
public sealed class SkipIfNoCertificateAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips if certificate credentials are missing.
    /// </summary>
    public SkipIfNoCertificateAttribute()
    {
        if (!Configuration.HasCertificateCredentials)
        {
            Skip = "Certificate credentials not configured. Set DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CERT_BASE64, and PPDS_TEST_TENANT_ID.";
        }
    }
}
