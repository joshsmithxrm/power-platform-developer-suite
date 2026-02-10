using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Planning.Nodes;

namespace PPDS.Query.Planning;

/// <summary>
/// Holds metadata used by the cost estimator: entity record counts and known statistics.
/// </summary>
public sealed class CostContext
{
    /// <summary>Entity logical name to estimated record count.</summary>
    public Dictionary<string, long> EntityRecordCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default record count when no metadata is available.</summary>
    public long DefaultRecordCount { get; init; } = 10_000;
}

/// <summary>
/// Estimates cardinality (row count) for query plan nodes using heuristic selectivity factors.
/// Uses entity record counts from metadata when available, falling back to defaults.
/// </summary>
/// <remarks>
/// Selectivity heuristics:
///   - Equality (=): 10% of input
///   - Range (&lt;, &gt;, &lt;=, &gt;=): 33% of input
///   - LIKE: 25% of input
///   - IS NULL / IS NOT NULL: 5% of input
///   - NOT EQUAL (&lt;&gt;): 90% of input
///   - Join: outer * inner * selectivity (default 10%)
/// </remarks>
public sealed class CostEstimator
{
    /// <summary>Selectivity for equality predicates (=).</summary>
    public const double EqualitySelectivity = 0.10;

    /// <summary>Selectivity for range predicates (&lt;, &gt;, &lt;=, &gt;=).</summary>
    public const double RangeSelectivity = 0.33;

    /// <summary>Selectivity for LIKE predicates.</summary>
    public const double LikeSelectivity = 0.25;

    /// <summary>Selectivity for IS NULL predicates.</summary>
    public const double IsNullSelectivity = 0.05;

    /// <summary>Selectivity for NOT EQUAL predicates (&lt;&gt;).</summary>
    public const double NotEqualSelectivity = 0.90;

    /// <summary>Default join selectivity for equijoin conditions.</summary>
    public const double DefaultJoinSelectivity = 0.10;

    /// <summary>
    /// Estimates the output cardinality (row count) for a query plan node.
    /// </summary>
    /// <param name="node">The plan node to estimate.</param>
    /// <param name="context">Cost context holding metadata and statistics.</param>
    /// <returns>Estimated row count. Returns -1 if estimation is not possible.</returns>
    public long EstimateCardinality(IQueryPlanNode node, CostContext context)
    {
        if (node == null) throw new ArgumentNullException(nameof(node));
        if (context == null) throw new ArgumentNullException(nameof(context));

        return EstimateNode(node, context);
    }

    private long EstimateNode(IQueryPlanNode node, CostContext context)
    {
        // For nodes with specialized estimation logic, always use the estimator's formula
        // rather than the node's built-in heuristic. This allows the CostEstimator to apply
        // selectivity factors and metadata-based cardinalities.
        return node switch
        {
            FetchXmlScanNode scan => EstimateScan(scan, context),
            ClientFilterNode filter => EstimateFilter(filter, context),
            HashJoinNode hashJoin => EstimateJoin(hashJoin.Children[0], hashJoin.Children[1], context),
            MergeJoinNode mergeJoin => EstimateJoin(mergeJoin.Children[0], mergeJoin.Children[1], context),
            NestedLoopJoinNode nestedLoop => EstimateJoin(nestedLoop.Children[0], nestedLoop.Children[1], context),
            ParallelPartitionNode parallel => EstimateParallel(parallel, context),
            MergeAggregateNode agg => EstimateAggregate(agg, context),
            ConcatenateNode concat => EstimateConcatenate(concat, context),
            DistinctNode distinct => EstimateDistinct(distinct, context),
            _ => node.EstimatedRows >= 0 ? node.EstimatedRows : EstimateFromChildren(node, context)
        };
    }

    private long EstimateScan(FetchXmlScanNode scan, CostContext context)
    {
        if (scan.MaxRows.HasValue)
        {
            return scan.MaxRows.Value;
        }

        if (context.EntityRecordCounts.TryGetValue(scan.EntityLogicalName, out var count))
        {
            return count;
        }

        return context.DefaultRecordCount;
    }

    private long EstimateFilter(ClientFilterNode filter, CostContext context)
    {
        var inputCardinality = EstimateNode(filter.Input, context);
        if (inputCardinality < 0) return -1;

        // Apply a default filter selectivity (equality heuristic)
        return Math.Max(1, (long)(inputCardinality * EqualitySelectivity));
    }

    private long EstimateJoin(IQueryPlanNode left, IQueryPlanNode right, CostContext context)
    {
        var leftCard = EstimateNode(left, context);
        var rightCard = EstimateNode(right, context);

        if (leftCard < 0 || rightCard < 0) return -1;

        return Math.Max(1, (long)(leftCard * rightCard * DefaultJoinSelectivity));
    }

    private long EstimateParallel(ParallelPartitionNode parallel, CostContext context)
    {
        long total = 0;
        foreach (var partition in parallel.Partitions)
        {
            var partCard = EstimateNode(partition, context);
            if (partCard < 0) return -1;
            total += partCard;
        }
        return total;
    }

    private long EstimateAggregate(MergeAggregateNode agg, CostContext context)
    {
        var inputCard = EstimateNode(agg.Input, context);
        if (inputCard < 0) return -1;

        // If there are group-by columns, estimate distinct groups
        if (agg.GroupByColumns.Count > 0)
        {
            // Heuristic: square root of input for group count
            return Math.Max(1, (long)Math.Sqrt(inputCard));
        }

        // No group-by: single aggregate result row
        return 1;
    }

    private long EstimateConcatenate(ConcatenateNode concat, CostContext context)
    {
        long total = 0;
        foreach (var child in concat.Children)
        {
            var childCard = EstimateNode(child, context);
            if (childCard < 0) return -1;
            total += childCard;
        }
        return total;
    }

    private long EstimateDistinct(DistinctNode distinct, CostContext context)
    {
        var inputCard = EstimateNode(distinct.Children[0], context);
        if (inputCard < 0) return -1;

        // Heuristic: 80% of input are distinct
        return Math.Max(1, (long)(inputCard * 0.80));
    }

    private long EstimateFromChildren(IQueryPlanNode node, CostContext context)
    {
        if (node.Children.Count == 0) return context.DefaultRecordCount;

        // For unknown node types, use the first child's estimate
        return EstimateNode(node.Children[0], context);
    }
}
