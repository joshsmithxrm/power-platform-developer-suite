using System.Text.Json.Serialization;

namespace PPDS.Cli.Services.Backlog;

/// <summary>
/// Backlog summary data for a repository or ecosystem.
/// </summary>
public sealed record BacklogData
{
    /// <summary>
    /// Repository owner (e.g., "joshsmithxrm").
    /// </summary>
    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    /// <summary>
    /// Repository name (e.g., "power-platform-developer-suite").
    /// </summary>
    [JsonPropertyName("repo")]
    public required string Repo { get; init; }

    /// <summary>
    /// When this data was fetched.
    /// </summary>
    [JsonPropertyName("fetchedAt")]
    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>
    /// Whether this data came from cache.
    /// </summary>
    [JsonPropertyName("fromCache")]
    public bool FromCache { get; init; }

    /// <summary>
    /// Project health metrics.
    /// </summary>
    [JsonPropertyName("health")]
    public required HealthMetrics Health { get; init; }

    /// <summary>
    /// Active milestone progress (null if no active milestone).
    /// </summary>
    [JsonPropertyName("activeMilestone")]
    public MilestoneProgress? ActiveMilestone { get; init; }

    /// <summary>
    /// Bug issues sorted by priority then age.
    /// </summary>
    [JsonPropertyName("bugs")]
    public required IReadOnlyList<BacklogItem> Bugs { get; init; }

    /// <summary>
    /// Issues currently in progress.
    /// </summary>
    [JsonPropertyName("inProgress")]
    public required IReadOnlyList<BacklogItem> InProgress { get; init; }

    /// <summary>
    /// Blocked issues.
    /// </summary>
    [JsonPropertyName("blocked")]
    public required IReadOnlyList<BacklogItem> Blocked { get; init; }

    /// <summary>
    /// Ready-for-work issues (have Size and Target set).
    /// </summary>
    [JsonPropertyName("ready")]
    public required IReadOnlyList<BacklogItem> Ready { get; init; }

    /// <summary>
    /// Issues missing Size or Target fields.
    /// </summary>
    [JsonPropertyName("untriaged")]
    public required IReadOnlyList<BacklogItem> Untriaged { get; init; }

    /// <summary>
    /// Open pull requests.
    /// </summary>
    [JsonPropertyName("pullRequests")]
    public required IReadOnlyList<OpenPullRequest> PullRequests { get; init; }
}

/// <summary>
/// Cross-repo backlog aggregation.
/// </summary>
public sealed record EcosystemBacklog
{
    /// <summary>
    /// Per-repo backlog data.
    /// </summary>
    [JsonPropertyName("repos")]
    public required IReadOnlyList<BacklogData> Repos { get; init; }

    /// <summary>
    /// When this data was fetched.
    /// </summary>
    [JsonPropertyName("fetchedAt")]
    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>
    /// Aggregated health metrics.
    /// </summary>
    [JsonPropertyName("health")]
    public required HealthMetrics Health { get; init; }
}

/// <summary>
/// Project health metrics.
/// </summary>
public sealed record HealthMetrics
{
    /// <summary>
    /// Total open issues.
    /// </summary>
    [JsonPropertyName("openIssues")]
    public int OpenIssues { get; init; }

    /// <summary>
    /// Number of open PRs.
    /// </summary>
    [JsonPropertyName("openPrs")]
    public int OpenPrs { get; init; }

    /// <summary>
    /// Total bugs.
    /// </summary>
    [JsonPropertyName("totalBugs")]
    public int TotalBugs { get; init; }

    /// <summary>
    /// Number of critical (P0) bugs.
    /// </summary>
    [JsonPropertyName("criticalBugs")]
    public int CriticalBugs { get; init; }

    /// <summary>
    /// Number of high priority (P1) bugs.
    /// </summary>
    [JsonPropertyName("highPriorityBugs")]
    public int HighPriorityBugs { get; init; }

    /// <summary>
    /// Issues in progress.
    /// </summary>
    [JsonPropertyName("inProgressCount")]
    public int InProgressCount { get; init; }

    /// <summary>
    /// Ready-for-work issues.
    /// </summary>
    [JsonPropertyName("readyCount")]
    public int ReadyCount { get; init; }

    /// <summary>
    /// Blocked issues.
    /// </summary>
    [JsonPropertyName("blockedCount")]
    public int BlockedCount { get; init; }

    /// <summary>
    /// Untriaged issues.
    /// </summary>
    [JsonPropertyName("untriagedCount")]
    public int UntriagedCount { get; init; }
}

/// <summary>
/// Milestone progress information.
/// </summary>
public sealed record MilestoneProgress
{
    /// <summary>
    /// Milestone title.
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// Completed issues in milestone.
    /// </summary>
    [JsonPropertyName("completedCount")]
    public int CompletedCount { get; init; }

    /// <summary>
    /// Total issues in milestone.
    /// </summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    /// <summary>
    /// Target date (if set).
    /// </summary>
    [JsonPropertyName("dueOn")]
    public DateTimeOffset? DueOn { get; init; }

    /// <summary>
    /// Completion percentage.
    /// </summary>
    [JsonPropertyName("percentComplete")]
    public int PercentComplete => TotalCount > 0 ? (int)((CompletedCount * 100.0) / TotalCount) : 0;
}

/// <summary>
/// A single backlog item (issue).
/// </summary>
public sealed record BacklogItem
{
    /// <summary>
    /// GitHub issue number.
    /// </summary>
    [JsonPropertyName("number")]
    public int Number { get; init; }

    /// <summary>
    /// Issue title.
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// Repository identifier (e.g., "joshsmithxrm/power-platform-developer-suite").
    /// </summary>
    [JsonPropertyName("repository")]
    public required string Repository { get; init; }

    /// <summary>
    /// Project type field value (bug, feature, enhancement, etc.).
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Project priority field value (P0-Critical, P1-High, P2-Medium, P3-Low).
    /// </summary>
    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    /// <summary>
    /// Project size field value (XS, S, M, L, XL).
    /// </summary>
    [JsonPropertyName("size")]
    public string? Size { get; init; }

    /// <summary>
    /// Project status field value (Todo, In Progress, Blocked, Done).
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>
    /// Project target field value (milestone/sprint).
    /// </summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>
    /// Primary assignee login (null if unassigned).
    /// </summary>
    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    /// <summary>
    /// Milestone title (if assigned to a milestone).
    /// </summary>
    [JsonPropertyName("milestone")]
    public string? Milestone { get; init; }

    /// <summary>
    /// Issue labels.
    /// </summary>
    [JsonPropertyName("labels")]
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>
    /// When the issue was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Age in days since creation.
    /// </summary>
    [JsonPropertyName("ageInDays")]
    public int AgeInDays => (int)(DateTimeOffset.UtcNow - CreatedAt).TotalDays;
}

/// <summary>
/// An open pull request.
/// </summary>
public sealed record OpenPullRequest
{
    /// <summary>
    /// PR number.
    /// </summary>
    [JsonPropertyName("number")]
    public int Number { get; init; }

    /// <summary>
    /// PR title.
    /// </summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>
    /// Repository identifier.
    /// </summary>
    [JsonPropertyName("repository")]
    public required string Repository { get; init; }

    /// <summary>
    /// Head branch name.
    /// </summary>
    [JsonPropertyName("headBranch")]
    public required string HeadBranch { get; init; }

    /// <summary>
    /// Author login.
    /// </summary>
    [JsonPropertyName("author")]
    public required string Author { get; init; }

    /// <summary>
    /// When the PR was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Whether this is a draft PR.
    /// </summary>
    [JsonPropertyName("isDraft")]
    public bool IsDraft { get; init; }

    /// <summary>
    /// Associated issue number (if PR references an issue).
    /// </summary>
    [JsonPropertyName("linkedIssue")]
    public int? LinkedIssue { get; init; }
}
