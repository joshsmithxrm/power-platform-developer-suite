using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Nested loop join: for each outer row, scans all inner rows and emits matches.
/// Suitable for small inner sets or when no equijoin condition is available.
/// O(n * m) complexity but no additional memory overhead beyond materializing the inner side.
/// </summary>
public sealed class NestedLoopJoinNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _left;
    private readonly IQueryPlanNode _right;

    /// <summary>The left (outer) join key column name.</summary>
    public string? LeftKeyColumn { get; }

    /// <summary>The right (inner) join key column name.</summary>
    public string? RightKeyColumn { get; }

    /// <summary>The join type.</summary>
    public JoinType JoinType { get; }

    /// <inheritdoc />
    public string Description => JoinType == JoinType.Cross
        ? "NestedLoopJoin: CROSS JOIN"
        : $"NestedLoopJoin: {JoinType} ON {LeftKeyColumn} = {RightKeyColumn}";

    /// <inheritdoc />
    public long EstimatedRows
    {
        get
        {
            var l = _left.EstimatedRows;
            var r = _right.EstimatedRows;
            if (l < 0 || r < 0) return -1;
            return JoinType is JoinType.Left or JoinType.FullOuter ? l : l * r;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _left, _right };

    /// <summary>Initializes a new instance of the <see cref="NestedLoopJoinNode"/> class.</summary>
    /// <param name="left">The outer (driving) input node.</param>
    /// <param name="right">The inner input node (materialized on first iteration).</param>
    /// <param name="leftKeyColumn">Column name on the left side for the equijoin condition. Null for CROSS JOIN.</param>
    /// <param name="rightKeyColumn">Column name on the right side for the equijoin condition. Null for CROSS JOIN.</param>
    /// <param name="joinType">The join type.</param>
    public NestedLoopJoinNode(
        IQueryPlanNode left,
        IQueryPlanNode right,
        string? leftKeyColumn,
        string? rightKeyColumn,
        JoinType joinType = JoinType.Inner)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
        JoinType = joinType;

        if (joinType == JoinType.Cross)
        {
            LeftKeyColumn = leftKeyColumn;
            RightKeyColumn = rightKeyColumn;
        }
        else
        {
            LeftKeyColumn = leftKeyColumn ?? throw new ArgumentNullException(nameof(leftKeyColumn));
            RightKeyColumn = rightKeyColumn ?? throw new ArgumentNullException(nameof(rightKeyColumn));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phase 1: Materialize the inner (right) side
        var innerRows = new List<QueryRow>();
        QueryRow? rightTemplate = null;
        await foreach (var row in _right.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            innerRows.Add(row);
            rightTemplate ??= row;
        }

        if (innerRows.Count == 0 && JoinType is JoinType.Inner or JoinType.Cross)
            yield break;

        // Track which inner rows have been matched (for Right and FullOuter)
        var innerMatched = (JoinType is JoinType.Right or JoinType.FullOuter)
            ? new bool[innerRows.Count]
            : null;

        // Build a left-side template (for Right/FullOuter unmatched right rows)
        QueryRow? leftTemplate = null;

        // Phase 2: For each outer (left) row, scan inner rows
        await foreach (var outerRow in _left.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            leftTemplate ??= outerRow;
            var matched = false;

            for (var i = 0; i < innerRows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var innerRow = innerRows[i];

                if (JoinType == JoinType.Cross || KeysMatchForRow(outerRow, innerRow))
                {
                    matched = true;
                    if (innerMatched != null) innerMatched[i] = true;
                    yield return CombineRows(outerRow, innerRow);
                }
            }

            // LEFT or FULL OUTER: emit unmatched left row with nulls
            if (!matched && JoinType is JoinType.Left or JoinType.FullOuter)
            {
                yield return CombineWithNulls(outerRow, rightTemplate);
            }
        }

        // RIGHT or FULL OUTER: emit unmatched right rows with nulls
        if (innerMatched != null && leftTemplate != null)
        {
            for (var i = 0; i < innerRows.Count; i++)
            {
                if (!innerMatched[i])
                {
                    yield return CombineWithNullsReversed(leftTemplate, innerRows[i]);
                }
            }
        }
    }

    private bool KeysMatchForRow(QueryRow outerRow, QueryRow innerRow)
    {
        var outerKey = GetColumnValue(outerRow, LeftKeyColumn!);
        var innerKey = GetColumnValue(innerRow, RightKeyColumn!);
        return KeysMatch(outerKey, innerKey);
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

    private static bool KeysMatch(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;

        // Numeric comparison
        if (IsNumeric(left) && IsNumeric(right))
        {
            var dl = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
            var dr = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            return dl == dr;
        }

        // Guid comparison
        if (left is Guid gl && right is Guid gr)
        {
            return gl == gr;
        }

        // String comparison (case-insensitive)
        var sl = Convert.ToString(left, CultureInfo.InvariantCulture) ?? "";
        var sr = Convert.ToString(right, CultureInfo.InvariantCulture) ?? "";
        return string.Equals(sl, sr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(object value)
    {
        return value is int or long or short or byte or decimal or double or float;
    }

    /// <summary>
    /// Combines columns from both rows into a single row.
    /// If column names collide, the right-side column is prefixed with its entity name.
    /// </summary>
    internal static QueryRow CombineRows(QueryRow left, QueryRow right)
    {
        var combined = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in left.Values)
        {
            combined[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in right.Values)
        {
            if (combined.ContainsKey(kvp.Key))
            {
                // Disambiguate: prefix with right entity name
                combined[right.EntityLogicalName + "." + kvp.Key] = kvp.Value;
            }
            else
            {
                combined[kvp.Key] = kvp.Value;
            }
        }

        return new QueryRow(combined, left.EntityLogicalName);
    }

    /// <summary>
    /// Combines the left row with null values for the right side (LEFT JOIN with no match).
    /// </summary>
    internal static QueryRow CombineWithNulls(QueryRow left, QueryRow? rightTemplate)
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

    /// <summary>
    /// Combines a null-filled left template with an actual right row.
    /// Used for RIGHT and FULL OUTER JOIN unmatched right-side rows.
    /// </summary>
    internal static QueryRow CombineWithNullsReversed(QueryRow leftTemplate, QueryRow rightRow)
    {
        var combined = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

        // Left side: all nulls
        foreach (var kvp in leftTemplate.Values)
            combined[kvp.Key] = QueryValue.Null;

        // Right side: actual values
        foreach (var kvp in rightRow.Values)
        {
            if (combined.ContainsKey(kvp.Key))
                combined[rightRow.EntityLogicalName + "." + kvp.Key] = kvp.Value;
            else
                combined[kvp.Key] = kvp.Value;
        }

        return new QueryRow(combined, rightRow.EntityLogicalName);
    }
}
