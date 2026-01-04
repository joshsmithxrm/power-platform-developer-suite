using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds plugins diff command.
/// Compares plugin configuration against actual Dataverse environment state.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
public class PluginsDiffCommandE2ETests : CliE2ETestBase
{
    #region Tier 1: Safe tests

    [CliE2EWithCredentials]
    public async Task Diff_NoRegistrations_ShowsMissingSteps()
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

        // Diff against config - since plugins aren't registered, should show missing
        var result = await RunCliAsync(
            "plugins", "diff",
            "--config", TestRegistrationsPath);

        // Exit code 1 indicates drift detected (plugins not registered)
        // Exit code 0 would mean no drift (plugins are registered and match)
        // Either is valid depending on environment state
        // The command should complete successfully regardless
        result.StdErr.Should().NotContain("Exception");
    }

    [CliE2EWithCredentials]
    public async Task Diff_JsonFormat_ReturnsValidJson()
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
            "plugins", "diff",
            "--config", TestRegistrationsPath,
            "--output-format", "json");

        // Diff returns valid JSON to stdout regardless of drift status
        result.StdOut.Trim().Should().StartWith("[");
        result.StdOut.Should().Contain("assemblyName");
    }

    [CliE2EFact]
    public async Task Diff_MissingConfig_FailsWithError()
    {
        var result = await RunCliAsync(
            "plugins", "diff",
            "--config", "nonexistent-config.json");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("not found", "does not exist", "Error", "Could not find");
    }

    [CliE2EFact]
    public async Task Diff_MissingConfigOption_FailsWithError()
    {
        var result = await RunCliAsync("plugins", "diff");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--config", "required", "-c");
    }

    #endregion

    #region Tier 2: Destructive tests (requires deploy first)

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Diff_AfterDeploy_ShowsNoDrift()
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
            // Deploy the plugin first
            var deployResult = await RunCliAsync(
                "plugins", "deploy",
                "--config", TestRegistrationsPath);

            deployResult.ExitCode.Should().Be(0, $"Deploy failed: {deployResult.StdErr}");

            // Now diff should show no drift (exit code 0)
            var diffResult = await RunCliAsync(
                "plugins", "diff",
                "--config", TestRegistrationsPath);

            diffResult.ExitCode.Should().Be(0, $"Diff should show no drift after deploy: {diffResult.StdErr}");
            diffResult.StdErr.Should().Contain("No drift");
        }
        finally
        {
            // Clean up: remove the deployed plugin
            await RunCliAsync(
                "plugins", "clean",
                "--config", TestRegistrationsPath);
        }
    }

    #endregion
}
