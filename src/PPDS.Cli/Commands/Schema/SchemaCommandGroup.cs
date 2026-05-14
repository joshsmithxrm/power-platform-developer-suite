using System.CommandLine;

namespace PPDS.Cli.Commands.Schema;

/// <summary>
/// <c>schema</c> command group — tooling for comparing Dataverse schemas
/// between environments and data packages.
/// </summary>
public static class SchemaCommandGroup
{
    /// <summary>
    /// Creates the <c>schema</c> command with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("schema", "Compare Dataverse schemas across environments and data packages.");
        command.Subcommands.Add(CompareCommand.Create());
        return command;
    }
}
