using System.Text.Json;
using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds data export command.
/// Exports data from Dataverse to a ZIP file based on a schema.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
public class DataExportCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Profile name used across Tier 2 tests in this class.
    /// </summary>
    private string? _profileName;
    private readonly List<Guid> _createdAccountIds = new();
    private string? _cleanupFailureType;
    private string? _createFailureType;
    private string? _uncertainAccountName;

    // Per-instance offline seam; failed creation never establishes an owned ID.
    internal Func<LiveTestConfiguration, string, Task<Guid>> CreateAccountAsync { get; set; }
        = LiveTestHelpers.CreateAccountAsync;

    #region Tier 1: Validation tests (no credentials needed)

    [CliE2EFact]
    public async Task Export_MissingSchema_FailsWithError()
    {
        var outputPath = GenerateTempFilePath(".zip");

        var result = await RunCliAsync(
            "data", "export",
            "--output", outputPath);

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--schema", "required");
    }

    [CliE2EFact]
    public async Task Export_MissingOutput_FailsWithError()
    {
        var schemaPath = GenerateTempFilePath(".xml");
        await File.WriteAllTextAsync(schemaPath, "<entities/>");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath);

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--output", "required");
    }

    [CliE2EFact]
    public async Task Export_SchemaFileNotFound_FailsWithError()
    {
        var nonexistentSchema = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.xml");
        var outputPath = GenerateTempFilePath(".zip");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", nonexistentSchema,
            "--output", outputPath);

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("does not exist", "not found", "Error");
    }

    [CliE2EFact]
    public async Task Export_OutputDirectoryNotExists_FailsWithError()
    {
        var schemaPath = GenerateTempFilePath(".xml");
        await File.WriteAllTextAsync(schemaPath, "<entities/>");

        // Construct an output path whose parent directory does not exist
        var outputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "file.zip");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", outputPath);

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("directory");
    }

    [CliE2EFact]
    public async Task Export_InvalidBatchSize_FailsWithError()
    {
        var schemaPath = GenerateTempFilePath(".xml");
        await File.WriteAllTextAsync(schemaPath, "<entities/>");
        var outputPath = GenerateTempFilePath(".zip");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", outputPath,
            "--batch-size", "0");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("batch-size");
    }

    [CliE2EFact]
    public async Task Export_BatchSizeExceedsMax_FailsWithError()
    {
        var schemaPath = GenerateTempFilePath(".xml");
        await File.WriteAllTextAsync(schemaPath, "<entities/>");
        var outputPath = GenerateTempFilePath(".zip");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", outputPath,
            "--batch-size", "6000");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("5000", "batch-size");
    }

    [CliE2EFact]
    public async Task Export_InvalidParallel_FailsWithError()
    {
        var schemaPath = GenerateTempFilePath(".xml");
        await File.WriteAllTextAsync(schemaPath, "<entities/>");
        var outputPath = GenerateTempFilePath(".zip");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", outputPath,
            "--parallel", "0");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().Contain("parallel");
    }

    #endregion

    #region Tier 2: Live Dataverse tests

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Export_ValidSchema_CreatesZipFile()
    {
        await EnsureProfileAsync();

        var accountId = await CreateTestAccountAsync();
        var zipPath = GenerateTempFilePath(".zip");
        var result = await ExportOwnedAccountAsync(accountId, zipPath, profile: null, jsonProgress: false);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        File.Exists(zipPath).Should().BeTrue("Export ZIP file should be created");
        new FileInfo(zipPath).Length.Should().BeGreaterThan(0, "Export ZIP file should not be empty");
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Export_JsonFormat_OutputsProgress()
    {
        await EnsureProfileAsync();

        var accountId = await CreateTestAccountAsync();
        var zipPath = GenerateTempFilePath(".zip");
        var result = await ExportOwnedAccountAsync(accountId, zipPath, profile: null, jsonProgress: true);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // JSON format outputs progress to stderr per output conventions
        result.StdErr.Should().Contain("{");
    }

    [CliE2EWithCredentials]
    [Trait("Category", "DestructiveE2E")]
    public async Task Export_WithProfileOption_UsesSpecifiedProfile()
    {
        // Create profile WITHOUT calling auth select
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        // Export using --profile (no profile is selected)
        var accountId = await CreateTestAccountAsync();
        var zipPath = GenerateTempFilePath(".zip");
        var result = await ExportOwnedAccountAsync(accountId, zipPath, profileName, jsonProgress: false);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    #endregion

    #region Helper Methods

    internal async Task<Guid> CreateTestAccountAsync()
    {
        if (_createFailureType != null) throw CreationFailure();
        if (_cleanupFailureType != null) throw CleanupFailure();
        var name = $"PPDS_E2EExport_{Guid.NewGuid():N}";
        _uncertainAccountName = name;
        TrackPossibleOwnedResource("account", name);
        try
        {
            var id = await CreateAccountAsync(Configuration, name);
            if (id == Guid.Empty) throw new InvalidOperationException("Creation returned no confirmed ID.");
            _createdAccountIds.Add(id);
            _uncertainAccountName = null;
            return id;
        }
        catch (Exception exception)
        {
            _createFailureType = exception.GetType().FullName ?? exception.GetType().Name;
            try
            {
                File.WriteAllText(Path.Combine(IsolatedConfigDir, "data-export-create.json"), JsonSerializer.Serialize(new
                {
                    requiresManualReview = true,
                    creationOutcome = "unknown",
                    failureType = _createFailureType,
                    possibleAccountName = _uncertainAccountName,
                    ownedAccountIds = _createdAccountIds.ToArray(),
                    ownedFiles = CreatedFiles.ToArray(),
                    configDirectory = IsolatedConfigDir
                }));
            }
            catch (Exception) { /* Preserve uncertain creation and local state even when reporting fails. */ }
            throw CreationFailure();
        }
    }

    private Task<CliResult> ExportOwnedAccountAsync(Guid accountId, string zipPath, string? profile, bool jsonProgress = false)
        => FixtureAccountExport.Create(_createdAccountIds, [accountId]).RunAsync(
            GenerateTempFilePath(".xml"), GenerateTempFilePath(".pending.zip"), zipPath,
            (args, token) => RunCliWithCancellationAsync(token, args), profile, jsonProgress);

    public override async Task DisposeAsync()
    {
        if (_createFailureType != null) throw CreationFailure();
        if (_cleanupFailureType != null) throw CleanupFailure();
        if (_createdAccountIds.Count > 0)
        {
            try
            {
                if (await RunCleanupIfSafeAsync(
                    () => DeleteAccountsAsync(Configuration, _createdAccountIds), "account", _createdAccountIds))
                    _createdAccountIds.Clear();
            }
            catch (Exception exception)
            {
                _cleanupFailureType = exception.GetType().FullName ?? exception.GetType().Name;
                try
                {
                    File.WriteAllText(Path.Combine(IsolatedConfigDir, "data-export-cleanup.json"), JsonSerializer.Serialize(new
                    {
                        requiresManualReview = true,
                        failureType = _cleanupFailureType,
                        ownedAccountIds = _createdAccountIds.ToArray(),
                        ownedFiles = CreatedFiles.ToArray(),
                        configDirectory = IsolatedConfigDir
                    }));
                }
                catch (Exception) { /* Keep the original failure type and owned local state even if reporting fails. */ }
                throw CleanupFailure();
            }
        }
        await base.DisposeAsync();
    }

    private InvalidOperationException CleanupFailure() => new(
        $"Data export fixture cleanup failed ({_cleanupFailureType}); automatic retry is prohibited. Retained recovery state: {IsolatedConfigDir}");

    private InvalidOperationException CreationFailure() => new(
        $"Data export fixture account creation has an unknown outcome ({_createFailureType}); automatic creation, adoption and cleanup are prohibited. Possible account name: {_uncertainAccountName}. Retained recovery state: {IsolatedConfigDir}");

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

    #endregion
}
