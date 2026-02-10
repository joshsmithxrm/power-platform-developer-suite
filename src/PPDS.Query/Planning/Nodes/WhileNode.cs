using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Implements WHILE loop execution. Evaluates the condition before each iteration
/// and executes the body node while the condition is true. Yields any rows produced
/// by the body across all iterations.
/// </summary>
public sealed class WhileNode : IQueryPlanNode
{
    private readonly CompiledPredicate _condition;
    private readonly IQueryPlanNode _body;

    /// <summary>Maximum number of iterations to prevent infinite loops.</summary>
    public int MaxIterations { get; }

    /// <inheritdoc />
    public string Description => $"While (max {MaxIterations} iterations)";

    /// <inheritdoc />
    public long EstimatedRows => -1; // Unknown for loops

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _body };

    /// <summary>
    /// Initializes a new instance of the <see cref="WhileNode"/> class.
    /// </summary>
    /// <param name="condition">The compiled predicate to evaluate before each iteration.</param>
    /// <param name="body">The body node to execute each iteration.</param>
    /// <param name="maxIterations">Maximum iterations to prevent infinite loops. Default is 10000.</param>
    public WhileNode(CompiledPredicate condition, IQueryPlanNode body, int maxIterations = 10000)
    {
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        if (maxIterations < 0)
            throw new ArgumentOutOfRangeException(nameof(maxIterations), "Max iterations must be non-negative.");
        MaxIterations = maxIterations;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Evaluate the loop condition against an empty row context
            var emptyValues = new Dictionary<string, Dataverse.Query.QueryValue>();
            if (!_condition(emptyValues))
            {
                yield break;
            }

            // Execute the body
            await foreach (var row in _body.ExecuteAsync(context, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }

        throw new InvalidOperationException(
            $"WHILE loop exceeded maximum iteration count of {MaxIterations}.");
    }
}
