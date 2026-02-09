using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Deduplicates rows from a single input using a composite key hash set.
/// Used for UNION (without ALL) to remove duplicate rows across branches.
/// </summary>
public sealed class DistinctNode : IQueryPlanNode
{
    /// <summary>The child node that produces input rows.</summary>
    public IQueryPlanNode Input { get; }

    /// <inheritdoc />
    public string Description => "Distinct";

    /// <inheritdoc />
    public long EstimatedRows => Input.EstimatedRows; // Conservative: assume no duplicates

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { Input };

    /// <summary>Initializes a new instance of the <see cref="DistinctNode"/> class.</summary>
    public DistinctNode(IQueryPlanNode input)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var row in Input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildCompositeKey(row);
            if (seen.Add(key))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Builds a composite key string from all column values in the row.
    /// Uses a separator unlikely to appear in data to avoid collisions.
    /// </summary>
    private static string BuildCompositeKey(QueryRow row)
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
