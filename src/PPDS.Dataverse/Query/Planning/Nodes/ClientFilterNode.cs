using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Filters rows by evaluating a condition client-side.
/// Used for HAVING clauses and expressions that can't be pushed to FetchXML.
/// </summary>
public sealed class ClientFilterNode : IQueryPlanNode
{
    /// <summary>The child node that produces input rows.</summary>
    public IQueryPlanNode Input { get; }

    /// <summary>The compiled predicate to evaluate against each row.</summary>
    public CompiledPredicate Predicate { get; }

    /// <summary>Human-readable description of the predicate for EXPLAIN output.</summary>
    public string PredicateDescription { get; }

    /// <inheritdoc />
    public string Description => $"ClientFilter: {PredicateDescription}";

    /// <inheritdoc />
    public long EstimatedRows => Input.EstimatedRows; // Conservative estimate

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { Input };

    /// <summary>Initializes a new instance of the <see cref="ClientFilterNode"/> class.</summary>
    public ClientFilterNode(IQueryPlanNode input, CompiledPredicate predicate, string predicateDescription)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        PredicateDescription = predicateDescription ?? throw new ArgumentNullException(nameof(predicateDescription));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in Input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Predicate(row.Values))
            {
                yield return row;
            }
        }
    }
}
