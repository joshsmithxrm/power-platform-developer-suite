using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class DeployCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempConfigFile;
    private readonly string _originalDir;

    public DeployCommandTests()
    {
        _command = DeployCommand.Create();

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
        Assert.Equal("deploy", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Deploy", _command.Description);
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
    public void Create_HasOptionalSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalCleanOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--clean");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalWhatIfOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--what-if");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalJsonOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--json");
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
    public void Parse_WithOptionalSolution_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --solution my_solution");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalClean_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --clean");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalWhatIf_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --what-if");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse($"-c \"{_tempConfigFile}\" --json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOptions_Succeeds()
    {
        var result = _command.Parse(
            $"-c \"{_tempConfigFile}\" " +
            "--profile dev " +
            "--environment https://org.crm.dynamics.com " +
            "--solution my_solution " +
            "--clean " +
            "--what-if " +
            "--json");
        Assert.Empty(result.Errors);
    }

    #endregion
}
