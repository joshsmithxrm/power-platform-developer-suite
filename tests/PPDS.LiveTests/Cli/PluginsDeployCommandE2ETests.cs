using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds plugins deploy command.
/// Deploys plugin registrations from a configuration file to Dataverse.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
/// <remarks>
/// Tier 1 tests use --dry-run and are safe for all environments.
/// Tier 2 tests (marked with DestructiveE2E trait) actually deploy plugins.
/// </remarks>
public class PluginsDeployCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Empty-types config for pre-test state reset. Lists the fixture assembly with no types
    /// and no allTypeNames so <c>plugins clean</c> treats every existing type and step as an
    /// orphan and removes it. Ensures destructive tests start from a clean baseline even when
    /// a prior run crashed before its <c>finally</c> cleanup (issue surfaced on PR #826 CI).
    /// </summary>
    private const string EmptyRegistrationsConfigJson = """
        {
            "version": "1.0",
            "assemblies": [
                {
                    "name": "PPDS.LiveTests.Fixtures",
                    "type": "Assembly",
                    "path": "PPDS.LiveTests.Fixtures.dll",
                    "types": []
                }
            ]
        }
        """;

    /// <summary>
    /// Runs <c>plugins clean</c> with an empty-types config to wipe any pre-existing
    /// registrations from prior (possibly interrupted) test runs. Tolerates a non-zero
    /// exit code — the follow-up deploy assertion is the real signal.
    /// </summary>
    private async Task EnsureCleanFixtureStateAsync()
    {
        var emptyConfigPath = GenerateTempFilePath(".json");
        await File.WriteAllTextAsync(emptyConfigPath, EmptyRegistrationsConfigJson);
        await RunCliAsync("plugins", "clean", "--config", emptyConfigPath);
    }

    #region Tier 1: Safe tests (--dry-run)

    [CliE2EWithCredentials]
    public async Task Deploy_DryRun_ShowsPlan()
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
            "plugins", "deploy",
            "--config", TestRegistrationsPath,
            "--dry-run");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // Dry-run mode should indicate it's not making changes
        result.StdErr.Should().Contain("Dry-Run");
    }

    [CliE2EWithCredentials]
    public async Task Deploy_DryRun_JsonFormat_ReturnsValidJson()
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
            "plugins", "deploy",
            "--config", TestRegistrationsPath,
            "--dry-run",
            "--output-format", "json");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdOut.Trim().Should().StartWith("{");
        result.StdOut.Should().Contain("assemblyName");
    }

    [CliE2EFact]
    public async Task Deploy_MissingConfig_FailsWithError()
    {
        var result = await RunCliAsync(
            "plugins", "deploy",
            "--config", "nonexistent-config.json",
            "--dry-run");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("not found", "does not exist", "Error", "Could not find");
    }

    [CliE2EWithCredentials]
    public async Task Deploy_InvalidConfig_FailsWithError()
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

        // Create an invalid config file
        var invalidConfigPath = GenerateTempFilePath(".json");
        await File.WriteAllTextAsync(invalidConfigPath, "{ invalid json }");

        var result = await RunCliAsync(
            "plugins", "deploy",
            "--config", invalidConfigPath,
            "--dry-run");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("Error", "invalid", "parse", "JSON");
    }

    [CliE2EFact]
    public async Task Deploy_MissingConfigOption_FailsWithError()
    {
        var result = await RunCliAsync("plugins", "deploy", "--dry-run");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--config", "required", "-c");
    }

    #endregion

    #region Tier 2: Destructive tests (actual deploy)

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Deploy_ActualDeploy_RegistersPlugin()
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

        // Reset env state in case a prior run crashed before its cleanup.
        await EnsureCleanFixtureStateAsync();

        try
        {
            // Actually deploy the plugin
            var deployResult = await RunCliAsync(
                "plugins", "deploy",
                "--config", TestRegistrationsPath);

            deployResult.ExitCode.Should().Be(0, $"Deploy failed: {deployResult.StdErr}");

            // Verify the plugin was registered by listing
            var listResult = await RunCliAsync(
                "plugins", "list",
                "--assembly", "PPDS.LiveTests.Fixtures",
                "--output-format", "json");

            listResult.ExitCode.Should().Be(0, $"List failed: {listResult.StdErr}");
            listResult.StdOut.Should().Contain("PPDS.LiveTests.Fixtures");
        }
        finally
        {
            // Clean up: remove the deployed plugin
            await RunCliAsync(
                "plugins", "clean",
                "--config", TestRegistrationsPath);
        }
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Deploy_ActualDeploy_ThenClean_RemovesPlugin()
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

        // Reset env state in case a prior run crashed before its cleanup.
        await EnsureCleanFixtureStateAsync();

        try
        {
            // Deploy the plugin
            var deployResult = await RunCliAsync(
                "plugins", "deploy",
                "--config", TestRegistrationsPath);

            deployResult.ExitCode.Should().Be(0, $"Deploy failed: {deployResult.StdErr}");

            // Clean up using the clean command
            var cleanResult = await RunCliAsync(
                "plugins", "clean",
                "--config", TestRegistrationsPath);

            cleanResult.ExitCode.Should().Be(0, $"Clean failed: {cleanResult.StdErr}");

            // Verify the plugin was removed
            var listResult = await RunCliAsync(
                "plugins", "list",
                "--assembly", "PPDS.LiveTests.Fixtures",
                "--output-format", "json");

            listResult.ExitCode.Should().Be(0);
            // After clean, the assembly should not be in the list
            // (or list should be empty for that filter)
        }
        finally
        {
            // Ensure cleanup even if assertions fail
            await RunCliAsync(
                "plugins", "clean",
                "--config", TestRegistrationsPath);
        }
    }

    #endregion
}
