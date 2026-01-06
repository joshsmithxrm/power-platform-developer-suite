using PPDS.Dataverse.Query;

namespace PPDS.Cli.Interactive.Components.QueryResults;

/// <summary>
/// Tracks the last navigation action for smart menu defaults.
/// </summary>
internal enum NavigationAction
{
    None,
    Previous,
    Next,
    JumpTo,
    ToggleNulls,
    OpenInBrowser,
    CopyUrl
}

/// <summary>
/// Manages navigation state for record-by-record viewing.
/// Accumulates records across pages for seamless navigation.
/// </summary>
internal sealed class RecordNavigationState
{
    private readonly List<IReadOnlyDictionary<string, QueryValue>> _loadedRecords;
    private string? _pagingCookie;
    private int _currentPageNumber;

    /// <summary>
    /// The column metadata for the result set.
    /// </summary>
    public IReadOnlyList<QueryColumn> Columns { get; }

    /// <summary>
    /// The entity logical name.
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// The environment URL for building record links.
    /// </summary>
    public string? EnvironmentUrl { get; }

    /// <summary>
    /// The last navigation action performed (for smart menu defaults).
    /// </summary>
    public NavigationAction LastAction { get; set; }

    /// <summary>
    /// The current record index (0-based).
    /// </summary>
    public int CurrentIndex { get; set; }

    /// <summary>
    /// Whether to show null/empty values.
    /// </summary>
    public bool ShowNullValues { get; set; }

    /// <summary>
    /// Whether more records are available from the server.
    /// </summary>
    public bool MoreRecordsAvailable { get; private set; }

    /// <summary>
    /// Query execution time in milliseconds.
    /// </summary>
    public long ExecutionTimeMs { get; }

    /// <summary>
    /// Total number of loaded records.
    /// </summary>
    public int TotalLoaded => _loadedRecords.Count;

    /// <summary>
    /// Gets the current record.
    /// </summary>
    public IReadOnlyDictionary<string, QueryValue> CurrentRecord => _loadedRecords[CurrentIndex];

    /// <summary>
    /// Gets all loaded records.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, QueryValue>> AllRecords => _loadedRecords;

    /// <summary>
    /// Whether the user can navigate to the next record (either loaded or fetchable).
    /// </summary>
    public bool CanMoveNext => CurrentIndex < TotalLoaded - 1 || MoreRecordsAvailable;

    /// <summary>
    /// Whether the user can navigate to the previous record.
    /// </summary>
    public bool CanMovePrevious => CurrentIndex > 0;

    /// <summary>
    /// Display string for total records (e.g., "50" or "50+").
    /// </summary>
    public string DisplayTotal => MoreRecordsAvailable ? $"{TotalLoaded}+" : TotalLoaded.ToString();

    /// <summary>
    /// Creates navigation state from an initial query result.
    /// </summary>
    /// <param name="initialResult">The initial query result.</param>
    /// <param name="environmentUrl">The environment URL for building record links (optional).</param>
    public RecordNavigationState(QueryResult initialResult, string? environmentUrl = null)
    {
        Columns = initialResult.Columns;
        EntityName = initialResult.EntityLogicalName;
        EnvironmentUrl = environmentUrl;
        ExecutionTimeMs = initialResult.ExecutionTimeMs;
        _loadedRecords = new List<IReadOnlyDictionary<string, QueryValue>>(initialResult.Records);
        _pagingCookie = initialResult.PagingCookie;
        _currentPageNumber = initialResult.PageNumber;
        MoreRecordsAvailable = initialResult.MoreRecords;
    }

    /// <summary>
    /// Adds records from a new page to the state.
    /// </summary>
    public void AddPage(QueryResult pageResult)
    {
        _loadedRecords.AddRange(pageResult.Records);
        _pagingCookie = pageResult.PagingCookie;
        _currentPageNumber = pageResult.PageNumber;
        MoreRecordsAvailable = pageResult.MoreRecords;
    }

    /// <summary>
    /// Gets the parameters needed to fetch the next page.
    /// </summary>
    public (int pageNumber, string? pagingCookie) GetNextPageInfo()
    {
        return (_currentPageNumber + 1, _pagingCookie);
    }

    /// <summary>
    /// Moves to the next record, returning true if successful.
    /// Does not load new pages - caller must handle that.
    /// </summary>
    public bool MoveNext()
    {
        if (CurrentIndex < TotalLoaded - 1)
        {
            CurrentIndex++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Moves to the previous record, returning true if successful.
    /// </summary>
    public bool MovePrevious()
    {
        if (CurrentIndex > 0)
        {
            CurrentIndex--;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Jumps to a specific record index.
    /// </summary>
    public bool JumpTo(int index)
    {
        if (index >= 0 && index < TotalLoaded)
        {
            CurrentIndex = index;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if the user is at the last loaded record and more are available.
    /// </summary>
    public bool NeedsMoreRecords => CurrentIndex >= TotalLoaded - 1 && MoreRecordsAvailable;

    /// <summary>
    /// Gets the URL to open the current record in the Dataverse web interface.
    /// </summary>
    /// <returns>The record URL, or null if insufficient information available.</returns>
    public string? GetCurrentRecordUrl()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl))
        {
            return null;
        }

        // Find primary key field: {entityname}id
        var pkField = $"{EntityName}id";
        if (!CurrentRecord.TryGetValue(pkField, out var pkValue))
        {
            return null;
        }

        // Extract GUID from the value
        Guid? recordId = pkValue.Value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => null
        };

        if (recordId == null)
        {
            return null;
        }

        // Build the Dataverse record URL
        var baseUrl = EnvironmentUrl.TrimEnd('/');
        return $"{baseUrl}/main.aspx?etn={EntityName}&id={recordId:D}&pagetype=entityrecord";
    }

    /// <summary>
    /// Gets whether a record URL can be constructed for the current record.
    /// </summary>
    public bool CanBuildRecordUrl => GetCurrentRecordUrl() != null;
}
