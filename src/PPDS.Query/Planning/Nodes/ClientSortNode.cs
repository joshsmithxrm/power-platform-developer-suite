using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Materializes all input rows, sorts them by the specified ORDER BY items,
/// then yields the sorted results. Used for client-side joins where FetchXML
/// cannot provide server-side sorting.
/// </summary>
public sealed class ClientSortNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _input;

    /// <summary>The ORDER BY items to sort by.</summary>
    public IReadOnlyList<CompiledOrderByItem> OrderByItems { get; }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            var parts = new List<string>(OrderByItems.Count);
            foreach (var item in OrderByItems)
            {
                parts.Add(item.Descending ? $"{item.ColumnName} DESC" : item.ColumnName);
            }
            return $"Sort: [{string.Join(", ", parts)}]";
        }
    }

    /// <inheritdoc />
    public long EstimatedRows => _input.EstimatedRows;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _input };

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientSortNode"/> class.
    /// </summary>
    /// <param name="input">The child node producing unsorted input rows.</param>
    /// <param name="orderByItems">The ORDER BY items specifying sort columns and direction.</param>
    public ClientSortNode(IQueryPlanNode input, IReadOnlyList<CompiledOrderByItem> orderByItems)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        OrderByItems = orderByItems ?? throw new ArgumentNullException(nameof(orderByItems));
        if (orderByItems.Count == 0)
            throw new ArgumentException("At least one ORDER BY item is required.", nameof(orderByItems));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Materialize all rows (sorting requires full dataset)
        var rows = new List<QueryRow>();
        await foreach (var row in _input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(row);
        }

        // Sort using stable sort (preserves original order for equal elements)
        rows.Sort((a, b) => CompareRows(a, b, OrderByItems));

        // Yield sorted rows
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    /// <summary>
    /// Compares two rows by ORDER BY items.
    /// </summary>
    private static int CompareRows(QueryRow a, QueryRow b, IReadOnlyList<CompiledOrderByItem> orderBy)
    {
        foreach (var item in orderBy)
        {
            var colName = item.ColumnName;
            var valA = GetColumnValue(a, colName);
            var valB = GetColumnValue(b, colName);

            var cmp = CompareValues(valA, valB);
            if (cmp != 0)
            {
                return item.Descending ? -cmp : cmp;
            }
        }
        return 0;
    }

    private static object? GetColumnValue(QueryRow row, string columnName)
    {
        if (row.Values.TryGetValue(columnName, out var qv))
        {
            return qv.Value;
        }

        foreach (var kvp in row.Values)
        {
            if (string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Compares two values for ordering. Nulls sort last.
    /// </summary>
    private static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;  // nulls last
        if (b is null) return -1;

        if (IsNumeric(a) && IsNumeric(b))
        {
            var da = Convert.ToDecimal(a, CultureInfo.InvariantCulture);
            var db = Convert.ToDecimal(b, CultureInfo.InvariantCulture);
            return da.CompareTo(db);
        }

        if (a is DateTime dtA && b is DateTime dtB)
        {
            return dtA.CompareTo(dtB);
        }

        var sa = Convert.ToString(a, CultureInfo.InvariantCulture) ?? "";
        var sb = Convert.ToString(b, CultureInfo.InvariantCulture) ?? "";
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(object value)
    {
        return value is int or long or short or byte or decimal or double or float;
    }
}
