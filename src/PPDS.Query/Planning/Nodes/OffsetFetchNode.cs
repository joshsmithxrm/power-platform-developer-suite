using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Applies OFFSET/FETCH paging to a child node's output.
/// Skips the first <see cref="Offset"/> rows and then yields at most <see cref="Fetch"/> rows.
/// </summary>
public sealed class OffsetFetchNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _input;

    /// <summary>Number of rows to skip (OFFSET n ROWS).</summary>
    public int Offset { get; }

    /// <summary>Number of rows to take after skipping (FETCH NEXT n ROWS ONLY). -1 means unlimited.</summary>
    public int Fetch { get; }

    /// <inheritdoc />
    public string Description => Fetch >= 0
        ? $"OffsetFetch: OFFSET {Offset} FETCH {Fetch}"
        : $"OffsetFetch: OFFSET {Offset}";

    /// <inheritdoc />
    public long EstimatedRows
    {
        get
        {
            var inputRows = _input.EstimatedRows;
            if (inputRows < 0) return -1;
            var afterSkip = Math.Max(0, inputRows - Offset);
            return Fetch >= 0 ? Math.Min(afterSkip, Fetch) : afterSkip;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _input };

    /// <summary>
    /// Initializes a new instance of the <see cref="OffsetFetchNode"/> class.
    /// </summary>
    /// <param name="input">The child node producing input rows (should already be ordered).</param>
    /// <param name="offset">Number of rows to skip. Must be non-negative.</param>
    /// <param name="fetch">Number of rows to take after skipping. Use -1 for unlimited.</param>
    public OffsetFetchNode(IQueryPlanNode input, int offset, int fetch = -1)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");
        Offset = offset;
        Fetch = fetch;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var skipped = 0;
        var taken = 0;

        await foreach (var row in _input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip phase
            if (skipped < Offset)
            {
                skipped++;
                continue;
            }

            // Take phase
            if (Fetch >= 0 && taken >= Fetch)
            {
                yield break;
            }

            taken++;
            yield return row;
        }
    }
}
