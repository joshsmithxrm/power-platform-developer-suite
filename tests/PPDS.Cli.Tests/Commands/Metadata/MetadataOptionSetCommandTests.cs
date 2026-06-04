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
}
