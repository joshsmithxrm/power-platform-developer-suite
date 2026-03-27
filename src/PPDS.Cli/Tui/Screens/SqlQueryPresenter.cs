using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Resilience;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// Presenter for SQL query execution. Contains query orchestration logic, state management,
/// and history saving -- all Terminal.Gui-free. The screen subscribes to events and marshals
/// UI updates to the main thread.
/// </summary>
internal sealed class SqlQueryPresenter : IDisposable
{
    private const int StreamingChunkSize = 100;

    private readonly InteractiveSession _session;
    private readonly string _environmentUrl;

    // State fields moved from SqlQueryScreen
    private string? _lastSql;
    private string? _lastPagingCookie;
    private int _lastPageNumber = 1;
    private bool _isExecuting;
    private CancellationTokenSource? _queryCts;
    private string? _lastErrorMessage;
    private QueryPlanDescription? _lastExecutionPlan;
    private long _lastExecutionTimeMs;
    private bool _useTdsEndpoint;
    private bool _confirmedDml;

    // Read-only properties
    public string? LastSql => _lastSql;
    public string? LastPagingCookie => _lastPagingCookie;
    public int LastPageNumber => _lastPageNumber;
    public bool IsExecuting => _isExecuting;
    public string? LastErrorMessage => _lastErrorMessage;
    public QueryPlanDescription? LastExecutionPlan => _lastExecutionPlan;
    public long LastExecutionTimeMs => _lastExecutionTimeMs;
    public bool UseTdsEndpoint => _useTdsEndpoint;

    // Events -- screen subscribes and wraps with Application.MainLoop.Invoke()

    /// <summary>
    /// Raised when column metadata is ready from the first streaming chunk.
    /// Args: columns, entityLogicalName.
    /// </summary>
    public event Action<IReadOnlyList<QueryColumn>, string>? StreamingColumnsReady;

    /// <summary>
    /// Raised when a batch of rows is ready for display.
    /// Args: rows, columns, isComplete, totalRowsSoFar.
    /// </summary>
    public event Action<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>, IReadOnlyList<QueryColumn>, bool, int>? StreamingRowsReady;

    /// <summary>
    /// Raised when status text changes (e.g., progress updates).
    /// Args: statusMessage.
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// Raised when query execution completes successfully.
    /// Args: statusText, elapsedMs, executionMode.
    /// </summary>
    public event Action<string, long, QueryExecutionMode?>? ExecutionComplete;

    /// <summary>
    /// Raised when authentication fails and re-auth is required.
    /// </summary>
    public event Action<DataverseAuthenticationException>? AuthenticationRequired;

    /// <summary>
    /// Raised when a DML operation requires user confirmation.
    /// </summary>
    public event Action<PpdsException>? DmlConfirmationRequired;

    /// <summary>
    /// Raised when the query is cancelled by the user (not by screen close).
    /// </summary>
    public event Action? QueryCancelled;

    /// <summary>
    /// Raised when an error occurs during execution.
    /// Args: errorMessage.
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Raised when a subsequent page of results is loaded.
    /// </summary>
    public event Action<QueryResult>? PageLoaded;

    public SqlQueryPresenter(InteractiveSession session, string environmentUrl)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _environmentUrl = environmentUrl ?? throw new ArgumentNullException(nameof(environmentUrl));
    }

    public void ToggleTds()
    {
        _useTdsEndpoint = !_useTdsEndpoint;
        StatusChanged?.Invoke(_useTdsEndpoint
            ? "Mode: TDS Read Replica (read-only, slight delay)"
            : "Mode: Dataverse (real-time)");
    }

    public void ConfirmDml()
    {
        _confirmedDml = true;
    }

    public void CancelQuery()
    {
        _queryCts?.Cancel();
    }

    /// <summary>
    /// Executes a SQL query with streaming results.
    /// Flattened single try-catch (AC-24) -- no nested try blocks.
    /// The UI timer is NOT managed here since the presenter is Terminal.Gui-free.
    /// </summary>
    public async Task ExecuteAsync(string sql, CancellationToken screenCancellation)
    {
        // Validation
        if (string.IsNullOrWhiteSpace(sql))
        {
            ErrorOccurred?.Invoke("Query cannot be empty.");
            return;
        }

        // Create/reset query-level cancellation
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = CancellationTokenSource.CreateLinkedTokenSource(screenCancellation);
        var queryCt = _queryCts.Token;

        _isExecuting = true;
        _lastErrorMessage = null;

        StatusChanged?.Invoke("Executing query...");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try // Single try-catch -- AC-24 flatten
        {
            TuiDebugLog.Log($"Getting SQL query service for URL: {_environmentUrl}");

            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, queryCt);
            TuiDebugLog.Log("Got service, executing streaming query...");

            // AC-13: capture and reset DML confirmation flag
            var isConfirmed = _confirmedDml;
            _confirmedDml = false;

            var request = new SqlQueryRequest
            {
                Sql = sql,
                PageNumber = null,
                PagingCookie = null,
                EnablePrefetch = true,
                UseTdsEndpoint = _useTdsEndpoint,
                DmlSafety = new DmlSafetyOptions
                {
                    IsConfirmed = isConfirmed,
                    IsDryRun = false
                }
            };

            IReadOnlyList<QueryColumn>? columns = null;
            var totalRows = 0;
            var isFirstChunk = true;
            QueryExecutionMode? executionMode = null;

            await foreach (var chunk in service.ExecuteStreamingAsync(request, StreamingChunkSize, queryCt))
            {
                // Capture column metadata from first chunk
                if (isFirstChunk && chunk.Columns != null)
                {
                    columns = chunk.Columns;
                    StreamingColumnsReady?.Invoke(columns, chunk.EntityLogicalName ?? "unknown");
                }

                totalRows = chunk.TotalRowsSoFar;

                if (chunk.Rows.Count > 0 && columns != null)
                {
                    StreamingRowsReady?.Invoke(chunk.Rows, columns, chunk.IsComplete, totalRows);
                }

                if (!chunk.IsComplete)
                {
                    StatusChanged?.Invoke($"Loading... {totalRows:N0} rows ({stopwatch.Elapsed.TotalSeconds:F1}s)");
                }

                if (chunk.IsComplete && chunk.ExecutionMode.HasValue)
                {
                    executionMode = chunk.ExecutionMode;
                }

                isFirstChunk = false;
            }

            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;

            TuiDebugLog.Log($"Streaming query complete: {totalRows} rows in {elapsedMs}ms");

            _lastSql = sql;
            _lastPageNumber = 1;
            _lastPagingCookie = null;
            _lastExecutionTimeMs = elapsedMs;
            _isExecuting = false;

            var modeText = executionMode == QueryExecutionMode.Tds ? " via TDS" : " via Dataverse";
            var statusText = $"Returned {totalRows:N0} rows in {elapsedMs}ms{modeText}";

            ExecutionComplete?.Invoke(statusText, elapsedMs, executionMode);

            // Save to history (fire-and-forget within presenter)
            _session.GetErrorService().FireAndForget(
                SaveToHistoryAsync(sql, totalRows, elapsedMs),
                "SaveHistory");

            // Cache execution plan (fire-and-forget within presenter)
            _session.GetErrorService().FireAndForget(
                CacheExecutionPlanAsync(sql),
                "CacheExecutionPlan");
        }
        catch (DataverseAuthenticationException authEx) when (authEx.RequiresReauthentication)
        {
            _isExecuting = false;
            AuthenticationRequired?.Invoke(authEx);
        }
        catch (OperationCanceledException) when (_queryCts?.IsCancellationRequested == true && !screenCancellation.IsCancellationRequested)
        {
            _isExecuting = false;
            QueryCancelled?.Invoke();
        }
        catch (PpdsException dmlEx) when (dmlEx.ErrorCode == ErrorCodes.Query.DmlConfirmationRequired)
        {
            _isExecuting = false;
            DmlConfirmationRequired?.Invoke(dmlEx);
        }
        catch (Exception ex)
        {
            _lastErrorMessage = ex.Message;
            _isExecuting = false;
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Loads the next page of results for the current query.
    /// </summary>
    public async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        if (_lastSql == null) return;

        try
        {
            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, cancellationToken);

            var request = new SqlQueryRequest
            {
                Sql = _lastSql,
                PageNumber = _lastPageNumber + 1,
                PagingCookie = _lastPagingCookie,
                EnablePrefetch = true
            };

            var result = await service.ExecuteAsync(request, cancellationToken);

            _lastPagingCookie = result.Result.PagingCookie;
            _lastPageNumber = result.Result.PageNumber;

            PageLoaded?.Invoke(result.Result);
        }
        catch (DataverseAuthenticationException authEx) when (authEx.RequiresReauthentication)
        {
            TuiDebugLog.Log($"Authentication error during load more: {authEx.Message}");
            AuthenticationRequired?.Invoke(authEx);
        }
        catch (Exception ex)
        {
            TuiDebugLog.Log($"Load more failed: {ex.Message}");
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Saves a successful query to history.
    /// </summary>
    private async Task SaveToHistoryAsync(string sql, int rowCount, long executionTimeMs)
    {
        try
        {
            var historyService = await _session.GetQueryHistoryServiceAsync(_environmentUrl, CancellationToken.None);
            await historyService.AddQueryAsync(_environmentUrl, sql, rowCount, executionTimeMs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TUI] Failed to save query to history: {ex.Message}");
            TuiDebugLog.Log($"History save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Caches the execution plan for the last executed query.
    /// </summary>
    private async Task CacheExecutionPlanAsync(string sql)
    {
        try
        {
            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, CancellationToken.None);
            var plan = await service.ExplainAsync(sql, CancellationToken.None);
            _lastExecutionPlan = plan;
        }
        catch (Exception ex)
        {
            TuiDebugLog.Log($"Execution plan cache failed: {ex.Message}");
        }
    }

    private bool _disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queryCts?.Cancel();
        _queryCts?.Dispose();
    }
}
