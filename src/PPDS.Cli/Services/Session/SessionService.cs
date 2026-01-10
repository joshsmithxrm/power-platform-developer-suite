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
                LastHeartbeat = DateTimeOffset.UtcNow
            });

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
                Status = SessionStatus.Cancelled,
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
