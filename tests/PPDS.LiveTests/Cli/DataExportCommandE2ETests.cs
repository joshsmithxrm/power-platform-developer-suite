using FluentAssertions;
using PPDS.LiveTests.Infrastructure;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds data export command.
/// Exports data from Dataverse to a ZIP file based on a schema.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
public class DataExportCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Unique identifier prefix for test artifacts to avoid conflicts.
    /// </summary>
    private readonly string _testPrefix = $"PPDS_E2EExport_{Guid.NewGuid():N}";

    /// <summary>
    /// Profile name used across Tier 2 tests in this class.
    /// </summary>
    private string? _profileName;

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
    public async Task Export_ValidSchema_CreatesZipFile()
    {
        await EnsureProfileAsync();

        var schemaPath = GenerateTempFilePath(".xml");
        var schemaResult = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", schemaPath);

        schemaResult.ExitCode.Should().Be(0, $"Schema generation StdErr: {schemaResult.StdErr}");

        var zipPath = GenerateTempFilePath(".zip");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", zipPath);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        File.Exists(zipPath).Should().BeTrue("Export ZIP file should be created");
        new FileInfo(zipPath).Length.Should().BeGreaterThan(0, "Export ZIP file should not be empty");
    }

    [CliE2EWithCredentials]
    public async Task Export_JsonFormat_OutputsProgress()
    {
        await EnsureProfileAsync();

        var schemaPath = GenerateTempFilePath(".xml");
        var schemaResult = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", schemaPath);

        schemaResult.ExitCode.Should().Be(0, $"Schema generation StdErr: {schemaResult.StdErr}");

        var zipPath = GenerateTempFilePath(".zip");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", zipPath,
            "--output-format", "json");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // JSON format outputs progress to stderr per output conventions
        result.StdErr.Should().Contain("{");
    }

    [CliE2EWithCredentials]
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

        // Generate schema using --profile (no profile is selected)
        var schemaPath = GenerateTempFilePath(".xml");
        var schemaResult = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", schemaPath,
            "--profile", profileName);

        schemaResult.ExitCode.Should().Be(0, $"Schema generation StdErr: {schemaResult.StdErr}");

        var zipPath = GenerateTempFilePath(".zip");

        var result = await RunCliAsync(
            "data", "export",
            "--schema", schemaPath,
            "--output", zipPath,
            "--profile", profileName);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
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

    #endregion
}
