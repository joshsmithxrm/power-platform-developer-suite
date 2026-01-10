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
    /// <summary>
    /// Threshold in seconds before showing "last update" time for active sessions.
    /// </summary>
    private const int LastUpdateDisplayThresholdSeconds = 60;

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
                        PullRequestNumber = ExtractPrNumber(s.PullRequestUrl),
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
                        // Show actual status - don't override based on time since last update
                        // Workers report state transitions, not heartbeats, so silence during
                        // active implementation is normal
                        var statusIcon = session.Status switch
                        {
                            SessionStatus.Planning => "[~]",
                            SessionStatus.Working => "[*]",
                            SessionStatus.Shipping => "[^]",
                            SessionStatus.Stuck => "[!]",
                            SessionStatus.Paused => "[-]",
                            SessionStatus.Complete => "[+]",
                            _ => "[?]"
                        };

                        var elapsed = DateTimeOffset.UtcNow - session.StartedAt;
                        var elapsedStr = FormatDuration(elapsed);

                        // For active sessions, show time since last update as informational
                        var timeSinceUpdate = DateTimeOffset.UtcNow - session.LastHeartbeat;
                        var lastUpdateStr = FormatDuration(timeSinceUpdate);

                        var isActiveSession = session.Status is SessionStatus.Working
                            or SessionStatus.Planning
                            or SessionStatus.Shipping;

                        var statusText = session.Status.ToString();
                        if (isActiveSession && timeSinceUpdate.TotalSeconds > LastUpdateDisplayThresholdSeconds)
                        {
                            statusText += $" (last update: {lastUpdateStr} ago)";
                        }

                        // Extract PR number from URL if present (format: .../pull/123)
                        var prNumber = ExtractPrNumber(session.PullRequestUrl);
                        var prSuffix = prNumber != null ? $" → PR #{prNumber}" : "";

                        Console.WriteLine($"  {statusIcon} #{session.IssueNumber}{prSuffix} - {session.IssueTitle}");
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

    /// <summary>
    /// Formats a TimeSpan as a friendly duration string (e.g., "1h 30m" or "45m").
    /// Uses integer truncation to avoid rounding issues.
    /// </summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }
        return $"{(int)duration.TotalMinutes}m";
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

        [JsonPropertyName("pullRequestNumber")]
        public int? PullRequestNumber { get; set; }

        [JsonPropertyName("isStale")]
        public bool IsStale { get; set; }
    }

    #endregion

    /// <summary>
    /// Extracts PR number from a GitHub PR URL.
    /// Delegates to SessionService.ExtractPrNumber for consistency.
    /// </summary>
    private static int? ExtractPrNumber(string? prUrl) => SessionService.ExtractPrNumber(prUrl);
}
