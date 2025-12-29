using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Data;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

public class CopyCommandTests : IDisposable
{
    private readonly Command _command;
    private readonly string _tempSchemaFile;
    private readonly string _tempUserMappingFile;

    public CopyCommandTests()
    {
        _command = CopyCommand.Create();

        // Create temp schema file for parsing tests
        _tempSchemaFile = Path.Combine(Path.GetTempPath(), $"test-schema-{Guid.NewGuid()}.xml");
        File.WriteAllText(_tempSchemaFile, "<entities></entities>");

        // Create temp user mapping file for parsing tests
        _tempUserMappingFile = Path.Combine(Path.GetTempPath(), $"test-usermapping-{Guid.NewGuid()}.xml");
        File.WriteAllText(_tempUserMappingFile, "<usermappings></usermappings>");
    }

    public void Dispose()
    {
        if (File.Exists(_tempSchemaFile))
            File.Delete(_tempSchemaFile);
        if (File.Exists(_tempUserMappingFile))
            File.Delete(_tempUserMappingFile);
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("copy", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.StartsWith("Copy data from source to target Dataverse environment", _command.Description);
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
    public void Create_HasRequiredSourceEnvOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--source-env");
        Assert.NotNull(option);
        Assert.True(option.Required);
    }

    [Fact]
    public void Create_HasRequiredTargetEnvOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--target-env");
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

    [Fact]
    public void Create_HasOptionalStripOwnerFieldsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--strip-owner-fields");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalUserMappingOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--user-mapping");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-u", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalContinueOnErrorOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--continue-on-error");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasOptionalSkipMissingColumnsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--skip-missing-columns");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse($"--schema \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingSchema_HasError()
    {
        var result = _command.Parse("--source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingSourceEnv_HasError()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --target-env https://qa.crm.dynamics.com");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingTargetEnv_HasError()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalTempDir_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --temp-dir \"{Path.GetTempPath()}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalVerbose_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --verbose");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalVerboseShortAlias_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com -v");
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("all")]
    public void Parse_WithBypassPlugins_ValidValues_Succeeds(string value)
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --bypass-plugins {value}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithBypassPlugins_InvalidValue_HasError()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --bypass-plugins invalid");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBypassFlows_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --bypass-flows");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllBypassOptions_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --bypass-plugins all --bypass-flows");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalDebug_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --debug");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalStripOwnerFields_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --strip-owner-fields");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalContinueOnError_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --continue-on-error");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalContinueOnErrorFalse_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --continue-on-error false");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalSkipMissingColumns_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --skip-missing-columns");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithUserMappingValidFile_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --user-mapping \"{_tempUserMappingFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithUserMappingShortAlias_Succeeds()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com -u \"{_tempUserMappingFile}\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithUserMappingNonExistentFile_HasError()
    {
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --user-mapping \"C:\\nonexistent\\mapping.xml\"");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllOwnershipOptions_Succeeds()
    {
        // Test combining strip-owner-fields with other options
        var result = _command.Parse($"-s \"{_tempSchemaFile}\" --source-env https://dev.crm.dynamics.com --target-env https://qa.crm.dynamics.com --strip-owner-fields --continue-on-error --skip-missing-columns");
        Assert.Empty(result.Errors);
    }

    #endregion
}
