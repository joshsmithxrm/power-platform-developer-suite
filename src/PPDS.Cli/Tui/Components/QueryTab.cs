using System.Text.RegularExpressions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Cli.Tui.Components;

/// <summary>
/// Holds per-tab state for the multi-tab query editor.
/// Each tab has independent query text, results, execution plan, and execution status.
/// </summary>
internal sealed class QueryTab
{
    private static int _nextTabNumber = 1;
    private static readonly Regex FromEntityRegex = new(
        @"\bFROM\s+(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string _queryText;
    private string _savedQueryText;

    /// <summary>
    /// Unique tab identifier for tracking.
    /// </summary>
    public int TabId { get; }

    /// <summary>
    /// The current query text in the editor.
    /// </summary>
    public string QueryText
    {
        get => _queryText;
        set
        {
            _queryText = value;
            UpdateTitle();
        }
    }

    /// <summary>
    /// The query text at the time of last save/execution.
    /// Used to detect unsaved changes.
    /// </summary>
    public string SavedQueryText
    {
        get => _savedQueryText;
        set => _savedQueryText = value;
    }

    /// <summary>
    /// The query results from the last execution (null if not yet executed).
    /// </summary>
    public QueryResult? Results { get; set; }

    /// <summary>
    /// The execution plan from the last execution (null if not available).
    /// </summary>
    public QueryPlanDescription? ExecutionPlan { get; set; }

    /// <summary>
    /// Auto-generated tab title based on the FROM entity, or "Query N".
    /// </summary>
    public string Title { get; private set; }

    /// <summary>
    /// Whether a query is currently executing in this tab.
    /// </summary>
    public bool IsExecuting { get; set; }

    /// <summary>
    /// Whether this tab has unsaved changes (query text differs from last save point).
    /// </summary>
    public bool HasUnsavedChanges => _queryText != _savedQueryText;

    /// <summary>
    /// Status text for this tab's last operation.
    /// </summary>
    public string StatusText { get; set; } = "Ready";

    /// <summary>
    /// Error message from the last execution, if any.
    /// </summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// Last executed SQL for pagination.
    /// </summary>
    public string? LastExecutedSql { get; set; }

    /// <summary>
    /// Paging cookie for next-page fetches.
    /// </summary>
    public string? LastPagingCookie { get; set; }

    /// <summary>
    /// Current page number for pagination.
    /// </summary>
    public int LastPageNumber { get; set; } = 1;

    public QueryTab(string initialQueryText = "")
    {
        TabId = _nextTabNumber++;
        _queryText = initialQueryText;
        _savedQueryText = initialQueryText;
        Title = GenerateTitle(initialQueryText);
    }

    /// <summary>
    /// Marks the current query text as saved (e.g., after execution).
    /// </summary>
    public void MarkAsSaved()
    {
        _savedQueryText = _queryText;
    }

    /// <summary>
    /// Gets the display title including unsaved changes indicator.
    /// </summary>
    public string GetDisplayTitle()
    {
        return HasUnsavedChanges ? $"{Title}*" : Title;
    }

    /// <summary>
    /// Generates a title from the SQL query text.
    /// Extracts the entity name from the FROM clause, or falls back to "Query N".
    /// </summary>
    internal static string GenerateTitle(string sql)
    {
        if (!string.IsNullOrWhiteSpace(sql))
        {
            var match = FromEntityRegex.Match(sql);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return $"Query {_nextTabNumber}";
    }

    private void UpdateTitle()
    {
        var newTitle = GenerateTitle(_queryText);
        if (!string.IsNullOrEmpty(newTitle))
        {
            Title = newTitle;
        }
    }

    /// <summary>
    /// Resets the tab number counter (for testing).
    /// </summary>
    internal static void ResetTabCounter()
    {
        _nextTabNumber = 1;
    }
}
