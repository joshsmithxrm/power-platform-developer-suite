using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Migration.Cli.Commands;
using Xunit;

namespace PPDS.Migration.Cli.Tests.Commands;

public class MigrateCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempSchemaFile;

    public MigrateCommandTests()
    {
        _command = MigrateCommand.Create();

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
        Assert.Equal("migrate", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.StartsWith("Migrate data from source to target Dataverse environment", _command.Description);
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
    public void Create_HasRequiredSourceUrlOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--source-url");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Create_HasRequiredTargetUrlOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--target-url");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Create_HasOptionalTempDirOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--temp-dir");
        Assert.NotNull(option);
        Assert.False(option.Required);
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
        var result = _command.Parse($"--schema \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingSchema_HasError()
    {
        var result = _command.Parse("--source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingSourceUrl_HasError()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --target-url https://qa.crm.dynamics.com");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingTargetUrl_HasError()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalTempDir_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com --temp-dir \"{Path.GetTempPath()}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalVerbose_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com --verbose");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalVerboseShortAlias_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com -v");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBypassPlugins_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com --bypass-plugins");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBypassFlows_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com --bypass-flows");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllBypassOptions_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com --bypass-plugins --bypass-flows");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com --json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalDebug_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-url https://dev.crm.dynamics.com --target-url https://qa.crm.dynamics.com --debug");
        Assert.Empty(result.Errors);
    }

    #endregion
}
