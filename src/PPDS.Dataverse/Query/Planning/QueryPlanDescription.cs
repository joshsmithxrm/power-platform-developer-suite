using System.Collections.Generic;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Describes an execution plan for display (EXPLAIN output).
/// </summary>
public sealed class QueryPlanDescription
{
    /// <summary>The root node description.</summary>
    public required string NodeType { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>Estimated row count (-1 if unknown).</summary>
    public long EstimatedRows { get; init; } = -1;

    /// <summary>Child node descriptions.</summary>
    public IReadOnlyList<QueryPlanDescription> Children { get; init; } = System.Array.Empty<QueryPlanDescription>();

    /// <summary>Connection pool capacity, if known.</summary>
    public int? PoolCapacity { get; set; }

    /// <summary>Effective parallelism (number of partitions), if applicable.</summary>
    public int? EffectiveParallelism { get; set; }

    /// <summary>
    /// Creates a description tree from a plan node.
    /// </summary>
    public static QueryPlanDescription FromNode(IQueryPlanNode node)
    {
        var children = new List<QueryPlanDescription>();
        foreach (var child in node.Children)
        {
            children.Add(FromNode(child));
        }

        return new QueryPlanDescription
        {
            NodeType = node.GetType().Name,
            Description = node.Description,
            EstimatedRows = node.EstimatedRows,
            Children = children
        };
    }
}
