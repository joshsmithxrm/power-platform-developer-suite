using System.CommandLine;
using PPDS.Cli.Commands.Metadata;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// Covers AC-40: canonical 'optionset' command has write subcommands + read lookup.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataOptionSetCommandGroupTests
{
    private readonly Command _command;

    public MetadataOptionSetCommandGroupTests()
    {
        _command = OptionSetCommand.Create();
    }

    [Fact]
    public void Create_CommandNameIsOptionSet()
    {
        Assert.Equal("optionset", _command.Name);
    }

    [Fact]
    public void Create_HasAllWriteSubcommands()
    {
        var names = _command.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("create", names);
        Assert.Contains("update", names);
        Assert.Contains("delete", names);
        Assert.Contains("add-option", names);
        Assert.Contains("update-option", names);
        Assert.Contains("remove-option", names);
        Assert.Contains("reorder", names);
    }

    [Fact]
    public void Create_HasSevenSubcommands()
    {
        Assert.Equal(7, _command.Subcommands.Count);
    }

    [Fact]
    public void Create_HasPositionalNameArgument()
    {
        var arg = _command.Arguments.FirstOrDefault();
        Assert.NotNull(arg);
        Assert.Equal("name", arg!.Name);
    }

    [Fact]
    public void Create_HasSetActionForQueryRoute()
    {
        Assert.NotNull(_command.Action);
    }

    [Fact]
    public void CreateSubcommand_HasRequiredSolutionOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "create");
        var opt = sub.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void AddOptionSubcommand_HasRequiredLabelOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "add-option");
        var opt = sub.Options.FirstOrDefault(o => o.Name == "--label");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void ReorderSubcommand_HasRequiredOrderOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "reorder");
        var opt = sub.Options.FirstOrDefault(o => o.Name == "--order");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    // ---- update-option flag alignment (#1170) ----

    [Fact]
    public void UpdateOptionSubcommand_HasSelectorAndNewLabelOptions()
    {
        // #1170: (--value | --label) selects the target, --new-label carries the update,
        // --color is optional — aligned with attribute update-option.
        var sub = _command.Subcommands.First(c => c.Name == "update-option");
        var names = sub.Options.Select(o => o.Name).ToList();
        Assert.Contains("--value", names);
        Assert.Contains("--label", names);
        Assert.Contains("--new-label", names);
        Assert.Contains("--color", names);
    }

    [Fact]
    public void UpdateOptionSubcommand_SelectorOptionsAreOptional()
    {
        var sub = _command.Subcommands.First(c => c.Name == "update-option");
        Assert.False(sub.Options.First(o => o.Name == "--value").Required);
        Assert.False(sub.Options.First(o => o.Name == "--label").Required);
    }

    [Fact]
    public void UpdateOptionSubcommand_ParsesWithValueSelector()
    {
        var sub = _command.Subcommands.First(c => c.Name == "update-option");
        var result = sub.Parse("--solution MySol --name new_status --value 1 --new-label Updated");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void UpdateOptionSubcommand_ParsesWithLabelSelector()
    {
        var sub = _command.Subcommands.First(c => c.Name == "update-option");
        var result = sub.Parse("--solution MySol --name new_status --label Old --new-label New --color #FF0000");
        Assert.Empty(result.Errors);
    }

    // ---- remove-option selector parity (#1169) ----

    [Fact]
    public void RemoveOptionSubcommand_HasLabelOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "remove-option");
        var opt = sub.Options.FirstOrDefault(o => o.Name == "--label");
        Assert.NotNull(opt);
        Assert.False(opt!.Required);
    }

    [Fact]
    public void RemoveOptionSubcommand_ValueOptionIsOptional()
    {
        // #1169: --value is no longer required; exactly one of --value/--label is
        // enforced at execution time (parity with attribute remove-option).
        var sub = _command.Subcommands.First(c => c.Name == "remove-option");
        var opt = sub.Options.FirstOrDefault(o => o.Name == "--value");
        Assert.NotNull(opt);
        Assert.False(opt!.Required);
    }

    [Fact]
    public void RemoveOptionSubcommand_ParsesWithValue()
    {
        var sub = _command.Subcommands.First(c => c.Name == "remove-option");
        var result = sub.Parse("--solution MySol --name new_status --value 1 --force");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void RemoveOptionSubcommand_ParsesWithLabel()
    {
        var sub = _command.Subcommands.First(c => c.Name == "remove-option");
        var result = sub.Parse("--solution MySol --name new_status --label Active --force");
        Assert.Empty(result.Errors);
    }
}
