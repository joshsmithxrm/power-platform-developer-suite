using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Implements the EXCEPT set operation: returns rows from the left child that do not
/// appear in the right child. Deduplicates output (set semantics).
/// </summary>
/// <remarks>
/// Implementation strategy: materialize the right side into a HashSet, then stream
/// the left side and yield only rows whose composite key does NOT exist in the right set.
/// A second HashSet tracks already-yielded keys for deduplication.
/// </remarks>
public sealed class ExceptNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _left;
    private readonly IQueryPlanNode _right;

    /// <inheritdoc />
    public string Description => "Except";

    /// <inheritdoc />
    public long EstimatedRows
    {
        get
        {
            var l = _left.EstimatedRows;
            if (l < 0) return -1;
            return l; // Conservative: assume no matches removed
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _left, _right };

    /// <summary>
    /// Initializes a new instance of the <see cref="ExceptNode"/> class.
    /// </summary>
    /// <param name="left">The left query expression (rows to keep if not in right).</param>
    /// <param name="right">The right query expression (rows to exclude).</param>
    public ExceptNode(IQueryPlanNode left, IQueryPlanNode right)
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
            rightKeys.Add(IntersectNode.BuildCompositeKey(row));
        }

        // Phase 2: Stream the left side, yield rows NOT in right set (deduplicated)
        var yielded = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var row in _left.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = IntersectNode.BuildCompositeKey(row);
            if (!rightKeys.Contains(key) && yielded.Add(key))
            {
                yield return row;
            }
        }
    }
}
