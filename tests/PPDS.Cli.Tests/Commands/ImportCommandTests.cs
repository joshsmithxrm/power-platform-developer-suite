using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Data;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

public class ImportCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempDataFile;

    public ImportCommandTests()
    {
        _command = ImportCommand.Create();

        // Create temp data file for parsing tests
        _tempDataFile = Path.Combine(Path.GetTempPath(), $"test-data-{Guid.NewGuid()}.zip");
        File.WriteAllBytes(_tempDataFile, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]); // Empty ZIP
    }

    public void Dispose()
    {
        if (File.Exists(_tempDataFile))
            File.Delete(_tempDataFile);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("import", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.StartsWith("Import data from a ZIP file into Dataverse", _command.Description);
    }

    [Fact]
    public void Create_HasRequiredDataOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--data");
        Assert.NotNull(option);
        Assert.True(option.Required);
        Assert.Contains("-d", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalVerboseOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--verbose");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-v", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalBypassPluginsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--bypass-plugins");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalBypassFlowsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--bypass-flows");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalContinueOnErrorOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--continue-on-error");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalModeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--mode");
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

    [Fact]
    public void Create_HasOptionalDebugOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--debug");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalStripOwnerFieldsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--strip-owner-fields");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse($"--data \"{_tempDataFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingData_HasError()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalVerbose_Succeeds()
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" --verbose");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalVerboseShortAlias_Succeeds()
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" -v");
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("all")]
    public void Parse_WithBypassPlugins_ValidValues_Succeeds(string value)
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" --bypass-plugins {value}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithBypassPlugins_InvalidValue_HasError()
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" --bypass-plugins invalid");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBypassFlows_Succeeds()
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" --bypass-flows");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalContinueOnError_Succeeds()
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" --continue-on-error");
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Upsert")]
    public void Parse_WithValidMode_Succeeds(string mode)
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" --mode {mode}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithInvalidMode_HasError()
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" --mode Invalid");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllBypassOptions_Succeeds()
    {
        var result = _command.Parse($"-d \"{_tempDataFile}\" --bypass-plugins all --bypass-flows");
        Assert.Empty(result.Errors);
    }

    #endregion
}
