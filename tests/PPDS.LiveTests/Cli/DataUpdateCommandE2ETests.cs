using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds data update command.
/// Updates records in Dataverse entities.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
/// <remarks>
/// Tier 1 tests use --dry-run and are safe for all environments.
/// Tier 2 tests (marked with DestructiveE2E trait) actually update records.
/// </remarks>
public class DataUpdateCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Unique identifier prefix for test records to avoid conflicts.
    /// </summary>
    private readonly string _testPrefix = $"PPDS_E2EUpdate_{Guid.NewGuid():N}";

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
    public async Task Update_MissingEntity_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "update",
            "--id", Guid.NewGuid().ToString(),
            "--set", "name=Test");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--entity", "-e", "required");
    }

    [CliE2EFact]
    public async Task Update_MissingInputMode_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--set", "name=Test");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--id", "--key", "--file", "--filter");
    }

    [CliE2EFact]
    public async Task Update_MissingSet_ForIdMode_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--id", Guid.NewGuid().ToString());

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("--set");
    }

    [CliE2EFact]
    public async Task Update_MultipleInputModes_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--id", Guid.NewGuid().ToString(),
            "--filter", "name like '%test%'",
            "--set", "name=Test");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("Only one", "input mode");
    }

    [CliE2EFact]
    public async Task Update_FileNotFound_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--file", "nonexistent-file.csv");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("not found", "does not exist", "File");
    }

    [CliE2EFact]
    public async Task Update_InvalidBatchSize_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--id", Guid.NewGuid().ToString(),
            "--set", "name=Test",
            "--batch-size", "0");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("batch-size");
    }

    [CliE2EFact]
    public async Task Update_InvalidLimit_FailsWithError()
    {
        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--filter", "name like '%test%'",
            "--set", "description=Updated",
            "--limit", "0");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("limit");
    }

    #endregion

    #region Tier 1: Safe tests (--dry-run)

    [CliE2EWithCredentials]
    public async Task Update_DryRun_ById_ShowsPreview()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--id", testId.ToString(),
            "--set", "name=Updated",
            "--dry-run",
            "--profile", _profileName!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        result.StdErr.Should().Contain("Dry-Run");
        result.StdErr.Should().Contain("1"); // Record count
    }

    [CliE2EWithCredentials]
    public async Task Update_DryRun_ById_JsonFormat_ReturnsValidJson()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--id", testId.ToString(),
            "--set", "name=Updated",
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
    public async Task Update_DryRun_ByFilter_ShowsMatchedRecords()
    {
        await EnsureProfileAsync();

        // Use a filter that should match 0 records (unique test prefix)
        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--filter", $"name like '%{_testPrefix}_UNLIKELY_TO_EXIST%'",
            "--set", "description=Updated",
            "--dry-run",
            "--profile", _profileName!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // Should either show "No records match" or dry-run preview with 0 records
        (result.StdOut + result.StdErr).Should().ContainAny("No records", "0", "Dry-Run");
    }

    [CliE2EWithCredentials]
    public async Task Update_NonInteractive_WithoutForce_FailsWithError()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        // Without --dry-run or --force, should fail in non-interactive mode
        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--id", testId.ToString(),
            "--set", "name=Updated",
            "--profile", _profileName!);

        // Expect failure because input is redirected (non-interactive) and --force not provided
        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("force", "confirmation", "non-interactive");
    }

    [CliE2EWithCredentials]
    public async Task Update_WithProfileOption_UsesSpecifiedProfile()
    {
        await EnsureProfileAsync();

        var testId = Guid.NewGuid();

        var result = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--id", testId.ToString(),
            "--set", "name=Updated",
            "--dry-run",
            "--profile", _profileName!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    #endregion

    #region Tier 2: Destructive tests (actual update)

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Update_ById_WithForce_UpdatesRecord()
    {
        await EnsureProfileAsync();

        // Create a test record first
        var originalName = $"{_testPrefix}_UpdateById";
        var accountId = await CreateTestAccountAsync(originalName);

        try
        {
            // Update the record
            var newDescription = "Updated by E2E test";
            var updateResult = await RunCliAsync(
                "data", "update",
                "--entity", "account",
                "--id", accountId.ToString(),
                "--set", $"description={newDescription}",
                "--force",
                "--profile", _profileName!);

            updateResult.ExitCode.Should().Be(0, $"Update failed: {updateResult.StdErr}");
            updateResult.StdErr.Should().Contain("Updated");
            updateResult.StdErr.Should().Contain("1");

            // Verify record was updated
            var description = await GetAccountDescriptionAsync(accountId);
            description.Should().Be(newDescription);
        }
        finally
        {
            // Cleanup handled by DisposeAsync
        }
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Update_ById_JsonFormat_ReturnsResult()
    {
        await EnsureProfileAsync();

        // Create a test record first
        var accountId = await CreateTestAccountAsync($"{_testPrefix}_UpdateByIdJson");

        try
        {
            // Update the record with JSON output
            var updateResult = await RunCliAsync(
                "data", "update",
                "--entity", "account",
                "--id", accountId.ToString(),
                "--set", "description=JSON test update",
                "--force",
                "--output-format", "json",
                "--profile", _profileName!);

            updateResult.ExitCode.Should().Be(0, $"Update failed: {updateResult.StdErr}");
            updateResult.StdOut.Trim().Should().StartWith("{");
            updateResult.StdOut.Should().Contain("\"success\": true");
            updateResult.StdOut.Should().Contain("\"updatedCount\": 1");
        }
        finally
        {
            // Cleanup handled by DisposeAsync
        }
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Update_ByFilter_WithForce_UpdatesMatchingRecords()
    {
        await EnsureProfileAsync();

        // Create test records
        var filterValue = $"{_testPrefix}_FilterUpdate";
        var account1 = await CreateTestAccountAsync(filterValue + "_1");
        var account2 = await CreateTestAccountAsync(filterValue + "_2");

        try
        {
            // Update by filter
            var updateResult = await RunCliAsync(
                "data", "update",
                "--entity", "account",
                "--filter", $"name like '{filterValue}%'",
                "--set", "description=Bulk updated",
                "--force",
                "--profile", _profileName!);

            updateResult.ExitCode.Should().Be(0, $"Update failed: {updateResult.StdErr}");
            updateResult.StdErr.Should().Contain("Updated");
            updateResult.StdErr.Should().Contain("2");

            // Verify records were updated
            (await GetAccountDescriptionAsync(account1)).Should().Be("Bulk updated");
            (await GetAccountDescriptionAsync(account2)).Should().Be("Bulk updated");
        }
        finally
        {
            // Cleanup handled by DisposeAsync
        }
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Update_FromCsvFile_WithForce_UpdatesRecords()
    {
        await EnsureProfileAsync();

        // Create test records
        var account1 = await CreateTestAccountAsync($"{_testPrefix}_CsvUpdate_1");
        var account2 = await CreateTestAccountAsync($"{_testPrefix}_CsvUpdate_2");

        // Create CSV file with IDs and values to update
        var csvPath = GenerateTempFilePath(".csv");
        var csvContent = $"accountid,description\n{account1},CSV Updated 1\n{account2},CSV Updated 2";
        await File.WriteAllTextAsync(csvPath, csvContent);

        try
        {
            // Update from file
            var updateResult = await RunCliAsync(
                "data", "update",
                "--entity", "account",
                "--file", csvPath,
                "--id-column", "accountid",
                "--force",
                "--profile", _profileName!);

            updateResult.ExitCode.Should().Be(0, $"Update failed: {updateResult.StdErr}");
            updateResult.StdErr.Should().Contain("Updated");
            updateResult.StdErr.Should().Contain("2");

            // Verify records were updated
            (await GetAccountDescriptionAsync(account1)).Should().Be("CSV Updated 1");
            (await GetAccountDescriptionAsync(account2)).Should().Be("CSV Updated 2");
        }
        finally
        {
            // Cleanup handled by DisposeAsync
        }
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Update_ByKey_WithForce_UpdatesRecord()
    {
        await EnsureProfileAsync();

        // Create a test record with a known name
        var uniqueName = $"{_testPrefix}_KeyUpdate_{Guid.NewGuid():N}";
        var accountId = await CreateTestAccountAsync(uniqueName);

        try
        {
            // Update by alternate key (name - uses query-based lookup)
            var updateResult = await RunCliAsync(
                "data", "update",
                "--entity", "account",
                "--key", $"name={uniqueName}",
                "--set", "description=Key updated",
                "--force",
                "--profile", _profileName!);

            updateResult.ExitCode.Should().Be(0, $"Update failed: {updateResult.StdErr}");
            updateResult.StdErr.Should().Contain("Updated");
            updateResult.StdErr.Should().Contain("1");

            // Verify record was updated
            (await GetAccountDescriptionAsync(accountId)).Should().Be("Key updated");
        }
        finally
        {
            // Cleanup handled by DisposeAsync
        }
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Update_ByKey_RecordNotFound_ReturnsError()
    {
        await EnsureProfileAsync();

        // Try to update with a key that doesn't match any record
        var updateResult = await RunCliAsync(
            "data", "update",
            "--entity", "account",
            "--key", $"name={_testPrefix}_NONEXISTENT_RECORD_{Guid.NewGuid():N}",
            "--set", "description=Should fail",
            "--force",
            "--profile", _profileName!);

        // Should fail with "record not found"
        updateResult.ExitCode.Should().NotBe(0);
        (updateResult.StdOut + updateResult.StdErr).Should().ContainAny("not found", "No record", "RECORD_NOT_FOUND");
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Update_MultipleFields_WithForce_UpdatesAllFields()
    {
        await EnsureProfileAsync();

        // Create a test record
        var accountId = await CreateTestAccountAsync($"{_testPrefix}_MultiField");

        try
        {
            // Update multiple fields at once
            var updateResult = await RunCliAsync(
                "data", "update",
                "--entity", "account",
                "--id", accountId.ToString(),
                "--set", "description=Multi updated,websiteurl=https://example.com",
                "--force",
                "--profile", _profileName!);

            updateResult.ExitCode.Should().Be(0, $"Update failed: {updateResult.StdErr}");
            updateResult.StdErr.Should().Contain("Updated");
        }
        finally
        {
            // Cleanup handled by DisposeAsync
        }
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
        var id = await LiveTestHelpers.CreateAccountAsync(Configuration, name);
        _createdAccountIds.Add(id);
        return id;
    }

    private async Task<string?> GetAccountDescriptionAsync(Guid accountId)
    {
        return await LiveTestHelpers.GetAccountDescriptionAsync(Configuration, accountId);
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
