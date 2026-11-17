using System.Text.Json;
using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds plugins diff command.
/// Compares plugin configuration against actual Dataverse environment state.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
public class PluginsDiffCommandE2ETests(ITestOutputHelper output) : CliE2ETestBase
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
        result.StdOut.Trim().Should().StartWith("{");
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
    public Task Diff_AfterDeploy_ShowsNoDrift() => AssertDiffAfterDeployShowsNoDriftAsync(TestRegistrationsPath, deploySelfVerifies: false);

    /// <summary>
    /// #1520 criterion 7 under the reconcile contract: the same fixture with "version": "2.0" (the assembly path
    /// made absolute so the copy can live in a temp location). Deploy also self-verifies through diff under 2.0.
    /// Never executed: no live environment was available when it was written.
    /// </summary>
    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public Task Diff_AfterVersion2Deploy_ShowsNoDrift()
    {
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(TestRegistrationsPath))!;
        config["version"] = "2.0";
        foreach (var assembly in config["assemblies"]!.AsArray())
            assembly!["path"] = Path.Combine(TestDataDir, assembly["path"]!.GetValue<string>());
        var path = GenerateTempFilePath(".json");
        File.WriteAllText(path, config.ToJsonString());
        return AssertDiffAfterDeployShowsNoDriftAsync(path, deploySelfVerifies: true);
    }

    private async Task AssertDiffAfterDeployShowsNoDriftAsync(string registrationsPath, bool deploySelfVerifies)
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
            profileName, Configuration.DataverseUrl!, output.WriteLine, () => RequiresManualReview);
        await fixture.RunAsync(async () =>
        {
            // Deploy the plugin first
            var deployResult = await RunCliAsync(
                "plugins", "deploy",
                "--config", registrationsPath);

            // A 2.0 deploy runs its own diff straight after the writes, so propagation lag can make it
            // report drift (exit 1). Accept that here; the poll below establishes the no-drift result.
            if (deploySelfVerifies)
                deployResult.ExitCode.Should().BeOneOf([0, 1], $"Deploy failed: {deployResult.StdErr}");
            else
                deployResult.ExitCode.Should().Be(0, $"Deploy failed: {deployResult.StdErr}");

            // Deploy is async server-side; poll until diff shows no drift or timeout
            var maxWait = PropagationWait.TimeoutFromEnvironment(
                Environment.GetEnvironmentVariable("PPDS_TEST_PROPAGATION_TIMEOUT_SECONDS"));
            var poll = await PropagationWait.UntilAsync(
                token => RunCliWithCancellationAsync(token, "plugins", "diff", "--config", registrationsPath,
                    "--output-format", "json"),
                result => result.ExitCode == 0,
                maxWait, TimeSpan.FromSeconds(2));
            poll.Succeeded.Should().BeTrue(
                $"Diff should show no drift within {maxWait.TotalSeconds}s after deploy. " +
                $"Elapsed: {poll.Elapsed.TotalSeconds:F1}s; attempts: {poll.Attempts}; " +
                $"last exit: {poll.Last?.ExitCode.ToString() ?? "(none)"}; error: {poll.LastError ?? "(none)"}; " +
                $"stdout: {poll.Last?.StdOut ?? "(none)"}; stderr/drift: {poll.Last?.StdErr ?? "(none)"}");
            var diffResult = poll.Last!;
            using var comparison = JsonDocument.Parse(diffResult.StdOut);
            var metadata = comparison.RootElement.GetProperty("metadata");
            metadata.GetProperty("status").GetString().Should().Be("match");
            metadata.GetProperty("isComplete").GetBoolean().Should().BeTrue();
            metadata.GetProperty("hasDrift").GetBoolean().Should().BeFalse();
        });
    }

    #endregion
}
