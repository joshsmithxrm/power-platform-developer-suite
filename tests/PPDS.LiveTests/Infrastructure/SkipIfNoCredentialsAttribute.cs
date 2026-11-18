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
/// DATAVERSE_URL plus PPDS_TEST_PROFILE, an existing signed-in InteractiveBrowser PPDS auth profile used silently through
/// <see cref="LiveTestProfileConnectionSource"/>; the client secret wins when both are set.
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
/// <see cref="MetadataLiveFactAttribute"/> for a test that can also decide at run time that it does not apply to the
/// connected organisation (for example a regression that needs a base language other than 1033): it calls
/// <c>Skip.If</c>/<c>Skip.IfNot</c> with a reason and is then reported as skipped with that reason, never as passed.
/// </summary>
public sealed class MetadataLiveSkippableFactAttribute : SkippableFactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips when prerequisites are missing.
    /// </summary>
    public MetadataLiveSkippableFactAttribute()
    {
        Skip = MetadataLivePrerequisites.MissingReason(Configuration);
    }
}

/// <summary>
/// The opt-in test that pins Dataverse's second-language label window (MetadataSecondLanguageWindowLiveTests): needs the
/// metadata live-test settings, PPDS_TEST_SECOND_LCID and PPDS_TEST_LABEL_WINDOW=1, and is meant to run in a test
/// process of its own. Without them it is reported as skipped; it can also skip at run time with the reason.
/// </summary>
public sealed class MetadataLiveWindowFactAttribute : SkippableFactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips when prerequisites are missing.
    /// </summary>
    public MetadataLiveWindowFactAttribute()
    {
        Skip = MetadataLivePrerequisites.MissingReason(Configuration)
            ?? (MetadataLivePrerequisites.SecondLanguage is null ? "Requires PPDS_TEST_SECOND_LCID (a provisioned language other than the base language)." : null)
            ?? (Environment.GetEnvironmentVariable("PPDS_TEST_LABEL_WINDOW")?.Trim() == "1" ? null
                : "Opt-in (PPDS_TEST_LABEL_WINDOW=1): pins Dataverse's second-language label window; run it on its own so its table create is the first of the test process.");
    }
}

/// <summary>
/// <see cref="MetadataLiveFactAttribute"/> plus a second provisioned language: requires PPDS_TEST_SECOND_LCID, the
/// LCID of a provisioned language other than the organisation's base language (checked at run time). Without it the test is reported as skipped,
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
                ? "Multi-language metadata test requires PPDS_TEST_SECOND_LCID: the LCID of a provisioned language other than the organisation's base language, e.g. 1036 on an English-base organisation."
                : null);
    }
}

/// <summary>
/// <see cref="MetadataLiveMultiLanguageFactAttribute"/> plus the explicit opt-in PPDS_TEST_SWITCH_UI_LANGUAGE=1. These
/// tests temporarily switch the CALLING user's Dataverse UI language (usersettings.uilanguageid, nothing else) to
/// PPDS_TEST_SECOND_LCID and restore it afterwards (<see cref="CallerUiLanguageSwitch"/>). Without the opt-in they are
/// reported as skipped, never as passed.
/// </summary>
public sealed class MetadataLiveSwitchUiLanguageFactAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    /// <summary>
    /// Initializes a new instance that skips when prerequisites are missing.
    /// </summary>
    public MetadataLiveSwitchUiLanguageFactAttribute()
    {
        Skip = MetadataLivePrerequisites.MissingReason(Configuration)
            ?? (MetadataLivePrerequisites.SecondLanguage is null
                ? "UI-language metadata test requires PPDS_TEST_SECOND_LCID: the LCID of a provisioned language other than the organisation's base language, e.g. 1036 on an English-base organisation."
                : null)
            ?? (MetadataLivePrerequisites.SwitchUiLanguage
                ? null
                : "UI-language metadata test requires the opt-in PPDS_TEST_SWITCH_UI_LANGUAGE=1: it temporarily sets the calling user's " +
                  "Dataverse UI language (usersettings.uilanguageid) to PPDS_TEST_SECOND_LCID and restores the original value afterwards. " +
                  "Run these tests on their own (filter FullyQualifiedName~MetadataGlobalChoiceLiveTests.FrenchCaller_).");
    }
}

/// <summary>Shared prerequisite checks for the metadata live-test attributes.</summary>
public static class MetadataLivePrerequisites
{
    /// <summary>True only when PPDS_TEST_SWITCH_UI_LANGUAGE is exactly "1".</summary>
    public static bool SwitchUiLanguage => Environment.GetEnvironmentVariable("PPDS_TEST_SWITCH_UI_LANGUAGE")?.Trim() == "1";

    /// <summary>PPDS_TEST_SECOND_LCID parsed, or null when unset or invalid. The test checks at run time that it is provisioned and is not the base language.</summary>
    public static int? SecondLanguage =>
        int.TryParse(Environment.GetEnvironmentVariable("PPDS_TEST_SECOND_LCID"), out var lcid) && lcid > 0 ? lcid : null;

    internal static string? MissingReason(LiveTestConfiguration configuration)
    {
#if !NET8_0
        return "Metadata live tests only run on .NET 8.0 (server-side behaviour; one framework avoids triplicate schema).";
#else
        if (!configuration.HasClientSecretCredentials && !configuration.HasProfileCredentials)
            return "Credentials not configured. Set DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, and PPDS_TEST_TENANT_ID; " +
                   "or, locally, DATAVERSE_URL and PPDS_TEST_PROFILE (an existing InteractiveBrowser PPDS auth profile bound to that environment, already signed in).";
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

