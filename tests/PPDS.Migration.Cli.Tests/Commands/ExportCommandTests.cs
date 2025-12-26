using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Migration.Cli.Commands;
using Xunit;

namespace PPDS.Migration.Cli.Tests.Commands;

public class ExportCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempSchemaFile;
    private readonly string _tempOutputFile;
    private readonly string _originalDir;

    public ExportCommandTests()
    {
        _command = ExportCommand.Create();

        // Create temp schema file for parsing tests
        _tempSchemaFile = Path.Combine(Path.GetTempPath(), $"test-schema-{Guid.NewGuid()}.xml");
        File.WriteAllText(_tempSchemaFile, "<entities></entities>");

        // Use relative path for output to avoid Windows path issues with AcceptLegalFileNamesOnly
        // Change to temp directory so relative path works
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.GetTempPath());
        _tempOutputFile = $"test-output-{Guid.NewGuid()}.zip";
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        if (File.Exists(_tempSchemaFile))
            File.Delete(_tempSchemaFile);
        var fullOutputPath = Path.Combine(Path.GetTempPath(), _tempOutputFile);
        if (File.Exists(fullOutputPath))
            File.Delete(fullOutputPath);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("export", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.StartsWith("Export data from Dataverse to a ZIP file", _command.Description);
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
    public void Create_HasRequiredOutputOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output");
        Assert.NotNull(option);
        Assert.True(option.Required);
        Assert.Contains("-o", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalParallelOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--parallel");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalPageSizeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--page-size");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalIncludeFilesOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--include-files");
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

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse($"--schema \"{_tempSchemaFile}\" --output \"{_tempOutputFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" -o \"{_tempOutputFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingSchema_HasError()
    {
        var result = _command.Parse($"--output \"{_tempOutputFile}\"");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingOutput_HasError()
    {
        var result = _command.Parse($"--schema \"{_tempSchemaFile}\"");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalParallel_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" -o \"{_tempOutputFile}\" --parallel 4");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalPageSize_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" -o \"{_tempOutputFile}\" --page-size 1000");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalIncludeFiles_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" -o \"{_tempOutputFile}\" --include-files");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" -o \"{_tempOutputFile}\" --json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalDebug_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" -o \"{_tempOutputFile}\" --debug");
        Assert.Empty(result.Errors);
    }

    #endregion
}
