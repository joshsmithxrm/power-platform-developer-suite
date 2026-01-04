using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Data;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

public class LoadCommandTests
{
    private readonly Command _command;

    public LoadCommandTests()
    {
        _command = LoadCommand.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("load", _command.Name);
    }

    [Fact]
    public void Create_HasRequiredEntityOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(option);
        Assert.True(option.Required);
        Assert.Contains("-e", option.Aliases);
    }

    [Fact]
    public void Create_HasRequiredFileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--file");
        Assert.NotNull(option);
        Assert.True(option.Required);
        Assert.Contains("-f", option.Aliases);
    }

    [Fact]
    public void Create_HasKeyOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--key");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-k", option.Aliases);
    }

    [Fact]
    public void Create_HasMappingOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--mapping");
        Assert.NotNull(option);
        Assert.False(option.Required);
        Assert.Contains("-m", option.Aliases);
    }

    [Fact]
    public void Create_HasGenerateMappingOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--generate-mapping");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasDryRunOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--dry-run");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasBatchSizeOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--batch-size");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasBypassPluginsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--bypass-plugins");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasBypassFlowsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--bypass-flows");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasContinueOnErrorOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--continue-on-error");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasOutputFormatOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasVerboseOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--verbose");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasDebugOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--debug");
        Assert.NotNull(option);
    }

    #endregion

    #region Parse Tests

    [Fact]
    public void Parse_MissingEntity_HasError()
    {
        var result = _command.Parse("--file test.csv");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingFile_HasError()
    {
        var result = _command.Parse("--entity account");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_InvalidBatchSize_Zero_HasError()
    {
        var result = _command.Parse("--entity account --file test.csv --batch-size 0");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_InvalidBatchSize_TooLarge_HasError()
    {
        var result = _command.Parse("--entity account --file test.csv --batch-size 5000");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_ValidBatchSize_NoError()
    {
        // Note: file validation happens at runtime, not parse time
        var result = _command.Parse("--entity account --file test.csv --batch-size 100");
        var batchSizeErrors = result.Errors.Where(e => e.Message.Contains("batch-size"));
        Assert.Empty(batchSizeErrors);
    }

    [Fact]
    public void Parse_InvalidBypassPlugins_HasError()
    {
        var result = _command.Parse("--entity account --file test.csv --bypass-plugins invalid");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_ValidBypassPlugins_Sync_NoError()
    {
        var result = _command.Parse("--entity account --file test.csv --bypass-plugins sync");
        var bypassErrors = result.Errors.Where(e => e.Message.Contains("bypass-plugins"));
        Assert.Empty(bypassErrors);
    }

    [Fact]
    public void Parse_ValidBypassPlugins_Async_NoError()
    {
        var result = _command.Parse("--entity account --file test.csv --bypass-plugins async");
        var bypassErrors = result.Errors.Where(e => e.Message.Contains("bypass-plugins"));
        Assert.Empty(bypassErrors);
    }

    [Fact]
    public void Parse_ValidBypassPlugins_All_NoError()
    {
        var result = _command.Parse("--entity account --file test.csv --bypass-plugins all");
        var bypassErrors = result.Errors.Where(e => e.Message.Contains("bypass-plugins"));
        Assert.Empty(bypassErrors);
    }

    [Fact]
    public void Parse_EntityWithSpaces_HasError()
    {
        var result = _command.Parse("--entity \"account name\" --file test.csv");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRun_NoError()
    {
        var result = _command.Parse("--entity account --file test.csv --dry-run");
        var dryRunErrors = result.Errors.Where(e => e.Message.Contains("dry-run"));
        Assert.Empty(dryRunErrors);
    }

    [Fact]
    public void Parse_WithAlternateKey_NoError()
    {
        var result = _command.Parse("--entity account --file test.csv --key accountnumber");
        var keyErrors = result.Errors.Where(e => e.Message.Contains("key"));
        Assert.Empty(keyErrors);
    }

    [Fact]
    public void Parse_WithCompositeKey_NoError()
    {
        var result = _command.Parse("--entity account --file test.csv --key \"name,stateid\"");
        var keyErrors = result.Errors.Where(e => e.Message.Contains("key"));
        Assert.Empty(keyErrors);
    }

    #endregion
}
