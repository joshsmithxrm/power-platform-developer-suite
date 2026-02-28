using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>The left (probe) side join key column names.</summary>
    public IReadOnlyList<string> LeftKeyColumns { get; }

    /// <summary>The right (build) side join key column names.</summary>
    public IReadOnlyList<string> RightKeyColumns { get; }

    /// <summary>The join type (Inner or Left).</summary>
    public JoinType JoinType { get; }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            var pairs = string.Join(" AND ",
                LeftKeyColumns.Zip(RightKeyColumns, (l, r) => $"{l} = {r}"));
            return $"HashJoin: {JoinType} ON {pairs}";
        }
    }

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

    /// <summary>Initializes a new instance for a single-column join key.</summary>
    public HashJoinNode(
        IQueryPlanNode left,
        IQueryPlanNode right,
        string leftKeyColumn,
        string rightKeyColumn,
        JoinType joinType = JoinType.Inner)
        : this(left, right, new[] { leftKeyColumn }, new[] { rightKeyColumn }, joinType)
    {
    }

    /// <summary>Initializes a new instance for multi-column join keys.</summary>
    public HashJoinNode(
        IQueryPlanNode left,
        IQueryPlanNode right,
        IReadOnlyList<string> leftKeyColumns,
        IReadOnlyList<string> rightKeyColumns,
        JoinType joinType = JoinType.Inner)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
        LeftKeyColumns = leftKeyColumns ?? throw new ArgumentNullException(nameof(leftKeyColumns));
        RightKeyColumns = rightKeyColumns ?? throw new ArgumentNullException(nameof(rightKeyColumns));
        if (leftKeyColumns.Count != rightKeyColumns.Count || leftKeyColumns.Count == 0)
            throw new ArgumentException("Left and right key column lists must have the same non-zero length.");
        JoinType = joinType;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phase 1: Build hash table from the right (build) side
        var hashTable = new Dictionary<string, List<(QueryRow row, int index)>>(StringComparer.OrdinalIgnoreCase);
        var nullKeyBuildRows = new List<(QueryRow row, int index)>();
        QueryRow? rightTemplate = null;
        var buildRowCount = 0;

        await foreach (var row in _right.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rightTemplate ??= row;

            var key = BuildCompositeKey(row, RightKeyColumns);

            if (key is null)
            {
                nullKeyBuildRows.Add((row, buildRowCount));
                buildRowCount++;
                continue;
            }

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

        var buildMatched = (JoinType is JoinType.Right or JoinType.FullOuter)
            ? new bool[buildRowCount]
            : null;

        QueryRow? leftTemplate = null;

        // Phase 2: Probe the hash table with each left-side row
        await foreach (var leftRow in _left.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            leftTemplate ??= leftRow;

            var probeKey = BuildCompositeKey(leftRow, LeftKeyColumns);
            var matched = false;

            if (probeKey != null && hashTable.TryGetValue(probeKey, out var bucket))
            {
                matched = true;
                foreach (var (buildRow, buildIndex) in bucket)
                {
                    if (buildMatched != null) buildMatched[buildIndex] = true;
                    yield return NestedLoopJoinNode.CombineRows(leftRow, buildRow);
                }
            }

            if (!matched && JoinType is JoinType.Left or JoinType.FullOuter)
            {
                yield return NestedLoopJoinNode.CombineWithNulls(leftRow, rightTemplate);
            }
        }

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

            foreach (var (buildRow, _) in nullKeyBuildRows)
            {
                yield return NestedLoopJoinNode.CombineWithNullsReversed(leftTemplate, buildRow);
            }
        }
    }

    /// <summary>
    /// Builds a composite hash key from multiple columns. Returns null if any column is null.
    /// </summary>
    private static string? BuildCompositeKey(QueryRow row, IReadOnlyList<string> columns)
    {
        if (columns.Count == 1)
            return NormalizeKey(QueryValueHelper.GetColumnValue(row, columns[0]));

        var parts = new string?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var part = NormalizeKey(QueryValueHelper.GetColumnValue(row, columns[i]));
            if (part is null) return null;
            parts[i] = part;
        }
        return string.Join("\0", parts);
    }

    /// <summary>
    /// Normalizes a key value to a consistent string representation for hashing.
    /// </summary>
    private static string? NormalizeKey(object? value)
    {
        if (value is null) return null;

        if (value is Guid g) return g.ToString("D");

        if (QueryValueHelper.IsNumeric(value))
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture)?.ToUpperInvariant();
    }

}
