using Microsoft.Extensions.DependencyInjection;
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
    private const string UnknownEntityName = "unknown";

    private readonly InteractiveSession _session;
    private readonly string _environmentUrl;
    private readonly string? _profileName;

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
    private bool _useFetchXmlMode;
    private bool _lastExecutionUsedFetchXml;

    // A confirmation is bound to the exact statement text it was asked for and the environment it was asked in; it is
    // taken (and cleared) at the start of the next execution of any kind, whatever happens after that.
    private string? _awaitingConfirmationSql;
    private (string Sql, string EnvironmentUrl)? _confirmation;

    // Read-only properties
    public string? LastSql => _lastSql;
    public string? LastPagingCookie => _lastPagingCookie;
    public int LastPageNumber => _lastPageNumber;
    public bool IsExecuting => _isExecuting;
    public string? LastErrorMessage => _lastErrorMessage;
    public QueryPlanDescription? LastExecutionPlan => _lastExecutionPlan;
    public long LastExecutionTimeMs => _lastExecutionTimeMs;
    public bool UseTdsEndpoint => _useTdsEndpoint;
    public bool UseFetchXmlMode => _useFetchXmlMode;

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

    public SqlQueryPresenter(InteractiveSession session, string environmentUrl, string? profileName = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _environmentUrl = environmentUrl ?? throw new ArgumentNullException(nameof(environmentUrl));
        _profileName = profileName;
    }

    public void ToggleTds()
    {
        _useTdsEndpoint = !_useTdsEndpoint;
        StatusChanged?.Invoke(_useTdsEndpoint
            ? "Mode: TDS Read Replica (read-only, slight delay)"
            : "Mode: Dataverse (real-time)");
    }

    public void ToggleFetchXml()
    {
        _useFetchXmlMode = !_useFetchXmlMode;
        StatusChanged?.Invoke(_useFetchXmlMode
            ? "Input: FetchXML (paste raw FetchXML)"
            : "Input: SQL");
    }

    /// <summary>The statement the last DmlConfirmationRequired was raised for, until it is confirmed, declined or replaced.</summary>
    public string? AwaitingConfirmationSql => _awaitingConfirmationSql;

    /// <summary>
    /// Confirms the statement the last confirmation request was raised for, in this presenter's environment, and returns
    /// that statement (run it with <see cref="ExecuteAsync"/>); null when nothing is awaiting confirmation. The
    /// confirmation applies only to the next execution, and only when it is that exact statement.
    /// </summary>
    public string? ConfirmDml()
    {
        var sql = _awaitingConfirmationSql;
        _awaitingConfirmationSql = null;
        _confirmation = sql == null ? null : (sql, _environmentUrl);
        return sql;
    }

    /// <summary>Drops a pending confirmation request (the user cancelled the dialog).</summary>
    public void DeclineDml()
    {
        _awaitingConfirmationSql = null;
        _confirmation = null;
    }

    /// <summary>Takes the confirmation for <paramref name="sql"/>: true only for the statement and environment it was given for.</summary>
    private bool TakeConfirmation(string? sql)
    {
        var confirmation = _confirmation;
        _confirmation = null;
        _awaitingConfirmationSql = null;
        return confirmation is { } c && sql != null
            && string.Equals(c.Sql, sql, StringComparison.Ordinal)
            && string.Equals(c.EnvironmentUrl, _environmentUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The text of the confirmation dialog: the service's message, the environment(s) the statement would change, every
    /// statement that changes data (verb and table), the whole statement text (control characters escaped, never
    /// shortened: the dialog scrolls), then the question.
    /// </summary>
    public string ConfirmationPrompt(PpdsException confirmation)
    {
        var targets = (confirmation as PpdsDmlConfirmationRequiredException)?.Targets;
        var lines = new List<string> { confirmation.Message, "" };
        if (targets is { Count: > 0 })
        {
            foreach (var t in targets)
                lines.Add($"Environment: {(t.Label != null ? t.Label + " " : "")}{t.Url ?? _environmentUrl} ({t.Protection} protection)");
        }
        else
        {
            lines.Add($"Environment: {_environmentUrl}");
        }

        var statement = PPDS.Cli.Infrastructure.Output.TerminalText.Sanitize(_awaitingConfirmationSql ?? "");
        var changes = DataChangingStatements(_awaitingConfirmationSql ?? "");
        lines.Add(changes is null
            ? "Statements that change data: could not be listed; read the whole text below."
            : $"Statements that change data ({changes.Count}): {string.Join("; ", changes)}");
        lines.Add("Statement: " + statement);
        lines.Add("");
        lines.Add("Do you want to proceed?");
        return string.Join("\n", lines);
    }

    /// <summary>Each INSERT, UPDATE, DELETE and MERGE in <paramref name="sql"/> as "VERB table" (null when it does not parse).</summary>
    internal static IReadOnlyList<string>? DataChangingStatements(string sql)
    {
        var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(initialQuotedIdentifiers: false);
        // Text too long or nested too deeply would overflow the parser's stack (#1952); the dialog then shows the whole text.
        if (PPDS.Query.Parsing.QueryComplexityGuard.FindRefusal(sql, parser) != null)
            return null;
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0 || fragment is null)
            return null;
        var collector = new DataChangeCollector();
        fragment.Accept(collector);
        return collector.Statements;
    }

    private sealed class DataChangeCollector : Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragmentVisitor
    {
        public List<string> Statements { get; } = new();

        private static string Name(Microsoft.SqlServer.TransactSql.ScriptDom.TableReference? table)
            => table is Microsoft.SqlServer.TransactSql.ScriptDom.NamedTableReference named
                ? string.Join(".", named.SchemaObject.Identifiers.Select(i => i.Value))
                : "(a table expression)";

        public override void Visit(Microsoft.SqlServer.TransactSql.ScriptDom.InsertStatement node)
            => Statements.Add("INSERT " + Name(node.InsertSpecification.Target));

        public override void Visit(Microsoft.SqlServer.TransactSql.ScriptDom.UpdateStatement node)
            => Statements.Add("UPDATE " + Name(node.UpdateSpecification.Target));

        public override void Visit(Microsoft.SqlServer.TransactSql.ScriptDom.DeleteStatement node)
            => Statements.Add("DELETE " + Name(node.DeleteSpecification.Target));

        public override void Visit(Microsoft.SqlServer.TransactSql.ScriptDom.MergeStatement node)
            => Statements.Add("MERGE " + Name(node.MergeSpecification.Target));
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
        // Taken before anything else (and before any await), so no failure, cancellation or retry can leave it set.
        var isConfirmed = TakeConfirmation(sql);

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

            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, _profileName, queryCt);
            TuiDebugLog.Log("Got service, executing streaming query...");

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
            DmlOutcome? dmlOutcome = null;

            await foreach (var chunk in service.ExecuteStreamingAsync(request, StreamingChunkSize, queryCt))
            {
                // Capture column metadata from first chunk
                if (isFirstChunk && chunk.Columns != null)
                {
                    columns = chunk.Columns;
                    StreamingColumnsReady?.Invoke(columns, chunk.EntityLogicalName ?? UnknownEntityName);
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

                if (chunk.IsComplete)
                {
                    dmlOutcome = chunk.DmlOutcome;
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
            _lastExecutionUsedFetchXml = false;
            _isExecuting = false;

            var modeText = executionMode == QueryExecutionMode.Tds ? " via TDS" : " via Dataverse";
            var statusText = FormatCompletionStatus(totalRows, elapsedMs, modeText, dmlOutcome);

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
        catch (PpdsAuthException ppdsAuth) when (ppdsAuth.RequiresReauthentication)
        {
            // The service wraps sign-in failures (before any data changed) in a PpdsAuthException.
            _isExecuting = false;
            AuthenticationRequired?.Invoke(ppdsAuth.InnerException as DataverseAuthenticationException
                ?? new DataverseAuthenticationException(ppdsAuth.UserMessage, requiresReauthentication: true));
        }
        catch (OperationCanceledException cancelled) when (_queryCts?.IsCancellationRequested == true && !screenCancellation.IsCancellationRequested)
        {
            _isExecuting = false;
            // Cancelling after DML ran must say what already changed (not rolled back) and what may be partly applied.
            var committed = DmlOutcome.From(cancelled)?.DescribeCommitted();
            if (committed == null)
            {
                QueryCancelled?.Invoke();
            }
            else
            {
                _lastErrorMessage = $"Query cancelled. {committed}";
                ErrorOccurred?.Invoke(_lastErrorMessage);
            }
        }
        catch (OperationCanceledException) when (screenCancellation.IsCancellationRequested)
        {
            _isExecuting = false;
            // Screen is closing — silently swallow
        }
        catch (PpdsException dmlEx) when (dmlEx.ErrorCode == ErrorCodes.Query.DmlConfirmationRequired)
        {
            _isExecuting = false;
            _awaitingConfirmationSql = sql;
            DmlConfirmationRequired?.Invoke(dmlEx);
        }
        catch (Exception ex)
        {
            // A script interrupted after some DML ran must say what already ran (not rolled back).
            var committed = DmlOutcome.From(ex)?.DescribeCommitted();
            var refusal = ex is PpdsDmlRowCapExceededException ? ex.Message + RowCapRemedy : ex.Message;
            var message = committed == null ? refusal : $"{refusal} {committed}";
            _lastErrorMessage = message;
            _isExecuting = false;
            ErrorOccurred?.Invoke(message);
        }
    }

    /// <summary>What the TUI adds to a row-cap refusal: the cap cannot be removed here, so it says where it can.</summary>
    internal const string RowCapRemedy =
        " The row cap cannot be removed in the TUI; to change every matching row, run the statement with 'ppds query sql --no-limit'.";

    /// <summary>
    /// Load-more re-reads the last statement; it must never run DML. It always asks for page 2 or later, and the
    /// service refuses DML on a request for a further page (Query.DmlBlocked), confirmed or not.
    /// </summary>
    internal static SqlQueryRequest BuildLoadMoreRequest(string sql, int pageNumber, string? pagingCookie) => new()
    {
        Sql = sql,
        PageNumber = pageNumber,
        PagingCookie = pagingCookie,
        EnablePrefetch = true,
        DmlSafety = new DmlSafetyOptions()
    };

    /// <summary>
    /// Status line for a finished streaming query. Failed DML rows are reported instead of a plain
    /// "Returned N rows" (a DML result is one summary row, which read as success). When a script stopped at a statement
    /// with failed rows, the sentence naming the statements that were not run is kept whole after the shortened message.
    /// </summary>
    internal static string FormatCompletionStatus(int totalRows, long elapsedMs, string modeText, DmlOutcome? dmlOutcome)
        => dmlOutcome is { HasFailures: true }
            ? dmlOutcome.NotRunNote is { } notRun
                ? $"{OneLine(dmlOutcome.MessageWithoutNotRunNote!, 160)} {OneLine(notRun, int.MaxValue)} ({elapsedMs}ms{modeText})"
                : $"{OneLine(dmlOutcome.Message!, 160)} ({elapsedMs}ms{modeText})"
            : $"Returned {totalRows:N0} rows in {elapsedMs}ms{modeText}";

    // The status line is a single line; server error text can contain newlines and be long.
    private static string OneLine(string text, int maxLength)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= maxLength ? collapsed : collapsed[..(maxLength - 3)] + "...";
    }

    public async Task ExecuteFetchXmlAsync(string fetchXml, CancellationToken screenCancellation)
    {
        TakeConfirmation(null); // any execution ends a pending confirmation
        if (string.IsNullOrWhiteSpace(fetchXml))
        {
            ErrorOccurred?.Invoke("FetchXML cannot be empty.");
            return;
        }

        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = CancellationTokenSource.CreateLinkedTokenSource(screenCancellation);
        var queryCt = _queryCts.Token;

        _isExecuting = true;
        _lastErrorMessage = null;
        StatusChanged?.Invoke("Executing FetchXML...");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var provider = await _session.GetServiceProviderAsync(_environmentUrl, _profileName, queryCt);
            var executor = provider.GetRequiredService<IQueryExecutor>();

            var result = await executor.ExecuteFetchXmlAsync(fetchXml, cancellationToken: queryCt);

            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;

            _lastSql = fetchXml;
            _lastPageNumber = result.PageNumber;
            _lastPagingCookie = result.PagingCookie;
            _lastExecutionTimeMs = elapsedMs;
            _lastExecutionPlan = null;
            _lastExecutionUsedFetchXml = true;
            _isExecuting = false;

            if (result.Columns.Count > 0)
                StreamingColumnsReady?.Invoke(result.Columns, result.EntityLogicalName ?? UnknownEntityName);

            if (result.Records.Count > 0 && result.Columns.Count > 0)
                StreamingRowsReady?.Invoke(result.Records, result.Columns, true, result.Count);

            var statusText = $"Returned {result.Count:N0} rows in {elapsedMs}ms via FetchXML";
            ExecutionComplete?.Invoke(statusText, elapsedMs, null);

            _session.GetErrorService().FireAndForget(
                SaveToHistoryAsync(fetchXml, result.Count, elapsedMs),
                "SaveHistory");
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
        catch (OperationCanceledException) when (screenCancellation.IsCancellationRequested)
        {
            _isExecuting = false;
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
        TakeConfirmation(null); // any execution ends a pending confirmation
        if (_lastSql == null) return;

        try
        {
            if (_lastExecutionUsedFetchXml)
            {
                var provider = await _session.GetServiceProviderAsync(_environmentUrl, _profileName, cancellationToken);
                var executor = provider.GetRequiredService<IQueryExecutor>();

                var result = await executor.ExecuteFetchXmlAsync(
                    _lastSql,
                    _lastPageNumber + 1,
                    _lastPagingCookie,
                    cancellationToken: cancellationToken);

                _lastPagingCookie = result.PagingCookie;
                _lastPageNumber = result.PageNumber;

                PageLoaded?.Invoke(result);
            }
            else
            {
                var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, _profileName, cancellationToken);

                var request = BuildLoadMoreRequest(_lastSql, _lastPageNumber + 1, _lastPagingCookie);

                var result = await service.ExecuteAsync(request, cancellationToken);

                _lastPagingCookie = result.Result.PagingCookie;
                _lastPageNumber = result.Result.PageNumber;

                PageLoaded?.Invoke(result.Result);
            }
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
            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, _profileName, CancellationToken.None);
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
