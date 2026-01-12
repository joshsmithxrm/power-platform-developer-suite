using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services.Session;

/// <summary>
/// Service for managing parallel worker sessions.
/// </summary>
/// <remarks>
/// Uses hybrid persistence: in-memory primary with disk writes on state changes.
/// Sessions are stored in ~/.ppds/sessions/work-{issue}.json.
/// See ADR-0030 for orchestration architecture.
/// </remarks>
public sealed class SessionService : ISessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new SessionStatusJsonConverter() }
    };

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly IWorkerSpawner _workerSpawner;
    private readonly ILogger<SessionService> _logger;
    private readonly string _sessionsDir;
    private readonly string _repoRoot;

    /// <summary>
    /// Stale threshold - sessions without heartbeat for this long are considered stale.
    /// </summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionService"/> class.
    /// </summary>
    public SessionService(IWorkerSpawner workerSpawner, ILogger<SessionService> logger)
    {
        _workerSpawner = workerSpawner ?? throw new ArgumentNullException(nameof(workerSpawner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Determine sessions directory
        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        _sessionsDir = Path.Combine(userProfile, ".ppds", "sessions");

        // Determine repo root (assumes we're running from within the repo)
        _repoRoot = FindRepoRoot(Directory.GetCurrentDirectory())
            ?? throw new InvalidOperationException("Could not find git repository root");

        // Load existing sessions from disk
        LoadSessionsFromDisk();
    }

    /// <inheritdoc />
    public async Task<SessionState> SpawnAsync(
        int issueNumber,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress ??= NullProgressReporter.Instance;
        var sessionId = issueNumber.ToString();

        // Check spawner availability first
        if (!_workerSpawner.IsAvailable())
        {
            throw new PpdsException(
                ErrorCodes.Operation.NotSupported,
                "Worker spawner is not available. On Windows, ensure Windows Terminal (wt.exe) is installed and in your PATH.");
        }

        if (_sessions.ContainsKey(sessionId))
        {
            throw new PpdsException(ErrorCodes.Session.AlreadyExists, $"Session for issue #{issueNumber} already exists");
        }

        // Phase 1: Fetch issue from GitHub and get repo info
        progress.ReportPhase("Fetching", $"Getting issue #{issueNumber} from GitHub...");
        var issueInfo = await FetchIssueAsync(issueNumber, cancellationToken);

        // Get GitHub owner/repo from git remote
        var remoteUrl = await GetGitHubRemoteAsync(cancellationToken);
        var (gitHubOwner, gitHubRepo) = ParseGitHubUrl(remoteUrl);

        // Phase 2: Create worktree
        // Use the repo folder name so each repo gets its own worktree namespace
        var repoName = Path.GetFileName(_repoRoot);
        var worktreeName = $"{repoName}-issue-{issueNumber}";
        progress.ReportPhase("Creating worktree", $"Setting up {worktreeName}...");
        var branchName = $"issue-{issueNumber}";
        var worktreePath = Path.Combine(Path.GetDirectoryName(_repoRoot)!, worktreeName);
        await CreateWorktreeAsync(worktreePath, branchName, cancellationToken);

        // Phase 3: Write worker prompt
        progress.ReportPhase("Preparing", "Writing session prompt...");
        var promptPath = await WriteWorkerPromptAsync(worktreePath, issueNumber, issueInfo.Title, issueInfo.Body, gitHubOwner, gitHubRepo, branchName, cancellationToken);

        // Phase 4: Register session
        var now = DateTimeOffset.UtcNow;
        var session = new SessionState
        {
            Id = sessionId,
            IssueNumber = issueNumber,
            IssueTitle = issueInfo.Title,
            Status = SessionStatus.Planning,
            Branch = branchName,
            WorktreePath = worktreePath,
            StartedAt = now,
            LastHeartbeat = now
        };

        _sessions[sessionId] = session;
        await PersistSessionAsync(session, cancellationToken);

        // Phase 5: Spawn worker
        progress.ReportPhase("Spawning", "Starting worker terminal...");
        var spawnRequest = new WorkerSpawnRequest
        {
            SessionId = sessionId,
            IssueNumber = issueNumber,
            IssueTitle = issueInfo.Title,
            WorkingDirectory = worktreePath,
            PromptFilePath = promptPath,
            GitHubOwner = gitHubOwner,
            GitHubRepo = gitHubRepo
        };

        try
        {
            await _workerSpawner.SpawnAsync(spawnRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn worker for issue #{IssueNumber}", issueNumber);
            // Clean up on failure
            _sessions.TryRemove(sessionId, out _);
            DeleteSessionFile(sessionId);
            throw;
        }

        // Update status to working (atomic update to avoid race conditions)
        session = _sessions.AddOrUpdate(
            sessionId,
            session with { Status = SessionStatus.Working, LastHeartbeat = DateTimeOffset.UtcNow },
            (_, existing) => existing with { Status = SessionStatus.Working, LastHeartbeat = DateTimeOffset.UtcNow });
        await PersistSessionAsync(session, cancellationToken);

        // Write initial state to worktree so worker can check for messages
        await WriteWorktreeStateAsync(session, cancellationToken);

        progress.ReportInfo($"Worker spawned for #{issueNumber}");
        return session;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionState>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Refresh from disk in case other processes updated
        LoadSessionsFromDisk();

        // Clean up orphaned sessions (worktree no longer exists)
        CleanOrphanedSessions();

        var sessions = _sessions.Values
            .OrderBy(s => s.IssueNumber)
            .ToList();

        return Task.FromResult<IReadOnlyList<SessionState>>(sessions);
    }

    /// <inheritdoc />
    public Task<SessionListResult> ListWithCleanupInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Refresh from disk in case other processes updated
        LoadSessionsFromDisk();

        // Clean up orphaned sessions (worktree no longer exists)
        var cleanedIssueNumbers = CleanOrphanedSessions();

        var sessions = _sessions.Values
            .OrderBy(s => s.IssueNumber)
            .ToList();

        return Task.FromResult(new SessionListResult(sessions, cleanedIssueNumbers));
    }

    /// <summary>
    /// Removes session records whose worktree paths no longer exist.
    /// </summary>
    /// <returns>List of issue numbers for cleaned up sessions.</returns>
    /// <remarks>
    /// This implements lazy cleanup: when /prune or manual deletion removes a worktree,
    /// the session record becomes orphaned garbage. We clean it up next time someone lists sessions.
    /// Sessions with existing worktrees are preserved even if stale (work may be recoverable).
    /// </remarks>
    private IReadOnlyList<int> CleanOrphanedSessions()
    {
        // Find orphaned sessions using testable static helper
        var orphanedSessions = FindOrphanedSessions(_sessions.Values);

        var cleanedIssueNumbers = new List<int>();

        foreach (var session in orphanedSessions)
        {
            if (_sessions.TryRemove(session.Id, out var removedSession))
            {
                try
                {
                    DeleteSessionFile(session.Id);
                    cleanedIssueNumbers.Add(session.IssueNumber);
                    _logger.LogInformation(
                        "Cleaned up orphaned session #{IssueNumber} (worktree no longer exists at {WorktreePath})",
                        session.IssueNumber,
                        session.WorktreePath);
                }
                catch (Exception ex)
                {
                    // Re-add the session so cleanup can be retried on next list
                    _sessions.TryAdd(removedSession.Id, removedSession);
                    _logger.LogWarning(
                        ex,
                        "Failed to delete session file for orphaned session #{IssueNumber}. Will retry on next list.",
                        session.IssueNumber);
                }
            }
        }

        return cleanedIssueNumbers;
    }

    /// <summary>
    /// Identifies sessions whose worktree paths no longer exist.
    /// </summary>
    /// <param name="sessions">Sessions to check.</param>
    /// <returns>List of sessions whose worktrees are missing.</returns>
    /// <remarks>
    /// This is extracted as a static method for testability.
    /// </remarks>
    internal static IReadOnlyList<SessionState> FindOrphanedSessions(IEnumerable<SessionState> sessions)
    {
        return sessions.Where(s => !Directory.Exists(s.WorktreePath)).ToList();
    }

    /// <inheritdoc />
    public Task<SessionState?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // Refresh from disk
        LoadSessionFromDisk(sessionId);

        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    /// <inheritdoc />
    public Task<SessionState?> GetByPullRequestAsync(int prNumber, CancellationToken cancellationToken = default)
    {
        // Load ALL sessions from disk, including those with PR-related statuses
        // (Shipping, ReviewsInProgress, PrReady, Complete)
        var allSessions = LoadAllSessionsFromDisk();

        // Find session with matching PR number using robust parsing
        var session = allSessions.FirstOrDefault(s => ExtractPrNumber(s.PullRequestUrl) == prNumber);

        return Task.FromResult(session);
    }

    /// <summary>
    /// Extracts PR number from a GitHub PR URL.
    /// Handles various URL formats: /pull/123, /pull/123/files, /pull/123#discussion_r456
    /// </summary>
    internal static int? ExtractPrNumber(string? prUrl)
    {
        if (string.IsNullOrEmpty(prUrl))
            return null;

        // Use regex to robustly extract PR number from URL
        var match = System.Text.RegularExpressions.Regex.Match(prUrl, @"/pull/(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var prNumber) ? prNumber : null;
    }

    /// <inheritdoc />
    public async Task<SessionState> UpdateAsync(
        string sessionId,
        SessionStatus status,
        string? reason = null,
        string? prUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.ContainsKey(sessionId))
        {
            throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found");
        }

        // Atomic update to avoid race conditions
        var session = _sessions.AddOrUpdate(
            sessionId,
            _ => throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found"),
            (_, existing) => existing with
            {
                Status = status,
                StuckReason = status == SessionStatus.Stuck ? reason : null,
                PullRequestUrl = prUrl ?? existing.PullRequestUrl,
                LastHeartbeat = DateTimeOffset.UtcNow,
                CompletedAt = IsTerminalStatus(status) && existing.CompletedAt == null
                    ? DateTimeOffset.UtcNow
                    : existing.CompletedAt,
                CompletionReason = IsTerminalStatus(status) ? reason : existing.CompletionReason
            });

        await PersistSessionAsync(session, cancellationToken);

        _logger.LogInformation(
            "Session {SessionId} updated to {Status}{Reason}",
            sessionId,
            status,
            reason != null ? $": {reason}" : "");

        return session;
    }

    /// <summary>
    /// Returns true if the status is a terminal state (Complete).
    /// </summary>
    private static bool IsTerminalStatus(SessionStatus status) =>
        status is SessionStatus.Complete;

    /// <inheritdoc />
    public async Task PauseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var existingSession))
        {
            throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found");
        }

        if (existingSession.Status == SessionStatus.Paused)
        {
            return; // Already paused
        }

        // Atomic update to avoid race conditions
        var session = _sessions.AddOrUpdate(
            sessionId,
            _ => throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found"),
            (_, existing) => existing with
            {
                Status = SessionStatus.Paused,
                LastHeartbeat = DateTimeOffset.UtcNow
            });

        await PersistSessionAsync(session, cancellationToken);

        _logger.LogInformation("Session {SessionId} paused", sessionId);
    }

    /// <inheritdoc />
    public async Task ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var existingSession))
        {
            throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found");
        }

        if (existingSession.Status != SessionStatus.Paused)
        {
            return; // Not paused
        }

        // Atomic update to avoid race conditions
        var session = _sessions.AddOrUpdate(
            sessionId,
            _ => throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found"),
            (_, existing) => existing with
            {
                Status = SessionStatus.Working,
                LastHeartbeat = DateTimeOffset.UtcNow
            });

        await PersistSessionAsync(session, cancellationToken);

        _logger.LogInformation("Session {SessionId} resumed", sessionId);
    }

    /// <inheritdoc />
    public async Task CancelAsync(
        string sessionId,
        bool keepWorktree = false,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.ContainsKey(sessionId))
        {
            throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found");
        }

        // Atomic update to avoid race conditions
        var session = _sessions.AddOrUpdate(
            sessionId,
            _ => throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found"),
            (_, existing) => existing with
            {
                Status = SessionStatus.Complete,
                CompletedAt = DateTimeOffset.UtcNow,
                CompletionReason = "Cancelled by user",
                LastHeartbeat = DateTimeOffset.UtcNow
            });

        await PersistSessionAsync(session, cancellationToken);

        if (!keepWorktree && Directory.Exists(session.WorktreePath))
        {
            await RemoveWorktreeAsync(session.WorktreePath, cancellationToken);
        }

        // Remove from memory but keep file for history
        _sessions.TryRemove(sessionId, out _);

        _logger.LogInformation(
            "Session {SessionId} cancelled{KeepWorktree}",
            sessionId,
            keepWorktree ? " (worktree preserved)" : "");
    }

    /// <inheritdoc />
    public async Task<int> CancelAllAsync(bool keepWorktrees = false, CancellationToken cancellationToken = default)
    {
        var sessions = _sessions.Values.ToList();
        var count = 0;

        foreach (var session in sessions)
        {
            if (session.Status is SessionStatus.Working or SessionStatus.Stuck or SessionStatus.Paused or SessionStatus.Planning or SessionStatus.Shipping)
            {
                await CancelAsync(session.Id, keepWorktrees, cancellationToken);
                count++;
            }
        }

        return count;
    }

    /// <inheritdoc />
    public async Task ForwardAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.ContainsKey(sessionId))
        {
            throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found");
        }

        // Atomic update to avoid race conditions
        var session = _sessions.AddOrUpdate(
            sessionId,
            _ => throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found"),
            (_, existing) => existing with
            {
                ForwardedMessage = message,
                LastHeartbeat = DateTimeOffset.UtcNow
            });

        await PersistSessionAsync(session, cancellationToken);

        // Also write to worktree so worker can read without permission issues
        await WriteWorktreeStateAsync(session, cancellationToken);

        _logger.LogInformation("Message forwarded to session {SessionId}", sessionId);
    }

    /// <summary>
    /// Writes session state to the worktree for worker access.
    /// </summary>
    private async Task WriteWorktreeStateAsync(SessionState session, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(session.WorktreePath))
        {
            return;
        }

        var statePath = Path.Combine(session.WorktreePath, "session-state.json");
        var state = new WorktreeSessionState
        {
            Status = session.Status,
            ForwardedMessage = session.ForwardedMessage,
            LastHeartbeat = session.LastHeartbeat
        };

        var json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(statePath, json, cancellationToken);
    }

    /// <summary>
    /// Session state written to worktree for worker access.
    /// Uses same JsonOptions as main session persistence for consistency.
    /// </summary>
    private sealed record WorktreeSessionState
    {
        public SessionStatus Status { get; init; }
        public string? ForwardedMessage { get; init; }
        public DateTimeOffset LastHeartbeat { get; init; }
    }

    /// <inheritdoc />
    public Task HeartbeatAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.ContainsKey(sessionId))
        {
            throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found");
        }

        // Atomic update to avoid race conditions
        _sessions.AddOrUpdate(
            sessionId,
            _ => throw new PpdsException(ErrorCodes.Session.NotFound, $"Session '{sessionId}' not found"),
            (_, existing) => existing with { LastHeartbeat = DateTimeOffset.UtcNow });

        // Don't persist heartbeats to disk to avoid excessive I/O
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<WorktreeStatus?> GetWorktreeStatusAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        if (!Directory.Exists(session.WorktreePath))
        {
            return null;
        }

        return await GetGitStatusAsync(session.WorktreePath, cancellationToken);
    }

    #region Private Methods

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Gets the GitHub remote URL from the repository.
    /// </summary>
    private async Task<string> GetGitHubRemoteAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "remote get-url origin",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");

        // Read both streams asynchronously to avoid deadlocks
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var error = await errorTask;
            var errorMessage = string.IsNullOrWhiteSpace(error)
                ? "No additional git error output."
                : error.Trim();
            throw new InvalidOperationException(
                $"Failed to get git remote URL from repository at '{_repoRoot}'. " +
                "Ensure this directory is a git repository with a remote named 'origin' configured. " +
                $"Git exited with code {process.ExitCode}. Git error: {errorMessage}");
        }

        return (await outputTask).Trim();
    }

    /// <summary>
    /// Parses a GitHub URL to extract owner and repo name.
    /// Supports both HTTPS and SSH formats.
    /// </summary>
    /// <param name="remoteUrl">The git remote URL.</param>
    /// <returns>Tuple of (owner, repo).</returns>
    /// <exception cref="ArgumentException">If the URL cannot be parsed.</exception>
    internal static (string Owner, string Repo) ParseGitHubUrl(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            throw new ArgumentException("Remote URL cannot be empty", nameof(remoteUrl));
        }

        // Remove trailing .git if present
        var url = remoteUrl.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        // Extract path portion from URL (HTTPS or SSH format)
        string? path = null;
        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            path = url["https://github.com/".Length..];
        }
        else if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = url["git@github.com:".Length..];
        }

        // Parse owner/repo from path
        if (path != null)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return (parts[0], parts[1]);
            }
        }

        throw new ArgumentException($"Cannot parse GitHub URL: {remoteUrl}", nameof(remoteUrl));
    }

    private void LoadSessionsFromDisk()
    {
        if (!Directory.Exists(_sessionsDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_sessionsDir, "work-*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var persisted = JsonSerializer.Deserialize<PersistedSession>(json, JsonOptions);
                if (persisted != null)
                {
                    var session = persisted.ToSessionState();
                    // Only load active sessions (excludes Complete)
                    if (session.Status is not SessionStatus.Complete)
                    {
                        _sessions[session.Id] = session;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load session from {File}", file);
            }
        }
    }

    /// <summary>
    /// Loads ALL sessions from disk, including completed ones.
    /// Used for PR lookup which needs to find completed sessions.
    /// </summary>
    private List<SessionState> LoadAllSessionsFromDisk()
    {
        var sessions = new List<SessionState>();

        if (!Directory.Exists(_sessionsDir))
        {
            return sessions;
        }

        foreach (var file in Directory.GetFiles(_sessionsDir, "work-*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var persisted = JsonSerializer.Deserialize<PersistedSession>(json, JsonOptions);
                if (persisted != null)
                {
                    sessions.Add(persisted.ToSessionState());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load session from {File}", file);
            }
        }

        return sessions;
    }

    private void LoadSessionFromDisk(string sessionId)
    {
        var filePath = Path.Combine(_sessionsDir, $"work-{sessionId}.json");
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var persisted = JsonSerializer.Deserialize<PersistedSession>(json, JsonOptions);
            if (persisted != null)
            {
                _sessions[sessionId] = persisted.ToSessionState();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session {SessionId} from disk", sessionId);
        }
    }

    private async Task PersistSessionAsync(SessionState session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_sessionsDir);

        var filePath = Path.Combine(_sessionsDir, $"work-{session.Id}.json");
        var persisted = PersistedSession.FromSessionState(session);
        var json = JsonSerializer.Serialize(persisted, JsonOptions);

        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private void DeleteSessionFile(string sessionId)
    {
        var filePath = Path.Combine(_sessionsDir, $"work-{sessionId}.json");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private async Task<(string Title, string Body)> FetchIssueAsync(int issueNumber, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = $"issue view {issueNumber} --json title,body",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start gh process");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to fetch issue #{issueNumber}: {error}");
        }

        using var doc = JsonDocument.Parse(output);
        var title = doc.RootElement.GetProperty("title").GetString() ?? $"Issue #{issueNumber}";
        var body = doc.RootElement.GetProperty("body").GetString() ?? "";

        return (title, body);
    }

    private async Task FetchOriginMainAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "fetch origin main",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");

        // Read stderr asynchronously to avoid deadlocks
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var error = await errorTask;
            throw new InvalidOperationException($"Failed to fetch origin main: {error}");
        }
    }

    private async Task CreateWorktreeAsync(string worktreePath, string branchName, CancellationToken cancellationToken)
    {
        // Fetch latest from origin/main to ensure we branch from current remote state
        await FetchOriginMainAsync(cancellationToken);

        // Remove existing worktree if present
        if (Directory.Exists(worktreePath))
        {
            await RemoveWorktreeAsync(worktreePath, cancellationToken);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"worktree add \"{worktreePath}\" -b {branchName} origin/main",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");

        await process.WaitForExitAsync(cancellationToken);

        // Branch might already exist, try without -b
        if (process.ExitCode != 0)
        {
            startInfo.Arguments = $"worktree add \"{worktreePath}\" {branchName}";
            using var retry = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start git process");
            await retry.WaitForExitAsync(cancellationToken);

            if (retry.ExitCode != 0)
            {
                var error = await retry.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException($"Failed to create worktree: {error}");
            }
        }
    }

    private async Task RemoveWorktreeAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"worktree remove \"{worktreePath}\" --force",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");

        await process.WaitForExitAsync(cancellationToken);
        // Ignore errors - worktree might not exist
    }

    private async Task<string> WriteWorkerPromptAsync(
        string worktreePath,
        int issueNumber,
        string title,
        string body,
        string gitHubOwner,
        string gitHubRepo,
        string branchName,
        CancellationToken cancellationToken)
    {
        // Write to .claude/session-prompt.md in the worktree
        var claudeDir = Path.Combine(worktreePath, ".claude");
        Directory.CreateDirectory(claudeDir);

        var promptPath = Path.Combine(claudeDir, "session-prompt.md");
        var prompt = $"""
            # Session: Issue #{issueNumber}

            ## Repository Context

            **IMPORTANT:** For all GitHub operations (CLI and MCP tools), use these values:
            - Owner: `{gitHubOwner}`
            - Repo: `{gitHubRepo}`
            - Issue: `#{issueNumber}`
            - Branch: `{branchName}`

            Examples:
            ```bash
            gh issue view {issueNumber} --repo {gitHubOwner}/{gitHubRepo}
            gh pr create --repo {gitHubOwner}/{gitHubRepo} ...
            ```

            For MCP GitHub tools:
            ```
            mcp__github__get_issue(owner: "{gitHubOwner}", repo: "{gitHubRepo}", issue_number: {issueNumber})
            ```

            ## Issue
            **{title}**

            {body}

            ## Status Reporting

            **Report status at each phase transition** so the orchestrator can track your progress:

            | Phase | Command |
            |-------|---------|
            | Starting | `ppds session update --id {issueNumber} --status planning` |
            | Plan complete | `ppds session update --id {issueNumber} --status planning_complete` |
            | Implementing | `ppds session update --id {issueNumber} --status working` |
            | Stuck | `ppds session update --id {issueNumber} --status stuck --reason "description"` |

            **Note:** `/ship` automatically updates status to `shipping` → `reviews_in_progress` → `complete`.

            ## Workflow

            ### Phase 1: Planning
            1. **First:** `ppds session update --id {issueNumber} --status planning`
            2. Read and understand the issue requirements
            3. Explore the codebase to understand existing patterns
            4. Create a detailed implementation plan
            5. Write your plan to `.claude/worker-plan.md`
            6. **Then:** `ppds session update --id {issueNumber} --status planning_complete`

            ### Phase 2: Check for Messages
            Before implementing, check for forwarded messages:
            - Read `session-state.json` (at worktree root) if it exists
            - If `forwardedMessage` field exists, incorporate it
            - Then continue to implementation

            ### Phase 3: Implementation
            1. **First:** `ppds session update --id {issueNumber} --status working`
            2. Follow your plan in `.claude/worker-plan.md`
            3. Build: `dotnet build`
            4. Test: `dotnet test --filter "Category!=Integration"`
            5. Create PR via `/ship` (handles remaining status updates automatically)

            ### Domain Gates
            If you encounter these, set status to `stuck` with a clear reason:
            - Auth/Security decisions
            - Performance-critical code
            - Breaking changes
            - Data migration

            Example: `ppds session update --id {issueNumber} --status stuck --reason "Need auth decision: should we use JWT or session tokens?"`

            ## Reference
            - Follow CLAUDE.md for coding standards
            - Build must pass before shipping
            - Tests must pass before shipping
            """;

        await File.WriteAllTextAsync(promptPath, prompt, cancellationToken);
        return promptPath;
    }

    private async Task<WorktreeStatus> GetGitStatusAsync(string worktreePath, CancellationToken cancellationToken)
    {
        // Get changed files
        var statusInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "status --porcelain",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = worktreePath
        };

        using var statusProcess = Process.Start(statusInfo);
        var statusOutput = statusProcess != null
            ? await statusProcess.StandardOutput.ReadToEndAsync(cancellationToken)
            : "";

        var changedFiles = statusOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
            .Where(f => !string.IsNullOrEmpty(f))
            .Take(10)
            .ToList();

        // Get diff stats (try HEAD~1 first, fall back to unstaged diff)
        var diffInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "diff --stat HEAD~1",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = worktreePath
        };

        using var diffProcess = Process.Start(diffInfo);
        var diffOutput = "";
        if (diffProcess != null)
        {
            diffOutput = await diffProcess.StandardOutput.ReadToEndAsync(cancellationToken);
            await diffProcess.WaitForExitAsync(cancellationToken);

            // If HEAD~1 failed (no commits), try unstaged diff
            if (diffProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(diffOutput))
            {
                diffInfo.Arguments = "diff --stat";
                using var fallbackProcess = Process.Start(diffInfo);
                if (fallbackProcess != null)
                {
                    diffOutput = await fallbackProcess.StandardOutput.ReadToEndAsync(cancellationToken);
                }
            }
        }

        // Parse insertions/deletions from last line
        var insertions = 0;
        var deletions = 0;
        var lastLine = diffOutput.Split('\n').LastOrDefault(l => l.Contains("insertion") || l.Contains("deletion")) ?? "";
        if (lastLine.Contains("insertion"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(lastLine, @"(\d+) insertion");
            if (match.Success) insertions = int.Parse(match.Groups[1].Value);
        }
        if (lastLine.Contains("deletion"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(lastLine, @"(\d+) deletion");
            if (match.Success) deletions = int.Parse(match.Groups[1].Value);
        }

        // Get last commit message
        var logInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "log -1 --format=%s",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = worktreePath
        };

        using var logProcess = Process.Start(logInfo);
        var commitMessage = logProcess != null
            ? (await logProcess.StandardOutput.ReadToEndAsync(cancellationToken)).Trim()
            : null;

        return new WorktreeStatus
        {
            FilesChanged = changedFiles.Count,
            Insertions = insertions,
            Deletions = deletions,
            LastCommitMessage = string.IsNullOrEmpty(commitMessage) ? null : commitMessage,
            ChangedFiles = changedFiles
        };
    }

    #endregion

    #region Persisted Session Model

    /// <summary>
    /// JSON-serializable session model for disk persistence.
    /// </summary>
    private sealed record PersistedSession
    {
        public string Id { get; init; } = "";
        public int IssueNumber { get; init; }
        public string IssueTitle { get; init; } = "";
        public SessionStatus Status { get; init; }
        public string Branch { get; init; } = "";
        public string WorktreePath { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastHeartbeat { get; init; }
        public string? StuckReason { get; init; }
        public string? ForwardedMessage { get; init; }
        public string? PullRequestUrl { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? CompletionReason { get; init; }

        public SessionState ToSessionState() => new()
        {
            Id = Id,
            IssueNumber = IssueNumber,
            IssueTitle = IssueTitle,
            Status = Status,
            Branch = Branch,
            WorktreePath = WorktreePath,
            StartedAt = StartedAt,
            LastHeartbeat = LastHeartbeat,
            StuckReason = StuckReason,
            ForwardedMessage = ForwardedMessage,
            PullRequestUrl = PullRequestUrl,
            CompletedAt = CompletedAt,
            CompletionReason = CompletionReason
        };

        public static PersistedSession FromSessionState(SessionState state) => new()
        {
            Id = state.Id,
            IssueNumber = state.IssueNumber,
            IssueTitle = state.IssueTitle,
            Status = state.Status,
            Branch = state.Branch,
            WorktreePath = state.WorktreePath,
            StartedAt = state.StartedAt,
            LastHeartbeat = state.LastHeartbeat,
            StuckReason = state.StuckReason,
            ForwardedMessage = state.ForwardedMessage,
            PullRequestUrl = state.PullRequestUrl,
            CompletedAt = state.CompletedAt,
            CompletionReason = state.CompletionReason
        };
    }

    #endregion
}

/// <summary>
/// JSON converter that migrates old session status values to the new simplified enum.
/// </summary>
internal sealed class SessionStatusJsonConverter : JsonConverter<SessionStatus>
{
    public override SessionStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return SessionStatus.Planning;
        }

        // Map old status values to new simplified enum
        return value.ToLowerInvariant() switch
        {
            "registered" => SessionStatus.Planning,
            "planning" => SessionStatus.Planning,
            "planningcomplete" => SessionStatus.Planning,
            "working" => SessionStatus.Working,
            "shipping" => SessionStatus.Shipping,
            "reviewsinprogress" => SessionStatus.Shipping,
            "prready" => SessionStatus.Shipping,
            "stuck" => SessionStatus.Stuck,
            "paused" => SessionStatus.Paused,
            "complete" => SessionStatus.Complete,
            "cancelled" => SessionStatus.Complete,
            _ => Enum.TryParse<SessionStatus>(value, true, out var status) ? status : SessionStatus.Planning
        };
    }

    public override void Write(Utf8JsonWriter writer, SessionStatus value, JsonSerializerOptions options)
    {
        // Write using camelCase naming
        var name = value.ToString();
        writer.WriteStringValue(char.ToLowerInvariant(name[0]) + name[1..]);
    }
}
