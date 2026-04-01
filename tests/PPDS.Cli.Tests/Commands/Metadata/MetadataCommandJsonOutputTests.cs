using System.CommandLine;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Commands.Metadata.Choice;
using PPDS.Cli.Commands.Metadata.Column;
using PPDS.Cli.Commands.Metadata.Key;
using PPDS.Cli.Commands.Metadata.Relationship;
using PPDS.Cli.Commands.Metadata.Table;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// Verifies that all metadata authoring commands accept --output-format json.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataCommandJsonOutputTests
{
    [Theory]
    [MemberData(nameof(AllAuthoringCommands))]
    public void Command_HasOutputFormatOption(string _groupName, string _subcommandName, Command command)
    {
        // groupName and subcommandName used by xUnit for test display naming
        var option = command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
    }

    [Theory]
    [MemberData(nameof(AllAuthoringCommands))]
    public void Command_HasVerboseOption(string _groupName, string _subcommandName, Command command)
    {
        var option = command.Options.FirstOrDefault(o => o.Name == "--verbose");
        Assert.NotNull(option);
    }

    [Theory]
    [MemberData(nameof(AllAuthoringCommands))]
    public void Command_HasDebugOption(string _groupName, string _subcommandName, Command command)
    {
        var option = command.Options.FirstOrDefault(o => o.Name == "--debug");
        Assert.NotNull(option);
    }

    [Theory]
    [MemberData(nameof(AllAuthoringCommands))]
    public void Command_HasProfileOption(string _groupName, string _subcommandName, Command command)
    {
        var option = command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Theory]
    [MemberData(nameof(AllAuthoringCommands))]
    public void Command_HasEnvironmentOption(string _groupName, string _subcommandName, Command command)
    {
        var option = command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
    }

    public static IEnumerable<object[]> AllAuthoringCommands()
    {
        var tableGroup = TableCommandGroup.Create();
        foreach (var sub in tableGroup.Subcommands)
            yield return new object[] { "table", sub.Name, sub };

        var columnGroup = ColumnCommandGroup.Create();
        foreach (var sub in columnGroup.Subcommands)
            yield return new object[] { "column", sub.Name, sub };

        var relGroup = RelationshipCommandGroup.Create();
        foreach (var sub in relGroup.Subcommands)
            yield return new object[] { "relationship", sub.Name, sub };

        var choiceGroup = ChoiceCommandGroup.Create();
        foreach (var sub in choiceGroup.Subcommands)
            yield return new object[] { "choice", sub.Name, sub };

        var keyGroup = KeyCommandGroup.Create();
        foreach (var sub in keyGroup.Subcommands)
            yield return new object[] { "key", sub.Name, sub };
    }
}

/// <summary>
/// Verifies that the MetadataCommandGroup has all authoring command groups registered.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataAuthoringRegistrationTests
{
    private readonly Command _command;

    public MetadataAuthoringRegistrationTests()
    {
        _command = MetadataCommandGroup.Create();
    }

    [Fact]
    public void MetadataGroup_HasTableSubcommand()
    {
        Assert.Contains(_command.Subcommands, c => c.Name == "table");
    }

    [Fact]
    public void MetadataGroup_HasColumnSubcommand()
    {
        Assert.Contains(_command.Subcommands, c => c.Name == "column");
    }

    [Fact]
    public void MetadataGroup_HasRelationshipSubcommand()
    {
        Assert.Contains(_command.Subcommands, c => c.Name == "relationship");
    }

    [Fact]
    public void MetadataGroup_HasChoiceSubcommand()
    {
        Assert.Contains(_command.Subcommands, c => c.Name == "choice");
    }

    [Fact]
    public void MetadataGroup_HasKeySubcommand()
    {
        Assert.Contains(_command.Subcommands, c => c.Name == "key");
    }
}
