using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
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

        if (_sessions.ContainsKey(sessionId))
        {
            throw new InvalidOperationException($"Session for issue #{issueNumber} already exists");
        }

        // Phase 1: Fetch issue from GitHub
        progress.ReportPhase("Fetching", $"Getting issue #{issueNumber} from GitHub...");
        var issueInfo = await FetchIssueAsync(issueNumber, cancellationToken);

        // Phase 2: Create worktree
        progress.ReportPhase("Creating worktree", $"Setting up ppds-issue-{issueNumber}...");
        var branchName = $"issue-{issueNumber}";
        var worktreePath = Path.Combine(Path.GetDirectoryName(_repoRoot)!, $"ppds-issue-{issueNumber}");

        await CreateWorktreeAsync(worktreePath, branchName, cancellationToken);

        // Phase 3: Write worker prompt
        progress.ReportPhase("Preparing", "Writing session prompt...");
        var promptPath = await WriteWorkerPromptAsync(worktreePath, issueNumber, issueInfo.Title, issueInfo.Body, cancellationToken);

        // Phase 4: Register session
        var now = DateTimeOffset.UtcNow;
        var session = new SessionState
        {
            Id = sessionId,
            IssueNumber = issueNumber,
            IssueTitle = issueInfo.Title,
            Status = SessionStatus.Registered,
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
            PromptFilePath = promptPath
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

        // Update status to working
        session = session with
        {
            Status = SessionStatus.Working,
            LastHeartbeat = DateTimeOffset.UtcNow
        };
        _sessions[sessionId] = session;
        await PersistSessionAsync(session, cancellationToken);

        progress.ReportInfo($"Worker spawned for #{issueNumber}");
        return session;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionState>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Refresh from disk in case other processes updated
        LoadSessionsFromDisk();

        var sessions = _sessions.Values
            .OrderBy(s => s.IssueNumber)
            .ToList();

        return Task.FromResult<IReadOnlyList<SessionState>>(sessions);
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
    public async Task UpdateAsync(
        string sessionId,
        SessionStatus status,
        string? reason = null,
        string? prUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found");
        }

        session = session with
        {
            Status = status,
            StuckReason = status == SessionStatus.Stuck ? reason : null,
            PullRequestUrl = prUrl ?? session.PullRequestUrl,
            LastHeartbeat = DateTimeOffset.UtcNow
        };

        _sessions[sessionId] = session;
        await PersistSessionAsync(session, cancellationToken);

        _logger.LogInformation(
            "Session {SessionId} updated to {Status}{Reason}",
            sessionId,
            status,
            reason != null ? $": {reason}" : "");
    }

    /// <inheritdoc />
    public async Task PauseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found");
        }

        if (session.Status == SessionStatus.Paused)
        {
            return; // Already paused
        }

        session = session with
        {
            Status = SessionStatus.Paused,
            LastHeartbeat = DateTimeOffset.UtcNow
        };

        _sessions[sessionId] = session;
        await PersistSessionAsync(session, cancellationToken);

        _logger.LogInformation("Session {SessionId} paused", sessionId);
    }

    /// <inheritdoc />
    public async Task ResumeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found");
        }

        if (session.Status != SessionStatus.Paused)
        {
            return; // Not paused
        }

        session = session with
        {
            Status = SessionStatus.Working,
            LastHeartbeat = DateTimeOffset.UtcNow
        };

        _sessions[sessionId] = session;
        await PersistSessionAsync(session, cancellationToken);

        _logger.LogInformation("Session {SessionId} resumed", sessionId);
    }

    /// <inheritdoc />
    public async Task CancelAsync(
        string sessionId,
        bool keepWorktree = false,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found");
        }

        session = session with
        {
            Status = SessionStatus.Cancelled,
            LastHeartbeat = DateTimeOffset.UtcNow
        };

        _sessions[sessionId] = session;
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
            if (session.Status is SessionStatus.Working or SessionStatus.Stuck or SessionStatus.Paused or SessionStatus.Registered)
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
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found");
        }

        session = session with
        {
            ForwardedMessage = message,
            LastHeartbeat = DateTimeOffset.UtcNow
        };

        _sessions[sessionId] = session;
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
        var state = new
        {
            status = session.Status.ToString().ToLowerInvariant(),
            forwardedMessage = session.ForwardedMessage,
            lastHeartbeat = session.LastHeartbeat
        };

        var json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(statePath, json, cancellationToken);
    }

    /// <inheritdoc />
    public async Task HeartbeatAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' not found");
        }

        session = session with
        {
            LastHeartbeat = DateTimeOffset.UtcNow
        };

        _sessions[sessionId] = session;
        // Don't persist heartbeats to disk to avoid excessive I/O
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
                    // Only load active sessions
                    if (session.Status is SessionStatus.Working or SessionStatus.Stuck or SessionStatus.Paused or SessionStatus.Registered)
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

    private async Task CreateWorktreeAsync(string worktreePath, string branchName, CancellationToken cancellationToken)
    {
        // Remove existing worktree if present
        if (Directory.Exists(worktreePath))
        {
            await RemoveWorktreeAsync(worktreePath, cancellationToken);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"worktree add \"{worktreePath}\" -b {branchName}",
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
        CancellationToken cancellationToken)
    {
        // Write to .claude/session-prompt.md in the worktree
        var claudeDir = Path.Combine(worktreePath, ".claude");
        Directory.CreateDirectory(claudeDir);

        var promptPath = Path.Combine(claudeDir, "session-prompt.md");
        var prompt = $"""
            # Session: Issue #{issueNumber}

            ## Issue
            **{title}**

            {body}

            ## Workflow

            ### Phase 1: Planning
            1. Read and understand the issue requirements
            2. Explore the codebase to understand existing patterns
            3. Create a detailed implementation plan
            4. Write your plan to `.claude/worker-plan.md`

            ### Phase 2: Check for Messages
            Before implementing, check for forwarded messages:
            - Read `session-state.json` (at worktree root) if it exists
            - If `forwardedMessage` field exists, incorporate it
            - Then continue to implementation

            ### Phase 3: Implementation
            1. Follow your plan in `.claude/worker-plan.md`
            2. Build: `dotnet build`
            3. Test: `dotnet test --filter "Category!=Integration"`
            4. Create PR via `/ship`

            ### Status Updates (Optional)
            Try to update status, but continue if it fails:
            - `ppds session update --id {issueNumber} --status planning`
            - `ppds session update --id {issueNumber} --status working`
            - `ppds session update --id {issueNumber} --status complete`
            - `ppds session update --id {issueNumber} --status stuck --reason "description"`
            If the command fails, note it and continue - the work is more important than status tracking.

            ### Domain Gates (set stuck if possible, otherwise note in PR)
            - Auth/Security decisions
            - Performance-critical code
            - Breaking changes
            - Data migration

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
            PullRequestUrl = PullRequestUrl
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
            PullRequestUrl = state.PullRequestUrl
        };
    }

    #endregion
}
