using System.CommandLine;
using PPDS.Cli.Interactive;

namespace PPDS.Cli.Commands;

/// <summary>
/// Launches interactive TUI mode for profile/environment selection and SQL querying.
/// </summary>
/// <remarks>
/// Also accessible via 'ppds -i' or 'ppds --interactive' (handled in Program.cs).
/// </remarks>
public static class InteractiveCommand
{
    /// <summary>
    /// Creates the 'interactive' command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("interactive", "Launch interactive TUI mode for profile selection and SQL queries");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            return await InteractiveCli.RunAsync(cancellationToken);
        });

        return command;
    }
}
