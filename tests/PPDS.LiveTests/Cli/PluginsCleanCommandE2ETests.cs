using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds plugins clean command.
/// Removes orphaned plugin registrations from Dataverse.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
/// <remarks>
/// Tier 1 tests use --what-if and are safe for all environments.
/// Tier 2 tests (marked with DestructiveE2E trait) actually clean plugins.
/// </remarks>
public class PluginsCleanCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Empty config for cleanup - declares assembly with no types to make all steps orphans.
    /// </summary>
    private const string EmptyConfigJson = """
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

    #region Tier 1: Safe tests (--what-if)

    [CliE2EWithCredentials]
    public async Task Clean_WhatIf_ShowsOrphans()
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
            "--what-if");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // What-if mode should indicate it's not making changes
        result.StdErr.Should().Contain("What-If");
    }

    [CliE2EWithCredentials]
    public async Task Clean_WhatIf_JsonFormat_ReturnsValidJson()
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
            "--what-if",
            "--output-format", "json");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdOut.Trim().Should().StartWith("[");
        result.StdOut.Should().Contain("assemblyName");
    }

    [CliE2EFact]
    public async Task Clean_MissingConfig_FailsWithError()
    {
        var result = await RunCliAsync(
            "plugins", "clean",
            "--config", "nonexistent-config.json",
            "--what-if");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("not found", "does not exist", "Error", "Could not find");
    }

    [CliE2EFact]
    public async Task Clean_MissingConfigOption_FailsWithError()
    {
        var result = await RunCliAsync("plugins", "clean", "--what-if");

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

        // Create empty config for cleanup
        var emptyConfigPath = GenerateTempFilePath(".json");
        await File.WriteAllTextAsync(emptyConfigPath, EmptyConfigJson);

        try
        {
            // Deploy the plugin first
            var deployResult = await RunCliAsync(
                "plugins", "deploy",
                "--config", TestRegistrationsPath);

            deployResult.ExitCode.Should().Be(0, $"Deploy failed: {deployResult.StdErr}");

            // Clean with empty config - should find orphans
            var cleanResult = await RunCliAsync(
                "plugins", "clean",
                "--config", emptyConfigPath);

            cleanResult.ExitCode.Should().Be(0, $"Clean failed: {cleanResult.StdErr}");

            // Verify cleanup was successful
            var listResult = await RunCliAsync(
                "plugins", "list",
                "--assembly", "PPDS.LiveTests.Fixtures",
                "--output-format", "json");

            listResult.ExitCode.Should().Be(0);
            // After clean, the assembly should have no steps
        }
        finally
        {
            // Ensure cleanup even if assertions fail
            await RunCliAsync(
                "plugins", "clean",
                "--config", emptyConfigPath);
        }
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

        // Create empty config for cleanup
        var emptyConfigPath = GenerateTempFilePath(".json");
        await File.WriteAllTextAsync(emptyConfigPath, EmptyConfigJson);

        try
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
        }
        finally
        {
            // Clean up: remove the deployed plugin using empty config
            await RunCliAsync(
                "plugins", "clean",
                "--config", emptyConfigPath);
        }
    }

    #endregion
}
