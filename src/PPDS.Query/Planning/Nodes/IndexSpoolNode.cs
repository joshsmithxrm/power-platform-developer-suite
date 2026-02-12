using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Caches inner query results indexed by correlation key values.
/// Used for correlated subqueries to avoid re-executing the inner query
/// when multiple outer rows have the same correlation value.
/// </summary>
public sealed class IndexSpoolNode : IQueryPlanNode
{
    private readonly Func<object, IAsyncEnumerable<QueryRow>> _innerFactory;
    private readonly Dictionary<string, List<QueryRow>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string Description => "IndexSpool (correlated cache)";

    /// <inheritdoc />
    public long EstimatedRows => _cache.Count;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Creates a new IndexSpoolNode.
    /// </summary>
    /// <param name="innerFactory">
    /// Factory that executes the inner query for a given correlation key value.
    /// Called once per unique key value; results are cached for subsequent lookups.
    /// </param>
    public IndexSpoolNode(Func<object, IAsyncEnumerable<QueryRow>> innerFactory)
    {
        _innerFactory = innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));
    }

    /// <summary>
    /// Looks up cached results for the given key, executing the inner factory if not cached.
    /// </summary>
    public async Task<IReadOnlyList<QueryRow>> LookupAsync(
        object keyValue,
        QueryPlanContext context,
        CancellationToken cancellationToken = default)
    {
        var key = keyValue?.ToString() ?? "__null__";

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var rows = new List<QueryRow>();
        await foreach (var row in _innerFactory(keyValue!).WithCancellation(cancellationToken))
        {
            rows.Add(row);
        }

        _cache[key] = rows;
        return rows;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // IndexSpoolNode is typically used via LookupAsync.
        // ExecuteAsync yields all cached rows for plan tree consistency.
        foreach (var entry in _cache.Values)
        {
            foreach (var row in entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }
        await Task.CompletedTask;
    }
}
