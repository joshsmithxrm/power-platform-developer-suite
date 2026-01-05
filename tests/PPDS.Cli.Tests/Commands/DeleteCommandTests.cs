using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Data;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

public class DeleteCommandTests
{
    private readonly Command _command;

    public DeleteCommandTests()
    {
        _command = DeleteCommand.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("delete", _command.Name);
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
    public void Create_HasIdOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--id");
        Assert.NotNull(option);
        Assert.False(option.Required);
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
    public void Create_HasFileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--file");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasIdColumnOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--id-column");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasFilterOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--filter");
        Assert.NotNull(option);
        Assert.False(option.Required);
    }

    [Fact]
    public void Create_HasForceOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--force");
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
    public void Create_HasLimitOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--limit");
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

    #region Input Mode Validation Tests

    [Fact]
    public void Parse_NoInputMode_HasError()
    {
        var result = _command.Parse("--entity account");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("--id") || e.Message.Contains("--key") || e.Message.Contains("--file") || e.Message.Contains("--filter"));
    }

    [Fact]
    public void Parse_WithIdOnly_NoInputModeError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001");
        var inputModeErrors = result.Errors.Where(e => e.Message.Contains("--id") && e.Message.Contains("--key"));
        Assert.Empty(inputModeErrors);
    }

    [Fact]
    public void Parse_WithKeyOnly_NoInputModeError()
    {
        var result = _command.Parse("--entity account --key name=Test");
        var inputModeErrors = result.Errors.Where(e => e.Message.Contains("--id") && e.Message.Contains("--key"));
        Assert.Empty(inputModeErrors);
    }

    [Fact]
    public void Parse_WithFilterOnly_NoInputModeError()
    {
        var result = _command.Parse("--entity account --filter \"name like '%test%'\"");
        var inputModeErrors = result.Errors.Where(e => e.Message.Contains("--id") && e.Message.Contains("--key"));
        Assert.Empty(inputModeErrors);
    }

    [Fact]
    public void Parse_MultipleInputModes_IdAndKey_HasError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --key name=Test");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("Only one input mode"));
    }

    [Fact]
    public void Parse_MultipleInputModes_IdAndFilter_HasError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --filter \"name like '%test%'\"");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("Only one input mode"));
    }

    [Fact]
    public void Parse_MultipleInputModes_KeyAndFilter_HasError()
    {
        var result = _command.Parse("--entity account --key name=Test --filter \"name like '%test%'\"");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("Only one input mode"));
    }

    #endregion

    #region Entity Validation Tests

    [Fact]
    public void Parse_MissingEntity_HasError()
    {
        var result = _command.Parse("--id 00000000-0000-0000-0000-000000000001");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_EntityWithSpaces_HasError()
    {
        var result = _command.Parse("--entity \"account name\" --id 00000000-0000-0000-0000-000000000001");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Batch Size Validation Tests

    [Fact]
    public void Parse_InvalidBatchSize_Zero_HasError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --batch-size 0");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("batch-size"));
    }

    [Fact]
    public void Parse_InvalidBatchSize_TooLarge_HasError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --batch-size 5000");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("batch-size"));
    }

    [Fact]
    public void Parse_ValidBatchSize_NoError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --batch-size 100");
        var batchSizeErrors = result.Errors.Where(e => e.Message.Contains("batch-size"));
        Assert.Empty(batchSizeErrors);
    }

    #endregion

    #region Limit Validation Tests

    [Fact]
    public void Parse_InvalidLimit_Zero_HasError()
    {
        var result = _command.Parse("--entity account --filter \"name like '%test%'\" --limit 0");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("limit"));
    }

    [Fact]
    public void Parse_ValidLimit_NoError()
    {
        var result = _command.Parse("--entity account --filter \"name like '%test%'\" --limit 100");
        var limitErrors = result.Errors.Where(e => e.Message.Contains("limit"));
        Assert.Empty(limitErrors);
    }

    #endregion

    #region Bypass Plugins Validation Tests

    [Fact]
    public void Parse_InvalidBypassPlugins_HasError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --bypass-plugins invalid");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_ValidBypassPlugins_Sync_NoError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --bypass-plugins sync");
        var bypassErrors = result.Errors.Where(e => e.Message.Contains("bypass-plugins"));
        Assert.Empty(bypassErrors);
    }

    [Fact]
    public void Parse_ValidBypassPlugins_Async_NoError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --bypass-plugins async");
        var bypassErrors = result.Errors.Where(e => e.Message.Contains("bypass-plugins"));
        Assert.Empty(bypassErrors);
    }

    [Fact]
    public void Parse_ValidBypassPlugins_All_NoError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --bypass-plugins all");
        var bypassErrors = result.Errors.Where(e => e.Message.Contains("bypass-plugins"));
        Assert.Empty(bypassErrors);
    }

    #endregion

    #region Flag Combination Tests

    [Fact]
    public void Parse_WithDryRun_NoError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --dry-run");
        var dryRunErrors = result.Errors.Where(e => e.Message.Contains("dry-run"));
        Assert.Empty(dryRunErrors);
    }

    [Fact]
    public void Parse_WithForce_NoError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --force");
        var forceErrors = result.Errors.Where(e => e.Message.Contains("force"));
        Assert.Empty(forceErrors);
    }

    [Fact]
    public void Parse_WithDryRunAndForce_NoError()
    {
        // Both flags together is valid - dry-run takes precedence
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --dry-run --force");
        var flagErrors = result.Errors.Where(e => e.Message.Contains("dry-run") || e.Message.Contains("force"));
        Assert.Empty(flagErrors);
    }

    [Fact]
    public void Parse_WithBypassFlows_NoError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --bypass-flows");
        var bypassErrors = result.Errors.Where(e => e.Message.Contains("bypass-flows"));
        Assert.Empty(bypassErrors);
    }

    [Fact]
    public void Parse_WithContinueOnError_NoError()
    {
        var result = _command.Parse("--entity account --id 00000000-0000-0000-0000-000000000001 --continue-on-error");
        var continueErrors = result.Errors.Where(e => e.Message.Contains("continue-on-error"));
        Assert.Empty(continueErrors);
    }

    #endregion

    #region Complex Scenario Tests

    [Fact]
    public void Parse_FilterWithLimit_NoError()
    {
        var result = _command.Parse("--entity account --filter \"name like '%test%'\" --limit 500 --force");
        var relevantErrors = result.Errors.Where(e =>
            e.Message.Contains("filter") ||
            e.Message.Contains("limit") ||
            e.Message.Contains("force"));
        Assert.Empty(relevantErrors);
    }

    [Fact]
    public void Parse_FullOptionsWithId_NoError()
    {
        var result = _command.Parse(
            "--entity account " +
            "--id 00000000-0000-0000-0000-000000000001 " +
            "--force " +
            "--batch-size 50 " +
            "--bypass-plugins sync " +
            "--bypass-flows " +
            "--continue-on-error " +
            "--profile dev");

        // Should only fail on profile validation (profile doesn't exist), not on option parsing
        var parseErrors = result.Errors.Where(e =>
            e.Message.Contains("batch-size") ||
            e.Message.Contains("bypass") ||
            e.Message.Contains("force") ||
            e.Message.Contains("Only one input mode"));
        Assert.Empty(parseErrors);
    }

    [Fact]
    public void Parse_WithCompositeKey_NoError()
    {
        var result = _command.Parse("--entity account --key \"name=Test,stateid=CA\"");
        var keyErrors = result.Errors.Where(e => e.Message.Contains("key"));
        Assert.Empty(keyErrors);
    }

    #endregion
}
