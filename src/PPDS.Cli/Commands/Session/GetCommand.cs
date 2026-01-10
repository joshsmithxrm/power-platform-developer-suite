using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Get detailed status of a worker session.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var sessionArg = new Argument<string?>("session")
        {
            Description = "Session ID (issue number)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var prOption = new Option<int?>("--pr", "-p")
        {
            Description = "Lookup session by pull request number"
        };

        var command = new Command("get", "Get detailed status of a worker session")
        {
            sessionArg,
            prOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sessionId = parseResult.GetValue(sessionArg);
            var prNumber = parseResult.GetValue(prOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(sessionId, prNumber, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? sessionId,
        int? prNumber,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Validate arguments
            if (string.IsNullOrEmpty(sessionId) && prNumber == null)
            {
                throw new ArgumentException("Either session ID or --pr option is required");
            }
            if (!string.IsNullOrEmpty(sessionId) && prNumber != null)
            {
                throw new ArgumentException("Cannot specify both session ID and --pr option");
            }

            var spawner = new WindowsTerminalWorkerSpawner();
            var logger = NullLogger<SessionService>.Instance;
            var service = new SessionService(spawner, logger);

            SessionState? session;
            if (prNumber != null)
            {
                session = await service.GetByPullRequestAsync(prNumber.Value, cancellationToken);
                if (session == null)
                {
                    throw new KeyNotFoundException($"No session found with PR #{prNumber}");
                }
                sessionId = session.Id;
            }
            else
            {
                session = await service.GetAsync(sessionId!, cancellationToken);
                if (session == null)
                {
                    throw new KeyNotFoundException($"Session '{sessionId}' not found");
                }
            }

            var worktreeStatus = await service.GetWorktreeStatusAsync(session.Id, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new SessionDetail
                {
                    SessionId = session.Id,
                    IssueNumber = session.IssueNumber,
                    IssueTitle = session.IssueTitle,
                    Status = session.Status.ToString().ToLowerInvariant(),
                    Branch = session.Branch,
                    WorktreePath = session.WorktreePath,
                    StartedAt = session.StartedAt,
                    LastHeartbeat = session.LastHeartbeat,
                    StuckReason = session.StuckReason,
                    ForwardedMessage = session.ForwardedMessage,
                    PullRequestUrl = session.PullRequestUrl,
                    IsStale = DateTimeOffset.UtcNow - session.LastHeartbeat > SessionService.StaleThreshold,
                    Worktree = worktreeStatus != null ? new WorktreeDetail
                    {
                        FilesChanged = worktreeStatus.FilesChanged,
                        Insertions = worktreeStatus.Insertions,
                        Deletions = worktreeStatus.Deletions,
                        LastCommitMessage = worktreeStatus.LastCommitMessage,
                        ChangedFiles = worktreeStatus.ChangedFiles.ToList()
                    } : null
                };

                writer.WriteSuccess(output);
            }
            else
            {
                var elapsed = DateTimeOffset.UtcNow - session.StartedAt;
                var elapsedStr = elapsed.TotalHours >= 1
                    ? $"{elapsed.TotalHours:F0}h {elapsed.Minutes}m"
                    : $"{elapsed.TotalMinutes:F0}m";

                Console.WriteLine($"Session #{session.IssueNumber}: {session.IssueTitle}");
                Console.WriteLine();
                Console.WriteLine($"  Status:    {session.Status} ({elapsedStr})");
                Console.WriteLine($"  Branch:    {session.Branch}");
                Console.WriteLine($"  Worktree:  {session.WorktreePath}");

                if (session.Status == SessionStatus.Stuck && !string.IsNullOrEmpty(session.StuckReason))
                {
                    Console.WriteLine($"  Stuck:     {session.StuckReason}");
                }

                if (!string.IsNullOrEmpty(session.ForwardedMessage))
                {
                    Console.WriteLine($"  Message:   {session.ForwardedMessage}");
                }

                if (!string.IsNullOrEmpty(session.PullRequestUrl))
                {
                    Console.WriteLine($"  PR:        {session.PullRequestUrl}");
                }

                if (worktreeStatus != null && worktreeStatus.FilesChanged > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  Git Status:");
                    Console.WriteLine($"    Files changed: {worktreeStatus.FilesChanged} (+{worktreeStatus.Insertions}, -{worktreeStatus.Deletions})");
                    if (!string.IsNullOrEmpty(worktreeStatus.LastCommitMessage))
                    {
                        Console.WriteLine($"    Last commit: {worktreeStatus.LastCommitMessage}");
                    }
                    if (worktreeStatus.ChangedFiles.Count > 0)
                    {
                        Console.WriteLine($"    Changed:");
                        foreach (var file in worktreeStatus.ChangedFiles.Take(5))
                        {
                            Console.WriteLine($"      {file}");
                        }
                        if (worktreeStatus.ChangedFiles.Count > 5)
                        {
                            Console.WriteLine($"      ... and {worktreeStatus.ChangedFiles.Count - 5} more");
                        }
                    }
                }

                // Show worker plan if exists
                var planPath = Path.Combine(session.WorktreePath, ".claude", "worker-plan.md");
                if (File.Exists(planPath))
                {
                    Console.WriteLine();
                    Console.WriteLine("  Worker Plan:");
                    var planContent = File.ReadAllText(planPath);
                    var lines = planContent.Split('\n').Take(20);
                    foreach (var line in lines)
                    {
                        Console.WriteLine($"    {line.TrimEnd()}");
                    }
                    if (planContent.Split('\n').Length > 20)
                    {
                        Console.WriteLine($"    ... (truncated, see full plan at {planPath})");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting session '{sessionId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class SessionDetail
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

        [JsonPropertyName("forwardedMessage")]
        public string? ForwardedMessage { get; set; }

        [JsonPropertyName("pullRequestUrl")]
        public string? PullRequestUrl { get; set; }

        [JsonPropertyName("isStale")]
        public bool IsStale { get; set; }

        [JsonPropertyName("worktree")]
        public WorktreeDetail? Worktree { get; set; }
    }

    private sealed class WorktreeDetail
    {
        [JsonPropertyName("filesChanged")]
        public int FilesChanged { get; set; }

        [JsonPropertyName("insertions")]
        public int Insertions { get; set; }

        [JsonPropertyName("deletions")]
        public int Deletions { get; set; }

        [JsonPropertyName("lastCommitMessage")]
        public string? LastCommitMessage { get; set; }

        [JsonPropertyName("changedFiles")]
        public List<string> ChangedFiles { get; set; } = [];
    }

    #endregion
}
