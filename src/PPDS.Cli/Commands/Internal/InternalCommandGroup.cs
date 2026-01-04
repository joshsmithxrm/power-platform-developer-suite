using System.CommandLine;

namespace PPDS.Cli.Commands.Internal;

/// <summary>
/// Internal/debug commands available only when PPDS_INTERNAL=1 is set.
/// These commands are for development and diagnostic purposes and are not
/// documented or supported for end users.
/// </summary>
public static class InternalCommandGroup
{
    /// <summary>
    /// Creates the internal command group.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("internal", "Internal/debug commands (development only)");

        // Add internal commands here as needed, e.g.:
        // command.Subcommands.Add(PoolStatusCommand.Create());
        // command.Subcommands.Add(CacheInfoCommand.Create());

        return command;
    }
}
