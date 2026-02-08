using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Merges partial aggregate results from parallel partitions.
/// Handles COUNT (sum), SUM (sum), AVG (weighted), MIN, MAX.
///
/// NOTE: COUNT(DISTINCT) cannot be parallel-partitioned because summing
/// partial distinct counts would double-count values that appear in
/// multiple partitions. COUNT(DISTINCT) must be handled by a single
/// aggregate query or a different strategy (e.g., client-side DISTINCT
/// followed by COUNT).
/// </summary>
public sealed class MergeAggregateNode : IQueryPlanNode
{
    public IQueryPlanNode Input { get; }
    public IReadOnlyList<MergeAggregateColumn> AggregateColumns { get; }
    public IReadOnlyList<string> GroupByColumns { get; }

    public string Description
    {
        get
        {
            var funcs = string.Join(", ", AggregateColumns.Select(a => $"{a.Function}({a.Alias})"));
            return $"MergeAggregate: [{funcs}]" +
                   (GroupByColumns.Count > 0 ? $" grouped by [{string.Join(", ", GroupByColumns)}]" : "");
        }
    }

    public long EstimatedRows => -1;
    public IReadOnlyList<IQueryPlanNode> Children => new[] { Input };

    public MergeAggregateNode(
        IQueryPlanNode input,
        IReadOnlyList<MergeAggregateColumn> aggregateColumns,
        IReadOnlyList<string>? groupByColumns = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        AggregateColumns = aggregateColumns ?? throw new ArgumentNullException(nameof(aggregateColumns));
        GroupByColumns = groupByColumns ?? Array.Empty<string>();
    }

    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Collect all input rows (partial aggregates from partitions)
        var groups = new Dictionary<string, AggregateAccumulator>(StringComparer.Ordinal);

        await foreach (var row in Input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var groupKey = BuildGroupKey(row);

            if (!groups.TryGetValue(groupKey, out var accumulator))
            {
                accumulator = new AggregateAccumulator(AggregateColumns, GroupByColumns, row);
                groups[groupKey] = accumulator;
            }

            accumulator.Merge(row);
        }

        // Emit merged results
        foreach (var kvp in groups)
        {
            yield return kvp.Value.ToRow();
        }
    }

    private string BuildGroupKey(QueryRow row)
    {
        if (GroupByColumns.Count == 0)
        {
            return "";
        }

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

    private sealed class AggregateAccumulator
    {
        private readonly IReadOnlyList<MergeAggregateColumn> _columns;
        private readonly IReadOnlyList<string> _groupByColumns;
        private readonly Dictionary<string, object?> _groupByValues;
        private readonly Dictionary<string, decimal> _sums;
        private readonly Dictionary<string, long> _counts;
        private readonly Dictionary<string, decimal> _mins;
        private readonly Dictionary<string, decimal> _maxes;
        private readonly string _entityLogicalName;

        public AggregateAccumulator(
            IReadOnlyList<MergeAggregateColumn> columns,
            IReadOnlyList<string> groupByColumns,
            QueryRow firstRow)
        {
            _columns = columns;
            _groupByColumns = groupByColumns;
            _entityLogicalName = firstRow.EntityLogicalName;
            _groupByValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _sums = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            _counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            _mins = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            _maxes = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            // Capture group-by values from first row
            foreach (var col in groupByColumns)
            {
                if (firstRow.Values.TryGetValue(col, out var qv))
                {
                    _groupByValues[col] = qv.Value;
                }
            }

            // Initialize accumulators
            foreach (var col in columns)
            {
                _sums[col.Alias] = 0;
                _counts[col.Alias] = 0;
                _mins[col.Alias] = decimal.MaxValue;
                _maxes[col.Alias] = decimal.MinValue;
            }
        }

        public void Merge(QueryRow row)
        {
            foreach (var col in _columns)
            {
                if (!row.Values.TryGetValue(col.Alias, out var qv) || qv.Value == null)
                    continue;

                var value = Convert.ToDecimal(qv.Value, CultureInfo.InvariantCulture);

                switch (col.Function)
                {
                    case AggregateFunction.Count:
                        _counts[col.Alias] += (long)value;
                        break;
                    case AggregateFunction.Sum:
                        _sums[col.Alias] += value;
                        break;
                    case AggregateFunction.Avg:
                        // For AVG, we need both sum and count to properly merge.
                        // The partial result is the average, but we need the count
                        // to compute a weighted average across partitions.
                        if (col.CountAlias != null && row.Values.TryGetValue(col.CountAlias, out var countQv) && countQv.Value != null)
                        {
                            var partialCount = Convert.ToInt64(countQv.Value, CultureInfo.InvariantCulture);
                            _counts[col.Alias] += partialCount;
                            _sums[col.Alias] += value * partialCount;
                        }
                        else
                        {
                            // Fallback: treat as sum with count=1
                            _counts[col.Alias]++;
                            _sums[col.Alias] += value;
                        }
                        break;
                    case AggregateFunction.Min:
                        if (value < _mins[col.Alias])
                            _mins[col.Alias] = value;
                        break;
                    case AggregateFunction.Max:
                        if (value > _maxes[col.Alias])
                            _maxes[col.Alias] = value;
                        break;
                }
            }
        }

        public QueryRow ToRow()
        {
            var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

            // Add group-by values
            foreach (var kvp in _groupByValues)
            {
                values[kvp.Key] = QueryValue.Simple(kvp.Value);
            }

            // Add aggregate results
            foreach (var col in _columns)
            {
                object? result = col.Function switch
                {
                    AggregateFunction.Count => _counts[col.Alias],
                    AggregateFunction.Sum => _sums[col.Alias],
                    AggregateFunction.Avg => _counts[col.Alias] > 0
                        ? _sums[col.Alias] / _counts[col.Alias]
                        : 0m,
                    AggregateFunction.Min => _mins[col.Alias] == decimal.MaxValue ? null : _mins[col.Alias],
                    AggregateFunction.Max => _maxes[col.Alias] == decimal.MinValue ? null : _maxes[col.Alias],
                    _ => null
                };
                values[col.Alias] = QueryValue.Simple(result);
            }

            return new QueryRow(values, _entityLogicalName);
        }
    }
}

/// <summary>
/// Describes an aggregate column to be merged.
/// </summary>
public sealed class MergeAggregateColumn
{
    public string Alias { get; }
    public AggregateFunction Function { get; }
    /// <summary>For AVG merging, the alias of the companion COUNT column. Null if not tracking.</summary>
    public string? CountAlias { get; }

    public MergeAggregateColumn(string alias, AggregateFunction function, string? countAlias = null)
    {
        Alias = alias ?? throw new ArgumentNullException(nameof(alias));
        Function = function;
        CountAlias = countAlias;
    }
}

/// <summary>
/// Aggregate functions supported by MergeAggregateNode.
/// Note: COUNT(DISTINCT) is NOT supported for parallel partitioning because
/// summing partial distinct counts would double-count values appearing in
/// multiple partitions. Use a single-partition approach for COUNT(DISTINCT).
/// </summary>
public enum AggregateFunction
{
    Count,
    Sum,
    Avg,
    Min,
    Max
}
