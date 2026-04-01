using System.CommandLine;
using PPDS.Cli.Commands.Metadata.Choice;
using PPDS.Cli.Commands.Metadata.Column;
using PPDS.Cli.Commands.Metadata.Key;
using PPDS.Cli.Commands.Metadata.Relationship;
using PPDS.Cli.Commands.Metadata.Table;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Metadata command group for browsing Dataverse entity metadata.
/// </summary>
public static class MetadataCommandGroup
{
    /// <summary>
    /// Profile option for authentication.
    /// </summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>
    /// Environment option for target environment.
    /// </summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-env")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'metadata' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("metadata", "Browse Dataverse entity metadata: entities, attributes, relationships, option sets");

        command.Subcommands.Add(EntitiesCommand.Create());
        command.Subcommands.Add(EntityCommand.Create());
        command.Subcommands.Add(AttributesCommand.Create());
        command.Subcommands.Add(RelationshipsCommand.Create());
        command.Subcommands.Add(KeysCommand.Create());
        command.Subcommands.Add(OptionSetsCommand.Create());
        command.Subcommands.Add(OptionSetCommand.Create());
        command.Subcommands.Add(PublishAliasCommand.Create());
        command.Subcommands.Add(TableCommandGroup.Create());
        command.Subcommands.Add(ColumnCommandGroup.Create());
        command.Subcommands.Add(RelationshipCommandGroup.Create());
        command.Subcommands.Add(ChoiceCommandGroup.Create());
        command.Subcommands.Add(KeyCommandGroup.Create());

        return command;
    }
}
