using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class ExtractCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempDllFile;
    private readonly string _tempNupkgFile;
    private readonly string _originalDir;

    public ExtractCommandTests()
    {
        _command = ExtractCommand.Create();

        // Create temp files for parsing tests
        _tempDllFile = Path.Combine(Path.GetTempPath(), $"test-plugin-{Guid.NewGuid()}.dll");
        _tempNupkgFile = Path.Combine(Path.GetTempPath(), $"test-plugin-{Guid.NewGuid()}.nupkg");

        // Create empty placeholder files for AcceptExistingOnly validation
        File.WriteAllBytes(_tempDllFile, []);
        File.WriteAllBytes(_tempNupkgFile, []);

        // Change to temp directory for relative path tests
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetTempPath());
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        if (File.Exists(_tempDllFile))
            File.Delete(_tempDllFile);
        if (File.Exists(_tempNupkgFile))
            File.Delete(_tempNupkgFile);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("extract", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Extract", _command.Description);
    }

    [Fact]
    public void Create_HasRequiredInputOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--input");
        Assert.NotNull(option);
        Assert.True(option.Required);
        Assert.Contains("-i", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalOutputOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-o", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalJsonOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--json");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-j", option.Aliases);
    }

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithDllInput_Succeeds()
    {
        var result = _command.Parse($"--input \"{_tempDllFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithNupkgInput_Succeeds()
    {
        var result = _command.Parse($"--input \"{_tempNupkgFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse($"-i \"{_tempDllFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingInput_HasError()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalOutput_Succeeds()
    {
        var outputFile = $"test-output-{Guid.NewGuid()}.json";
        var result = _command.Parse($"-i \"{_tempDllFile}\" --output \"{outputFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse($"-i \"{_tempDllFile}\" --json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_NonExistentFile_HasError()
    {
        var result = _command.Parse("--input \"nonexistent.dll\"");
        Assert.NotEmpty(result.Errors);
    }

    #endregion
}
