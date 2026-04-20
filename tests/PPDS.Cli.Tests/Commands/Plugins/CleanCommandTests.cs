using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Collection(nameof(CurrentDirectoryMutatingCollection))]
public class CleanCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempConfigFile;
    private readonly string _originalDir;

    public CleanCommandTests()
    {
        _command = CleanCommand.Create();

        // Create temp config file for parsing tests
        _tempConfigFile = Path.Combine(Path.GetTempPath(), $"registrations-{Guid.NewGuid()}.json");
        File.WriteAllText(_tempConfigFile, "{}");

        // Change to temp directory for relative path tests
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetTempPath());
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        if (File.Exists(_tempConfigFile))
            File.Delete(_tempConfigFile);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("clean", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Remove", _command.Description);
    }

    [Fact]
    public void Create_HasRequiredConfigOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--config");
        Assert.NotNull(option);
        Assert.True(option.Required);
        Assert.Contains("-c", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalDryRunOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--dry-run");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalOutputFormatOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithRequiredConfig_Succeeds()
    {
        var result = _command.Parse($"--config \"{_tempConfigFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingConfig_HasError()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalProfile_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalEnvironment_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --environment https://org.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalDryRun_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --dry-run");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            $"-c \"{_tempConfigFile}\" " +
            "--profile dev " +
            "--environment https://org.crm.dynamics.com " +
            "--dry-run " +
            "--output-format Json");
        Assert.Empty(result.Errors);
    }

    #endregion
}
