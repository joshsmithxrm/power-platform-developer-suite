using System;
using System.Threading;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Shared context for plan execution: pool, cancellation, statistics.
/// </summary>
public sealed class QueryPlanContext
{
    /// <summary>Connection pool for executing FetchXML queries.</summary>
    public IQueryExecutor QueryExecutor { get; }

    /// <summary>Cancellation token for the entire plan execution.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Mutable statistics: nodes report actual row counts and timing.</summary>
    public QueryPlanStatistics Statistics { get; }

    /// <summary>Optional progress reporter for long-running operations.</summary>
    public IQueryProgressReporter? ProgressReporter { get; }

    /// <summary>Optional TDS Endpoint executor for direct SQL execution (Phase 3.5).</summary>
    public ITdsQueryExecutor? TdsQueryExecutor { get; }

    /// <summary>Optional metadata query executor for metadata virtual tables (Phase 6).</summary>
    public IMetadataQueryExecutor? MetadataQueryExecutor { get; }

    /// <summary>Optional bulk operation executor for DML operations (INSERT, UPDATE, DELETE).</summary>
    public IBulkOperationExecutor? BulkOperationExecutor { get; }

    /// <summary>Optional variable scope for resolving @variable references in expressions.</summary>
    public VariableScope? VariableScope { get; }

    /// <summary>
    /// Optional cached metadata provider. When supplied, DML nodes use this to coerce
    /// SQL literals into Dataverse SDK types (<c>EntityReference</c>, <c>OptionSetValue</c>, etc.)
    /// for lookup, choice, and money attributes. Null disables coercion (raw CLR values are
    /// passed through to <c>Entity[attr]</c>).
    /// </summary>
    public Metadata.ICachedMetadataProvider? MetadataProvider { get; }

    /// <summary>
    /// Maximum rows a node may materialize in memory (e.g., for sorting or aggregation).
    /// Default is 500,000. Set to 0 for unlimited.
    /// </summary>
    public int MaxMaterializationRows { get; }

    /// <summary>
    /// Per-query execution options (bypass plugins/flows). Null means no overrides.
    /// Consumed by <see cref="PPDS.Dataverse.Query.Planning.Nodes.FetchXmlScanNode"/>
    /// when calling <see cref="IQueryExecutor.ExecuteFetchXmlAsync(string, int?, string?, bool, QueryExecutionOptions?, CancellationToken)"/>.
    /// </summary>
    public QueryExecutionOptions? ExecutionOptions { get; }

    /// <summary>Initializes a new instance of the <see cref="QueryPlanContext"/> class.</summary>
    public QueryPlanContext(
        IQueryExecutor queryExecutor,
        CancellationToken cancellationToken = default,
        QueryPlanStatistics? statistics = null,
        IQueryProgressReporter? progressReporter = null,
        ITdsQueryExecutor? tdsQueryExecutor = null,
        IMetadataQueryExecutor? metadataQueryExecutor = null,
        IBulkOperationExecutor? bulkOperationExecutor = null,
        VariableScope? variableScope = null,
        int maxMaterializationRows = 500_000,
        QueryExecutionOptions? executionOptions = null,
        Metadata.ICachedMetadataProvider? metadataProvider = null)
    {
        QueryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));
        CancellationToken = cancellationToken;
        Statistics = statistics ?? new QueryPlanStatistics();
        ProgressReporter = progressReporter;
        TdsQueryExecutor = tdsQueryExecutor;
        MetadataQueryExecutor = metadataQueryExecutor;
        BulkOperationExecutor = bulkOperationExecutor;
        VariableScope = variableScope;
        MaxMaterializationRows = maxMaterializationRows;
        ExecutionOptions = executionOptions;
        MetadataProvider = metadataProvider;
    }
}
