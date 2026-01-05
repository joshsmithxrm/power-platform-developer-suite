using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds data delete command.
/// Deletes records from Dataverse entities.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
/// <remarks>
/// Tier 1 tests use --dry-run and are safe for all environments.
/// Tier 2 tests (marked with DestructiveE2E trait) actually delete records.
/// </remarks>
public class DataDeleteCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Unique identifier prefix for test records to avoid conflicts.
    /// </summary>
    private readonly string _testPrefix = $"PPDS_E2EDelete_{Guid.NewGuid():N}";

    /// <summary>
    /// Tracks account IDs created during tests for cleanup.
    /// </summary>
    private readonly List<Guid> _createdAccountIds = new();

    /// <summary>
    /// Profile name used across tests in this class.
    /// </summary>
    private string? _profileName;

    #region Tier 1: Validation tests (no credentials needed)

    [CliE2EFact]
    public async Task Delete_MissingEntity_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "delete",
            "--id", Guid.NewGuid().ToString());

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--entity", "-e", "required");
    }

    [CliE2EFact]
    public async Task Delete_MissingInputMode_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--id", "--key", "--file", "--filter");
    }

    [CliE2EFact]
    public async Task Delete_MultipleInputModes_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", Guid.NewGuid().ToString(),
            "--filter", "name like '%test%'");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("Only one", "input mode");
    }

    [CliE2EFact]
    public async Task Delete_FileNotFound_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--file", "nonexistent-file.csv");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("not found", "does not exist", "File");
    }

    [CliE2EFact]
    public async Task Delete_InvalidBatchSize_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", Guid.NewGuid().ToString(),
            "--batch-size", "0");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("batch-size");
    }

    [CliE2EFact]
    public async Task Delete_InvalidLimit_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--filter", "name like '%test%'",
            "--limit", "0");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("limit");
    }

    #endregion

    #region Tier 1: Safe tests (--dry-run)

    [CliE2EWithCredentials]
    public async Task Delete_DryRun_ById_ShowsPreview()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", testId.ToString(),
            "--dry-run",
            "--profile", _profileName!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdErr.Should().Contain("Dry-Run");
        result.StdErr.Should().Contain("1"); // Record count
    }

    [CliE2EWithCredentials]
    public async Task Delete_DryRun_ById_JsonFormat_ReturnsValidJson()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", testId.ToString(),
            "--dry-run",
            "--output-format", "json",
            "--profile", _profileName!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdOut.Trim().Should().StartWith("{");
        result.StdOut.Should().Contain("\"dryRun\": true");
        result.StdOut.Should().Contain("\"recordCount\": 1");
        result.StdOut.Should().Contain(testId.ToString());
    }

    [CliE2EWithCredentials]
    public async Task Delete_DryRun_ByFilter_ShowsMatchedRecords()
    {
        await EnsureProfileAsync();

        // Use a filter that should match 0 records (unique test prefix)
        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--filter", $"name like '%{_testPrefix}_UNLIKELY_TO_EXIST%'",
            "--dry-run",
            "--profile", _profileName!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // Should either show "No records match" or dry-run preview with 0 records
        (result.StdOut + result.StdErr).Should().ContainAny("No records", "0", "Dry-Run");
    }

    [CliE2EWithCredentials]
    public async Task Delete_NonInteractive_WithoutForce_FailsWithError()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        // Without --dry-run or --force, should fail in non-interactive mode
        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", testId.ToString(),
            "--profile", _profileName!);

        // Expect failure because input is redirected (non-interactive) and --force not provided
        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("force", "confirmation", "non-interactive");
    }

    [CliE2EWithCredentials]
    public async Task Delete_WithProfileOption_UsesSpecifiedProfile()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", testId.ToString(),
            "--dry-run",
            "--profile", _profileName!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    [CliE2EWithCredentials]
    public async Task Delete_WithEnvironmentOverride_UsesSpecifiedEnvironment()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        var result = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", testId.ToString(),
            "--dry-run",
            "--profile", _profileName!,
            "--environment", Configuration.DataverseUrl!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    #endregion

    #region Tier 2: Destructive tests (actual delete)

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Delete_ById_WithForce_DeletesRecord()
    {
        await EnsureProfileAsync();

        // Create a test record first
        var accountId = await CreateTestAccountAsync($"{_testPrefix}_DeleteById");

        // Delete the record
        var deleteResult = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", accountId.ToString(),
            "--force",
            "--profile", _profileName!);

        deleteResult.ExitCode.Should().Be(0, $"Delete failed: {deleteResult.StdErr}");
        deleteResult.StdErr.Should().Contain("Deleted");
        deleteResult.StdErr.Should().Contain("1");

        // Verify record was deleted
        var exists = await RecordExistsAsync(accountId);
        exists.Should().BeFalse("Record should have been deleted");

        // Remove from cleanup list since it's already deleted
        _createdAccountIds.Remove(accountId);
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Delete_ById_JsonFormat_ReturnsResult()
    {
        await EnsureProfileAsync();

        // Create a test record first
        var accountId = await CreateTestAccountAsync($"{_testPrefix}_DeleteByIdJson");

        // Delete the record with JSON output
        var deleteResult = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", accountId.ToString(),
            "--force",
            "--output-format", "json",
            "--profile", _profileName!);

        deleteResult.ExitCode.Should().Be(0, $"Delete failed: {deleteResult.StdErr}");
        deleteResult.StdOut.Trim().Should().StartWith("{");
        deleteResult.StdOut.Should().Contain("\"success\": true");
        deleteResult.StdOut.Should().Contain("\"deletedCount\": 1");

        _createdAccountIds.Remove(accountId);
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Delete_ByFilter_WithForce_DeletesMatchingRecords()
    {
        await EnsureProfileAsync();

        // Create test records
        var filterValue = $"{_testPrefix}_FilterDelete";
        var account1 = await CreateTestAccountAsync(filterValue + "_1");
        var account2 = await CreateTestAccountAsync(filterValue + "_2");

        // Delete by filter
        var deleteResult = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--filter", $"name like '{filterValue}%'",
            "--force",
            "--profile", _profileName!);

        deleteResult.ExitCode.Should().Be(0, $"Delete failed: {deleteResult.StdErr}");
        deleteResult.StdErr.Should().Contain("Deleted");
        deleteResult.StdErr.Should().Contain("2");

        // Verify records were deleted
        (await RecordExistsAsync(account1)).Should().BeFalse("First record should be deleted");
        (await RecordExistsAsync(account2)).Should().BeFalse("Second record should be deleted");

        _createdAccountIds.Remove(account1);
        _createdAccountIds.Remove(account2);
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Delete_FromCsvFile_WithForce_DeletesRecords()
    {
        await EnsureProfileAsync();

        // Create test records
        var account1 = await CreateTestAccountAsync($"{_testPrefix}_CsvDelete_1");
        var account2 = await CreateTestAccountAsync($"{_testPrefix}_CsvDelete_2");

        // Create CSV file with IDs
        var csvPath = GenerateTempFilePath(".csv");
        var csvContent = $"accountid,name\n{account1},Test1\n{account2},Test2";
        await File.WriteAllTextAsync(csvPath, csvContent);

        // Delete from file
        var deleteResult = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--file", csvPath,
            "--id-column", "accountid",
            "--force",
            "--profile", _profileName!);

        deleteResult.ExitCode.Should().Be(0, $"Delete failed: {deleteResult.StdErr}");
        deleteResult.StdErr.Should().Contain("Deleted");
        deleteResult.StdErr.Should().Contain("2");

        // Verify records were deleted
        (await RecordExistsAsync(account1)).Should().BeFalse();
        (await RecordExistsAsync(account2)).Should().BeFalse();

        _createdAccountIds.Remove(account1);
        _createdAccountIds.Remove(account2);
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Delete_FromJsonFile_WithForce_DeletesRecords()
    {
        await EnsureProfileAsync();

        // Create test records
        var account1 = await CreateTestAccountAsync($"{_testPrefix}_JsonDelete_1");
        var account2 = await CreateTestAccountAsync($"{_testPrefix}_JsonDelete_2");

        // Create JSON file with ID array
        var jsonPath = GenerateTempFilePath(".json");
        var jsonContent = $"[\"{account1}\", \"{account2}\"]";
        await File.WriteAllTextAsync(jsonPath, jsonContent);

        // Delete from file
        var deleteResult = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--file", jsonPath,
            "--force",
            "--profile", _profileName!);

        deleteResult.ExitCode.Should().Be(0, $"Delete failed: {deleteResult.StdErr}");
        deleteResult.StdErr.Should().Contain("Deleted");
        deleteResult.StdErr.Should().Contain("2");

        // Verify records were deleted
        (await RecordExistsAsync(account1)).Should().BeFalse();
        (await RecordExistsAsync(account2)).Should().BeFalse();

        _createdAccountIds.Remove(account1);
        _createdAccountIds.Remove(account2);
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Delete_ByKey_WithForce_DeletesRecord()
    {
        await EnsureProfileAsync();

        // Create a test record with a known name
        var uniqueName = $"{_testPrefix}_KeyDelete_{Guid.NewGuid():N}";
        var accountId = await CreateTestAccountAsync(uniqueName);

        // Delete by alternate key (name - though account doesn't have a true alternate key,
        // the command uses query-based lookup for --key)
        var deleteResult = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--key", $"name={uniqueName}",
            "--force",
            "--profile", _profileName!);

        deleteResult.ExitCode.Should().Be(0, $"Delete failed: {deleteResult.StdErr}");
        deleteResult.StdErr.Should().Contain("Deleted");
        deleteResult.StdErr.Should().Contain("1");

        // Verify record was deleted
        (await RecordExistsAsync(accountId)).Should().BeFalse();

        _createdAccountIds.Remove(accountId);
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Delete_NonexistentId_WithForce_HandlesGracefully()
    {
        await EnsureProfileAsync();

        // Try to delete a non-existent record
        var fakeId = Guid.NewGuid();

        var deleteResult = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--id", fakeId.ToString(),
            "--force",
            "--profile", _profileName!);

        // Should handle the error (record not found during delete)
        // With --continue-on-error (default true), it may still return 0
        // but should show the error in output
        (deleteResult.StdOut + deleteResult.StdErr).Should().ContainAny("Failed", "0", "not found", "Error", "does not exist");
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Delete_ByKey_RecordNotFound_ReturnsError()
    {
        await EnsureProfileAsync();

        // Try to delete with a key that doesn't match any record
        var deleteResult = await RunCliAsync(
            "data", "delete",
            "--entity", "account",
            "--key", $"name={_testPrefix}_NONEXISTENT_RECORD_{Guid.NewGuid():N}",
            "--force",
            "--profile", _profileName!);

        // Should fail with "record not found"
        deleteResult.ExitCode.Should().NotBe(0);
        (deleteResult.StdOut + deleteResult.StdErr).Should().ContainAny("not found", "No record", "RECORD_NOT_FOUND");
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

    private async Task<Guid> CreateTestAccountAsync(string name)
    {
        // Use the CLI to create a simple account record via a workaround:
        // Since we don't have a create command, we'll use the Dataverse pool directly
        // through a helper that creates via SDK

        // For now, use a direct SDK call through infrastructure
        var id = await LiveTestHelpers.CreateAccountAsync(Configuration, name);
        _createdAccountIds.Add(id);
        return id;
    }

    private async Task<bool> RecordExistsAsync(Guid accountId)
    {
        return await LiveTestHelpers.AccountExistsAsync(Configuration, accountId);
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
