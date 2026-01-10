using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// List all active worker sessions.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List all active worker sessions");

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var spawner = new WindowsTerminalWorkerSpawner();
            var logger = NullLogger<SessionService>.Instance;
            var service = new SessionService(spawner, logger);

            var result = await service.ListWithCleanupInfoAsync(cancellationToken);
            var sessions = result.Sessions;

            // Report any cleaned up orphaned sessions
            if (result.CleanedIssueNumbers.Count > 0 && !globalOptions.IsJsonMode)
            {
                foreach (var issueNumber in result.CleanedIssueNumbers)
                {
                    Console.Error.WriteLine($"Cleaned up orphaned session #{issueNumber}");
                }
                Console.Error.WriteLine();
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new SessionListOutput
                {
                    Sessions = sessions.Select(s => new SessionListItem
                    {
                        SessionId = s.Id,
                        IssueNumber = s.IssueNumber,
                        IssueTitle = s.IssueTitle,
                        Status = s.Status.ToString().ToLowerInvariant(),
                        Branch = s.Branch,
                        WorktreePath = s.WorktreePath,
                        StartedAt = s.StartedAt,
                        LastHeartbeat = s.LastHeartbeat,
                        StuckReason = s.StuckReason,
                        PullRequestUrl = s.PullRequestUrl,
                        IsStale = DateTimeOffset.UtcNow - s.LastHeartbeat > SessionService.StaleThreshold
                    }).ToList(),
                    CleanedIssueNumbers = result.CleanedIssueNumbers.ToList()
                };

                writer.WriteSuccess(output);
            }
            else
            {
                if (sessions.Count == 0)
                {
                    Console.Error.WriteLine("No active sessions.");
                }
                else
                {
                    Console.Error.WriteLine($"Active Sessions ({sessions.Count}):");
                    Console.Error.WriteLine();

                    foreach (var session in sessions)
                    {
                        var isStale = DateTimeOffset.UtcNow - session.LastHeartbeat > SessionService.StaleThreshold;

                        var statusIcon = isStale ? "[?]" : session.Status switch
                        {
                            SessionStatus.Registered => "[ ]",
                            SessionStatus.Planning => "[~]",
                            SessionStatus.PlanningComplete => "[P]",
                            SessionStatus.Working => "[*]",
                            SessionStatus.Shipping => "[^]",
                            SessionStatus.ReviewsInProgress => "[R]",
                            SessionStatus.PrReady => "[+]",
                            SessionStatus.Stuck => "[!]",
                            SessionStatus.Paused => "[-]",
                            SessionStatus.Complete => "[+]",
                            SessionStatus.Cancelled => "[x]",
                            _ => "[?]"
                        };

                        var elapsed = DateTimeOffset.UtcNow - session.StartedAt;
                        var elapsedStr = elapsed.TotalHours >= 1
                            ? $"{elapsed.TotalHours:F0}h {elapsed.Minutes}m"
                            : $"{elapsed.TotalMinutes:F0}m";

                        var statusText = isStale ? "STALE (no heartbeat)" : session.Status.ToString();

                        Console.WriteLine($"  {statusIcon} #{session.IssueNumber} - {session.IssueTitle}");
                        Console.WriteLine($"      Status: {statusText} ({elapsedStr})");
                        Console.WriteLine($"      Branch: {session.Branch}");

                        if (session.Status == SessionStatus.Stuck && !string.IsNullOrEmpty(session.StuckReason))
                        {
                            Console.WriteLine($"      Reason: {session.StuckReason}");
                        }

                        if (!string.IsNullOrEmpty(session.PullRequestUrl))
                        {
                            Console.WriteLine($"      PR: {session.PullRequestUrl}");
                        }

                        Console.WriteLine();
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing sessions", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class SessionListOutput
    {
        [JsonPropertyName("sessions")]
        public List<SessionListItem> Sessions { get; set; } = [];

        [JsonPropertyName("cleanedIssueNumbers")]
        public List<int> CleanedIssueNumbers { get; set; } = [];
    }

    private sealed class SessionListItem
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

        [JsonPropertyName("lastHeartbeat")]
        public DateTimeOffset LastHeartbeat { get; set; }

        [JsonPropertyName("stuckReason")]
        public string? StuckReason { get; set; }

        [JsonPropertyName("pullRequestUrl")]
        public string? PullRequestUrl { get; set; }

        [JsonPropertyName("isStale")]
        public bool IsStale { get; set; }
    }

    #endregion
}
