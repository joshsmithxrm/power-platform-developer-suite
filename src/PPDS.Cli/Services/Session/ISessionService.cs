namespace PPDS.Cli.Services.Session;

using PPDS.Cli.Infrastructure.Progress;

/// <summary>
/// Application service for managing parallel worker sessions.
/// </summary>
/// <remarks>
/// This service coordinates autonomous Claude sessions working on GitHub issues.
/// Each session runs in its own git worktree with isolated file changes.
/// See ADR-0030 for orchestration architecture.
/// </remarks>
public interface ISessionService
{
    /// <summary>
    /// Spawns a new worker session for a GitHub issue.
    /// </summary>
    /// <param name="issueNumber">GitHub issue number to work on.</param>
    /// <param name="progress">Progress reporter for status updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created session state.</returns>
    /// <remarks>
    /// This method:
    /// 1. Fetches issue details from GitHub
    /// 2. Creates a worktree at ../ppds-issue-{N}
    /// 3. Starts a worker process in the worktree
    /// 4. Registers the session for monitoring
    /// </remarks>
    Task<SessionState> SpawnAsync(
        int issueNumber,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all active and recently completed sessions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of session states.</returns>
    /// <remarks>
    /// This method also performs lazy cleanup of orphaned sessions (where the worktree
    /// no longer exists). Use <see cref="ListWithCleanupInfoAsync"/> if you need to
    /// report which sessions were cleaned up.
    /// </remarks>
    Task<IReadOnlyList<SessionState>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all active and recently completed sessions, with information about cleaned up sessions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing sessions and any cleaned up issue numbers.</returns>
    /// <remarks>
    /// Orphaned sessions (where the worktree no longer exists) are automatically cleaned up.
    /// The result includes the issue numbers of any sessions that were removed.
    /// </remarks>
    Task<SessionListResult> ListWithCleanupInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed state for a specific session.
    /// </summary>
    /// <param name="sessionId">Session identifier (issue number as string).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session state, or null if not found.</returns>
    Task<SessionState?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a session by its associated pull request number.
    /// </summary>
    /// <param name="prNumber">Pull request number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session state, or null if no session has this PR.</returns>
    /// <remarks>
    /// Enables lookup by PR number for humans who interact with PRs rather than issues.
    /// The PR URL must have been recorded via UpdateAsync with prUrl parameter.
    /// </remarks>
    Task<SessionState?> GetByPullRequestAsync(int prNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates session status (called by workers).
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="status">New status.</param>
    /// <param name="reason">Reason for status change (required for Stuck).</param>
    /// <param name="prUrl">Pull request URL (for Complete status).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated session state.</returns>
    Task<SessionState> UpdateAsync(
        string sessionId,
        SessionStatus status,
        string? reason = null,
        string? prUrl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses a worker session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Worker will pause at next iteration when it reads the pause flag.
    /// </remarks>
    Task PauseAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a paused worker session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResumeAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a worker session and cleans up its worktree.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="keepWorktree">If true, preserve the worktree for debugging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CancelAsync(
        string sessionId,
        bool keepWorktree = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels all active worker sessions.
    /// </summary>
    /// <param name="keepWorktrees">If true, preserve worktrees for debugging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of sessions cancelled.</returns>
    Task<int> CancelAllAsync(
        bool keepWorktrees = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forwards a message to a worker session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="message">Message to forward (guidance, instructions, etc).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Worker will receive the message at next iteration.
    /// Messages overwrite any previous unread message.
    /// </remarks>
    Task ForwardAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a heartbeat from a worker.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// Workers should call this every 30 seconds to prove they're alive.
    /// Sessions are marked stale after 90 seconds without heartbeat.
    /// </para>
    /// <para>
    /// <b>Important:</b> Heartbeats are tracked in-memory only and are not persisted to disk
    /// to avoid excessive I/O. After an orchestrator restart, all sessions will appear stale
    /// until workers send a new heartbeat. Workers should also send status updates (not just
    /// heartbeats) periodically to ensure state survives restarts.
    /// </para>
    /// </remarks>
    Task HeartbeatAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed worktree status for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Worktree status including git state, or null if session not found.</returns>
    Task<WorktreeStatus?> GetWorktreeStatusAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
