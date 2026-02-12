using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Executes a subquery and returns a single scalar value.
/// Asserts at most one row is returned. If 0 rows, returns NULL. If >1 row, throws.
/// Used for scalar subqueries in SELECT lists and WHERE predicates.
/// </summary>
public sealed class ScalarSubqueryNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _inner;

    /// <inheritdoc />
    public string Description => $"ScalarSubquery: ({_inner.Description})";

    /// <inheritdoc />
    public long EstimatedRows => 1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _inner };

    public ScalarSubqueryNode(IQueryPlanNode inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Executes the subquery and returns the single scalar value.</summary>
    public async Task<object?> ExecuteScalarAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken = default)
    {
        QueryRow? firstRow = null;
        var rowCount = 0;

        await foreach (var row in _inner.ExecuteAsync(context, cancellationToken))
        {
            rowCount++;
            if (rowCount == 1)
                firstRow = row;
            else
                throw new QueryExecutionException(
                    QueryErrorCode.SubqueryMultipleRows,
                    "Scalar subquery returned more than one row.");
        }

        if (firstRow == null)
            return null;

        // Return the first (and only) column value
        var firstValue = firstRow.Values.Values.FirstOrDefault();
        return firstValue?.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // ScalarSubqueryNode is typically used via ExecuteScalarAsync,
        // but supports the standard interface for plan tree consistency
        await foreach (var row in _inner.ExecuteAsync(context, cancellationToken))
        {
            yield return row;
        }
    }
}
