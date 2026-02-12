using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Implements semi-join and anti-semi-join for IN (subquery), EXISTS, NOT IN, and NOT EXISTS.
/// Materializes the inner side into a HashSet keyed by the join column, then probes per outer row.
/// Semi-join: yields outer row when key IS found in inner set.
/// Anti-semi-join: yields outer row when key is NOT found in inner set.
/// </summary>
public sealed class HashSemiJoinNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _outer;
    private readonly IQueryPlanNode _inner;
    private readonly string _outerKeyColumn;
    private readonly string _innerKeyColumn;
    private readonly bool _antiSemiJoin;

    public string Description => _antiSemiJoin
        ? $"HashAntiSemiJoin: {_outerKeyColumn} NOT IN ({_inner.Description})"
        : $"HashSemiJoin: {_outerKeyColumn} IN ({_inner.Description})";

    public long EstimatedRows => _outer.EstimatedRows;

    public IReadOnlyList<IQueryPlanNode> Children => new[] { _outer, _inner };

    public HashSemiJoinNode(
        IQueryPlanNode outer,
        IQueryPlanNode inner,
        string outerKeyColumn,
        string innerKeyColumn,
        bool antiSemiJoin)
    {
        _outer = outer ?? throw new ArgumentNullException(nameof(outer));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _outerKeyColumn = outerKeyColumn ?? throw new ArgumentNullException(nameof(outerKeyColumn));
        _innerKeyColumn = innerKeyColumn ?? throw new ArgumentNullException(nameof(innerKeyColumn));
        _antiSemiJoin = antiSemiJoin;
    }

    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phase 1: Build hash set from inner side
        var innerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool innerHasNull = false;
        await foreach (var innerRow in _inner.ExecuteAsync(context, cancellationToken))
        {
            if (innerRow.Values.TryGetValue(_innerKeyColumn, out var val) && val.Value != null)
            {
                innerKeys.Add(val.Value.ToString()!);
            }
            else
            {
                innerHasNull = true;
            }
        }

        // NOT IN with NULL in inner: SQL standard says return NO rows
        if (_antiSemiJoin && innerHasNull)
            yield break;

        // Phase 2: Probe outer side
        await foreach (var outerRow in _outer.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outerKeyValue = outerRow.Values.TryGetValue(_outerKeyColumn, out var outerVal)
                ? outerVal.Value?.ToString()
                : null;

            // NULL keys never match (SQL NULL semantics)
            if (outerKeyValue == null)
                continue;

            var found = innerKeys.Contains(outerKeyValue);

            if (_antiSemiJoin ? !found : found)
            {
                yield return outerRow;
            }
        }
    }
}
