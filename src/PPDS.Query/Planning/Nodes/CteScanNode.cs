using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Returns pre-materialized CTE results. Used when the outer query references a CTE
/// that has already been evaluated. The rows are stored in memory and yielded on demand.
/// </summary>
public sealed class CteScanNode : IQueryPlanNode
{
    private readonly List<QueryRow> _materializedRows;

    /// <summary>The name of the CTE this node provides data for.</summary>
    public string CteName { get; }

    /// <inheritdoc />
    public string Description => $"CteScan: {CteName} ({_materializedRows.Count} rows)";

    /// <inheritdoc />
    public long EstimatedRows => _materializedRows.Count;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="CteScanNode"/> class.
    /// </summary>
    /// <param name="cteName">The CTE name.</param>
    /// <param name="materializedRows">The pre-collected CTE result rows.</param>
    public CteScanNode(string cteName, List<QueryRow> materializedRows)
    {
        CteName = cteName ?? throw new ArgumentNullException(nameof(cteName));
        _materializedRows = materializedRows ?? throw new ArgumentNullException(nameof(materializedRows));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _materializedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }

        await System.Threading.Tasks.Task.CompletedTask; // Ensure async signature
    }
}
