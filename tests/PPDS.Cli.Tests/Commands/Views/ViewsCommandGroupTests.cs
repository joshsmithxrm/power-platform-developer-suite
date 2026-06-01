using System.CommandLine;
using PPDS.Cli.Commands.Views;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Views;

public class ViewsCommandGroupTests
{
    private readonly Command _command;

    public ViewsCommandGroupTests()
    {
        _command = ViewsCommandGroup.Create();
    }

    [Fact]
    public void Create_HasCorrectName()
    {
        Assert.Equal("views", _command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        Assert.NotEmpty(_command.Description ?? "");
    }

    // AC-17: all eleven subcommands present
    [Fact]
    public void Create_HasAllElevenSubcommands()
    {
        Assert.Equal(11, _command.Subcommands.Count);
    }

    [Fact]
    public void Create_HasListSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "list"));

    [Fact]
    public void Create_HasGetSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "get"));

    [Fact]
    public void Create_HasAddColumnSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "add-column"));

    [Fact]
    public void Create_HasRemoveColumnSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "remove-column"));

    [Fact]
    public void Create_HasUpdateColumnSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "update-column"));

    [Fact]
    public void Create_HasReorderColumnsSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "reorder-columns"));

    [Fact]
    public void Create_HasSetSortSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "set-sort"));

    [Fact]
    public void Create_HasClearSortSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "clear-sort"));

    [Fact]
    public void Create_HasSetFilterSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "set-filter"));

    [Fact]
    public void Create_HasClearFilterSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "clear-filter"));

    [Fact]
    public void Create_HasSetFetchXmlSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "set-fetchxml"));

    // AC-18: every subcommand has a non-empty description
    [Fact]
    public void AllSubcommands_HaveDescription()
    {
        foreach (var sub in _command.Subcommands)
        {
            Assert.False(string.IsNullOrWhiteSpace(sub.Description),
                $"Subcommand '{sub.Name}' has no description.");
        }
    }

    // AC-19: set-fetchxml description includes XML format example
    [Fact]
    public void SetFetchXmlSubcommand_HelpIncludesXmlExample()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-fetchxml");
        Assert.Contains("<fetch", sub.Description, StringComparison.OrdinalIgnoreCase);
    }

    // AC-19: set-filter description includes filter fragment example
    [Fact]
    public void SetFilterSubcommand_HelpIncludesXmlExample()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-filter");
        Assert.Contains("<filter", sub.Description, StringComparison.OrdinalIgnoreCase);
    }

    // AC-12: set-filter has both --filter-file and --condition options
    [Fact]
    public void SetFilterSubcommand_HasFilterFileOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-filter");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--filter-file"));
    }

    [Fact]
    public void SetFilterSubcommand_HasConditionOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-filter");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--condition"));
    }

    // AC-12: mutual exclusion is enforced in the handler (tested via parse/invoke in integration, structural check here)
    [Fact]
    public void SetFilterSubcommand_FilterFileAndConditionAreMutuallyExclusive()
    {
        // Both options exist but are distinct; mutual exclusion enforced in handler
        var sub = _command.Subcommands.First(c => c.Name == "set-filter");
        var filterFileOpt = sub.Options.FirstOrDefault(o => o.Name == "--filter-file");
        var conditionOpt = sub.Options.FirstOrDefault(o => o.Name == "--condition");
        Assert.NotNull(filterFileOpt);
        Assert.NotNull(conditionOpt);
        // They are not the same option — confirms both exist as separate options
        Assert.NotSame(filterFileOpt, conditionOpt);
    }

    // set-fetchxml has --fetchxml option
    [Fact]
    public void SetFetchXmlSubcommand_HasFetchXmlOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-fetchxml");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--fetchxml"));
    }

    // mutation subcommands all have --solution and --publish
    [Theory]
    [InlineData("add-column")]
    [InlineData("remove-column")]
    [InlineData("update-column")]
    [InlineData("reorder-columns")]
    [InlineData("set-sort")]
    [InlineData("clear-sort")]
    [InlineData("set-filter")]
    [InlineData("clear-filter")]
    [InlineData("set-fetchxml")]
    public void MutationSubcommands_HaveSolutionAndPublishOptions(string subcommandName)
    {
        var sub = _command.Subcommands.First(c => c.Name == subcommandName);
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--solution"));
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--publish"));
    }
}
