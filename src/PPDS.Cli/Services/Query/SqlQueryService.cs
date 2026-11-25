using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Implementation of <see cref="ISqlQueryService"/> that parses SQL,
/// transpiles to FetchXML, and executes against Dataverse.
/// </summary>
public sealed class SqlQueryService : ISqlQueryService
{
    private readonly IQueryExecutor _queryExecutor;
    private readonly ITdsQueryExecutor? _tdsQueryExecutor;
    private readonly IBulkOperationExecutor? _bulkOperationExecutor;
    private readonly IMetadataQueryExecutor? _metadataQueryExecutor;
    private readonly ICachedMetadataProvider? _metadataProvider;
    private readonly IShakedownGuard _guard;
    private readonly int _poolCapacity;
    private readonly ExecutionPlanBuilder _planBuilder;
    private readonly PlanExecutor _planExecutor;
    private readonly DmlSafetyGuard _dmlSafetyGuard = new();
    private readonly QueryParser _queryParser = new();
    private readonly PPDS.Query.Transpilation.FetchXmlGeneratorService _fetchXmlGeneratorService = new();

    /// <summary>
    /// Optional factory that resolves a profile label to a remote <see cref="IQueryExecutor"/>.
    /// Set by the TUI's <c>InteractiveSession</c> to enable cross-environment queries
    /// like <c>SELECT * FROM [QA].account</c>.
    /// </summary>
    public Func<string, IQueryExecutor?>? RemoteExecutorFactory { get; set; }

    /// <summary>
    /// Safety settings for the current environment. When null, uses defaults.
    /// </summary>
    public QuerySafetySettings? EnvironmentSafetySettings { get; set; }

    /// <summary>
    /// Protection level for the current environment. Defaults to Production (safest).
    /// </summary>
    public ProtectionLevel EnvironmentProtectionLevel { get; set; } = ProtectionLevel.Production;

    /// <summary>
    /// Optional profile resolution service for cross-environment DML checks.
    /// </summary>
    public ProfileResolutionService? ProfileResolver { get; set; }

    private string? _environmentUrl;
    private string? _environmentLabel;
    private string? _environmentType;
    private bool _environmentTypeUnknown;

    /// <summary>
    /// Refuses a confirmed DML request that does not name the environment it was confirmed for, or names another one than
    /// the environment this service runs against.
    /// </summary>
    private void CheckConfirmedTarget(string? confirmedUrl)
    {
        static string? Normal(string? url) => string.IsNullOrWhiteSpace(url)
            ? null
            : Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : url.Trim().TrimEnd('/').ToLowerInvariant();

        var confirmed = Normal(confirmedUrl);
        var actual = Normal(_environmentUrl);
        if (confirmed == null)
        {
            throw new PpdsException(ErrorCodes.Query.DmlBlocked,
                "A confirmed INSERT, UPDATE or DELETE request must name the environment it was confirmed for (environmentUrl). " +
                "Nothing was executed. Send the request again with the environment URL that was shown when it was confirmed.");
        }
        if (actual == null || confirmed != actual)
        {
            throw new PpdsException(ErrorCodes.Query.DmlBlocked,
                $"This request was confirmed for {confirmedUrl}, but it would run against {_environmentUrl ?? "an unknown environment"}. " +
                "Nothing was executed. Confirm it again for the environment it runs against.");
        }
    }

    /// <summary>
    /// Refuses a confirmed DML request that does not name the profile it was confirmed for (RPC <c>profileIdentity</c>),
    /// or names another one than the profile it runs as: without it the request would run as whichever profile is
    /// active when it arrives, which may see (and change) other rows than the confirming user was shown.
    /// </summary>
    private static void CheckConfirmedProfile(string? confirmedIdentity, string? executingIdentity)
    {
        if (string.IsNullOrWhiteSpace(confirmedIdentity))
        {
            throw new PpdsException(ErrorCodes.Query.DmlBlocked,
                "A confirmed INSERT, UPDATE or DELETE request must name the profile it was confirmed for (profileIdentity). " +
                "Nothing was executed. Send the request again with the profile identity that was shown when it was confirmed.");
        }
        if (executingIdentity != null && !string.Equals(confirmedIdentity.Trim(), executingIdentity, StringComparison.OrdinalIgnoreCase))
        {
            throw new PpdsException(ErrorCodes.Query.DmlBlocked,
                "This request was confirmed for another profile than the one it would run as. Nothing was executed. " +
                "Confirm it again for the profile it runs as.");
        }
    }

    /// <summary>
    /// Applies the connected environment's DML safety settings and protection level (from its configuration, when
    /// there is one; otherwise the defaults stay: Production protection) and records its identity, which is reported
    /// as the write target when confirmation is required. Every surface calls this, so they configure the guard alike.
    /// </summary>
    public void ConfigureEnvironment(string environmentUrl, EnvironmentConfig? config)
    {
        _environmentUrl = environmentUrl;
        _environmentLabel = config?.Label;
        _environmentType = config?.Type?.ToString();
        // A configuration that states neither a protection level nor a known type is Production (maintainer decision,
        // 2026-11-24: no saved type counts as Production), and is reported so in the confirmation's target.
        _environmentTypeUnknown = config != null && config.Protection == null && config.Type is null or EnvironmentType.Unknown;
        if (config == null)
            return;

        EnvironmentSafetySettings = config.SafetySettings;
        EnvironmentProtectionLevel = config.Protection
            ?? (_environmentTypeUnknown ? ProtectionLevel.Production : DmlSafetyGuard.DetectProtectionLevel(config.Type!.Value));
    }

    /// <summary>
    /// Creates a new instance of <see cref="SqlQueryService"/>.
    /// </summary>
    /// <param name="queryExecutor">The query executor for FetchXML execution.</param>
    /// <param name="tdsQueryExecutor">Optional TDS Endpoint executor for direct SQL execution.</param>
    /// <param name="bulkOperationExecutor">Optional bulk operation executor for DML statements.</param>
    /// <param name="metadataQueryExecutor">Optional metadata query executor for metadata virtual tables.</param>
    /// <param name="poolCapacity">Connection pool parallelism capacity for aggregate partitioning.</param>
    /// <param name="metadataProvider">Optional cached metadata provider. Required for DML type coercion
    /// (lookup → <c>EntityReference</c>, choice → <c>OptionSetValue</c>, etc.); when null, DML passes
    /// raw CLR values through to Dataverse, which rejects them for non-scalar columns.</param>
    /// <param name="guard">Shakedown-session safety guard (refuses DML execution while active).
    /// Required — production DI supplies the real guard; tests use a fake.</param>
    public SqlQueryService(
        IQueryExecutor queryExecutor,
        IShakedownGuard guard,
        ITdsQueryExecutor? tdsQueryExecutor = null,
        IBulkOperationExecutor? bulkOperationExecutor = null,
        IMetadataQueryExecutor? metadataQueryExecutor = null,
        int poolCapacity = 1,
        ICachedMetadataProvider? metadataProvider = null)
    {
        _queryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _tdsQueryExecutor = tdsQueryExecutor;
        _bulkOperationExecutor = bulkOperationExecutor;
        _metadataQueryExecutor = metadataQueryExecutor;
        _metadataProvider = metadataProvider;
        _poolCapacity = poolCapacity;
        _planBuilder = new ExecutionPlanBuilder(_fetchXmlGeneratorService);
        _planExecutor = new PlanExecutor();
    }

    /// <summary>The sentence a confirmation adds for active bypass hints (empty when there are none).</summary>
    internal static string BypassNote(IReadOnlyList<string> bypasses) => bypasses.Count == 0
        ? string.Empty
        : $" It also bypasses {string.Join(" and ", bypasses.Select(b => b == "BYPASS_PLUGINS" ? "synchronous custom plugins and workflows" : "Power Automate flows"))} " +
          $"(-- ppds:{string.Join(", -- ppds:", bypasses)}).";

    /// <summary>The surface-neutral message of <c>Query.DmlConfirmationRequired</c>.</summary>
    public const string ConfirmationRequiredMessage =
        "This request changes data (INSERT, UPDATE or DELETE) and must be confirmed before it runs. Nothing was executed.";

    /// <inheritdoc />
    public string TranspileSql(string sql, int? topOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        try
        {
            var fragment = _queryParser.Parse(sql);

            if (topOverride.HasValue)
            {
                // Only inject TOP override if the SQL doesn't already have one —
                // don't clobber the user's explicit TOP with the extension's default
                var querySpec = ExtractQuerySpecification(fragment);
                if (querySpec?.TopRowFilter == null)
                {
                    InjectTopOverride(fragment, topOverride.Value);
                }
            }

            // Extract the first statement from the TSqlScript wrapper.
            // QueryParser.Parse returns a TSqlScript, but FetchXmlGenerator
            // expects a SelectStatement or QuerySpecification.
            var statement = ExtractFirstStatement(fragment);
            var generator = new PPDS.Query.Transpilation.FetchXmlGenerator();
            return generator.Generate(statement);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }
    }

    /// <summary>
    /// Extracts the first <see cref="TSqlStatement"/> from a parsed fragment.
    /// </summary>
    private static TSqlStatement ExtractFirstStatement(TSqlFragment fragment)
    {
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                if (batch.Statements.Count > 0)
                    return batch.Statements[0];
            }
        }

        if (fragment is TSqlStatement statement)
            return statement;

        throw new PpdsException(ErrorCodes.Query.ParseError, "SQL text does not contain any statements.");
    }

    /// <summary>
    /// Extracts the statement shape that the planner will execute. Multiple statements in
    /// the first batch are represented as a block so safety checks inspect the whole plan.
    /// </summary>
    private static TSqlStatement ExtractPlannedStatement(TSqlFragment fragment)
    {
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                if (batch.Statements.Count == 0)
                    continue;

                if (batch.Statements.Count == 1)
                    return batch.Statements[0];

                var block = new BeginEndBlockStatement
                {
                    StatementList = new StatementList()
                };
                foreach (var batchStatement in batch.Statements)
                    block.StatementList.Statements.Add(batchStatement);

                return block;
            }
        }

        if (fragment is TSqlStatement directStatement)
            return directStatement;

        throw new PpdsException(ErrorCodes.Query.ParseError, "SQL text does not contain any statements.");
    }

    /// <inheritdoc />
    public async Task<SqlQueryResult> ExecuteAsync(
        SqlQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var (fragment, planResult, safetyResult, executionOptions, appliedHints) =
            await PrepareExecutionAsync(request, cancellationToken).ConfigureAwait(false);

        var dataSources = CollectDataSources(planResult.RootNode, "Local");

        // Dry-run: return the plan without executing. The planner is side-effect-free,
        // so running it gives the user the FetchXML and execution plan for review.
        if (safetyResult is { ContainsDml: true, IsDryRun: true })
        {
            var preview = BuildDryRunPreview(planResult);
            return new SqlQueryResult
            {
                OriginalSql = request.Sql,
                TranspiledFetchXml = preview.FetchXml,
                Result = QueryResult.Empty(planResult.EntityLogicalName),
                DmlSafetyResult = safetyResult,
                DryRunPlan = preview.Plan,
                DataSources = CollectDataSources(preview.PlannedNodes, "Local"),
                AppliedHints = appliedHints
            };
        }

        // Shakedown guard fires only when DML will actually execute (not dry-run, not SELECT).
        if (safetyResult is { ContainsDml: true, IsDryRun: false })
        {
            _guard.EnsureCanMutate("query.dml");
        }

        // Execute the plan
        var context = new QueryPlanContext(
            _queryExecutor,
            cancellationToken,
            bulkOperationExecutor: _bulkOperationExecutor,
            metadataQueryExecutor: _metadataQueryExecutor,
            executionOptions: executionOptions,
            metadataProvider: _metadataProvider);

        QueryResult result;
        try
        {
            result = await _planExecutor.ExecuteAsync(planResult, context, cancellationToken);
        }
        catch (QueryExecutionException ex) when (ex.ErrorCode == QueryErrorCode.DmlRowCapExceeded)
        {
            throw WrapRowCapExceeded(ex, context);
        }
        catch (DmlSafetyRefusalException ex)
        {
            throw WrapRefusal(ex, context);
        }
        catch (Exception ex) when (DmlRan(context) && ex is not OperationCanceledException && (ex is not PpdsException || ex is PpdsAuthException))
        {
            throw WrapInterrupted(ex, context);
        }
        catch (Exception ex) when (RecordCommittedOutcome(ex, context))
        {
            throw; // never reached: the filter only attaches what already ran and returns false
        }
        catch (Exception ex) when (
            request.UseTdsEndpoint
            && ContainsTdsScanNode(planResult.RootNode)
            && ex is not OperationCanceledException
            && ex is not PpdsException)
        {
            throw new PpdsException(
                ErrorCodes.Query.TdsConnectionFailed,
                $"TDS Endpoint connection failed: {ex.Message}. The TDS Endpoint may be disabled " +
                "on this environment. Disable TDS mode to query via Dataverse, or ask your Power " +
                "Platform admin to enable the TDS Endpoint.",
                ex);
        }
        catch (Exception ex) when (ex is not PpdsException and not OperationCanceledException)
        {
            throw WrapFailure(ex, context);
        }

        // Expand lookup, optionset, and boolean columns to include *name variants.
        // Virtual column expansion stays in the service layer because it depends on
        // SDK-specific FormattedValues metadata from the Entity objects.
        // Aggregate results are excluded — their FormattedValues are locale-formatted
        // numbers, not meaningful attribute labels.
        var isAggregate = HasAggregatesInFragment(fragment);
        var expandedResult = SqlQueryResultExpander.ExpandFormattedValueColumns(
            result,
            planResult.VirtualColumns,
            isAggregate);

        return new SqlQueryResult
        {
            OriginalSql = request.Sql,
            TranspiledFetchXml = planResult.FetchXml,
            Result = expandedResult,
            DataSources = dataSources,
            AppliedHints = appliedHints,
            ExecutionMode = DetectExecutionMode(planResult.RootNode),
            DmlOutcome = DmlOutcome.From(context.Statistics.DmlOutcomes)
        };
    }

    /// <summary>
    /// The row-cap refusal as a <see cref="PpdsException"/> with its own error code, carrying the outcomes of
    /// any earlier script statements that already ran.
    /// </summary>
    private static PpdsException WrapRowCapExceeded(QueryExecutionException ex, QueryPlanContext context)
    {
        var wrapped = ex is DmlRowCapExceededException cap
            ? new PpdsDmlRowCapExceededException(cap.Operation, cap.EntityLogicalName, cap.RowCap, ex.Message, ex)
            : new PpdsException(ErrorCodes.Query.DmlRowCapExceeded, ex.Message, ex);
        RecordCommittedOutcome(wrapped, context);
        return wrapped;
    }

    private const string NothingExecutedSentence = " Nothing was executed.";

    private static bool DmlRan(QueryPlanContext context)
        => context.Statistics.DmlOutcomes.Any(o => o.AffectedRows > 0 || o.FailedRows > 0 || o.Interrupted);

    /// <summary>
    /// A failure before any data changed, as a <see cref="PpdsException"/> (Constitution D4). A sign-in failure keeps
    /// its Auth code and re-authentication flag, so callers can still offer to sign in again.
    /// </summary>
    private static PpdsException WrapFailure(Exception ex, QueryPlanContext context)
    {
        var message = PPDS.Dataverse.Security.ConnectionStringRedactor.RedactExceptionMessage(ex.Message);
        PpdsException wrapped = ex switch
        {
            PPDS.Dataverse.Resilience.DataverseAuthenticationException auth => new PpdsAuthException(
                auth.RequiresReauthentication ? ErrorCodes.Auth.Expired : ErrorCodes.Auth.InsufficientPermissions, message, ex)
            {
                RequiresReauthentication = auth.RequiresReauthentication
            },
            QueryExecutionException query => new PpdsException(query.ErrorCode, message, ex),
            _ => new PpdsException(ErrorCodes.Query.ExecutionFailed, message, ex)
        };
        RecordCommittedOutcome(wrapped, context);
        return wrapped;
    }

    /// <summary>A planner refusal raised while a script ran, as a <see cref="PpdsException"/> with its own code.</summary>
    internal static PpdsException WrapRefusal(DmlSafetyRefusalException ex, QueryPlanContext context)
    {
        // After earlier statements changed data, "Nothing was executed" would be false: only this statement and the
        // ones after it did not run (what did run is attached and reported by every surface).
        var message = DmlRan(context)
            ? ex.Message.Replace(NothingExecutedSentence, " This statement and the ones after it did not run.", StringComparison.Ordinal)
                .Replace(", so nothing was executed.", ", so this statement and the ones after it did not run.", StringComparison.Ordinal)
            : ex.Message;
        var wrapped = new PpdsException(ex.ErrorCode, message, ex);
        RecordCommittedOutcome(wrapped, context);
        return wrapped;
    }

    /// <summary>
    /// A failure after DML already changed rows (an earlier statement ran, or the failing statement was part-way
    /// through). Wrapped in a <see cref="PpdsException"/> (keeping the original code for query errors) that carries
    /// what already ran, so every caller can report it. Cancellation stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    private static PpdsException WrapInterrupted(Exception ex, QueryPlanContext context)
    {
        var message = PPDS.Dataverse.Security.ConnectionStringRedactor.RedactExceptionMessage(ex.Message);
        PpdsException wrapped = ex switch
        {
            // Never an Auth.* code after data changed: clients re-authenticate and replay on those, which would run the
            // statements that already ran again.
            PPDS.Dataverse.Resilience.DataverseAuthenticationException or PpdsAuthException => new PpdsDmlInterruptedException(
                $"The run was interrupted by a sign-in failure after it had already changed data: {message} Sign in again " +
                "before running anything else, and do not simply re-run the request: statements that already ran would run again.",
                authenticationRequired: true,
                ex),
            QueryExecutionException query => new PpdsException(query.ErrorCode, message, ex),
            _ => new PpdsException(ErrorCodes.Query.ExecutionFailed, message, ex)
        };
        RecordCommittedOutcome(wrapped, context);
        return wrapped;
    }

    /// <summary>
    /// Exception filter: when a run is interrupted (error, cancellation) after some DML already ran, attaches the
    /// outcomes (including a statement that was part-way through) to the exception so callers can report what was
    /// changed. Always false.
    /// </summary>
    private static bool RecordCommittedOutcome(Exception exception, QueryPlanContext context)
    {
        var outcome = DmlOutcome.From(context.Statistics.DmlOutcomes);
        if (outcome != null)
            DmlOutcome.Attach(exception, outcome);
        return false;
    }

    /// <inheritdoc />
    public Task<QueryPlanDescription> ExplainAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        TSqlFragment fragment;
        try
        {
            fragment = _queryParser.Parse(sql);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }

        if (fragment is TSqlScript { Batches: var explainBatches } && explainBatches.Count(b => b.Statements.Count > 0) > 1)
            throw new PpdsValidationException("sql", ExecutionPlanBuilder.MultipleBatchesMessage);

        // Parse query hints for EXPLAIN path too
        var hints = QueryHintParser.Parse(fragment);

        var effectivePoolCapacity = hints.MaxParallelism.HasValue
            ? Math.Min(hints.MaxParallelism.Value, _poolCapacity)
            : _poolCapacity;

        // Don't override explicit TOP with hints.MaxResultRows (same guard as PrepareExecutionAsync)
        var sqlHasExplicitTop = ExtractQuerySpecification(fragment)?.TopRowFilter != null;
        var containsDml = _dmlSafetyGuard.Check(
            ExtractPlannedStatement(fragment), new DmlSafetyOptions { IsConfirmed = true, IsDryRun = true },
            EnvironmentSafetySettings, EnvironmentProtectionLevel).ContainsDml;
        // Row limits never apply to DML (see PrepareExecutionAsync), so the plan must not show one.
        var effectiveMaxRows = sqlHasExplicitTop || containsDml ? null : hints.MaxResultRows;

        var planOptions = new QueryPlanOptions
        {
            RemoteExecutorFactory = RemoteExecutorFactory,
            PoolCapacity = effectivePoolCapacity,
            ForceClientAggregation = hints.ForceClientAggregation == true,
            NoLock = hints.NoLock == true,
            UseTdsEndpoint = hints.UseTdsEndpoint == true,
            MaxRows = effectiveMaxRows,
            TdsQueryExecutor = _tdsQueryExecutor,
            OriginalSql = sql
        };

        QueryPlanResult planResult;
        try
        {
            planResult = _planBuilder.Plan(fragment, planOptions);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }
        catch (DmlSafetyRefusalException ex)
        {
            throw new PpdsException(ex.ErrorCode, ex.Message, ex);
        }
        catch (QueryExecutionException ex)
        {
            // A planning error with a code (for example a number literal PPDS cannot hold) keeps its code.
            throw new PpdsException(ex.ErrorCode, ex.Message, ex);
        }

        var description = QueryPlanDescription.FromNode(planResult.RootNode);

        // Extract parallelism metadata from plan tree
        description.PoolCapacity = ExtractPoolCapacity(planResult.RootNode);
        description.EffectiveParallelism = ExtractEffectiveParallelism(planResult.RootNode);

        return Task.FromResult(description);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SqlQueryStreamChunk> ExecuteStreamingAsync(
        SqlQueryRequest request,
        int chunkSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chunkSize <= 0) chunkSize = 100;

        var (fragment, planResult, safetyResult, executionOptions, streamAppliedHints) =
            await PrepareExecutionAsync(request, cancellationToken).ConfigureAwait(false);

        var streamDataSources = CollectDataSources(planResult.RootNode, "Local");

        // Dry-run: yield empty completion chunk
        if (safetyResult is { ContainsDml: true, IsDryRun: true })
        {
            var preview = BuildDryRunPreview(planResult);
            yield return new SqlQueryStreamChunk
            {
                Rows = new List<IReadOnlyDictionary<string, QueryValue>>(),
                Columns = Array.Empty<QueryColumn>(),
                EntityLogicalName = planResult.EntityLogicalName,
                TotalRowsSoFar = 0,
                IsComplete = true,
                TranspiledFetchXml = preview.FetchXml,
                DmlSafetyResult = safetyResult,
                DryRunPlan = preview.Plan,
                DataSources = CollectDataSources(preview.PlannedNodes, "Local"),
                AppliedHints = streamAppliedHints
            };
            yield break;
        }

        // Match the non-streaming safety boundary: only an executing DML plan is a mutation.
        if (safetyResult is { ContainsDml: true, IsDryRun: false })
        {
            _guard.EnsureCanMutate("query.dml");
        }

        // Execute the plan with streaming
        var context = new QueryPlanContext(
            _queryExecutor,
            cancellationToken,
            bulkOperationExecutor: _bulkOperationExecutor,
            metadataQueryExecutor: _metadataQueryExecutor,
            executionOptions: executionOptions,
            metadataProvider: _metadataProvider);

        var chunkRows = new List<IReadOnlyDictionary<string, QueryValue>>(chunkSize);
        IReadOnlyList<QueryColumn>? columns = null;
        var totalRows = 0;
        var isFirstChunk = true;
        var streamIsAggregate = HasAggregatesInFragment(fragment);

        // Wrap streaming enumeration in TDS connection failure catch.
        // Cannot use try/catch around yield return, so we use a helper that
        // catches during MoveNextAsync and rethrows as PpdsException.
        var streamEnumerator = _planExecutor
            .ExecuteStreamingAsync(planResult, context, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                QueryRow row;
                try
                {
                    if (!await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    row = streamEnumerator.Current;
                }
                catch (QueryExecutionException ex) when (ex.ErrorCode == QueryErrorCode.DmlRowCapExceeded)
                {
                    throw WrapRowCapExceeded(ex, context);
                }
                catch (DmlSafetyRefusalException ex)
                {
                    throw WrapRefusal(ex, context);
                }
                catch (Exception ex) when (DmlRan(context) && ex is not OperationCanceledException && (ex is not PpdsException || ex is PpdsAuthException))
                {
                    throw WrapInterrupted(ex, context);
                }
                catch (Exception ex) when (RecordCommittedOutcome(ex, context))
                {
                    throw; // never reached: the filter only attaches what already ran and returns false
                }
                catch (Exception ex) when (
                    request.UseTdsEndpoint
                    && ContainsTdsScanNode(planResult.RootNode)
                    && ex is not OperationCanceledException
                    && ex is not PpdsException)
                {
                    throw new PpdsException(
                        ErrorCodes.Query.TdsConnectionFailed,
                        $"TDS Endpoint connection failed: {ex.Message}. The TDS Endpoint may be disabled " +
                        "on this environment. Disable TDS mode to query via Dataverse, or ask your Power " +
                        "Platform admin to enable the TDS Endpoint.",
                        ex);
                }
                catch (Exception ex) when (ex is not PpdsException and not OperationCanceledException)
                {
                    throw WrapFailure(ex, context);
                }

                // Infer columns from first row
                if (columns == null)
                {
                    columns = InferColumnsFromRow(row);
                }

                chunkRows.Add(row.Values);
                totalRows++;

                if (chunkRows.Count >= chunkSize)
                {
                    // Expand virtual columns (owneridname, statuscodename, etc.)
                    var expandedChunk = ExpandStreamingChunk(
                        chunkRows, columns!, planResult.VirtualColumns, streamIsAggregate);

                    yield return new SqlQueryStreamChunk
                    {
                        Rows = expandedChunk.rows,
                        Columns = isFirstChunk ? expandedChunk.columns : null,
                        EntityLogicalName = isFirstChunk ? planResult.EntityLogicalName : null,
                        TotalRowsSoFar = totalRows,
                        IsComplete = false,
                        TranspiledFetchXml = isFirstChunk ? planResult.FetchXml : null
                    };

                    isFirstChunk = false;
                    chunkRows.Clear();
                }
            }
        }
        finally
        {
            await streamEnumerator.DisposeAsync().ConfigureAwait(false);
        }

        // Yield final chunk with any remaining rows
        var finalExpanded = ExpandStreamingChunk(
            chunkRows, columns ?? Array.Empty<QueryColumn>(), planResult.VirtualColumns, streamIsAggregate);

        yield return new SqlQueryStreamChunk
        {
            Rows = finalExpanded.rows,
            Columns = isFirstChunk ? finalExpanded.columns : null,
            EntityLogicalName = isFirstChunk ? planResult.EntityLogicalName : null,
            TotalRowsSoFar = totalRows,
            IsComplete = true,
            TranspiledFetchXml = isFirstChunk ? planResult.FetchXml : null,
            DataSources = streamDataSources,
            AppliedHints = streamAppliedHints,
            ExecutionMode = DetectExecutionMode(planResult.RootNode),
            DmlOutcome = DmlOutcome.From(context.Statistics.DmlOutcomes)
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Shared execution pipeline
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shared setup for <see cref="ExecuteAsync"/> and <see cref="ExecuteStreamingAsync"/>:
    /// parse, DML safety check, aggregate metadata, plan build, cross-env check.
    /// </summary>
    private async Task<(TSqlFragment fragment, QueryPlanResult planResult, DmlSafetyResult? safetyResult, QueryExecutionOptions? executionOptions, List<string>? appliedHints)>
        PrepareExecutionAsync(SqlQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);

        TSqlFragment fragment;
        try
        {
            fragment = _queryParser.Parse(request.Sql);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }

        // Only one batch runs per request: refuse rather than drop the batches after GO without a word.
        if (fragment is TSqlScript { Batches: var batches } && batches.Count(b => b.Statements.Count > 0) > 1)
            throw new PpdsValidationException("sql", ExecutionPlanBuilder.MultipleBatchesMessage);

        // Parse query hints (-- ppds:* comments and OPTION() clauses)
        var hints = QueryHintParser.Parse(fragment);

        // Apply plan-level overrides: hints win over caller settings
        if (hints.UseTdsEndpoint == true)
        {
            request = request with { UseTdsEndpoint = true };
        }
        if (hints.MaxResultRows.HasValue)
        {
            request = request with { TopOverride = hints.MaxResultRows.Value };
        }

        // TDS compatibility pre-check — fail fast before planning.
        // When the user explicitly requests TDS, they get TDS or an error,
        // never a silent substitution to Dataverse.
        if (request.UseTdsEndpoint)
        {
            var querySpec = ExtractQuerySpecification(fragment);
            var entityName = querySpec != null ? ExtractEntityName(querySpec) : null;
            var tdsCompatibility = TdsCompatibilityChecker.CheckCompatibility(
                request.Sql, entityName);

            if (tdsCompatibility != TdsCompatibility.Compatible)
            {
                var reason = tdsCompatibility switch
                {
                    TdsCompatibility.IncompatibleDml =>
                        "Cannot execute via TDS: DML statements (DELETE, UPDATE, INSERT) are not supported by the TDS Endpoint. Disable TDS mode to execute this query against Dataverse.",
                    TdsCompatibility.IncompatibleEntity =>
                        $"Cannot execute via TDS: The target entity is not available via the TDS Endpoint (elastic/virtual table). Disable TDS mode to query via Dataverse.",
                    _ =>
                        "Cannot execute via TDS: This query uses features not supported by the TDS Endpoint. Disable TDS mode to execute via Dataverse."
                };

                throw new PpdsException(ErrorCodes.Query.TdsIncompatible, reason);
            }
        }

        // DML safety check. A request without explicit options is checked with the defaults (not
        // confirmed, not dry-run, default row cap): the guard fails closed instead of being skipped.
        var dmlSafety = request.DmlSafety ?? new DmlSafetyOptions();
        int? dmlRowCap = null;
        DmlSafetyResult? safetyResult = null;

        var plannedStatement = ExtractPlannedStatement(fragment);

        safetyResult = _dmlSafetyGuard.Check(
            plannedStatement, dmlSafety,
            EnvironmentSafetySettings, EnvironmentProtectionLevel);

        // An explicit --top limits returned rows only. Refuse it for DML rather than silently dropping it
        // (which would turn "delete 5 rows" into "delete every matching row"). A default top the caller did
        // not ask for (the extension's default top over RPC) is not marked explicit and is simply ignored for DML.
        // The MAX_ROWS hint is text the user wrote, so ignoring it would change what the statement affects. The engine
        // cannot apply a row limit to DML, so the request is refused instead (on every surface, before anything runs).
        // The TDS endpoint is a read-only replica. The pre-check above reads the statement text and can miss DML (for
        // example behind a leading comment), so the parsed request decides: DML never runs with TDS selected, whether
        // asked for by the caller (useTds, the TUI toggle) or by a -- ppds:USE_TDS hint.
        if (safetyResult.ContainsDml && request.UseTdsEndpoint)
        {
            throw new PpdsException(
                ErrorCodes.Query.TdsIncompatible,
                "Cannot execute via TDS: DML statements (DELETE, UPDATE, INSERT) are not supported by the TDS Endpoint. " +
                "Nothing was executed. Disable TDS mode (and remove any -- ppds:USE_TDS hint) to run this statement against Dataverse.");
        }

        if (safetyResult.ContainsDml && dmlSafety.RowCap is <= 0)
        {
            throw new PpdsValidationException(
                "rowCap",
                $"The row cap must be a positive number of rows (got {dmlSafety.RowCap}). Nothing was executed.");
        }

        if (safetyResult.ContainsDml && hints.UnrecognizedHints.Count > 0)
        {
            throw new PpdsValidationException(
                "hint",
                $"The comment '{hints.UnrecognizedHints[0]}' mentions ppds: but is not an exact supported hint, so nothing " +
                "was executed: on INSERT, UPDATE or DELETE a hint must be applied exactly or not given. Write it as " +
                "-- ppds:NAME [value] (a line comment), or remove it.");
        }

        if (safetyResult.ContainsDml && hints.DmlBatchSize is < 1 or > PPDS.Dataverse.BulkOperations.BulkOperationOptions.MaxBatchSize)
        {
            throw new PpdsValidationException(
                "BATCH_SIZE",
                $"-- ppds:BATCH_SIZE must be between 1 and {PPDS.Dataverse.BulkOperations.BulkOperationOptions.MaxBatchSize}, " +
                $"so nothing was executed (got {hints.DmlBatchSize}).");
        }

        if (safetyResult.ContainsDml && hints.MaxResultRows.HasValue)
        {
            throw new PpdsValidationException(
                "MAX_ROWS",
                "The -- ppds:MAX_ROWS hint limits returned rows only and does not apply to INSERT, UPDATE or DELETE, so " +
                "nothing was executed. Remove the hint, and put the limit in the statement's WHERE clause instead.");
        }

        if (safetyResult.ContainsDml && request.TopIsUserSpecified && request.TopOverride.HasValue)
        {
            throw new PpdsValidationException(
                "top",
                "--top limits returned rows only and does not apply to INSERT, UPDATE or DELETE, so nothing was " +
                "executed. Put the limit in the statement's WHERE clause instead.");
        }

        // Paging selects returned rows only. A DML source scan given a page would change one page of matches and
        // report success, so paging never reaches DML: refused when the user asked for it, refused for a request
        // that asks for a further page (load more), and otherwise (a caller's first-page value) ignored below.
        var isPaged = request.PageNumber.HasValue || request.PagingCookie != null;
        if (safetyResult.ContainsDml && isPaged)
        {
            if (request.PagingIsUserSpecified)
            {
                throw new PpdsValidationException(
                    "page",
                    "--page and --paging-cookie select returned rows only and do not apply to INSERT, UPDATE or " +
                    "DELETE, so nothing was executed. Run the statement without them.");
            }

            if (request.PagingCookie != null || request.PageNumber > 1)
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlBlocked,
                    "This is a request for a further page of results, which never runs INSERT, UPDATE or DELETE. " +
                    "Nothing was executed. Run the statement again as a new query.");
            }
        }

        // Writes to another environment are refused here, before any confirmation is asked for (confirming cannot make
        // them run where the statement says), and before the allowlist refusal so the message names the environment.
        CheckCrossEnvironmentDmlPolicy(fragment, safetyResult);

        if (safetyResult.IsBlocked)
        {
            throw new PpdsException(
                safetyResult.ErrorCode ?? ErrorCodes.Query.DmlBlocked,
                safetyResult.BlockReason ?? "DML operation blocked by safety guard.");
        }

        // The whole-script rule: a script that writes may contain only admitted INSERT, UPDATE and DELETE statements.
        // Refused here, before any row is read, any confirmation is asked for and anything is written, for confirmed,
        // unconfirmed and dry-run requests alike. After the guard's own refusals, as the spec orders them.
        if (DmlStatementSupport.FindScriptRefusal(fragment) is { } scriptRefusal)
            throw new PpdsException(scriptRefusal.Code, scriptRefusal.Message);

        // NOLOCK, HASH_GROUP and MAXDOP were not proven for DML, and NOLOCK changes which rows the statement selects
        // (uncommitted rows). They are refused, like any OPTION (...) clause on the statement itself.
        if (safetyResult.ContainsDml)
        {
            var unproven = new[]
            {
                hints.NoLock == true ? "NOLOCK" : null,
                hints.ForceClientAggregation == true ? "HASH_GROUP" : null,
                hints.MaxParallelism.HasValue ? "MAXDOP" : null
            }.OfType<string>().FirstOrDefault();
            if (unproven != null)
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlUnsupportedConstruct,
                    $"The -- ppds:{unproven} hint is not supported on INSERT, UPDATE or DELETE. Nothing was executed. " +
                    "Remove the hint (only -- ppds:BATCH_SIZE, -- ppds:BYPASS_PLUGINS and -- ppds:BYPASS_FLOWS apply to DML).");
            }
        }

        // Every column the DML reads or writes must be a real attribute of its table (checked against metadata, so a
        // display column such as owneridname or a misspelt name is refused instead of reading NULL). Without metadata
        // nothing can be checked, so DML is refused here, before any confirmation is asked for and also for a dry-run.
        if (safetyResult.ContainsDml)
        {
            if (_metadataProvider == null)
            {
                throw new PpdsException(ErrorCodes.Query.DmlMetadataUnavailable,
                    "INSERT, UPDATE and DELETE need the table's column metadata to be checked before they run, and none is available here. Nothing was executed.");
            }
            await DmlColumnValidator.ValidateAsync(plannedStatement, _metadataProvider, cancellationToken).ConfigureAwait(false);
        }

        // BYPASS_PLUGINS / BYPASS_FLOWS weaken safeguards: one statement only (a hint is read from the whole request),
        // never in a Production-protected environment unless its settings already bypass the same logic.
        var bypasses = safetyResult.ContainsDml
            ? new[] { hints.BypassPlugins == true ? "BYPASS_PLUGINS" : null, hints.BypassFlows == true ? "BYPASS_FLOWS" : null }
                .OfType<string>().ToList()
            : new List<string>();
        if (bypasses.Count > 0)
        {
            if (plannedStatement is not (InsertStatement or UpdateStatement or DeleteStatement))
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlBlocked,
                    $"-- ppds:{bypasses[0]} applies to the whole request, so it is allowed only on a request with a single " +
                    "INSERT, UPDATE or DELETE statement. Nothing was executed. Run that statement on its own.");
            }

            // BYPASS_PLUGINS bypasses synchronous logic, so only settings that already bypass synchronous plugins allow it.
            // An environment whose configuration states neither a type nor a protection level counts as Production here.
            var settings = EnvironmentSafetySettings ?? new QuerySafetySettings();
            var allowed = (hints.BypassPlugins != true || settings.BypassCustomPlugins is BypassPluginMode.Synchronous or BypassPluginMode.All)
                && (hints.BypassFlows != true || settings.BypassPowerAutomateFlows);
            var gateProtection = _environmentTypeUnknown ? ProtectionLevel.Production : EnvironmentProtectionLevel;
            if (gateProtection == ProtectionLevel.Production && !allowed)
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlBlocked,
                    $"-- ppds:{string.Join(", -- ppds:", bypasses)} is not allowed in a Production-protected environment " +
                    "whose safety settings do not already bypass that logic. Nothing was executed.");
            }
        }

        // A dry-run is the confirmation preview, not an execution attempt. Keep the
        // confirmation requirement on the result so callers know actual execution is
        // still gated, but do not require --confirm merely to build the side-effect-free
        // plan. Hard safety blocks above (for example, UPDATE/DELETE without WHERE) still
        // apply to previews.
        // A confirmation over RPC names the environment it was given for; it is applied only to that environment.
        if (safetyResult.ContainsDml && dmlSafety is { IsConfirmed: true, IsDryRun: false, RequireConfirmedTarget: true })
            CheckConfirmedTarget(dmlSafety.ConfirmedEnvironmentUrl);
        if (safetyResult.ContainsDml && dmlSafety is { IsConfirmed: true, IsDryRun: false, RequireConfirmedProfile: true })
            CheckConfirmedProfile(dmlSafety.ConfirmedProfileIdentity, dmlSafety.ExecutingProfileIdentity);

        if (safetyResult.ContainsDml && safetyResult.RequiresConfirmation && !safetyResult.IsDryRun)
        {
            // Surface-neutral: the CLI replaces it with its --confirm/--dry-run wording, the TUI and the extension ask.
            // Writes to another environment were refused above, so the connected environment is the only target.
            throw new PpdsDmlConfirmationRequiredException(
                ConfirmationRequiredMessage + BypassNote(bypasses),
                [new DmlTarget(_environmentLabel, _environmentUrl, _environmentType, EnvironmentProtectionLevel.ToString())])
            {
                Bypasses = bypasses
            };
        }

        dmlRowCap = safetyResult.RowCap;

        // For aggregate queries, fetch metadata needed for partitioning decisions.
        // A DML dry-run must remain side-effect-free, including avoiding Dataverse
        // metadata calls made only to optimize a read that will not be executed.
        var (estimatedRecordCount, minDate, maxDate) =
            safetyResult is { ContainsDml: true, IsDryRun: true }
                ? (null, null, null)
                : await FetchAggregateMetadataAsync(fragment, cancellationToken).ConfigureAwait(false);

        // Apply hint-level overrides to pool capacity
        var effectivePoolCapacity = hints.MaxParallelism.HasValue
            ? Math.Min(hints.MaxParallelism.Value, _poolCapacity)
            : _poolCapacity;

        // Build execution options from bypass hints
        var dmlBatchSize = safetyResult.ContainsDml ? hints.DmlBatchSize : null;
        var executionOptions = (hints.BypassPlugins == true || hints.BypassFlows == true || dmlBatchSize.HasValue)
            ? new QueryExecutionOptions
            {
                BypassPlugins = hints.BypassPlugins == true,
                BypassFlows = hints.BypassFlows == true,
                DmlBatchSize = dmlBatchSize
            }
            : null;

        // Only apply TopOverride as MaxRows if the SQL itself does not contain a TOP clause.
        // The extension sends a default top (100) on every query — we must not clobber the
        // user's explicit TOP 5 with the default 100.
        // A caller's default TopOverride limits result sets only and is ignored when the request contains DML (an
        // explicit --top and the MAX_ROWS hint were refused above for DML).
        var sqlHasExplicitTop = ExtractQuerySpecification(fragment)?.TopRowFilter != null;
        var effectiveMaxRows = sqlHasExplicitTop || safetyResult.ContainsDml ? null : request.TopOverride;

        // Build execution plan
        var planOptions = new QueryPlanOptions
        {
            MaxRows = effectiveMaxRows,
            PageNumber = safetyResult.ContainsDml ? null : request.PageNumber,
            PagingCookie = safetyResult.ContainsDml ? null : request.PagingCookie,
            IncludeCount = request.IncludeCount,
            UseTdsEndpoint = request.UseTdsEndpoint,
            OriginalSql = request.Sql,
            TdsQueryExecutor = _tdsQueryExecutor,
            DmlRowCap = dmlRowCap,
            EnablePrefetch = request.EnablePrefetch,
            PoolCapacity = effectivePoolCapacity,
            EstimatedRecordCount = estimatedRecordCount,
            MinDate = minDate,
            MaxDate = maxDate,
            RemoteExecutorFactory = RemoteExecutorFactory,
            ForceClientAggregation = hints.ForceClientAggregation == true,
            NoLock = hints.NoLock == true
        };

        QueryPlanResult planResult;
        try
        {
            planResult = _planBuilder.Plan(fragment, planOptions);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }
        catch (DmlSafetyRefusalException ex)
        {
            throw new PpdsException(ex.ErrorCode, ex.Message, ex);
        }
        catch (QueryExecutionException ex)
        {
            // A planning error with a code (for example a number literal PPDS cannot hold) keeps its code, before any read.
            throw new PpdsException(ex.ErrorCode, ex.Message, ex);
        }

        // MAX_ROWS is reported as applied only when it limited the plan: not when the statement has its own TOP,
        // and not for scripts, whose statements are planned one at a time without the limit.
        var maxRowsApplied = hints.MaxResultRows.HasValue && effectiveMaxRows.HasValue
            && planResult.RootNode is not ScriptExecutionNode;

        return (fragment, planResult, safetyResult, executionOptions,
            CollectAppliedHints(hints, maxRowsApplied, batchSizeApplied: dmlBatchSize.HasValue,
                isDml: safetyResult.ContainsDml, isScript: planResult.RootNode is ScriptExecutionNode));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ScriptDom AST helpers (replace legacy ISqlStatement checks)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetches estimated record count and date range for aggregate queries.
    /// Returns nulls for non-aggregate statements (no metadata fetch needed).
    /// Uses ScriptDom AST analysis instead of legacy ISqlStatement.
    /// </summary>
    private async Task<(long? EstimatedRecordCount, DateTime? MinDate, DateTime? MaxDate)> FetchAggregateMetadataAsync(
        TSqlFragment fragment,
        CancellationToken cancellationToken)
    {
        var querySpec = ExtractQuerySpecification(fragment);
        if (querySpec is null) return (null, null, null);

        if (!HasAggregateColumns(querySpec)) return (null, null, null);

        var entityName = ExtractEntityName(querySpec);
        if (entityName is null) return (null, null, null);

        var countTask = _queryExecutor.GetTotalRecordCountAsync(entityName, cancellationToken);
        var dateTask = _queryExecutor.GetMinMaxCreatedOnAsync(entityName, cancellationToken);
        await Task.WhenAll(countTask, dateTask).ConfigureAwait(false);

        var count = await countTask.ConfigureAwait(false);
        var dateRange = await dateTask.ConfigureAwait(false);
        return (count, dateRange.Min, dateRange.Max);
    }

    /// <summary>
    /// Checks if a parsed ScriptDom fragment represents a SELECT with aggregate functions.
    /// </summary>
    private static bool HasAggregatesInFragment(TSqlFragment fragment)
    {
        var querySpec = ExtractQuerySpecification(fragment);
        return querySpec is not null && HasAggregateColumns(querySpec);
    }

    /// <summary>
    /// Extracts a <see cref="QuerySpecification"/> from a parsed fragment.
    /// </summary>
    private static QuerySpecification? ExtractQuerySpecification(TSqlFragment fragment)
    {
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                foreach (var stmt in batch.Statements)
                {
                    if (stmt is SelectStatement sel && sel.QueryExpression is QuerySpecification qs)
                        return qs;
                }
            }
            return null;
        }

        if (fragment is SelectStatement selectStmt && selectStmt.QueryExpression is QuerySpecification querySpec)
            return querySpec;

        if (fragment is QuerySpecification directQs)
            return directQs;

        return null;
    }

    /// <summary>
    /// Checks if a QuerySpecification's SELECT list contains aggregate function calls.
    /// </summary>
    private static bool HasAggregateColumns(QuerySpecification querySpec)
    {
        foreach (var elem in querySpec.SelectElements)
        {
            if (elem is SelectScalarExpression scalar && scalar.Expression is FunctionCall funcCall)
            {
                var funcName = funcCall.FunctionName?.Value;
                if (funcName is not null && IsAggregateFunction(funcName))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if a function name is a recognized aggregate function.
    /// </summary>
    private static bool IsAggregateFunction(string functionName)
    {
        return functionName.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("SUM", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("AVG", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("MIN", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("MAX", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the primary entity name from a QuerySpecification's FROM clause.
    /// </summary>
    private static string? ExtractEntityName(QuerySpecification querySpec)
    {
        if (querySpec.FromClause is null || querySpec.FromClause.TableReferences.Count == 0)
            return null;

        var tableRef = querySpec.FromClause.TableReferences[0];

        // Drill through qualified joins to the base table
        while (tableRef is QualifiedJoin qj)
        {
            tableRef = qj.FirstTableReference;
        }

        if (tableRef is NamedTableReference named)
        {
            return named.SchemaObject.BaseIdentifier.Value;
        }

        return null;
    }

    /// <summary>
    /// Injects a TOP override into the first SelectStatement's QuerySpecification.
    /// Modifies the ScriptDom AST in place.
    /// </summary>
    private static void InjectTopOverride(TSqlFragment fragment, int topValue)
    {
        var querySpec = ExtractQuerySpecification(fragment);
        if (querySpec is null) return;

        querySpec.TopRowFilter = new TopRowFilter
        {
            Expression = new IntegerLiteral { Value = topValue.ToString() }
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Result helpers
    // ═══════════════════════════════════════════════════════════════════

    private static IReadOnlyList<QueryColumn> InferColumnsFromRow(QueryRow row)
    {
        var columns = new List<QueryColumn>();
        foreach (var kvp in row.Values)
        {
            var value = kvp.Value;
            var dataType = value.IsLookup ? QueryColumnType.Lookup
                : value.IsOptionSet ? QueryColumnType.OptionSet
                : value.IsBoolean ? QueryColumnType.Boolean
                : QueryColumnType.Unknown;

            columns.Add(new QueryColumn
            {
                LogicalName = kvp.Key,
                DataType = dataType
            });
        }
        return columns;
    }

    private static (List<IReadOnlyDictionary<string, QueryValue>> rows, IReadOnlyList<QueryColumn> columns) ExpandStreamingChunk(
        List<IReadOnlyDictionary<string, QueryValue>> chunkRows,
        IReadOnlyList<QueryColumn> columns,
        IReadOnlyDictionary<string, VirtualColumnInfo> virtualColumns,
        bool isAggregate = false)
    {
        // Build a mini QueryResult for the chunk so we can reuse the expander
        var chunkResult = new QueryResult
        {
            EntityLogicalName = "chunk",
            Columns = columns.ToList(),
            Records = chunkRows,
            Count = chunkRows.Count,
            MoreRecords = false,
            PageNumber = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(
            chunkResult, virtualColumns, isAggregate);

        return (expanded.Records.ToList(), expanded.Columns);
    }

    /// <summary>
    /// Collects all data sources (local + remote) that participated in the query.
    /// </summary>
    private static List<QueryDataSource> CollectDataSources(
        IQueryPlanNode rootNode,
        string localLabel)
        => CollectDataSources([rootNode], localLabel);

    private static List<QueryDataSource> CollectDataSources(
        IEnumerable<IQueryPlanNode> rootNodes,
        string localLabel)
    {
        var sources = new List<QueryDataSource>
        {
            new() { Label = localLabel, IsRemote = false }
        };
        foreach (var rootNode in rootNodes)
            CollectRemoteLabels(rootNode, sources);
        return sources;
    }

    private static (
        QueryPlanDescription Plan,
        string FetchXml,
        IReadOnlyList<IQueryPlanNode> PlannedNodes) BuildDryRunPreview(QueryPlanResult planResult)
    {
        if (planResult.RootNode is ScriptExecutionNode script)
            return script.BuildDryRunPreview();

        return (
            QueryPlanDescription.FromNode(planResult.RootNode),
            planResult.FetchXml,
            [planResult.RootNode]);
    }

    private static void CollectRemoteLabels(IQueryPlanNode node, List<QueryDataSource> sources)
    {
        if (node is RemoteScanNode remote)
        {
            if (!sources.Any(s => s.Label == remote.RemoteLabel))
                sources.Add(new QueryDataSource { Label = remote.RemoteLabel, IsRemote = true });
        }
        foreach (var child in node.Children)
            CollectRemoteLabels(child, sources);
    }

    /// <summary>
    /// Collects the names of query hints that were actively applied.
    /// Returns null when no hints were active.
    /// </summary>
    private static List<string>? CollectAppliedHints(
        QueryHintOverrides hints, bool maxRowsApplied, bool batchSizeApplied, bool isDml, bool isScript)
    {
        var applied = new List<string>();
        if (hints.UseTdsEndpoint == true) applied.Add("USE_TDS");
        if (hints.NoLock == true && !isScript) applied.Add("NOLOCK"); // script statements are planned without it
        if (hints.BypassPlugins == true) applied.Add("BYPASS_PLUGINS");
        if (hints.BypassFlows == true) applied.Add("BYPASS_FLOWS");
        if (maxRowsApplied) applied.Add("MAX_ROWS");
        if (hints.MaxParallelism.HasValue) applied.Add("MAXDOP");
        if (hints.ForceClientAggregation == true && !isDml) applied.Add("HASH_GROUP"); // no aggregates in DML
        if (batchSizeApplied) applied.Add("BATCH_SIZE"); // DML only: the bulk writes use it
        return applied.Count > 0 ? applied : null;
    }

    /// <summary>
    /// Detects every remote environment targeted by DML in the syntax tree.
    /// All distinct targets must pass their own safety policy.
    /// </summary>
    private IReadOnlyList<string> DetectCrossEnvironmentDmlTargets(
        TSqlFragment fragment,
        DmlSafetyResult? safetyResult)
    {
        if (safetyResult?.ContainsDml != true)
            return [];

        // The AST identifies write targets without confusing remote read sources in
        // the plan for remote mutation targets. UPDATE/DELETE one-part targets are
        // resolved against their FROM bindings below.
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectCrossEnvironmentDmlTargets(ExtractPlannedStatement(fragment), labels, seen);
        return labels;
    }

    private void CollectCrossEnvironmentDmlTargets(
        TSqlStatement statement,
        List<string> labels,
        HashSet<string> seen)
    {
        switch (statement)
        {
            case InsertStatement insert:
                AddRemoteTargetLabel(insert.InsertSpecification.Target, labels, seen);
                break;
            case UpdateStatement update:
                AddRemoteTargetLabel(
                    update.UpdateSpecification.Target,
                    update.UpdateSpecification.FromClause,
                    labels,
                    seen);
                break;
            case DeleteStatement delete:
                AddRemoteTargetLabel(
                    delete.DeleteSpecification.Target,
                    delete.DeleteSpecification.FromClause,
                    labels,
                    seen);
                break;
            case MergeStatement merge:
                AddRemoteTargetLabel(merge.MergeSpecification.Target, labels, seen);
                break;
            case BeginEndBlockStatement block:
                CollectCrossEnvironmentDmlTargets(block.StatementList.Statements, labels, seen);
                break;
            case IfStatement ifStatement:
                CollectCrossEnvironmentDmlTargets(ifStatement.ThenStatement, labels, seen);
                if (ifStatement.ElseStatement != null)
                    CollectCrossEnvironmentDmlTargets(ifStatement.ElseStatement, labels, seen);
                break;
            case WhileStatement whileStatement:
                CollectCrossEnvironmentDmlTargets(whileStatement.Statement, labels, seen);
                break;
            case TryCatchStatement tryCatch:
                CollectCrossEnvironmentDmlTargets(tryCatch.TryStatements.Statements, labels, seen);
                CollectCrossEnvironmentDmlTargets(tryCatch.CatchStatements.Statements, labels, seen);
                break;
        }
    }

    private void CollectCrossEnvironmentDmlTargets(
        IEnumerable<TSqlStatement> statements,
        List<string> labels,
        HashSet<string> seen)
    {
        foreach (var statement in statements)
            CollectCrossEnvironmentDmlTargets(statement, labels, seen);
    }

    private void AddRemoteTargetLabel(
        TableReference target,
        List<string> labels,
        HashSet<string> seen)
        => AddRemoteTargetLabel(target, fromClause: null, labels, seen);

    private void AddRemoteTargetLabel(
        TableReference target,
        FromClause? fromClause,
        List<string> labels,
        HashSet<string> seen)
    {
        var label = GetRemoteTargetLabel(target)
            ?? ResolveRemoteTargetAlias(target, fromClause);
        if (label != null && seen.Add(label))
            labels.Add(label);
    }

    private string? ResolveRemoteTargetAlias(TableReference target, FromClause? fromClause)
    {
        if (target is not NamedTableReference namedTarget || fromClause == null)
            return null;

        // A multipart target names its table directly (for example dbo.account).
        // Only a one-part target can be an alias or unqualified FROM binding.
        if (namedTarget.SchemaObject.ServerIdentifier != null
            || namedTarget.SchemaObject.DatabaseIdentifier != null
            || namedTarget.SchemaObject.SchemaIdentifier != null)
            return null;

        var targetAlias = namedTarget.SchemaObject.BaseIdentifier?.Value;
        if (targetAlias == null)
            return null;

        var aliasMatches = new List<NamedTableReference>();
        var baseNameMatches = new List<NamedTableReference>();
        foreach (var tableReference in fromClause.TableReferences)
            CollectNamedTableBindings(tableReference, targetAlias, aliasMatches, baseNameMatches);

        // An explicit alias takes precedence over an unaliased table name. If either
        // binding form is ambiguous, fail closed rather than guessing which remote
        // environment would receive the mutation.
        var matches = aliasMatches.Count > 0 ? aliasMatches : baseNameMatches;
        if (matches.Count > 1)
        {
            throw new PpdsException(
                ErrorCodes.Query.DmlUnsupportedConstruct,
                $"Cannot safely identify DML target '{targetAlias}' because multiple FROM tables match it. Nothing was executed. " +
                "Name the target table alone in FROM (for example UPDATE a SET ... FROM account a WHERE ...), or leave FROM out " +
                "and put the condition in the WHERE clause.");
        }

        return matches.Count == 1 ? GetRemoteTargetLabel(matches[0]) : null;
    }

    private static void CollectNamedTableBindings(
        TableReference tableReference,
        string targetAlias,
        List<NamedTableReference> aliasMatches,
        List<NamedTableReference> baseNameMatches)
    {
        switch (tableReference)
        {
            case NamedTableReference named:
                if (string.Equals(named.Alias?.Value, targetAlias, StringComparison.OrdinalIgnoreCase))
                    aliasMatches.Add(named);
                else if (named.Alias == null
                    && string.Equals(
                        named.SchemaObject.BaseIdentifier?.Value,
                        targetAlias,
                        StringComparison.OrdinalIgnoreCase))
                    baseNameMatches.Add(named);
                break;
            case QualifiedJoin qualified:
                CollectNamedTableBindings(
                    qualified.FirstTableReference, targetAlias, aliasMatches, baseNameMatches);
                CollectNamedTableBindings(
                    qualified.SecondTableReference, targetAlias, aliasMatches, baseNameMatches);
                break;
            case UnqualifiedJoin unqualified:
                CollectNamedTableBindings(
                    unqualified.FirstTableReference, targetAlias, aliasMatches, baseNameMatches);
                CollectNamedTableBindings(
                    unqualified.SecondTableReference, targetAlias, aliasMatches, baseNameMatches);
                break;
            case JoinParenthesisTableReference parenthesized:
                CollectNamedTableBindings(
                    parenthesized.Join, targetAlias, aliasMatches, baseNameMatches);
                break;
        }
    }

    private string? GetRemoteTargetLabel(TableReference target)
    {
        if (target is not NamedTableReference named)
            return null;

        var explicitLabel = named.SchemaObject.ServerIdentifier?.Value
            ?? named.SchemaObject.DatabaseIdentifier?.Value;
        if (explicitLabel != null)
            return explicitLabel;

        var schemaLabel = named.SchemaObject.SchemaIdentifier?.Value;
        if (schemaLabel == null || schemaLabel.Equals("dbo", StringComparison.OrdinalIgnoreCase))
            return null;

        // Planning treats every non-dbo two-part name as a remote target whenever a
        // remote executor factory is configured. Classify it the same way here so
        // missing profile metadata falls back to the fail-closed read-only policy.
        return schemaLabel;
    }

    /// <summary>
    /// Refuses every DML statement that writes to another environment (<c>[LABEL].entity</c>, or an alias bound to
    /// one). The planner sends every write to the connected environment, so such a statement cannot do what it says:
    /// a target whose policy is read-only gets that policy's message, every other target is refused as unsupported.
    /// Runs before the confirmation check and for dry-runs, so neither confirmation nor a preview can lead to it.
    /// </summary>
    private void CheckCrossEnvironmentDmlPolicy(TSqlFragment fragment, DmlSafetyResult safetyResult)
    {
        var plannedStatement = ExtractPlannedStatement(fragment);
        foreach (var targetLabel in DetectCrossEnvironmentDmlTargets(fragment, safetyResult))
        {
            if (_environmentLabel != null && string.Equals(targetLabel, _environmentLabel, StringComparison.OrdinalIgnoreCase))
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlBlocked,
                    $"[{targetLabel}] is the connected environment. Name the table without the environment label (for " +
                    "example UPDATE account ... instead of UPDATE [" + targetLabel + "].account ...). Nothing was executed.");
            }

            var targetConfig = ProfileResolver?.ResolveByLabel(targetLabel);
            var targetProtection = PPDS.Cli.Services.Environment.WriteProtectionResolver.ResolveForCrossEnvironmentTarget(targetConfig);

            var crossEnvResult = _dmlSafetyGuard.CheckCrossEnvironmentDml(
                plannedStatement,
                targetConfig?.SafetySettings,
                "local",
                targetLabel,
                targetProtection);

            if (crossEnvResult.IsBlocked)
            {
                throw new PpdsException(
                    crossEnvResult.ErrorCode ?? ErrorCodes.Query.DmlBlocked,
                    crossEnvResult.BlockReason ?? "Cross-environment DML blocked.");
            }

            throw new PpdsException(
                ErrorCodes.Query.DmlBlocked,
                $"Writing to another environment ([{targetLabel}]) is not supported: the statement would not run against " +
                $"[{targetLabel}]. Nothing was executed. Connect to that environment and run the statement there, " +
                "naming the table without the environment label.");
        }
    }

    private static int? ExtractPoolCapacity(IQueryPlanNode node)
    {
        if (node is ParallelPartitionNode ppn)
            return ppn.MaxParallelism;

        foreach (var child in node.Children)
        {
            var result = ExtractPoolCapacity(child);
            if (result.HasValue) return result;
        }

        return null;
    }

    private static int? ExtractEffectiveParallelism(IQueryPlanNode node)
    {
        if (node is ParallelPartitionNode ppn)
            return ppn.Partitions.Count;

        foreach (var child in node.Children)
        {
            var result = ExtractEffectiveParallelism(child);
            if (result.HasValue) return result;
        }

        return null;
    }

    /// <summary>
    /// Detects the execution mode by walking the plan tree for TdsScanNode.
    /// </summary>
    private static QueryExecutionMode DetectExecutionMode(IQueryPlanNode rootNode)
    {
        if (ContainsTdsScanNode(rootNode))
            return QueryExecutionMode.Tds;
        return QueryExecutionMode.Dataverse;
    }

    private static bool ContainsTdsScanNode(IQueryPlanNode node)
    {
        if (node is TdsScanNode)
            return true;
        foreach (var child in node.Children)
        {
            if (ContainsTdsScanNode(child))
                return true;
        }
        return false;
    }
}
