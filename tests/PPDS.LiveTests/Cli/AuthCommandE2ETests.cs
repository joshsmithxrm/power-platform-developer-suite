using FluentAssertions;
using PPDS.LiveTests.Infrastructure;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds auth commands.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
public class AuthCommandE2ETests : CliE2ETestBase
{
    #region auth list

    [CliE2EFact]
    public async Task AuthList_ReturnsSuccessExitCode()
    {
        // auth list should work even with no profiles
        var result = await RunCliAsync("auth", "list");

        result.ExitCode.Should().Be(0);
    }

    [CliE2EFact]
    public async Task AuthList_JsonFormat_ReturnsValidJson()
    {
        var result = await RunCliAsync("auth", "list", "--output-format", "json");

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("\"profiles\"");
        // Note: activeIndex is omitted when null (no profiles) due to WhenWritingNull
        result.StdOut.Trim().Should().StartWith("{");
    }

    #endregion

    #region auth who

    [CliE2EFact]
    public async Task AuthWho_NoActiveProfile_ReturnsSuccessWithMessage()
    {
        // If there's no active profile, auth who should still succeed but indicate no profile
        var result = await RunCliAsync("auth", "who");

        // Exit code is 0 even when no profile (graceful handling)
        result.ExitCode.Should().Be(0);
        // Should mention something about profile or connected status
        (result.StdOut + result.StdErr).Should().NotBeEmpty();
    }

    [CliE2EFact]
    public async Task AuthWho_JsonFormat_ReturnsValidJson()
    {
        var result = await RunCliAsync("auth", "who", "--output-format", "json");

        result.ExitCode.Should().Be(0);
        // Output should be valid JSON (starts with { or contains "active")
        result.StdOut.Trim().Should().StartWith("{");
    }

    #endregion

    #region auth create with client secret

    [CliE2EWithCredentials]
    public async Task AuthCreate_WithClientSecret_CreatesProfile()
    {
        var profileName = GenerateTestProfileName();

        var result = await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdOut.Should().Contain("Profile created");
    }

    [CliE2EWithCredentials]
    public async Task AuthCreate_DuplicateName_Fails()
    {
        var profileName = GenerateTestProfileName();

        // Create first profile
        var result1 = await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        result1.ExitCode.Should().Be(0);

        // Try to create duplicate
        var result2 = await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        result2.ExitCode.Should().NotBe(0);
        result2.StdErr.Should().Contain("already in use");
    }

    [CliE2EFact]
    public async Task AuthCreate_MissingRequiredArgs_Fails()
    {
        // Client secret auth requires --applicationId, --clientSecret, --tenant, --environment
        var result = await RunCliAsync(
            "auth", "create",
            "--clientSecret", "some-secret");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("applicationId", "tenant", "required");
    }

    #endregion

    #region auth delete

    [CliE2EWithCredentials]
    public async Task AuthDelete_ExistingProfile_DeletesSuccessfully()
    {
        var profileName = GenerateTestProfileName();

        // Create profile
        var createResult = await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        createResult.ExitCode.Should().Be(0);

        // Remove from cleanup list since we're deleting it manually
        CreatedProfiles.Remove(profileName);

        // Delete profile
        var deleteResult = await RunCliAsync("auth", "delete", "--name", profileName);

        deleteResult.ExitCode.Should().Be(0);
        deleteResult.StdOut.Should().Contain("deleted");
    }

    [CliE2EFact]
    public async Task AuthDelete_NonExistentProfile_Fails()
    {
        var result = await RunCliAsync("auth", "delete", "--name", "nonexistent-profile-12345");

        result.ExitCode.Should().NotBe(0);
        result.StdErr.Should().Contain("not found");
    }

    [CliE2EFact]
    public async Task AuthDelete_NoIdentifier_Fails()
    {
        var result = await RunCliAsync("auth", "delete");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--index", "--name", "provide");
    }

    #endregion

    #region auth select

    [CliE2EWithCredentials]
    public async Task AuthSelect_ByName_SelectsProfile()
    {
        var profileName = GenerateTestProfileName();

        // Create profile
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        // Select by name
        var result = await RunCliAsync("auth", "select", "--name", profileName);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("Active profile");
    }

    [CliE2EFact]
    public async Task AuthSelect_NonExistentProfile_Fails()
    {
        var result = await RunCliAsync("auth", "select", "--name", "nonexistent-profile-xyz");

        result.ExitCode.Should().NotBe(0);
        result.StdErr.Should().Contain("not found");
    }

    #endregion

    #region auth clear

    [CliE2EFact]
    public async Task AuthClear_NoProfiles_Succeeds()
    {
        // Clear should succeed even when there are no profiles
        // Note: This test might affect other profiles if run in parallel, hence sequential collection
        var result = await RunCliAsync("auth", "clear");

        result.ExitCode.Should().Be(0);
    }

    [CliE2EWithCredentials]
    public async Task AuthClear_ClearsStoredCredentials()
    {
        var profileName = GenerateTestProfileName();

        // Create profile with credentials
        var createResult = await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        createResult.ExitCode.Should().Be(0);

        // Verify credential file was created
        var credentialFile = Path.Combine(IsolatedConfigDir, "ppds.credentials.dat");
        File.Exists(credentialFile).Should().BeTrue("credential file should exist after creating profile");
        var sizeBeforeClear = new FileInfo(credentialFile).Length;
        sizeBeforeClear.Should().BeGreaterThan(0, "credential file should have content");

        // Remove from cleanup list since we're clearing everything
        CreatedProfiles.Remove(profileName);

        // Clear all profiles
        var clearResult = await RunCliAsync("auth", "clear");

        clearResult.ExitCode.Should().Be(0);
        clearResult.StdOut.Should().Contain("stored credentials removed");

        // Credential file should be deleted by ClearAsync
        File.Exists(credentialFile).Should().BeFalse("credential file should be deleted after auth clear");
    }

    #endregion

    #region auth delete credential cleanup

    [CliE2EWithCredentials]
    public async Task AuthDelete_RemovesStoredCredentials()
    {
        var profileName = GenerateTestProfileName();

        // Create profile with credentials
        var createResult = await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        createResult.ExitCode.Should().Be(0);

        // Verify credential file was created and has content
        var credentialFile = Path.Combine(IsolatedConfigDir, "ppds.credentials.dat");
        File.Exists(credentialFile).Should().BeTrue("credential file should exist after creating profile");

        // Read the credential file content to check later
        var contentBefore = await File.ReadAllTextAsync(credentialFile);
        contentBefore.Should().Contain(Configuration.ApplicationId!.ToLowerInvariant(),
            "credential file should contain the application ID");

        // Remove from cleanup list since we're deleting it manually
        CreatedProfiles.Remove(profileName);

        // Delete the profile
        var deleteResult = await RunCliAsync("auth", "delete", "--name", profileName);

        deleteResult.ExitCode.Should().Be(0);
        deleteResult.StdOut.Should().Contain("deleted");

        // After delete, credential file might still exist but shouldn't contain this app's credentials
        if (File.Exists(credentialFile))
        {
            var contentAfter = await File.ReadAllTextAsync(credentialFile);
            contentAfter.Should().NotContain(Configuration.ApplicationId!.ToLowerInvariant(),
                "credential for deleted profile should be removed from credential file");
        }
    }

    #endregion

    #region auth name validation

    [CliE2EFact]
    public async Task AuthCreate_InvalidProfileName_TooLong_Fails()
    {
        var longName = new string('a', 35); // > 30 chars

        var result = await RunCliAsync(
            "auth", "create",
            "--name", longName,
            "--deviceCode"); // Use device code to avoid needing credentials

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("30");
    }

    [CliE2EFact]
    public async Task AuthCreate_InvalidProfileName_SpecialChars_Fails()
    {
        var result = await RunCliAsync(
            "auth", "create",
            "--name", "test@profile!",
            "--deviceCode");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("letter", "number", "invalid");
    }

    #endregion
}

/// <summary>
/// Extension methods for FluentAssertions string assertions.
/// </summary>
internal static class StringAssertionExtensions
{
    public static void ContainAny(this FluentAssertions.Primitives.StringAssertions assertions, params string[] values)
    {
        var subject = assertions.Subject;
        var containsAny = values.Any(v => subject.Contains(v, StringComparison.OrdinalIgnoreCase));
        containsAny.Should().BeTrue(
            $"Expected string to contain any of [{string.Join(", ", values)}] but was: {subject}");
    }
}
