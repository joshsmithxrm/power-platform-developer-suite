namespace PPDS.Cli.Services.Session;

/// <summary>
/// Abstraction for spawning worker processes in terminals.
/// </summary>
/// <remarks>
/// This interface allows different implementations for different platforms:
/// - Windows: Uses Windows Terminal (wt.exe)
/// - macOS/Linux: Could use tmux or similar (future)
/// </remarks>
public interface IWorkerSpawner
{
    /// <summary>
    /// Spawns a worker process in a new terminal.
    /// </summary>
    /// <param name="request">Worker spawn configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Information about the spawned worker.</returns>
    Task<SpawnedWorker> SpawnAsync(WorkerSpawnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the terminal application is available.
    /// </summary>
    /// <returns>True if the terminal is available.</returns>
    bool IsAvailable();
}

/// <summary>
/// Configuration for spawning a worker.
/// </summary>
public sealed record WorkerSpawnRequest
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// GitHub issue number.
    /// </summary>
    public required int IssueNumber { get; init; }

    /// <summary>
    /// Issue title for display.
    /// </summary>
    public required string IssueTitle { get; init; }

    /// <summary>
    /// Working directory (worktree path).
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Path to the prompt file for the worker.
    /// </summary>
    public required string PromptFilePath { get; init; }

    /// <summary>
    /// GitHub repository owner (for GITHUB_REPOSITORY env var).
    /// </summary>
    public required string GitHubOwner { get; init; }

    /// <summary>
    /// GitHub repository name (for GITHUB_REPOSITORY env var).
    /// </summary>
    public required string GitHubRepo { get; init; }

    /// <summary>
    /// Maximum iterations for the worker loop (default 50).
    /// </summary>
    public int MaxIterations { get; init; } = 50;
}

/// <summary>
/// Information about a spawned worker.
/// </summary>
public sealed record SpawnedWorker
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Process ID of the spawned terminal (may be the terminal host, not Claude).
    /// </summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Terminal window/tab title.
    /// </summary>
    public string? TerminalTitle { get; init; }
}
