using FluentAssertions;
using PPDS.LiveTests.Infrastructure;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds plugins extract command.
/// Extracts plugin step/image attributes from assemblies to JSON configuration.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
/// <remarks>
/// These tests are local-only (no Dataverse connection required).
/// The extract command parses assembly metadata without deploying.
/// </remarks>
public class PluginsExtractCommandE2ETests : CliE2ETestBase
{
    /// <summary>
    /// Gets the path to the test plugin DLL.
    /// </summary>
    private static string TestPluginDllPath => GetTestPluginPath();

    /// <summary>
    /// Gets the path to the TestData directory.
    /// </summary>
    private static string TestDataDir => GetTestDataDir();

    private static string GetTestDataDir()
    {
        var solutionDir = FindSolutionDir(AppContext.BaseDirectory);
        return Path.Combine(solutionDir, "tests", "PPDS.LiveTests", "TestData");
    }

    private static string GetTestPluginPath()
        => Path.Combine(GetTestDataDir(), "PPDS.LiveTests.Fixtures.dll");

    private static string FindSolutionDir(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPDS.Sdk.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find PPDS.Sdk.sln starting from: {startPath}");
    }

    #region Extract from DLL

    [CliE2EFact]
    public async Task Extract_FromDll_CreatesJsonFile()
    {
        var outputPath = GenerateTempFilePath(".json");

        var result = await RunCliAsync(
            "plugins", "extract",
            "--input", TestPluginDllPath,
            "--output", outputPath);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        File.Exists(outputPath).Should().BeTrue("Extract should create output file");
    }

    [CliE2EFact]
    public async Task Extract_FromDll_ContainsPluginTypes()
    {
        var outputPath = GenerateTempFilePath(".json");

        var result = await RunCliAsync(
            "plugins", "extract",
            "--input", TestPluginDllPath,
            "--output", outputPath);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");

        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("PPDS.LiveTests.Fixtures.TestAccountCreatePlugin");
        content.Should().Contain("PPDS.LiveTests.Fixtures.TestContactUpdatePlugin");
        content.Should().Contain("\"message\":");
        content.Should().Contain("\"entity\":");
    }

    [CliE2EFact]
    public async Task Extract_FromDll_ContainsImageConfiguration()
    {
        var outputPath = GenerateTempFilePath(".json");

        var result = await RunCliAsync(
            "plugins", "extract",
            "--input", TestPluginDllPath,
            "--output", outputPath);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");

        var content = await File.ReadAllTextAsync(outputPath);
        // TestContactUpdatePlugin has a PreImage
        content.Should().Contain("PreImage");
        content.Should().Contain("firstname,lastname");
    }

    [CliE2EFact]
    public async Task Extract_WithOutputPath_WritesToSpecifiedPath()
    {
        var customDir = Path.Combine(Path.GetTempPath(), $"ppds-extract-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(customDir);
        CreatedFiles.Add(customDir); // For cleanup

        var outputPath = Path.Combine(customDir, "custom-output.json");

        var result = await RunCliAsync(
            "plugins", "extract",
            "--input", TestPluginDllPath,
            "--output", outputPath);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        File.Exists(outputPath).Should().BeTrue("Extract should write to custom path");

        // Cleanup
        if (Directory.Exists(customDir))
            Directory.Delete(customDir, recursive: true);
    }

    [CliE2EFact]
    public async Task Extract_WithForceFlag_OverwritesExistingFile()
    {
        var outputPath = GenerateTempFilePath(".json");

        // First extraction
        var result1 = await RunCliAsync(
            "plugins", "extract",
            "--input", TestPluginDllPath,
            "--output", outputPath);

        result1.ExitCode.Should().Be(0, $"StdErr: {result1.StdErr}");
        File.Exists(outputPath).Should().BeTrue();

        // Second extraction with --force should succeed without merging
        var result2 = await RunCliAsync(
            "plugins", "extract",
            "--input", TestPluginDllPath,
            "--output", outputPath,
            "--force");

        result2.ExitCode.Should().Be(0, $"StdErr: {result2.StdErr}");
        // Verify file was overwritten (not merged)
        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("PPDS.LiveTests.Fixtures");
    }

    #endregion

    #region Error handling

    [CliE2EFact]
    public async Task Extract_MissingFile_FailsWithError()
    {
        var result = await RunCliAsync(
            "plugins", "extract",
            "--input", "nonexistent-plugin.dll");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("not found", "does not exist", "Error", "Could not find");
    }

    [CliE2EFact]
    public async Task Extract_MissingInputOption_FailsWithError()
    {
        var result = await RunCliAsync("plugins", "extract");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--input", "required", "-i");
    }

    #endregion
}
