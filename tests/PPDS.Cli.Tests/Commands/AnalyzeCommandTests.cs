using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Data;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

public class AnalyzeCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempSchemaFile;

    public AnalyzeCommandTests()
    {
        _command = AnalyzeCommand.Create();

        // Create temp schema file for parsing tests
        _tempSchemaFile = Path.Combine(Path.GetTempPath(), $"test-schema-{Guid.NewGuid()}.xml");
        File.WriteAllText(_tempSchemaFile, "<entities></entities>");
    }

    public void Dispose()
    {
        if (File.Exists(_tempSchemaFile))
            File.Delete(_tempSchemaFile);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("analyze", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Equal("Analyze schema and display dependency graph", _command.Description);
    }

    [Fact]
    public void Create_HasRequiredSchemaOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--schema");
        Assert.NotNull(option);
        Assert.True(option.Required);
        Assert.Contains("-s", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalOutputFormatOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-f", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalDebugOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--debug");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithRequiredSchema_Succeeds()
    {
        var result = _command.Parse($"--schema \"{_tempSchemaFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAlias_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingSchema_HasError()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Json")]
    public void Parse_WithValidOutputFormat_Succeeds(string format)
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --output-format {format}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortOutputFormat_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" -f Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithInvalidOutputFormat_HasError()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --output-format Invalid");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithVerbose_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --debug");
        Assert.Empty(result.Errors);
    }

    #endregion
}
