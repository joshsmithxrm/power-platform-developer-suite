using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Merge join: walks two sorted inputs in lockstep, matching on the join key.
/// Both inputs MUST be sorted on their respective join key columns.
/// O(n + m) complexity with minimal memory overhead.
/// Suitable for pre-sorted data with equijoin conditions.
/// </summary>
public sealed class MergeJoinNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _left;
    private readonly IQueryPlanNode _right;

    /// <summary>The left join key column name.</summary>
    public string LeftKeyColumn { get; }

    /// <summary>The right join key column name.</summary>
    public string RightKeyColumn { get; }

    /// <summary>The join type (Inner or Left).</summary>
    public JoinType JoinType { get; }

    /// <inheritdoc />
    public string Description => $"MergeJoin: {JoinType} ON {LeftKeyColumn} = {RightKeyColumn}";

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

    /// <summary>Initializes a new instance of the <see cref="MergeJoinNode"/> class.</summary>
    /// <param name="left">The left input node (must be sorted on leftKeyColumn).</param>
    /// <param name="right">The right input node (must be sorted on rightKeyColumn).</param>
    /// <param name="leftKeyColumn">Column name on the left side for the equijoin condition.</param>
    /// <param name="rightKeyColumn">Column name on the right side for the equijoin condition.</param>
    /// <param name="joinType">The join type (Inner or Left).</param>
    public MergeJoinNode(
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
        // Materialize both sides since we need random access for handling duplicates.
        // A streaming merge join would be more memory-efficient but the IAsyncEnumerable
        // interface makes handling the "rewind right for duplicates" case complex.
        var leftRows = new List<QueryRow>();
        var rightRows = new List<QueryRow>();

        await foreach (var row in _left.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            leftRows.Add(row);
        }

        await foreach (var row in _right.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rightRows.Add(row);
        }

        // Merge join: walk both sorted lists
        var leftIdx = 0;
        var rightIdx = 0;

        while (leftIdx < leftRows.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (rightIdx >= rightRows.Count)
            {
                // Right side exhausted
                if (JoinType == JoinType.Left)
                {
                    // Emit remaining left rows with nulls
                    while (leftIdx < leftRows.Count)
                    {
                        yield return CombineWithNulls(leftRows[leftIdx], rightRows.Count > 0 ? rightRows[0] : null);
                        leftIdx++;
                    }
                }
                yield break;
            }

            var leftKey = GetColumnValue(leftRows[leftIdx], LeftKeyColumn);
            var rightKey = GetColumnValue(rightRows[rightIdx], RightKeyColumn);
            var cmp = CompareKeys(leftKey, rightKey);

            if (cmp < 0)
            {
                // Left key is smaller: advance left
                if (JoinType == JoinType.Left)
                {
                    yield return CombineWithNulls(leftRows[leftIdx], rightRows[0]);
                }
                leftIdx++;
            }
            else if (cmp > 0)
            {
                // Right key is smaller: advance right
                rightIdx++;
            }
            else
            {
                // Keys match: handle duplicates on both sides
                // Find the range of equal keys on the right side
                var rightStart = rightIdx;
                while (rightIdx < rightRows.Count &&
                       CompareKeys(GetColumnValue(rightRows[rightIdx], RightKeyColumn), leftKey) == 0)
                {
                    rightIdx++;
                }

                // For each left row with this key value, emit a row for each matching right row
                while (leftIdx < leftRows.Count &&
                       CompareKeys(GetColumnValue(leftRows[leftIdx], LeftKeyColumn), leftKey) == 0)
                {
                    for (var ri = rightStart; ri < rightIdx; ri++)
                    {
                        yield return NestedLoopJoinNode.CombineRows(leftRows[leftIdx], rightRows[ri]);
                    }
                    leftIdx++;
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
    /// Compares two join key values. Nulls sort last (after all non-null values).
    /// </summary>
    private static int CompareKeys(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;

        // Numeric comparison
        if (IsNumeric(a) && IsNumeric(b))
        {
            var da = Convert.ToDecimal(a, CultureInfo.InvariantCulture);
            var db = Convert.ToDecimal(b, CultureInfo.InvariantCulture);
            return da.CompareTo(db);
        }

        // Guid comparison
        if (a is Guid ga && b is Guid gb)
        {
            return ga.CompareTo(gb);
        }

        // DateTime comparison
        if (a is DateTime dtA && b is DateTime dtB)
        {
            return dtA.CompareTo(dtB);
        }

        // String comparison (case-insensitive)
        var sa = Convert.ToString(a, CultureInfo.InvariantCulture) ?? "";
        var sb = Convert.ToString(b, CultureInfo.InvariantCulture) ?? "";
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(object value)
    {
        return value is int or long or short or byte or decimal or double or float;
    }

    /// <summary>
    /// Combines the left row with null values for the right side (LEFT JOIN with no match).
    /// </summary>
    private static QueryRow CombineWithNulls(QueryRow left, QueryRow? rightTemplate)
    {
        var combined = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in left.Values)
        {
            combined[kvp.Key] = kvp.Value;
        }

        if (rightTemplate != null)
        {
            foreach (var kvp in rightTemplate.Values)
            {
                var key = combined.ContainsKey(kvp.Key)
                    ? rightTemplate.EntityLogicalName + "." + kvp.Key
                    : kvp.Key;
                combined[key] = QueryValue.Null;
            }
        }

        return new QueryRow(combined, left.EntityLogicalName);
    }
}
