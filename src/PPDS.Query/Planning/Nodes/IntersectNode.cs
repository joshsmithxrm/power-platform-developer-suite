using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Implements the INTERSECT set operation: returns only rows that appear in both
/// the left and right child nodes. Deduplicates output (set semantics).
/// </summary>
/// <remarks>
/// Implementation strategy: materialize the right side into a HashSet, then stream
/// the left side and yield only rows whose composite key exists in the right set.
/// A second HashSet tracks already-yielded keys for deduplication.
/// </remarks>
public sealed class IntersectNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _left;
    private readonly IQueryPlanNode _right;

    /// <inheritdoc />
    public string Description => "Intersect";

    /// <inheritdoc />
    public long EstimatedRows
    {
        get
        {
            var l = _left.EstimatedRows;
            var r = _right.EstimatedRows;
            if (l < 0 || r < 0) return -1;
            return Math.Min(l, r);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _left, _right };

    /// <summary>
    /// Initializes a new instance of the <see cref="IntersectNode"/> class.
    /// </summary>
    /// <param name="left">The left query expression (rows to check).</param>
    /// <param name="right">The right query expression (rows to match against).</param>
    public IntersectNode(IQueryPlanNode left, IQueryPlanNode right)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phase 1: Materialize the right side into a hash set
        var rightKeys = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var row in _right.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rightKeys.Add(BuildCompositeKey(row));
        }

        // Phase 2: Stream the left side, yield rows present in right set (deduplicated)
        var yielded = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var row in _left.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildCompositeKey(row);
            if (rightKeys.Contains(key) && yielded.Add(key))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Builds a composite key string from all column values in the row.
    /// Uses separators unlikely to appear in data to avoid collisions.
    /// </summary>
    internal static string BuildCompositeKey(QueryRow row)
    {
        var sb = new StringBuilder();
        var first = true;

        foreach (var kvp in row.Values.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append('\x1F'); // Unit Separator
            first = false;

            sb.Append(kvp.Key);
            sb.Append('\x1E'); // Record Separator
            sb.Append(kvp.Value.Value?.ToString() ?? "\x00");
        }

        return sb.ToString();
    }
}
