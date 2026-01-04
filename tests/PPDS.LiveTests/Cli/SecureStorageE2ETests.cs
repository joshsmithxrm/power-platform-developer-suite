using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests that specifically exercise the SecureCredentialStore path.
/// These tests do NOT use the PPDS_SPN_SECRET bypass and require a working
/// OS-level secure storage (DPAPI on Windows, Keychain on macOS, libsecret on Linux).
///
/// These tests are skipped in CI (where secure storage is often unavailable/broken)
/// and should be run locally on developer workstations.
///
/// To run these tests locally:
///   dotnet test --filter "Category=SecureStorage"
///
/// To skip these tests in CI:
///   dotnet test --filter "Category!=SecureStorage"
/// </summary>
[Trait("Category", "SecureStorage")]
public class SecureStorageE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Tests that auth create can store credentials in SecureCredentialStore
    /// and plugins list can retrieve them without the PPDS_SPN_SECRET bypass.
    /// </summary>
    [CliE2EWithCredentials]
    public async Task AuthCreate_StoresCredentials_PluginsList_RetrievesThem()
    {
        var profileName = GenerateTestProfileName();

        // Create profile WITHOUT bypass - credentials go to SecureCredentialStore
        var createResult = await RunCliWithoutBypassAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        createResult.ExitCode.Should().Be(0, $"auth create failed: {createResult.StdErr}");

        await RunCliWithoutBypassAsync("auth", "select", "--name", profileName);

        // Run plugins list WITHOUT bypass - should retrieve credentials from SecureCredentialStore
        var listResult = await RunCliWithoutBypassAsync("plugins", "list");

        listResult.ExitCode.Should().Be(0, $"plugins list failed: {listResult.StdErr}");
    }

    /// <summary>
    /// Tests that the 15-second timeout in SecureCredentialStore works correctly.
    /// This test verifies that if SecureCredentialStore hangs, it fails fast with a clear error.
    ///
    /// Note: This test can only verify the timeout works when OS secure storage is slow/broken.
    /// On a healthy system, it will just pass quickly.
    /// </summary>
    [CliE2EWithCredentials]
    public async Task AuthCreate_WithSecureStorage_CompletesWithinTimeout()
    {
        var profileName = GenerateTestProfileName();

        // Time the auth create command - should complete well under 2 minutes
        // If SecureCredentialStore hangs and timeout doesn't work, this would take 120s+
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var createResult = await RunCliWithoutBypassAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        sw.Stop();

        // Either the command succeeds quickly, or it fails with a timeout error
        // It should NOT hang for close to the test's 120s CommandTimeout
        if (createResult.ExitCode != 0)
        {
            // If it failed, it should be a fast failure (timeout or other error)
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
                "SecureCredentialStore should fail fast if it can't initialize, not hang");

            // Error message should be informative
            (createResult.StdErr + createResult.StdOut).Should().ContainAny(
                "timeout", "Timeout", "PPDS_SPN_SECRET", "credential", "storage");
        }
        else
        {
            // If it succeeded, great - secure storage works on this machine
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60),
                "auth create should not take longer than 60s even with slow secure storage");
        }
    }
}
