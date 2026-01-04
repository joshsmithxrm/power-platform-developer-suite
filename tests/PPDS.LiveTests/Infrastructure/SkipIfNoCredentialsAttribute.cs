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

/// <summary>
/// Fact attribute for CLI E2E tests that only runs on .NET 8.0.
/// CLI E2E tests spawn the CLI with --framework net8.0, so running them on other TFMs
/// is redundant and wastes CI time. Use this instead of [Fact] in CLI test classes.
/// </summary>
public sealed class CliE2EFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance that skips on non-.NET 8.0 runtimes.
    /// </summary>
    public CliE2EFactAttribute()
    {
        if (!IsNet8Runtime())
        {
            Skip = "CLI E2E tests only run on .NET 8.0 (CLI is spawned with --framework net8.0, making other TFMs redundant).";
        }
    }

    private static bool IsNet8Runtime()
    {
#if NET8_0
        return true;
#else
        return false;
#endif
    }
}

/// <summary>
/// Fact attribute for CLI E2E tests that require client secret credentials and only run on .NET 8.0.
/// Combines the TFM check from <see cref="CliE2EFactAttribute"/> with credential check.
/// Use this instead of [SkipIfNoClientSecret] in CLI test classes.
/// </summary>
public sealed class CliE2EWithCredentialsAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips on non-.NET 8.0 or missing credentials.
    /// </summary>
    public CliE2EWithCredentialsAttribute()
    {
        if (!IsNet8Runtime())
        {
            Skip = "CLI E2E tests only run on .NET 8.0 (CLI is spawned with --framework net8.0, making other TFMs redundant).";
        }
        else if (!Configuration.HasClientSecretCredentials)
        {
            Skip = "Client secret credentials not configured. Set DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, and PPDS_TEST_TENANT_ID.";
        }
    }

    private static bool IsNet8Runtime()
    {
#if NET8_0
        return true;
#else
        return false;
#endif
    }
}

