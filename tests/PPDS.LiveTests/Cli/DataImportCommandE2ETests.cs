using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds data import command.
/// Imports data from CMT-format ZIP files into a Dataverse environment.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
/// <remarks>
/// Tier 1 tests validate option parsing and file existence (no credentials).
/// Tier 2 tests perform a full schema -> export -> delete -> import round-trip
/// against a live Dataverse environment and are marked DestructiveE2E.
/// </remarks>
public class DataImportCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Unique identifier prefix for test records to avoid conflicts.
    /// </summary>
    private readonly string _testPrefix = $"PPDS_E2EImport_{Guid.NewGuid():N}";

    /// <summary>
    /// Tracks account IDs created during tests for cleanup.
    /// </summary>
    private readonly List<Guid> _createdAccountIds = new();

    /// <summary>
    /// Profile name used across tests in this class.
    /// </summary>
    private string? _profileName;

    /// <summary>
    /// Import + export round-trips can take several minutes against a real environment.
    /// </summary>
    protected override TimeSpan CommandTimeout => TimeSpan.FromMinutes(5);

    #region Tier 1: Validation tests (no credentials needed)

    [CliE2EFact]
    public async Task Import_MissingData_FailsWithError()
    {
        var result = await RunCliAsync("data", "import");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--data", "required");
    }

    [CliE2EFact]
    public async Task Import_DataFileNotFound_FailsWithError()
    {
        var nonexistentZip = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.zip");

        var result = await RunCliAsync(
            "data", "import",
            "--data", nonexistentZip);

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("does not exist", "not found", "File");
    }

    [CliE2EFact]
    public async Task Import_InvalidMode_FailsWithError()
    {
        // Provide a placeholder file so AcceptExistingOnly passes; we want the
        // failure to come from the --mode enum parser, not from --data validation.
        var zipPath = GenerateTempFilePath(".zip");
        await File.WriteAllBytesAsync(zipPath, Array.Empty<byte>());

        var result = await RunCliAsync(
            "data", "import",
            "--data", zipPath,
            "--mode", "Bogus");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("Bogus", "mode", "Create");
    }

    [CliE2EFact]
    public async Task Import_InvalidBypassPlugins_FailsWithError()
    {
        var zipPath = GenerateTempFilePath(".zip");
        await File.WriteAllBytesAsync(zipPath, Array.Empty<byte>());

        var result = await RunCliAsync(
            "data", "import",
            "--data", zipPath,
            "--bypass-plugins", "notavalue");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("bypass-plugins", "sync", "async", "all");
    }

    [CliE2EFact]
    public async Task Import_UserMappingNotFound_FailsWithError()
    {
        var zipPath = GenerateTempFilePath(".zip");
        await File.WriteAllBytesAsync(zipPath, Array.Empty<byte>());

        var result = await RunCliAsync(
            "data", "import",
            "--data", zipPath,
            "--user-mapping", "nonexistent.xml");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("user mapping", "not found", "does not exist");
    }

    #endregion

    #region Tier 2: Live Dataverse round-trip tests

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Import_ValidZip_ImportsAccount()
    {
        await EnsureProfileAsync();

        // 1. Create the account.
        var accountName = $"{_testPrefix}_Import_{Guid.NewGuid():N}";
        var accountId = await LiveTestHelpers.CreateAccountAsync(Configuration, accountName);
        _createdAccountIds.Add(accountId);

        // 2. Generate a schema scoped to just this account via filter.
        var schemaPath = GenerateTempFilePath(".xml");
        var schemaResult = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", schemaPath,
            "--filter", $"account:name eq '{accountName}'",
            "--profile", _profileName!);
        schemaResult.ExitCode.Should().Be(0, $"schema failed: {schemaResult.StdErr}");

        // 3. Export the data using that schema.
        var zipPath = GenerateTempFilePath(".zip");
        var exportResult = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", zipPath,
            "--profile", _profileName!);
        exportResult.ExitCode.Should().Be(0, $"export failed: {exportResult.StdErr}");
        File.Exists(zipPath).Should().BeTrue("export should have produced a zip");

        // 4. Delete the account so the import can recreate it.
        await LiveTestHelpers.DeleteAccountsAsync(Configuration, new[] { accountId });
        _createdAccountIds.Remove(accountId);

        // 5. Import the data back.
        var importResult = await RunCliAsync(
            "data", "import",
            "--data", zipPath,
            "--mode", "Upsert",
            "--profile", _profileName!);

        importResult.ExitCode.Should().Be(0, $"import failed: {importResult.StdErr}");

        // 6. Verify the account exists again, and re-track for cleanup.
        var exists = await LiveTestHelpers.AccountExistsAsync(Configuration, accountId);
        exists.Should().BeTrue("account should have been re-imported");

        _createdAccountIds.Add(accountId);
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Import_JsonFormat_ProducesJsonOutput()
    {
        await EnsureProfileAsync();

        // Mini round-trip: create -> schema -> export -> delete -> import (json)
        var accountName = $"{_testPrefix}_ImportJson_{Guid.NewGuid():N}";
        var accountId = await LiveTestHelpers.CreateAccountAsync(Configuration, accountName);
        _createdAccountIds.Add(accountId);

        var schemaPath = GenerateTempFilePath(".xml");
        var schemaResult = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", schemaPath,
            "--filter", $"account:name eq '{accountName}'",
            "--profile", _profileName!);
        schemaResult.ExitCode.Should().Be(0, $"schema failed: {schemaResult.StdErr}");

        var zipPath = GenerateTempFilePath(".zip");
        var exportResult = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", zipPath,
            "--profile", _profileName!);
        exportResult.ExitCode.Should().Be(0, $"export failed: {exportResult.StdErr}");

        await LiveTestHelpers.DeleteAccountsAsync(Configuration, new[] { accountId });
        _createdAccountIds.Remove(accountId);

        var importResult = await RunCliAsync(
            "data", "import",
            "--data", zipPath,
            "--output-format", "json",
            "--profile", _profileName!);

        importResult.ExitCode.Should().Be(0, $"import failed: {importResult.StdErr}");
        // JSON format streams progress objects to stderr per output conventions
        // (mirrors DataSchema_JsonFormat_OutputsProgress).
        importResult.StdErr.Should().Contain("{");

        _createdAccountIds.Add(accountId);
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Import_WithProfileOption_UsesSpecifiedProfile()
    {
        // Create profile but DO NOT auth select - rely solely on --profile.
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        // Set up data via the round-trip pattern, all driven through --profile.
        var accountName = $"{_testPrefix}_ImportProf_{Guid.NewGuid():N}";
        var accountId = await LiveTestHelpers.CreateAccountAsync(Configuration, accountName);
        _createdAccountIds.Add(accountId);

        var schemaPath = GenerateTempFilePath(".xml");
        var schemaResult = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", schemaPath,
            "--filter", $"account:name eq '{accountName}'",
            "--profile", profileName);
        schemaResult.ExitCode.Should().Be(0, $"schema failed: {schemaResult.StdErr}");

        var zipPath = GenerateTempFilePath(".zip");
        var exportResult = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", zipPath,
            "--profile", profileName);
        exportResult.ExitCode.Should().Be(0, $"export failed: {exportResult.StdErr}");

        await LiveTestHelpers.DeleteAccountsAsync(Configuration, new[] { accountId });
        _createdAccountIds.Remove(accountId);

        var importResult = await RunCliAsync(
            "data", "import",
            "--data", zipPath,
            "--profile", profileName);

        importResult.ExitCode.Should().Be(0, $"import failed: {importResult.StdErr}");

        _createdAccountIds.Add(accountId);
    }

    #endregion

    #region Helper Methods

    private async Task EnsureProfileAsync()
    {
        if (_profileName != null) return;

        _profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", _profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        await RunCliAsync("auth", "select", "--name", _profileName);
    }

    public override async Task DisposeAsync()
    {
        // Clean up any test accounts that weren't deleted by tests
        if (_createdAccountIds.Count > 0)
        {
            try
            {
                await LiveTestHelpers.DeleteAccountsAsync(Configuration, _createdAccountIds);
            }
            catch (Exception)
            {
                // Ignore cleanup errors
            }
        }

        await base.DisposeAsync();
    }

    #endregion
}
