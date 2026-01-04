using FluentAssertions;
using PPDS.LiveTests.Infrastructure;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds plugins list command.
/// Lists registered plugins in the connected Dataverse environment.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
public class PluginsListCommandE2ETests : CliE2ETestBase
{
    #region List plugins

    [CliE2EWithCredentials]
    public async Task List_ReturnsSuccess()
    {
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        await RunCliAsync("auth", "select", "--name", profileName);

        // Pass PPDS_SPN_SECRET to bypass SecureCredentialStore lookup
        // which calls MsalCacheHelper.CreateAsync() and may hang on Windows CI.
        // The profile still needs to exist (for auth method, tenant, etc.),
        // but the secret is provided via env var instead of credential store.
        var envVars = new Dictionary<string, string>
        {
            ["PPDS_SPN_SECRET"] = Configuration.ClientSecret!
        };
        var result = await RunCliWithEnvAsync(envVars, "plugins", "list");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    [CliE2EWithCredentials]
    public async Task List_JsonFormat_ReturnsValidJson()
    {
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        await RunCliAsync("auth", "select", "--name", profileName);

        // Pass PPDS_SPN_SECRET to bypass SecureCredentialStore lookup
        var envVars = new Dictionary<string, string>
        {
            ["PPDS_SPN_SECRET"] = Configuration.ClientSecret!
        };
        var result = await RunCliWithEnvAsync(envVars, "plugins", "list", "--output-format", "json");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdOut.Trim().Should().StartWith("{");
        // JSON output should have assemblies and packages arrays
        result.StdOut.Should().Contain("\"assemblies\"");
        result.StdOut.Should().Contain("\"packages\"");
    }

    [CliE2EWithCredentials]
    public async Task List_WithAssemblyFilter_FiltersResults()
    {
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        await RunCliAsync("auth", "select", "--name", profileName);

        // Pass PPDS_SPN_SECRET to bypass SecureCredentialStore lookup
        var envVars = new Dictionary<string, string>
        {
            ["PPDS_SPN_SECRET"] = Configuration.ClientSecret!
        };

        // Filter by a likely non-existent assembly
        var result = await RunCliWithEnvAsync(
            envVars,
            "plugins", "list",
            "--assembly", "NonExistentAssembly12345");

        // Should succeed even with no matches
        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    [CliE2EWithCredentials]
    public async Task List_WithPackageFilter_FiltersResults()
    {
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        await RunCliAsync("auth", "select", "--name", profileName);

        // Pass PPDS_SPN_SECRET to bypass SecureCredentialStore lookup
        var envVars = new Dictionary<string, string>
        {
            ["PPDS_SPN_SECRET"] = Configuration.ClientSecret!
        };

        // Filter by a likely non-existent package
        var result = await RunCliWithEnvAsync(
            envVars,
            "plugins", "list",
            "--package", "NonExistentPackage12345");

        // Should succeed even with no matches
        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    #endregion

    #region Error handling

    [CliE2EFact]
    public async Task List_NoProfile_FailsWithError()
    {
        // No profile selected - should fail with helpful message
        var result = await RunCliAsync("plugins", "list");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("profile", "auth", "connect");
    }

    #endregion
}
