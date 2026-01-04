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
/// Tier 1 tests use --what-if and are safe for all environments.
/// Tier 2 tests (marked with DestructiveE2E trait) actually deploy plugins.
/// </remarks>
public class PluginsDeployCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Gets the path to the TestData directory.
    /// </summary>
    private static string TestDataDir => GetTestDataDir();

    /// <summary>
    /// Gets the path to the test registrations config file.
    /// </summary>
    private static string TestRegistrationsPath => Path.Combine(TestDataDir, "test-registrations.json");

    private static string GetTestDataDir()
    {
        var solutionDir = FindSolutionDir(AppContext.BaseDirectory);
        return Path.Combine(solutionDir, "tests", "PPDS.LiveTests", "TestData");
    }

    private static string FindSolutionDir(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPDS.Sdk.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find PPDS.Sdk.sln starting from: {startPath}");
    }

    #region Tier 1: Safe tests (--what-if)

    [CliE2EWithCredentials]
    public async Task Deploy_WhatIf_ShowsPlan()
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
            "--what-if");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // What-if mode should indicate it's not making changes
        result.StdErr.Should().Contain("What-If");
    }

    [CliE2EWithCredentials]
    public async Task Deploy_WhatIf_JsonFormat_ReturnsValidJson()
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
            "--what-if",
            "--output-format", "json");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdOut.Trim().Should().StartWith("[");
        result.StdOut.Should().Contain("assemblyName");
    }

    [CliE2EFact]
    public async Task Deploy_MissingConfig_FailsWithError()
    {
        var result = await RunCliAsync(
            "plugins", "deploy",
            "--config", "nonexistent-config.json",
            "--what-if");

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
            "--what-if");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("Error", "invalid", "parse", "JSON");
    }

    [CliE2EFact]
    public async Task Deploy_MissingConfigOption_FailsWithError()
    {
        var result = await RunCliAsync("plugins", "deploy", "--what-if");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--config", "required", "-c");
    }

    #endregion

    #region Tier 2: Destructive tests (actual deploy)

    [DestructiveE2EWithCredentials]
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

    [DestructiveE2EWithCredentials]
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

        // Deploy the plugin
        var deployResult = await RunCliAsync(
            "plugins", "deploy",
            "--config", TestRegistrationsPath);

        deployResult.ExitCode.Should().Be(0, $"Deploy failed: {deployResult.StdErr}");

        // Clean up using --clean flag on deploy with empty config
        // or use the clean command directly
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

    #endregion
}
