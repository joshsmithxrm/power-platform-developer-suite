using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Cancel all active worker sessions.
/// </summary>
public static class CancelAllCommand
{
    public static Command Create()
    {
        var keepWorktreesOption = new Option<bool>("--keep-worktrees", "-k")
        {
            Description = "Keep worktrees for debugging (don't clean up)"
        };

        var command = new Command("cancel-all", "Cancel all active worker sessions")
        {
            keepWorktreesOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var keepWorktrees = parseResult.GetValue(keepWorktreesOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(keepWorktrees, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        bool keepWorktrees,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var spawner = new WindowsTerminalWorkerSpawner();
            var logger = NullLogger<SessionService>.Instance;
            var service = new SessionService(spawner, logger);

            var count = await service.CancelAllAsync(keepWorktrees, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new CancelAllResult
                {
                    CancelledCount = count,
                    WorktreesPreserved = keepWorktrees
                };

                writer.WriteSuccess(output);
            }
            else
            {
                if (count == 0)
                {
                    Console.WriteLine("No active sessions to cancel.");
                }
                else
                {
                    Console.WriteLine($"Cancelled {count} session(s).");
                    if (keepWorktrees)
                    {
                        Console.WriteLine("Worktrees preserved for debugging.");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "cancelling all sessions", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class CancelAllResult
    {
        [JsonPropertyName("cancelledCount")]
        public int CancelledCount { get; set; }

        [JsonPropertyName("worktreesPreserved")]
        public bool WorktreesPreserved { get; set; }
    }

    #endregion
}
