using System.CommandLine;
using PPDS.Cli.Commands.Metadata;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// Covers AC-37 (entity has create/update/delete + status-reason subcommands)
/// and AC-38 (bare positional routes to query).
/// </summary>
[Trait("Category", "Unit")]
public class MetadataEntityCommandTests
{
    private readonly Command _command;

    public MetadataEntityCommandTests()
    {
        _command = EntityCommand.Create();
    }

    [Fact]
    public void Create_CommandNameIsEntity()
    {
        Assert.Equal("entity", _command.Name);
    }

    [Fact]
    public void Create_HasAuthoringSubcommands()
    {
        var names = _command.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("create", names);
        Assert.Contains("update", names);
        Assert.Contains("delete", names);
    }

    [Fact]
    public void Create_HasStatusReasonSubcommands()
    {
        var names = _command.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("add-statusreason", names);
        Assert.Contains("list-statusreasons", names);
        Assert.Contains("update-statusreason", names);
        Assert.Contains("remove-statusreason", names);
    }

    [Fact]
    public void Create_HasAtLeastSevenSubcommands()
    {
        Assert.True(_command.Subcommands.Count >= 7,
            $"Expected at least 7 subcommands but found {_command.Subcommands.Count}");
    }

    [Fact]
    public void Create_HasPositionalEntityArgument()
    {
        var arg = _command.Arguments.FirstOrDefault();
        Assert.NotNull(arg);
        Assert.Equal("entity", arg!.Name);
    }

    [Fact]
    public void Create_HasSetActionForQueryRoute()
    {
        // Bare 'entity <name>' has a SetAction (routes to query — AC-38)
        Assert.NotNull(_command.Action);
    }

    [Fact]
    public void AddStatusReason_HasRequiredEntityOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "add-statusreason");
        var opt = sub.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void AddStatusReason_HasRequiredLabelOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "add-statusreason");
        var opt = sub.Options.FirstOrDefault(o => o.Name == "--label");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Fact]
    public void AddStatusReason_HasStateOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "add-statusreason");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--state"));
    }

    [Fact]
    public void AddStatusReason_HasStateCodeOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "add-statusreason");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--state-code"));
    }

    [Fact]
    public void AddStatusReason_HasPublishOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "add-statusreason");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--publish"));
    }

    [Fact]
    public void UpdateStatusReason_HasValueAndLabelOptions()
    {
        var sub = _command.Subcommands.First(c => c.Name == "update-statusreason");
        var names = sub.Options.Select(o => o.Name).ToList();
        Assert.Contains("--value", names);
        Assert.Contains("--label", names);
    }

    [Fact]
    public void RemoveStatusReason_HasValueAndLabelOptions()
    {
        var sub = _command.Subcommands.First(c => c.Name == "remove-statusreason");
        var names = sub.Options.Select(o => o.Name).ToList();
        Assert.Contains("--value", names);
        Assert.Contains("--label", names);
    }

    [Fact]
    public void ListStatusReasons_HasEntityOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "list-statusreasons");
        var opt = sub.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(opt);
        Assert.True(opt!.Required);
    }

    [Theory]
    [InlineData("--entity account --label Active2 --state Active --solution MySol")]
    [InlineData("--entity account --label Active2 --state-code 0 --solution MySol")]
    public void AddStatusReason_ParsesValidArgs(string args)
    {
        var sub = _command.Subcommands.First(c => c.Name == "add-statusreason");
        var result = sub.Parse(args);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AddStatusReason_Parse_MissingEntity_HasErrors()
    {
        var sub = _command.Subcommands.First(c => c.Name == "add-statusreason");
        var result = sub.Parse("--label Active2 --state Active --solution MySol");
        Assert.NotEmpty(result.Errors);
    }
}
