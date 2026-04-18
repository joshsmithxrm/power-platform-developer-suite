using System.CommandLine;

namespace PPDS.Cli.Commands.Logs;

/// <summary>
/// The 'logs' command group. Local-only observability for post-launch triage —
/// <b>v1 emits no remote telemetry</b>. These commands read files already on disk
/// (<c>~/.ppds/*.log</c>) and bundle them for user-submitted bug reports.
/// </summary>
public static class LogsCommandGroup
{
    /// <summary>
    /// Creates the 'logs' command group with 'tail' (default) and 'dump' subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command(
            "logs",
            "Show recent PPDS log entries or bundle diagnostics for a bug report");

        command.Subcommands.Add(LogsTailCommand.Create());
        command.Subcommands.Add(LogsDumpCommand.Create());

        // Default action — run tail with defaults so 'ppds logs' is a useful shorthand.
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            return await LogsTailCommand.ExecuteAsync(
                lineCount: 50,
                levelFilter: null,
                cancellationToken);
        });

        return command;
    }
}
