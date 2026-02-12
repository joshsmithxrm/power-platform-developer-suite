using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Hash join: builds a hash table from the smaller (build) side, then probes it
/// with the larger (probe) side. O(n + m) complexity with O(min(n,m)) memory.
/// Suitable for unsorted inputs with equijoin conditions.
/// </summary>
public sealed class HashJoinNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _left;
    private readonly IQueryPlanNode _right;

    /// <summary>The left (probe) side join key column name.</summary>
    public string LeftKeyColumn { get; }

    /// <summary>The right (build) side join key column name.</summary>
    public string RightKeyColumn { get; }

    /// <summary>The join type (Inner or Left).</summary>
    public JoinType JoinType { get; }

    /// <inheritdoc />
    public string Description => $"HashJoin: {JoinType} ON {LeftKeyColumn} = {RightKeyColumn}";

    /// <inheritdoc />
    public long EstimatedRows
    {
        get
        {
            var l = _left.EstimatedRows;
            var r = _right.EstimatedRows;
            if (l < 0 || r < 0) return -1;
            return JoinType == JoinType.Left ? l : Math.Min(l, r);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _left, _right };

    /// <summary>Initializes a new instance of the <see cref="HashJoinNode"/> class.</summary>
    /// <param name="left">The probe-side input node (streamed).</param>
    /// <param name="right">The build-side input node (materialized into hash table).</param>
    /// <param name="leftKeyColumn">Column name on the left side for the equijoin condition.</param>
    /// <param name="rightKeyColumn">Column name on the right side for the equijoin condition.</param>
    /// <param name="joinType">The join type (Inner or Left).</param>
    public HashJoinNode(
        IQueryPlanNode left,
        IQueryPlanNode right,
        string leftKeyColumn,
        string rightKeyColumn,
        JoinType joinType = JoinType.Inner)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
        LeftKeyColumn = leftKeyColumn ?? throw new ArgumentNullException(nameof(leftKeyColumn));
        RightKeyColumn = rightKeyColumn ?? throw new ArgumentNullException(nameof(rightKeyColumn));
        JoinType = joinType;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phase 1: Build hash table from the right (build) side
        var hashTable = new Dictionary<string, List<(QueryRow row, int index)>>(StringComparer.OrdinalIgnoreCase);
        QueryRow? rightTemplate = null;
        var buildRowCount = 0;

        await foreach (var row in _right.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rightTemplate ??= row;

            var key = NormalizeKey(GetColumnValue(row, RightKeyColumn));

            if (!hashTable.TryGetValue(key, out var bucket))
            {
                bucket = new List<(QueryRow, int)>();
                hashTable[key] = bucket;
            }
            bucket.Add((row, buildRowCount));
            buildRowCount++;
        }

        if (buildRowCount == 0 && JoinType == JoinType.Inner)
            yield break;

        // Track matched build-side rows (for Right and FullOuter)
        var buildMatched = (JoinType is JoinType.Right or JoinType.FullOuter)
            ? new bool[buildRowCount]
            : null;

        QueryRow? leftTemplate = null;

        // Phase 2: Probe the hash table with each left-side row
        await foreach (var leftRow in _left.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            leftTemplate ??= leftRow;

            var probeKey = NormalizeKey(GetColumnValue(leftRow, LeftKeyColumn));
            var matched = false;

            if (hashTable.TryGetValue(probeKey, out var bucket))
            {
                matched = true;
                foreach (var (buildRow, buildIndex) in bucket)
                {
                    if (buildMatched != null) buildMatched[buildIndex] = true;
                    yield return NestedLoopJoinNode.CombineRows(leftRow, buildRow);
                }
            }

            // LEFT or FULL OUTER: emit unmatched probe row with nulls
            if (!matched && JoinType is JoinType.Left or JoinType.FullOuter)
            {
                yield return NestedLoopJoinNode.CombineWithNulls(leftRow, rightTemplate);
            }
        }

        // RIGHT or FULL OUTER: emit unmatched build-side rows
        if (buildMatched != null && leftTemplate != null)
        {
            foreach (var bucket in hashTable.Values)
            {
                foreach (var (buildRow, buildIndex) in bucket)
                {
                    if (!buildMatched[buildIndex])
                    {
                        yield return NestedLoopJoinNode.CombineWithNullsReversed(leftTemplate, buildRow);
                    }
                }
            }
        }
    }

    private static object? GetColumnValue(QueryRow row, string columnName)
    {
        if (row.Values.TryGetValue(columnName, out var qv))
        {
            return qv.Value;
        }

        // Case-insensitive fallback
        foreach (var kvp in row.Values)
        {
            if (string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes a key value to a consistent string representation for hashing.
    /// </summary>
    private static string NormalizeKey(object? value)
    {
        if (value is null) return "\x00NULL\x00";

        if (value is Guid g) return g.ToString("D");

        if (IsNumeric(value))
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture)?.ToUpperInvariant()
            ?? "\x00NULL\x00";
    }

    private static bool IsNumeric(object value)
    {
        return value is int or long or short or byte or decimal or double or float;
    }

}
