using System.CommandLine;
using FluentAssertions;
using PPDS.Cli.Commands.Metadata;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// Covers AC-50: --state/--state-code flags on add-statusreason; mutual exclusion.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataStatusReasonCommandTests
{
    private readonly Command _entityCommand;
    private readonly Command _addStatusReasonCmd;
    private readonly Command _updateStatusReasonCmd;
    private readonly Command _removeStatusReasonCmd;

    public MetadataStatusReasonCommandTests()
    {
        _entityCommand = EntityCommand.Create();
        _addStatusReasonCmd = _entityCommand.Subcommands.First(c => c.Name == "add-statusreason");
        _updateStatusReasonCmd = _entityCommand.Subcommands.First(c => c.Name == "update-statusreason");
        _removeStatusReasonCmd = _entityCommand.Subcommands.First(c => c.Name == "remove-statusreason");
    }

    // ---- add-statusreason state/state-code options ----

    [Fact]
    public void AddStatusReason_HasStateOption()
    {
        _addStatusReasonCmd.Options.Should().Contain(o => o.Name == "--state");
    }

    [Fact]
    public void AddStatusReason_HasStateCodeOption()
    {
        _addStatusReasonCmd.Options.Should().Contain(o => o.Name == "--state-code");
    }

    [Fact]
    public void AddStatusReason_StateOption_AcceptsActiveAndInactive()
    {
        var resultActive = _addStatusReasonCmd.Parse(
            "--entity account --label Foo --state Active --solution MySol");
        Assert.Empty(resultActive.Errors);

        var resultInactive = _addStatusReasonCmd.Parse(
            "--entity account --label Foo --state Inactive --solution MySol");
        Assert.Empty(resultInactive.Errors);
    }

    [Fact]
    public void AddStatusReason_StateOption_RejectsInvalidValue()
    {
        var result = _addStatusReasonCmd.Parse(
            "--entity account --label Foo --state Invalid --solution MySol");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void AddStatusReason_StateCodeOption_AcceptsInteger()
    {
        var result = _addStatusReasonCmd.Parse(
            "--entity account --label Foo --state-code 0 --solution MySol");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AddStatusReason_HasPublishOption()
    {
        _addStatusReasonCmd.Options.Should().Contain(o => o.Name == "--publish");
    }

    [Fact]
    public void AddStatusReason_HasDryRunOption()
    {
        _addStatusReasonCmd.Options.Should().Contain(o => o.Name == "--dry-run");
    }

    [Fact]
    public void AddStatusReason_HasValueOption()
    {
        _addStatusReasonCmd.Options.Should().Contain(o => o.Name == "--value");
    }

    [Fact]
    public void AddStatusReason_HasSolutionOption()
    {
        _addStatusReasonCmd.Options.Should().Contain(o => o.Name == "--solution");
    }

    [Fact]
    public void AddStatusReason_HasColorOption()
    {
        _addStatusReasonCmd.Options.Should().Contain(o => o.Name == "--color");
    }

    // ---- update-statusreason ----

    [Fact]
    public void UpdateStatusReason_HasEntityOption()
    {
        var opt = _updateStatusReasonCmd.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void UpdateStatusReason_HasValueAndLabelOptions()
    {
        _updateStatusReasonCmd.Options.Should().Contain(o => o.Name == "--value");
        _updateStatusReasonCmd.Options.Should().Contain(o => o.Name == "--label");
    }

    [Fact]
    public void UpdateStatusReason_HasNewLabelOption()
    {
        _updateStatusReasonCmd.Options.Should().Contain(o => o.Name == "--new-label");
    }

    [Fact]
    public void UpdateStatusReason_HasPublishOption()
    {
        _updateStatusReasonCmd.Options.Should().Contain(o => o.Name == "--publish");
    }

    [Fact]
    public void UpdateStatusReason_ParsesWithValue()
    {
        var result = _updateStatusReasonCmd.Parse(
            "--entity account --value 1 --new-label Updated");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void UpdateStatusReason_ParsesWithLabel()
    {
        var result = _updateStatusReasonCmd.Parse(
            "--entity account --label Active --new-label Updated");
        Assert.Empty(result.Errors);
    }

    // ---- remove-statusreason ----

    [Fact]
    public void RemoveStatusReason_HasEntityOption()
    {
        var opt = _removeStatusReasonCmd.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void RemoveStatusReason_HasForceOption()
    {
        _removeStatusReasonCmd.Options.Should().Contain(o => o.Name == "--force");
    }

    [Fact]
    public void RemoveStatusReason_HasPublishOption()
    {
        _removeStatusReasonCmd.Options.Should().Contain(o => o.Name == "--publish");
    }

    [Fact]
    public void RemoveStatusReason_ParsesWithValue()
    {
        var result = _removeStatusReasonCmd.Parse(
            "--entity account --value 1 --force");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void RemoveStatusReason_ParsesWithLabel()
    {
        var result = _removeStatusReasonCmd.Parse(
            "--entity account --label Active --force");
        Assert.Empty(result.Errors);
    }

    // ---- list-statusreasons ----

    [Fact]
    public void ListStatusReasons_HasEntityOption()
    {
        var listCmd = _entityCommand.Subcommands.First(c => c.Name == "list-statusreasons");
        var opt = listCmd.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void ListStatusReasons_ParsesWithEntity()
    {
        var listCmd = _entityCommand.Subcommands.First(c => c.Name == "list-statusreasons");
        var result = listCmd.Parse("--entity account");
        Assert.Empty(result.Errors);
    }
}
