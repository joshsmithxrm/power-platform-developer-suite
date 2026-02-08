using System;
using System.Threading;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Shared context for plan execution: pool, evaluator, cancellation, statistics.
/// </summary>
public sealed class QueryPlanContext
{
    /// <summary>Connection pool for executing FetchXML queries.</summary>
    public IQueryExecutor QueryExecutor { get; }

    /// <summary>Expression evaluator for client-side computation.</summary>
    public IExpressionEvaluator ExpressionEvaluator { get; }

    /// <summary>Cancellation token for the entire plan execution.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Mutable statistics: nodes report actual row counts and timing.</summary>
    public QueryPlanStatistics Statistics { get; }

    /// <summary>Optional progress reporter for long-running operations.</summary>
    public IQueryProgressReporter? ProgressReporter { get; }

    /// <summary>Optional TDS Endpoint executor for direct SQL execution (Phase 3.5).</summary>
    public ITdsQueryExecutor? TdsQueryExecutor { get; }

    public QueryPlanContext(
        IQueryExecutor queryExecutor,
        IExpressionEvaluator expressionEvaluator,
        CancellationToken cancellationToken = default,
        QueryPlanStatistics? statistics = null,
        IQueryProgressReporter? progressReporter = null,
        ITdsQueryExecutor? tdsQueryExecutor = null)
    {
        QueryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));
        ExpressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
        CancellationToken = cancellationToken;
        Statistics = statistics ?? new QueryPlanStatistics();
        ProgressReporter = progressReporter;
        TdsQueryExecutor = tdsQueryExecutor;
    }
}
