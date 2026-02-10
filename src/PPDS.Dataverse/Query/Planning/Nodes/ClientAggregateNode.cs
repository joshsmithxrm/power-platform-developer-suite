using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Computes aggregate functions client-side from raw (non-aggregated) input rows.
/// Used for aggregates that FetchXML doesn't support natively (STDEV, VAR).
/// Collects all input rows, groups them if needed, and computes the aggregates.
/// </summary>
public sealed class ClientAggregateNode : IQueryPlanNode
{
    /// <summary>The child node providing raw data rows.</summary>
    public IQueryPlanNode Input { get; }

    /// <summary>The aggregate columns to compute.</summary>
    public IReadOnlyList<ClientAggregateColumn> AggregateColumns { get; }

    /// <summary>Column names to group by.</summary>
    public IReadOnlyList<string> GroupByColumns { get; }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            var funcs = string.Join(", ", AggregateColumns.Select(a => $"{a.Function}({a.SourceColumn})"));
            return $"ClientAggregate: [{funcs}]" +
                   (GroupByColumns.Count > 0 ? $" grouped by [{string.Join(", ", GroupByColumns)}]" : "");
        }
    }

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { Input };

    /// <summary>Initializes a new instance of the <see cref="ClientAggregateNode"/> class.</summary>
    public ClientAggregateNode(
        IQueryPlanNode input,
        IReadOnlyList<ClientAggregateColumn> aggregateColumns,
        IReadOnlyList<string>? groupByColumns = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        AggregateColumns = aggregateColumns ?? throw new ArgumentNullException(nameof(aggregateColumns));
        GroupByColumns = groupByColumns ?? Array.Empty<string>();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Collect all input rows
        var groups = new Dictionary<string, List<QueryRow>>(StringComparer.Ordinal);

        await foreach (var row in Input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var groupKey = BuildGroupKey(row);
            if (!groups.TryGetValue(groupKey, out var list))
            {
                list = new List<QueryRow>();
                groups[groupKey] = list;
            }
            list.Add(row);
        }

        // Compute aggregates for each group
        foreach (var kvp in groups)
        {
            var groupRows = kvp.Value;
            var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

            // Add group-by values from first row
            if (groupRows.Count > 0)
            {
                foreach (var col in GroupByColumns)
                {
                    if (groupRows[0].Values.TryGetValue(col, out var qv))
                    {
                        values[col] = qv;
                    }
                }
            }

            // Compute each aggregate
            foreach (var aggCol in AggregateColumns)
            {
                var result = ComputeAggregate(aggCol, groupRows);
                values[aggCol.OutputAlias] = QueryValue.Simple(result);
            }

            var entityName = groupRows.Count > 0 ? groupRows[0].EntityLogicalName : "aggregate";
            yield return new QueryRow(values, entityName);
        }
    }

    private string BuildGroupKey(QueryRow row)
    {
        if (GroupByColumns.Count == 0) return "";

        var parts = new string[GroupByColumns.Count];
        for (var i = 0; i < GroupByColumns.Count; i++)
        {
            var col = GroupByColumns[i];
            parts[i] = row.Values.TryGetValue(col, out var qv)
                ? qv.Value?.ToString() ?? "\x00"
                : "\x00";
        }
        return string.Join("\x1F", parts);
    }

    private static object? ComputeAggregate(ClientAggregateColumn aggCol, List<QueryRow> rows)
    {
        var numericValues = new List<decimal>();
        foreach (var row in rows)
        {
            if (row.Values.TryGetValue(aggCol.SourceColumn, out var qv) && qv.Value != null)
            {
                try
                {
                    numericValues.Add(Convert.ToDecimal(qv.Value, CultureInfo.InvariantCulture));
                }
                catch (Exception)
                {
                    // Skip non-numeric values
                }
            }
        }

        return aggCol.Function switch
        {
            ClientAggregateFunction.Stdev => ComputeStdev(numericValues),
            ClientAggregateFunction.Var => ComputeVariance(numericValues),
            ClientAggregateFunction.Count => (long)numericValues.Count,
            ClientAggregateFunction.Sum => numericValues.Count > 0 ? numericValues.Sum() : (object?)null,
            ClientAggregateFunction.Avg => numericValues.Count > 0 ? numericValues.Average() : (object?)null,
            ClientAggregateFunction.Min => numericValues.Count > 0 ? numericValues.Min() : (object?)null,
            ClientAggregateFunction.Max => numericValues.Count > 0 ? numericValues.Max() : (object?)null,
            _ => null
        };
    }

    /// <summary>
    /// Computes sample standard deviation: sqrt(sum((x - mean)^2) / (n - 1)).
    /// </summary>
    private static object? ComputeStdev(List<decimal> values)
    {
        if (values.Count == 0) return null;
        if (values.Count == 1) return 0m;

        var variance = ComputeVarianceValue(values);
        return (decimal)Math.Sqrt((double)variance);
    }

    /// <summary>
    /// Computes sample variance: sum((x - mean)^2) / (n - 1).
    /// </summary>
    private static object? ComputeVariance(List<decimal> values)
    {
        if (values.Count == 0) return null;
        if (values.Count == 1) return 0m;

        return ComputeVarianceValue(values);
    }

    private static decimal ComputeVarianceValue(List<decimal> values)
    {
        var n = values.Count;
        var mean = values.Sum() / n;
        var sumOfSquaredDiffs = values.Sum(v => (v - mean) * (v - mean));
        return sumOfSquaredDiffs / (n - 1);
    }
}

/// <summary>
/// Describes an aggregate column for client-side computation.
/// </summary>
public sealed class ClientAggregateColumn
{
    /// <summary>The source column name to aggregate.</summary>
    public string SourceColumn { get; }

    /// <summary>The output alias for the aggregate result.</summary>
    public string OutputAlias { get; }

    /// <summary>The aggregate function to compute.</summary>
    public ClientAggregateFunction Function { get; }

    /// <summary>Initializes a new instance of the <see cref="ClientAggregateColumn"/> class.</summary>
    public ClientAggregateColumn(string sourceColumn, string outputAlias, ClientAggregateFunction function)
    {
        SourceColumn = sourceColumn ?? throw new ArgumentNullException(nameof(sourceColumn));
        OutputAlias = outputAlias ?? throw new ArgumentNullException(nameof(outputAlias));
        Function = function;
    }
}

/// <summary>
/// Aggregate functions supported by ClientAggregateNode.
/// </summary>
public enum ClientAggregateFunction
{
    /// <summary>COUNT aggregate.</summary>
    Count,
    /// <summary>SUM aggregate.</summary>
    Sum,
    /// <summary>AVG aggregate.</summary>
    Avg,
    /// <summary>MIN aggregate.</summary>
    Min,
    /// <summary>MAX aggregate.</summary>
    Max,
    /// <summary>STDEV (sample standard deviation) aggregate.</summary>
    Stdev,
    /// <summary>VAR (sample variance) aggregate.</summary>
    Var
}
