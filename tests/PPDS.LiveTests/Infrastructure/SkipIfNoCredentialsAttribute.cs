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

/// <summary>
/// Skips the test if GitHub OIDC federated credentials are not configured.
/// This is only available when running inside GitHub Actions with id-token permission.
/// </summary>
public sealed class SkipIfNoGitHubOidcAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips if GitHub OIDC credentials are missing.
    /// </summary>
    public SkipIfNoGitHubOidcAttribute()
    {
        if (!Configuration.HasGitHubOidcCredentials)
        {
            Skip = "GitHub OIDC not available. This test only runs in GitHub Actions with 'id-token: write' permission and federated credential configured in Azure.";
        }
    }
}

/// <summary>
/// Skips the test if Azure DevOps OIDC federated credentials are not configured.
/// This is only available when running inside Azure Pipelines with workload identity federation.
/// </summary>
public sealed class SkipIfNoAzureDevOpsOidcAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips if Azure DevOps OIDC credentials are missing.
    /// </summary>
    public SkipIfNoAzureDevOpsOidcAttribute()
    {
        if (!Configuration.HasAzureDevOpsOidcCredentials)
        {
            Skip = "Azure DevOps OIDC not available. This test only runs in Azure Pipelines with workload identity federation service connection.";
        }
    }
}
