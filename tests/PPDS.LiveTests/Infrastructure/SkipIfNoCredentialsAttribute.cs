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

/// <summary>
/// Skips the test unless client secret credentials AND a target solution + publisher prefix
/// (PPDS_TEST_SOLUTION, PPDS_TEST_PREFIX) are configured. Use for in-process (non-CLI) live tests
/// that create solution-scoped metadata (option sets, columns) whose schema name must satisfy a
/// publisher prefix. Unlike <see cref="CliE2EWithSolutionAttribute"/> this carries no TFM constraint,
/// so the test runs on every target framework.
/// </summary>
public sealed class SkipIfNoSolutionAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips when prerequisites are missing.
    /// </summary>
    public SkipIfNoSolutionAttribute()
    {
        if (!Configuration.HasClientSecretCredentials)
        {
            Skip = "Client secret credentials not configured. Set DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, and PPDS_TEST_TENANT_ID.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION"))
                 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")))
        {
            Skip = "Solution-scoped metadata test requires PPDS_TEST_SOLUTION (target unmanaged solution unique name) and PPDS_TEST_PREFIX (publisher customization prefix, e.g., 'new' or 'hsl').";
        }
    }
}

/// <summary>
/// In-process metadata live tests that create schema in PPDS_TEST_SOLUTION. Like <see cref="SkipIfNoSolutionAttribute"/>
/// but runs on .NET 8.0 only (as the CLI E2E attributes do): the behaviour under test is server-side, so running the
/// same schema-creating test on three target frameworks would only triple the throwaway objects and contend for the
/// organisation's customisation lock. Credentials: the service-principal client-secret settings, or (local runs only)
/// DATAVERSE_URL plus PPDS_TEST_PROFILE, an existing signed-in PPDS auth profile used silently through
/// <see cref="ProfileConnectionSource"/>; the client secret wins when both are set.
/// </summary>
public sealed class MetadataLiveFactAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips when prerequisites are missing.
    /// </summary>
    public MetadataLiveFactAttribute()
    {
        Skip = MetadataLivePrerequisites.MissingReason(Configuration);
    }
}

/// <summary>
/// <see cref="MetadataLiveFactAttribute"/> plus a second provisioned language: requires PPDS_TEST_SECOND_LCID, the
/// LCID (not 1033) of a language provisioned in the target organisation. Without it the test is reported as skipped,
/// never as passed. The test fails with a clear message if that LCID is not actually provisioned.
/// </summary>
public sealed class MetadataLiveMultiLanguageFactAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips when prerequisites are missing.
    /// </summary>
    public MetadataLiveMultiLanguageFactAttribute()
    {
        Skip = MetadataLivePrerequisites.MissingReason(Configuration)
            ?? (MetadataLivePrerequisites.SecondLanguage is null
                ? "Multi-language metadata test requires PPDS_TEST_SECOND_LCID: the LCID (other than 1033) of a language provisioned in the target organisation, e.g. 1036."
                : null);
    }
}

/// <summary>Shared prerequisite checks for the metadata live-test attributes.</summary>
public static class MetadataLivePrerequisites
{
    /// <summary>PPDS_TEST_SECOND_LCID parsed, or null when unset, invalid or 1033.</summary>
    public static int? SecondLanguage =>
        int.TryParse(Environment.GetEnvironmentVariable("PPDS_TEST_SECOND_LCID"), out var lcid) && lcid > 0 && lcid != 1033 ? lcid : null;

    internal static string? MissingReason(LiveTestConfiguration configuration)
    {
#if !NET8_0
        return "Metadata live tests only run on .NET 8.0 (server-side behaviour; one framework avoids triplicate schema).";
#else
        if (!configuration.HasClientSecretCredentials && !configuration.HasProfileCredentials)
            return "Credentials not configured. Set DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, and PPDS_TEST_TENANT_ID; " +
                   "or, locally, DATAVERSE_URL and PPDS_TEST_PROFILE (an existing PPDS auth profile bound to that environment, already signed in).";
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")))
            return "Solution-scoped metadata test requires PPDS_TEST_SOLUTION (target unmanaged solution unique name) and PPDS_TEST_PREFIX (publisher customization prefix, e.g., 'new' or 'hsl').";
        return null;
#endif
    }
}

/// <summary>
/// Fact attribute for CLI E2E tests that mutate solution-scoped metadata. Requires client secret
/// credentials, .NET 8.0, and a configured target solution and publisher prefix
/// (PPDS_TEST_SOLUTION, PPDS_TEST_PREFIX). Use for tests that create custom tables, columns,
/// relationships, etc., where the schema name must satisfy a publisher prefix.
/// </summary>
public sealed class CliE2EWithSolutionAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips when prerequisites are missing.
    /// </summary>
    public CliE2EWithSolutionAttribute()
    {
        if (!IsNet8Runtime())
        {
            Skip = "CLI E2E tests only run on .NET 8.0 (CLI is spawned with --framework net8.0, making other TFMs redundant).";
        }
        else if (!Configuration.HasClientSecretCredentials)
        {
            Skip = "Client secret credentials not configured. Set DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, and PPDS_TEST_TENANT_ID.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION"))
                 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")))
        {
            Skip = "Solution-scoped E2E test requires PPDS_TEST_SOLUTION (target unmanaged solution unique name) and PPDS_TEST_PREFIX (publisher customization prefix, e.g., 'new' or 'hsl').";
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

