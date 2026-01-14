using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Backlog;

/// <summary>
/// Service for fetching and categorizing GitHub project backlog.
/// </summary>
public sealed class BacklogService : IBacklogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// GitHub Project ID for the PPDS project (tracks all repos).
    /// </summary>
    private const string ProjectId = "PVT_kwHOAGk32c4BLj-0";

    /// <summary>
    /// Default cache TTL in minutes.
    /// </summary>
    private const int DefaultCacheTtlMinutes = 5;

    /// <summary>
    /// Default owner when not detected from git.
    /// </summary>
    private const string DefaultOwner = "joshsmithxrm";

    /// <summary>
    /// Default repo when not detected from git.
    /// </summary>
    private const string DefaultRepo = "power-platform-developer-suite";

    private readonly ILogger<BacklogService> _logger;
    private readonly string _cacheDir;
    private readonly string _repoRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="BacklogService"/> class.
    /// </summary>
    public BacklogService(ILogger<BacklogService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        _cacheDir = Path.Combine(userProfile, ".ppds", "cache");
        Directory.CreateDirectory(_cacheDir);

        // Try to find repo root, but don't fail if not in a repo
        _repoRoot = FindRepoRoot(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
    }

    /// <inheritdoc />
    public async Task<BacklogData> GetBacklogAsync(
        string? owner = null,
        string? repo = null,
        bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        // Resolve owner/repo
        var (resolvedOwner, resolvedRepo) = await ResolveOwnerRepoAsync(owner, repo, cancellationToken);
        var repoFilter = $"{resolvedOwner}/{resolvedRepo}";

        // Check cache
        var cacheFile = GetCacheFilePath();
        if (!noCache && TryReadCache(cacheFile, out var cachedData))
        {
            // Filter to requested repo and mark as from cache
            return FilterToRepo(cachedData!, repoFilter, fromCache: true);
        }

        // Fetch fresh data from GitHub
        var allData = await FetchProjectDataAsync(cancellationToken);

        // Cache the full data
        WriteCache(cacheFile, allData);

        // Filter to requested repo
        return FilterToRepo(allData, repoFilter, fromCache: false);
    }

    /// <inheritdoc />
    public async Task<EcosystemBacklog> GetEcosystemBacklogAsync(
        bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        // Check cache
        var cacheFile = GetCacheFilePath();
        if (!noCache && TryReadCache(cacheFile, out var cachedData))
        {
            return CreateEcosystemBacklog(cachedData!, fromCache: true);
        }

        // Fetch fresh data
        var allData = await FetchProjectDataAsync(cancellationToken);

        // Cache the data
        WriteCache(cacheFile, allData);

        return CreateEcosystemBacklog(allData, fromCache: false);
    }

    /// <inheritdoc />
    public void InvalidateCache()
    {
        var cacheFile = GetCacheFilePath();
        if (File.Exists(cacheFile))
        {
            File.Delete(cacheFile);
            _logger.LogDebug("Backlog cache invalidated");
        }
    }

    private string GetCacheFilePath() => Path.Combine(_cacheDir, "backlog.json");

    private bool TryReadCache(string cacheFile, out CachedBacklogData? data)
    {
        data = null;

        if (!File.Exists(cacheFile))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(cacheFile);
            var cached = JsonSerializer.Deserialize<CachedBacklogData>(json, JsonOptions);

            if (cached == null)
            {
                return false;
            }

            // Check TTL
            var age = DateTimeOffset.UtcNow - cached.FetchedAt;
            if (age.TotalMinutes > DefaultCacheTtlMinutes)
            {
                _logger.LogDebug("Backlog cache expired ({Age} minutes old)", (int)age.TotalMinutes);
                return false;
            }

            data = cached;
            _logger.LogDebug("Using cached backlog data ({Age} minutes old)", (int)age.TotalMinutes);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read backlog cache");
            return false;
        }
    }

    private void WriteCache(string cacheFile, CachedBacklogData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(cacheFile, json);
            _logger.LogDebug("Backlog data cached");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write backlog cache");
        }
    }

    private async Task<CachedBacklogData> FetchProjectDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching project data from GitHub...");

        var allItems = new List<ProjectItem>();
        string? cursor = null;

        // Paginate through all project items
        do
        {
            var (items, nextCursor) = await FetchProjectPageAsync(cursor, cancellationToken);
            allItems.AddRange(items);
            cursor = nextCursor;
        }
        while (cursor != null);

        _logger.LogDebug("Fetched {Count} project items", allItems.Count);

        // Convert to BacklogItems and categorize
        var now = DateTimeOffset.UtcNow;
        var bugs = new List<BacklogItem>();
        var inProgress = new List<BacklogItem>();
        var blocked = new List<BacklogItem>();
        var ready = new List<BacklogItem>();
        var untriaged = new List<BacklogItem>();

        foreach (var item in allItems)
        {
            // Skip closed issues
            if (item.State != "OPEN")
            {
                continue;
            }

            var backlogItem = ToBacklogItem(item);

            // Categorize based on type, status, and fields
            if (IsBug(backlogItem))
            {
                bugs.Add(backlogItem);
            }
            else if (backlogItem.Status?.Equals("In Progress", StringComparison.OrdinalIgnoreCase) == true)
            {
                inProgress.Add(backlogItem);
            }
            else if (backlogItem.Status?.Equals("Blocked", StringComparison.OrdinalIgnoreCase) == true ||
                     backlogItem.Labels.Any(l => l.Equals("blocked", StringComparison.OrdinalIgnoreCase)))
            {
                blocked.Add(backlogItem);
            }
            else if (!string.IsNullOrEmpty(backlogItem.Size) && !string.IsNullOrEmpty(backlogItem.Target))
            {
                ready.Add(backlogItem);
            }
            else
            {
                untriaged.Add(backlogItem);
            }
        }

        // Sort categories
        bugs = bugs.OrderBy(b => GetPriorityOrder(b.Priority)).ThenByDescending(b => b.AgeInDays).ToList();
        inProgress = inProgress.OrderByDescending(i => i.AgeInDays).ToList();
        blocked = blocked.OrderByDescending(b => b.AgeInDays).ToList();
        ready = ready.OrderBy(r => GetPriorityOrder(r.Priority)).ThenBy(r => GetSizeOrder(r.Size)).ToList();
        untriaged = untriaged.OrderByDescending(u => u.AgeInDays).ToList();

        // Fetch PRs separately
        var prs = await FetchOpenPullRequestsAsync(cancellationToken);

        return new CachedBacklogData
        {
            FetchedAt = now,
            Bugs = bugs,
            InProgress = inProgress,
            Blocked = blocked,
            Ready = ready,
            Untriaged = untriaged,
            PullRequests = prs
        };
    }

    private async Task<(List<ProjectItem> Items, string? NextCursor)> FetchProjectPageAsync(
        string? cursor,
        CancellationToken cancellationToken)
    {
        // Build GraphQL query
        var afterClause = cursor != null ? $", after: \"{cursor}\"" : "";
        var query = $$"""
            query {
              node(id: "{{ProjectId}}") {
                ... on ProjectV2 {
                  items(first: 100{{afterClause}}) {
                    pageInfo { hasNextPage endCursor }
                    nodes {
                      content {
                        ... on Issue {
                          number
                          state
                          title
                          createdAt
                          repository { nameWithOwner }
                          labels(first: 10) { nodes { name } }
                          assignees(first: 3) { nodes { login } }
                          milestone { title }
                        }
                      }
                      fieldValues(first: 10) {
                        nodes {
                          ... on ProjectV2ItemFieldSingleSelectValue {
                            field { ... on ProjectV2SingleSelectField { name } }
                            name
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = "api graphql -f query=\"" + query.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "") + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        using var process = Process.Start(startInfo)
            ?? throw new PpdsException(ErrorCodes.External.GitHubApiError, "Failed to start gh process");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("GitHub API error: {Error}", error);
            throw new PpdsException(
                ErrorCodes.External.GitHubApiError,
                $"Failed to fetch project data from GitHub: {error}");
        }

        // Parse response
        var items = new List<ProjectItem>();
        string? nextCursor = null;

        try
        {
            var json = JsonNode.Parse(output);
            var projectNode = json?["data"]?["node"]?["items"];

            if (projectNode == null)
            {
                _logger.LogWarning("Unexpected GraphQL response structure");
                return (items, null);
            }

            var pageInfo = projectNode["pageInfo"];
            var hasNextPage = pageInfo?["hasNextPage"]?.GetValue<bool>() ?? false;
            nextCursor = hasNextPage ? pageInfo?["endCursor"]?.GetValue<string>() : null;

            var nodes = projectNode["nodes"]?.AsArray();
            if (nodes == null)
            {
                return (items, nextCursor);
            }

            foreach (var node in nodes)
            {
                var content = node?["content"];
                if (content == null || content.GetValueKind() == JsonValueKind.Null)
                {
                    continue; // Skip non-issue items (draft issues, etc.)
                }

                var item = new ProjectItem
                {
                    Number = content["number"]?.GetValue<int>() ?? 0,
                    State = content["state"]?.GetValue<string>() ?? "OPEN",
                    Title = content["title"]?.GetValue<string>() ?? "",
                    CreatedAt = DateTimeOffset.TryParse(
                        content["createdAt"]?.GetValue<string>(),
                        out var created) ? created : DateTimeOffset.UtcNow,
                    Repository = content["repository"]?["nameWithOwner"]?.GetValue<string>() ?? "",
                    Labels = content["labels"]?["nodes"]?.AsArray()?
                        .Select(l => l?["name"]?.GetValue<string>() ?? "")
                        .Where(l => !string.IsNullOrEmpty(l))
                        .ToList() ?? [],
                    Assignee = content["assignees"]?["nodes"]?.AsArray()?
                        .FirstOrDefault()?["login"]?.GetValue<string>(),
                    Milestone = content["milestone"]?["title"]?.GetValue<string>()
                };

                // Parse field values
                if (node?["fieldValues"]?["nodes"]?.AsArray() is { } fieldValues)
                {
                    foreach (var fv in fieldValues)
                    {
                        var fieldName = fv?["field"]?["name"]?.GetValue<string>();
                        var value = fv?["name"]?.GetValue<string>();

                        if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(value))
                        {
                            continue;
                        }

                        switch (fieldName.ToLowerInvariant())
                        {
                            case "status":
                                item.Status = value;
                                break;
                            case "type":
                                item.Type = value;
                                break;
                            case "priority":
                                item.Priority = value;
                                break;
                            case "size":
                                item.Size = value;
                                break;
                            case "target":
                                item.Target = value;
                                break;
                        }
                    }
                }

                items.Add(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse GraphQL response");
            throw new PpdsException(
                ErrorCodes.External.GitHubApiError,
                "Failed to parse GitHub project data");
        }

        return (items, nextCursor);
    }

    private async Task<List<OpenPullRequest>> FetchOpenPullRequestsAsync(CancellationToken cancellationToken)
    {
        var prs = new List<OpenPullRequest>();

        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = "pr list --state open --json number,title,headRefName,author,createdAt,isDraft,repository --limit 50",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return prs;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Failed to fetch PRs");
            return prs;
        }

        try
        {
            var nodes = JsonNode.Parse(output)?.AsArray();
            if (nodes == null)
            {
                return prs;
            }

            foreach (var node in nodes)
            {
                prs.Add(new OpenPullRequest
                {
                    Number = node?["number"]?.GetValue<int>() ?? 0,
                    Title = node?["title"]?.GetValue<string>() ?? "",
                    Repository = node?["repository"]?["nameWithOwner"]?.GetValue<string>() ?? "",
                    HeadBranch = node?["headRefName"]?.GetValue<string>() ?? "",
                    Author = node?["author"]?["login"]?.GetValue<string>() ?? "",
                    CreatedAt = DateTimeOffset.TryParse(
                        node?["createdAt"]?.GetValue<string>(),
                        out var created) ? created : DateTimeOffset.UtcNow,
                    IsDraft = node?["isDraft"]?.GetValue<bool>() ?? false,
                    LinkedIssue = ExtractLinkedIssue(node?["headRefName"]?.GetValue<string>())
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse PR response");
        }

        return prs;
    }

    private static BacklogItem ToBacklogItem(ProjectItem item)
    {
        return new BacklogItem
        {
            Number = item.Number,
            Title = item.Title,
            Repository = item.Repository,
            Type = item.Type,
            Priority = item.Priority,
            Size = item.Size,
            Status = item.Status,
            Target = item.Target,
            Assignee = item.Assignee,
            Milestone = item.Milestone,
            Labels = item.Labels,
            CreatedAt = item.CreatedAt
        };
    }

    private static bool IsBug(BacklogItem item)
    {
        if (item.Type?.Equals("bug", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return item.Labels.Any(l => l.Equals("bug", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetPriorityOrder(string? priority)
    {
        return priority?.ToUpperInvariant() switch
        {
            "P0-CRITICAL" or "P0" => 0,
            "P1-HIGH" or "P1" => 1,
            "P2-MEDIUM" or "P2" => 2,
            "P3-LOW" or "P3" => 3,
            _ => 99
        };
    }

    private static int GetSizeOrder(string? size)
    {
        return size?.ToUpperInvariant() switch
        {
            "XS" => 0,
            "S" => 1,
            "M" => 2,
            "L" => 3,
            "XL" => 4,
            _ => 99
        };
    }

    private static int? ExtractLinkedIssue(string? branchName)
    {
        if (string.IsNullOrEmpty(branchName))
        {
            return null;
        }

        // Match patterns like "issue-123" or "123-feature-name"
        var match = System.Text.RegularExpressions.Regex.Match(branchName, @"(?:issue-)?(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var issueNumber))
        {
            return issueNumber;
        }

        return null;
    }

    private BacklogData FilterToRepo(CachedBacklogData data, string repoFilter, bool fromCache)
    {
        bool MatchesRepo(BacklogItem item) =>
            item.Repository.Equals(repoFilter, StringComparison.OrdinalIgnoreCase);

        bool MatchesPrRepo(OpenPullRequest pr) =>
            string.IsNullOrEmpty(pr.Repository) || pr.Repository.Equals(repoFilter, StringComparison.OrdinalIgnoreCase);

        var bugs = data.Bugs.Where(MatchesRepo).ToList();
        var inProgress = data.InProgress.Where(MatchesRepo).ToList();
        var blocked = data.Blocked.Where(MatchesRepo).ToList();
        var ready = data.Ready.Where(MatchesRepo).ToList();
        var untriaged = data.Untriaged.Where(MatchesRepo).ToList();
        var prs = data.PullRequests.Where(MatchesPrRepo).ToList();

        var parts = repoFilter.Split('/');
        var owner = parts.Length > 0 ? parts[0] : DefaultOwner;
        var repo = parts.Length > 1 ? parts[1] : DefaultRepo;

        return new BacklogData
        {
            Owner = owner,
            Repo = repo,
            FetchedAt = data.FetchedAt,
            FromCache = fromCache,
            Health = new HealthMetrics
            {
                OpenIssues = bugs.Count + inProgress.Count + blocked.Count + ready.Count + untriaged.Count,
                OpenPrs = prs.Count,
                TotalBugs = bugs.Count,
                CriticalBugs = bugs.Count(b => b.Priority?.Contains("P0", StringComparison.OrdinalIgnoreCase) == true),
                HighPriorityBugs = bugs.Count(b => b.Priority?.Contains("P1", StringComparison.OrdinalIgnoreCase) == true),
                InProgressCount = inProgress.Count,
                ReadyCount = ready.Count,
                BlockedCount = blocked.Count,
                UntriagedCount = untriaged.Count
            },
            Bugs = bugs,
            InProgress = inProgress,
            Blocked = blocked,
            Ready = ready,
            Untriaged = untriaged,
            PullRequests = prs
        };
    }

    private EcosystemBacklog CreateEcosystemBacklog(CachedBacklogData data, bool fromCache)
    {
        // Group by repo
        var repos = data.Bugs.Concat(data.InProgress).Concat(data.Blocked).Concat(data.Ready).Concat(data.Untriaged)
            .Select(i => i.Repository)
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct()
            .ToList();

        var repoBacklogs = repos.Select(r => FilterToRepo(data, r, fromCache)).ToList();

        return new EcosystemBacklog
        {
            Repos = repoBacklogs,
            FetchedAt = data.FetchedAt,
            Health = new HealthMetrics
            {
                OpenIssues = data.Bugs.Count + data.InProgress.Count + data.Blocked.Count + data.Ready.Count + data.Untriaged.Count,
                OpenPrs = data.PullRequests.Count,
                TotalBugs = data.Bugs.Count,
                CriticalBugs = data.Bugs.Count(b => b.Priority?.Contains("P0", StringComparison.OrdinalIgnoreCase) == true),
                HighPriorityBugs = data.Bugs.Count(b => b.Priority?.Contains("P1", StringComparison.OrdinalIgnoreCase) == true),
                InProgressCount = data.InProgress.Count,
                ReadyCount = data.Ready.Count,
                BlockedCount = data.Blocked.Count,
                UntriagedCount = data.Untriaged.Count
            }
        };
    }

    private async Task<(string Owner, string Repo)> ResolveOwnerRepoAsync(
        string? owner,
        string? repo,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo))
        {
            return (owner, repo);
        }

        // Try to detect from git remote
        try
        {
            var remoteUrl = await GetGitHubRemoteAsync(cancellationToken);
            return ParseGitHubUrl(remoteUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not detect repo from git remote, using defaults");
            return (DefaultOwner, DefaultRepo);
        }
    }

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

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Failed to get git remote URL");
        }

        return output.Trim();
    }

    private static (string Owner, string Repo) ParseGitHubUrl(string remoteUrl)
    {
        var url = remoteUrl.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        string? path = null;
        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            path = url["https://github.com/".Length..];
        }
        else if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = url["git@github.com:".Length..];
        }

        if (path != null)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return (parts[0], parts[1]);
            }
        }

        throw new ArgumentException($"Cannot parse GitHub URL: {remoteUrl}");
    }

    private static string? FindRepoRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
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

    #region Internal Types

    private sealed class ProjectItem
    {
        public int Number { get; set; }
        public string State { get; set; } = "OPEN";
        public string Title { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string Repository { get; set; } = "";
        public List<string> Labels { get; set; } = [];
        public string? Assignee { get; set; }
        public string? Milestone { get; set; }
        public string? Status { get; set; }
        public string? Type { get; set; }
        public string? Priority { get; set; }
        public string? Size { get; set; }
        public string? Target { get; set; }
    }

    private sealed class CachedBacklogData
    {
        public DateTimeOffset FetchedAt { get; set; }
        public List<BacklogItem> Bugs { get; set; } = [];
        public List<BacklogItem> InProgress { get; set; } = [];
        public List<BacklogItem> Blocked { get; set; } = [];
        public List<BacklogItem> Ready { get; set; } = [];
        public List<BacklogItem> Untriaged { get; set; } = [];
        public List<OpenPullRequest> PullRequests { get; set; } = [];
    }

    #endregion
}
