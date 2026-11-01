using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds plugins clean command.
/// Removes orphaned plugin registrations from Dataverse.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
/// <remarks>
/// Tier 1 tests use --dry-run and are safe for all environments.
/// Tier 2 tests (marked with DestructiveE2E trait) actually clean plugins.
/// </remarks>
public class PluginsCleanCommandE2ETests(ITestOutputHelper output) : CliE2ETestBase
{
    #region Tier 1: Safe tests (--dry-run)

    [CliE2EWithCredentials]
    public async Task Clean_DryRun_ShowsOrphans()
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

        var result = await RunCliAsync(
            "plugins", "clean",
            "--config", TestRegistrationsPath,
            "--dry-run");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // Dry-run mode should indicate it's not making changes
        result.StdErr.Should().Contain("Dry-Run");
    }

    [CliE2EWithCredentials]
    public async Task Clean_DryRun_JsonFormat_ReturnsValidJson()
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

        var result = await RunCliAsync(
            "plugins", "clean",
            "--config", TestRegistrationsPath,
            "--dry-run",
            "--output-format", "json");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdOut.Trim().Should().StartWith("{");
        result.StdOut.Should().Contain("assemblyName");
    }

    [CliE2EFact]
    public async Task Clean_MissingConfig_FailsWithError()
    {
        var result = await RunCliAsync(
            "plugins", "clean",
            "--config", "nonexistent-config.json",
            "--dry-run");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("not found", "does not exist", "Error", "Could not find");
    }

    [CliE2EFact]
    public async Task Clean_MissingConfigOption_FailsWithError()
    {
        var result = await RunCliAsync("plugins", "clean", "--dry-run");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--config", "required", "-c");
    }

    #endregion

    #region Tier 2: Destructive tests (actual clean)

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Clean_ActualClean_RemovesOrphans()
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

        var fixture = new PluginFixtureCleanup(RunCliWithCancellationAsync, GenerateTempFilePath(".json"),
            profileName, Configuration.DataverseUrl!, output.WriteLine);
        await fixture.RunAsync(async () =>
        {
            // Deploy the plugin first
            var deployResult = await RunCliAsync(
                "plugins", "deploy",
                "--config", TestRegistrationsPath);

            deployResult.ExitCode.Should().Be(0, $"Deploy failed: {deployResult.StdErr}");

            // The empty config makes the fixture registrations orphaned; read back their absence.
            await fixture.CleanupAsync();
        });
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Clean_AfterDeploy_NoOrphans()
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

        var fixture = new PluginFixtureCleanup(RunCliWithCancellationAsync, GenerateTempFilePath(".json"),
            profileName, Configuration.DataverseUrl!, output.WriteLine);
        await fixture.RunAsync(async () =>
        {
            // Deploy the plugin
            var deployResult = await RunCliAsync(
                "plugins", "deploy",
                "--config", TestRegistrationsPath);

            deployResult.ExitCode.Should().Be(0, $"Deploy failed: {deployResult.StdErr}");

            // Clean with same config - should find no orphans
            var cleanResult = await RunCliAsync(
                "plugins", "clean",
                "--config", TestRegistrationsPath);

            cleanResult.ExitCode.Should().Be(0, $"Clean failed: {cleanResult.StdErr}");
            cleanResult.StdErr.Should().Contain("No orphaned");
        });
    }

    #endregion
}
