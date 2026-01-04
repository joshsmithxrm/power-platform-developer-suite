using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds plugins list command.
/// Lists registered plugins in the connected Dataverse environment.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
public class PluginsListCommandE2ETests : CliE2ETestBase
{
    #region List plugins

    /// <summary>
    /// Tests unfiltered plugin list. Skipped in CI because stock Dataverse has ~60k plugins
    /// and listing all with nested types/steps/images takes 100+ seconds.
    /// Filter tests below verify the same code paths with fast queries.
    /// </summary>
    [CliE2EWithCredentials]
    [Trait("Category", "SlowIntegration")]
    public async Task List_ReturnsSuccess()
    {
        var profileName = GenerateTestProfileName();
        // PPDS_SPN_SECRET is automatically set by RunCliAsync to bypass SecureCredentialStore
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        await RunCliAsync("auth", "select", "--name", profileName);

        var result = await RunCliAsync("plugins", "list");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    /// <summary>
    /// Tests unfiltered plugin list with JSON output. Skipped in CI because stock Dataverse
    /// has ~60k plugins. JSON formatting is verified by unit tests; connectivity by filter tests.
    /// </summary>
    [CliE2EWithCredentials]
    [Trait("Category", "SlowIntegration")]
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

        var result = await RunCliAsync("plugins", "list", "--output-format", "json");

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

        // Filter by a likely non-existent assembly
        var result = await RunCliAsync(
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

        // Filter by a likely non-existent package
        var result = await RunCliAsync(
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
