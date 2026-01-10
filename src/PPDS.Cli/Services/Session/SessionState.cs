namespace PPDS.Cli.Services.Session;

/// <summary>
/// Represents the state of a worker session.
/// </summary>
public sealed record SessionState
{
    /// <summary>
    /// Unique session identifier (typically the issue number as string).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// GitHub issue number this session is working on.
    /// </summary>
    public required int IssueNumber { get; init; }

    /// <summary>
    /// Issue title from GitHub.
    /// </summary>
    public required string IssueTitle { get; init; }

    /// <summary>
    /// Current session status.
    /// </summary>
    public required SessionStatus Status { get; init; }

    /// <summary>
    /// Git branch name for this session.
    /// </summary>
    public required string Branch { get; init; }

    /// <summary>
    /// Absolute path to the worktree directory.
    /// </summary>
    public required string WorktreePath { get; init; }

    /// <summary>
    /// When the session was started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the session last reported status.
    /// </summary>
    public required DateTimeOffset LastHeartbeat { get; init; }

    /// <summary>
    /// Reason for stuck status (null unless Status is Stuck).
    /// </summary>
    public string? StuckReason { get; init; }

    /// <summary>
    /// Message forwarded from orchestrator (null if none pending).
    /// </summary>
    public string? ForwardedMessage { get; init; }

    /// <summary>
    /// Pull request URL (null until PR is created).
    /// </summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>
    /// When the session completed (null if not yet complete).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Reason for completion (e.g., "PR merged", "Cancelled by user").
    /// Only set when status is Complete.
    /// </summary>
    public string? CompletionReason { get; init; }

    /// <summary>
    /// Git status summary for the worktree.
    /// </summary>
    public WorktreeStatus? WorktreeStatus { get; init; }
}

/// <summary>
/// Session lifecycle status.
/// Simplified to 6 core states for clarity.
/// </summary>
public enum SessionStatus
{
    /// <summary>
    /// Worker is exploring codebase and creating plan.
    /// Encompasses: registered, exploring, plan written.
    /// </summary>
    Planning,

    /// <summary>
    /// Worker actively implementing.
    /// </summary>
    Working,

    /// <summary>
    /// PR created, in review pipeline (CI, bot review, ready for human).
    /// </summary>
    Shipping,

    /// <summary>
    /// Worker hit a domain gate or repeated failure, needs human guidance.
    /// </summary>
    Stuck,

    /// <summary>
    /// Human requested pause.
    /// </summary>
    Paused,

    /// <summary>
    /// Terminal state - work done or cancelled.
    /// Check CompletionReason for details.
    /// </summary>
    Complete
}

/// <summary>
/// Git worktree status information.
/// </summary>
public sealed record WorktreeStatus
{
    /// <summary>
    /// Number of files changed.
    /// </summary>
    public int FilesChanged { get; init; }

    /// <summary>
    /// Number of insertions.
    /// </summary>
    public int Insertions { get; init; }

    /// <summary>
    /// Number of deletions.
    /// </summary>
    public int Deletions { get; init; }

    /// <summary>
    /// Most recent commit message (null if no commits yet).
    /// </summary>
    public string? LastCommitMessage { get; init; }

    /// <summary>
    /// When tests were last run (null if not run).
    /// </summary>
    public DateTimeOffset? LastTestRun { get; init; }

    /// <summary>
    /// Whether tests passed on last run (null if not run).
    /// </summary>
    public bool? TestsPassing { get; init; }

    /// <summary>
    /// List of changed file paths (abbreviated if many).
    /// </summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];
}

/// <summary>
/// Result of listing sessions with cleanup information.
/// </summary>
/// <param name="Sessions">Active sessions after cleanup.</param>
/// <param name="CleanedIssueNumbers">Issue numbers of sessions that were cleaned up because their worktrees no longer exist.</param>
public sealed record SessionListResult(
    IReadOnlyList<SessionState> Sessions,
    IReadOnlyList<int> CleanedIssueNumbers);
