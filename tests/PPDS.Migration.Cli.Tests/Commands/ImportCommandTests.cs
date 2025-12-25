using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Migration.Cli.Commands;
using Xunit;

namespace PPDS.Migration.Cli.Tests.Commands;

public class ImportCommandTests
{
    private readonly Command _command;

    public ImportCommandTests()
    {
        _command = ImportCommand.Create();
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
        var option = _command.Options.FirstOrDefault(o => o.Name == "data");
        Assert.NotNull(option);
        Assert.True(option.IsRequired);
        Assert.Contains("-d", option.Aliases);
        Assert.Contains("--data", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalBatchSizeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "batch-size");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
    }

    [Fact]
    public void Create_HasOptionalBypassPluginsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "bypass-plugins");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
    }

    [Fact]
    public void Create_HasOptionalBypassFlowsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "bypass-flows");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
    }

    [Fact]
    public void Create_HasOptionalContinueOnErrorOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "continue-on-error");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
    }

    [Fact]
    public void Create_HasOptionalModeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "mode");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
    }

    [Fact]
    public void Create_HasOptionalJsonOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "json");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
    }

    [Fact]
    public void Create_HasOptionalDebugOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "debug");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
    }

    [Fact]
    public void Create_HasOptionalStripOwnerFieldsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "strip-owner-fields");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
    }

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--data data.zip --env Dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse("-d data.zip --env Dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingData_HasError()
    {
        var result = _command.Parse("--env Dev");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingEnv_HasError()
    {
        var result = _command.Parse("-d data.zip");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBatchSize_Succeeds()
    {
        var result = _command.Parse("-d data.zip --env Dev --batch-size 500");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBypassPlugins_Succeeds()
    {
        var result = _command.Parse("-d data.zip --env Dev --bypass-plugins");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBypassFlows_Succeeds()
    {
        var result = _command.Parse("-d data.zip --env Dev --bypass-flows");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalContinueOnError_Succeeds()
    {
        var result = _command.Parse("-d data.zip --env Dev --continue-on-error");
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Upsert")]
    public void Parse_WithValidMode_Succeeds(string mode)
    {
        var result = _command.Parse($"-d data.zip --env Dev --mode {mode}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithInvalidMode_HasError()
    {
        var result = _command.Parse("-d data.zip --env Dev --mode Invalid");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllBypassOptions_Succeeds()
    {
        var result = _command.Parse("-d data.zip --env Dev --bypass-plugins --bypass-flows");
        Assert.Empty(result.Errors);
    }

    #endregion
}
