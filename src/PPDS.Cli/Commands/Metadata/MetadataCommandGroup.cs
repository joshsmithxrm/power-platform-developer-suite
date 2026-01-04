using System.CommandLine;

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

        return command;
    }
}
