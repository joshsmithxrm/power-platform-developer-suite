using System.Collections.Generic;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// A node in the query execution plan tree (Volcano/iterator model).
/// Each node produces rows lazily via IAsyncEnumerable.
/// </summary>
public interface IQueryPlanNode
{
    /// <summary>Human-readable description for EXPLAIN output.</summary>
    string Description { get; }

    /// <summary>Estimated row count (for cost-based decisions). -1 if unknown.</summary>
    long EstimatedRows { get; }

    /// <summary>Child nodes (inputs to this operator).</summary>
    IReadOnlyList<IQueryPlanNode> Children { get; }

    /// <summary>Execute this node, producing rows.</summary>
    IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken = default);
}
