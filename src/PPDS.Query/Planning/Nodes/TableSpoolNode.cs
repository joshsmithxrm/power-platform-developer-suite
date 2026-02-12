using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Materializes all rows from a child node into memory, then yields them on demand.
/// Can be read multiple times (unlike streaming nodes). Used for derived tables,
/// remote result materialization, and correlated subquery caching.
/// </summary>
public sealed class TableSpoolNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _source;
    private List<QueryRow>? _materializedRows;

    /// <summary>The materialized rows, available after first execution.</summary>
    public IReadOnlyList<QueryRow> MaterializedRows =>
        _materializedRows ?? (IReadOnlyList<QueryRow>)Array.Empty<QueryRow>();

    /// <inheritdoc />
    public string Description => $"TableSpool: {_source.Description} ({MaterializedRows.Count} rows)";

    /// <inheritdoc />
    public long EstimatedRows => _source.EstimatedRows;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _source };

    /// <summary>
    /// Initializes a new instance wrapping a child node whose rows will be materialized
    /// on first execution.
    /// </summary>
    /// <param name="source">The child node to materialize rows from.</param>
    public TableSpoolNode(IQueryPlanNode source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Creates a TableSpoolNode from pre-materialized rows (no child node).
    /// </summary>
    /// <param name="materializedRows">The pre-collected rows.</param>
    public TableSpoolNode(List<QueryRow> materializedRows)
    {
        _source = null!;
        _materializedRows = materializedRows ?? throw new ArgumentNullException(nameof(materializedRows));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Materialize on first execution
        if (_materializedRows == null)
        {
            _materializedRows = new List<QueryRow>();
            await foreach (var row in _source.ExecuteAsync(context, cancellationToken))
            {
                _materializedRows.Add(row);
            }
        }

        // Yield materialized rows
        foreach (var row in _materializedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
