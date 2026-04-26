using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// End-to-end tests for the full data migration workflow:
/// schema generation -> export -> import round-trip.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
/// <remarks>
/// These tests are marked [Trait("Category", "DestructiveE2E")] because they
/// create and delete real Dataverse records as part of the round-trip.
/// </remarks>
public class DataMigrationWorkflowE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Unique identifier prefix for test records to avoid conflicts.
    /// </summary>
    private readonly string _testPrefix = $"PPDS_E2EWorkflow_{Guid.NewGuid():N}";

    /// <summary>
    /// Tracks account IDs created during tests for cleanup.
    /// </summary>
    private readonly List<Guid> _createdAccountIds = new();

    /// <summary>
    /// Profile name used across tests in this class.
    /// </summary>
    private string? _profileName;

    /// <summary>
    /// The full round-trip pipeline runs three CLI invocations sequentially
    /// (schema, export, import), so allow extra time.
    /// </summary>
    protected override TimeSpan CommandTimeout => TimeSpan.FromMinutes(5);

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task FullRoundTrip_SchemaExportImport_PreservesAccountData()
    {
        await EnsureProfileAsync();

        // 1. Create a test account in Dataverse.
        var name = $"{_testPrefix}_RoundTrip_{Guid.NewGuid():N}";
        var accountId = await LiveTestHelpers.CreateAccountAsync(Configuration, name);
        _createdAccountIds.Add(accountId);

        // 2. Generate a filtered schema that targets only this account by name.
        var schemaPath = GenerateTempFilePath(".xml");
        var schemaResult = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", schemaPath,
            "--filter", $"account:name = '{name}'",
            "--profile", _profileName!);

        schemaResult.ExitCode.Should().Be(0, $"Schema generation failed: {schemaResult.StdErr}");
        File.Exists(schemaPath).Should().BeTrue("Schema file should be created");

        // 3. Export data using the filtered schema.
        var zipPath = GenerateTempFilePath(".zip");
        var exportResult = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", zipPath,
            "--profile", _profileName!);

        exportResult.ExitCode.Should().Be(0, $"Export failed: {exportResult.StdErr}");
        File.Exists(zipPath).Should().BeTrue("Export ZIP file should be created");
        new FileInfo(zipPath).Length.Should().BeGreaterThan(0, "Export ZIP should have content");

        // 4. Delete the source account so import has to recreate it.
        await LiveTestHelpers.DeleteAccountsAsync(Configuration, new[] { accountId });
        (await LiveTestHelpers.AccountExistsAsync(Configuration, accountId))
            .Should().BeFalse("Account should have been deleted before import");

        // 5. Import the exported data with Upsert mode.
        var importResult = await RunCliAsync(
            "data", "import",
            "--data", zipPath,
            "--mode", "Upsert",
            "--profile", _profileName!);

        importResult.ExitCode.Should().Be(0, $"Import failed: {importResult.StdErr}");

        // 6. Verify the account exists again with the same id (Upsert preserves primary key).
        (await LiveTestHelpers.AccountExistsAsync(Configuration, accountId))
            .Should().BeTrue("import should re-create with original id via Upsert");

        // accountId remains in _createdAccountIds — DisposeAsync will clean up.
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task FullRoundTrip_UpsertMode_UpdatesExistingRecord()
    {
        await EnsureProfileAsync();

        // 1. Create a test account with a known description.
        var name = $"{_testPrefix}_Upsert_{Guid.NewGuid():N}";
        var accountId = await CreateAccountWithDescriptionAsync(name, "ORIGINAL");

        // 2. Generate a filtered schema. By default, schema includes all attributes
        // for an entity, so the description column will be captured.
        var schemaPath = GenerateTempFilePath(".xml");
        var schemaResult = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", schemaPath,
            "--filter", $"account:name = '{name}'",
            "--profile", _profileName!);

        schemaResult.ExitCode.Should().Be(0, $"Schema generation failed: {schemaResult.StdErr}");

        // 3. Export the original record (description=ORIGINAL) to a ZIP.
        var zipPath = GenerateTempFilePath(".zip");
        var exportResult = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", zipPath,
            "--profile", _profileName!);

        exportResult.ExitCode.Should().Be(0, $"Export failed: {exportResult.StdErr}");
        File.Exists(zipPath).Should().BeTrue("Export ZIP file should be created");
        new FileInfo(zipPath).Length.Should().BeGreaterThan(0, "Export ZIP should have content");

        // 4. Modify the live record's description so we can verify Upsert restores it.
        await UpdateAccountDescriptionAsync(accountId, "MODIFIED");

        // 5. Verify the modification took effect before importing.
        (await LiveTestHelpers.GetAccountDescriptionAsync(Configuration, accountId))
            .Should().Be("MODIFIED", "live record should have been updated");

        // 6. Import with Upsert to overwrite the modified record with the exported data.
        var importResult = await RunCliAsync(
            "data", "import",
            "--data", zipPath,
            "--mode", "Upsert",
            "--profile", _profileName!);

        importResult.ExitCode.Should().Be(0, $"Import failed: {importResult.StdErr}");

        // 7. Verify Upsert restored the original description from the export.
        (await LiveTestHelpers.GetAccountDescriptionAsync(Configuration, accountId))
            .Should().Be("ORIGINAL", "Upsert should have restored the exported description");

        // accountId remains in _createdAccountIds — DisposeAsync will clean up.
    }

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

    private async Task<Guid> CreateAccountWithDescriptionAsync(string name, string description)
    {
        using var client = await LiveTestHelpers.CreateServiceClientAsync(Configuration);
        var entity = new Entity("account")
        {
            ["name"] = name,
            ["description"] = description
        };
        var id = client.Create(entity);
        _createdAccountIds.Add(id);
        return id;
    }

    private async Task UpdateAccountDescriptionAsync(Guid id, string description)
    {
        using var client = await LiveTestHelpers.CreateServiceClientAsync(Configuration);
        client.Update(new Entity("account", id) { ["description"] = description });
    }

    public override async Task DisposeAsync()
    {
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
