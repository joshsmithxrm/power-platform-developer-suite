using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Implements recursive CTE execution. Evaluates the anchor query first, then iteratively
/// executes the recursive query using the previous iteration's results until no new rows
/// are produced or the maximum recursion depth is reached.
/// </summary>
/// <remarks>
/// The recursive member's plan is rebuilt/re-executed each iteration with the previous
/// iteration's rows available via a <see cref="CteScanNode"/>. The caller is responsible
/// for providing a factory that builds the recursive plan node given the current iteration's
/// input rows.
/// </remarks>
public sealed class RecursiveCteNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _anchorNode;
    private readonly Func<List<QueryRow>, IQueryPlanNode> _recursiveNodeFactory;

    /// <summary>The CTE name for diagnostic output.</summary>
    public string CteName { get; }

    /// <summary>Maximum recursion depth (default 100, matching SQL Server's MAXRECURSION).</summary>
    public int MaxRecursion { get; }

    /// <inheritdoc />
    public string Description => $"RecursiveCte: {CteName} (max {MaxRecursion})";

    /// <inheritdoc />
    public long EstimatedRows => -1; // Unknown for recursive queries

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _anchorNode };

    /// <summary>
    /// Initializes a new instance of the <see cref="RecursiveCteNode"/> class.
    /// </summary>
    /// <param name="cteName">The CTE name.</param>
    /// <param name="anchorNode">The anchor query plan node (non-recursive part).</param>
    /// <param name="recursiveNodeFactory">
    /// Factory that creates the recursive plan node for each iteration.
    /// Receives the previous iteration's rows and returns a node that produces new rows.
    /// </param>
    /// <param name="maxRecursion">Maximum recursion depth. Default is 100.</param>
    public RecursiveCteNode(
        string cteName,
        IQueryPlanNode anchorNode,
        Func<List<QueryRow>, IQueryPlanNode> recursiveNodeFactory,
        int maxRecursion = 100)
    {
        CteName = cteName ?? throw new ArgumentNullException(nameof(cteName));
        _anchorNode = anchorNode ?? throw new ArgumentNullException(nameof(anchorNode));
        _recursiveNodeFactory = recursiveNodeFactory ?? throw new ArgumentNullException(nameof(recursiveNodeFactory));
        if (maxRecursion < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRecursion), "Max recursion must be non-negative.");
        MaxRecursion = maxRecursion;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Phase 1: Execute anchor query and collect results
        var currentIterationRows = new List<QueryRow>();

        await foreach (var row in _anchorNode.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentIterationRows.Add(row);
            yield return row;
        }

        // Phase 2: Iterate recursive member until no new rows or max depth
        var reachedMaxDepth = false;

        for (var depth = 0; depth < MaxRecursion; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (currentIterationRows.Count == 0)
                break;

            // Build the recursive node with current iteration's rows
            var recursiveNode = _recursiveNodeFactory(currentIterationRows);

            var newRows = new List<QueryRow>();
            await foreach (var row in recursiveNode.ExecuteAsync(context, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                newRows.Add(row);
                yield return row;
            }

            if (newRows.Count == 0)
                break;

            currentIterationRows = newRows;

            // If this was the last allowed iteration and we still got rows, flag overflow
            if (depth == MaxRecursion - 1)
            {
                reachedMaxDepth = true;
            }
        }

        // SQL Server behavior: throw when max recursion is exhausted with rows still being produced
        if (reachedMaxDepth)
        {
            throw new InvalidOperationException(
                $"The maximum recursion {MaxRecursion} has been exhausted before statement completion " +
                $"for CTE '{CteName}'.");
        }
    }
}
