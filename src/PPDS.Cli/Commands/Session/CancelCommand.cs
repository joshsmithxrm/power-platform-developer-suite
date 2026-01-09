using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Cancel a worker session.
/// </summary>
public static class CancelCommand
{
    public static Command Create()
    {
        var sessionArg = new Argument<string>("session")
        {
            Description = "Session ID (issue number)"
        };

        var keepWorktreeOption = new Option<bool>("--keep-worktree", "-k")
        {
            Description = "Keep the worktree for debugging (don't clean up)"
        };

        var command = new Command("cancel", "Cancel a worker session")
        {
            sessionArg,
            keepWorktreeOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sessionId = parseResult.GetValue(sessionArg)!;
            var keepWorktree = parseResult.GetValue(keepWorktreeOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(sessionId, keepWorktree, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string sessionId,
        bool keepWorktree,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var spawner = new WindowsTerminalWorkerSpawner();
            var logger = NullLogger<SessionService>.Instance;
            var service = new SessionService(spawner, logger);

            await service.CancelAsync(sessionId, keepWorktree, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new CancelResult
                {
                    SessionId = sessionId,
                    Cancelled = true,
                    WorktreePreserved = keepWorktree
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Session #{sessionId} cancelled.");
                if (keepWorktree)
                {
                    Console.WriteLine("Worktree preserved for debugging.");
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"cancelling session '{sessionId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class CancelResult
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("cancelled")]
        public bool Cancelled { get; set; }

        [JsonPropertyName("worktreePreserved")]
        public bool WorktreePreserved { get; set; }
    }

    #endregion
}
