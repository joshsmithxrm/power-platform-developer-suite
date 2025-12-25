using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Migration.Cli.Commands;
using Xunit;

namespace PPDS.Migration.Cli.Tests.Commands;

public class MigrateCommandTests
{
    private readonly Command _command;

    public MigrateCommandTests()
    {
        _command = MigrateCommand.Create();
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
        var option = _command.Options.FirstOrDefault(o => o.Name == "schema");
        Assert.NotNull(option);
        Assert.True(option.IsRequired);
        Assert.Contains("-s", option.Aliases);
        Assert.Contains("--schema", option.Aliases);
    }

    [Fact]
    public void Create_HasOptionalTempDirOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "temp-dir");
        Assert.NotNull(option);
        Assert.False(option.IsRequired);
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

    #endregion

    #region Argument Parsing Tests

    [Fact]
    public void Parse_WithAllRequiredOptions_Succeeds()
    {
        var result = _command.Parse("--schema schema.xml --source-env Dev --target-env QA");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithShortAliases_Succeeds()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev --target-env QA");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingSchema_HasError()
    {
        var result = _command.Parse("--source-env Dev --target-env QA");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingSourceEnv_HasError()
    {
        var result = _command.Parse("-s schema.xml --target-env QA");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingTargetEnv_HasError()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalTempDir_Succeeds()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev --target-env QA --temp-dir /tmp");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBatchSize_Succeeds()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev --target-env QA --batch-size 500");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBypassPlugins_Succeeds()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev --target-env QA --bypass-plugins");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalBypassFlows_Succeeds()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev --target-env QA --bypass-flows");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithAllBypassOptions_Succeeds()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev --target-env QA --bypass-plugins --bypass-flows");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalJson_Succeeds()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev --target-env QA --json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOptionalDebug_Succeeds()
    {
        var result = _command.Parse("-s schema.xml --source-env Dev --target-env QA --debug");
        Assert.Empty(result.Errors);
    }

    #endregion
}
