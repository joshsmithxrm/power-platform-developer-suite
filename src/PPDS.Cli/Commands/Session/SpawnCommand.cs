using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Spawn a new worker session for a GitHub issue.
/// </summary>
public static class SpawnCommand
{
    public static Command Create()
    {
        var issueArg = new Argument<int>("issue")
        {
            Description = "GitHub issue number to work on"
        };

        var command = new Command("spawn", "Spawn a new worker session for a GitHub issue")
        {
            issueArg
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var issueNumber = parseResult.GetValue(issueArg);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(issueNumber, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        int issueNumber,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var spawner = new WindowsTerminalWorkerSpawner();
            if (!spawner.IsAvailable())
            {
                throw new InvalidOperationException("Windows Terminal (wt.exe) is not available. Install it from the Microsoft Store.");
            }

            var logger = NullLogger<SessionService>.Instance;
            var service = new SessionService(spawner, logger);

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Spawning worker for issue #{issueNumber}...");
            }

            var session = await service.SpawnAsync(issueNumber, cancellationToken: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new SpawnResult
                {
                    SessionId = session.Id,
                    IssueNumber = session.IssueNumber,
                    IssueTitle = session.IssueTitle,
                    Status = session.Status.ToString().ToLowerInvariant(),
                    Branch = session.Branch,
                    WorktreePath = session.WorktreePath,
                    StartedAt = session.StartedAt
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine();
                Console.WriteLine($"Worker spawned for #{issueNumber}: {session.IssueTitle}");
                Console.WriteLine($"  Branch: {session.Branch}");
                Console.WriteLine($"  Worktree: {session.WorktreePath}");
                Console.WriteLine();
                Console.WriteLine("Use 'ppds session list' to monitor progress.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"spawning session for issue #{issueNumber}", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class SpawnResult
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("issueNumber")]
        public int IssueNumber { get; set; }

        [JsonPropertyName("issueTitle")]
        public string IssueTitle { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("branch")]
        public string Branch { get; set; } = "";

        [JsonPropertyName("worktreePath")]
        public string WorktreePath { get; set; } = "";

        [JsonPropertyName("startedAt")]
        public DateTimeOffset StartedAt { get; set; }
    }

    #endregion
}
