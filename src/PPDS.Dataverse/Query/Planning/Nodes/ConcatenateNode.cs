using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Concatenates rows from multiple child nodes sequentially.
/// Used for UNION ALL — yields all rows from child 1, then child 2, etc.
/// </summary>
public sealed class ConcatenateNode : IQueryPlanNode
{
    /// <summary>The child nodes whose rows are concatenated.</summary>
    public IReadOnlyList<IQueryPlanNode> Inputs { get; }

    /// <inheritdoc />
    public string Description => $"Concatenate: {Inputs.Count} inputs";

    /// <inheritdoc />
    public long EstimatedRows
    {
        get
        {
            long total = 0;
            foreach (var input in Inputs)
            {
                if (input.EstimatedRows < 0) return -1;
                total += input.EstimatedRows;
            }
            return total;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Inputs;

    /// <summary>Initializes a new instance of the <see cref="ConcatenateNode"/> class.</summary>
    public ConcatenateNode(IReadOnlyList<IQueryPlanNode> inputs)
    {
        if (inputs == null) throw new ArgumentNullException(nameof(inputs));
        if (inputs.Count < 2) throw new ArgumentException("ConcatenateNode requires at least two inputs.", nameof(inputs));
        Inputs = inputs;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var input in Inputs)
        {
            await foreach (var row in input.ExecuteAsync(context, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }
    }
}
