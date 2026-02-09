using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Computes window functions (ROW_NUMBER, RANK, DENSE_RANK, aggregate OVER) client-side.
/// Materializes all input rows, partitions them, sorts within partitions, and computes
/// window function values. All rows are emitted with additional window columns added.
/// </summary>
public sealed class ClientWindowNode : IQueryPlanNode
{
    /// <summary>The child node that produces input rows.</summary>
    public IQueryPlanNode Input { get; }

    /// <summary>The window function definitions to compute.</summary>
    public IReadOnlyList<WindowDefinition> Windows { get; }

    /// <summary>Maximum rows to materialize before throwing. 0 = unlimited.</summary>
    public int MaxMaterializationRows { get; }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            var funcs = string.Join(", ", Windows.Select(w => $"{w.Expression.FunctionName} AS {w.OutputColumnName}"));
            return $"ClientWindow: [{funcs}]";
        }
    }

    /// <inheritdoc />
    public long EstimatedRows => Input.EstimatedRows;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { Input };

    /// <summary>Initializes a new instance of the <see cref="ClientWindowNode"/> class.</summary>
    public ClientWindowNode(IQueryPlanNode input, IReadOnlyList<WindowDefinition> windows, int maxMaterializationRows = 500_000)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Windows = windows ?? throw new ArgumentNullException(nameof(windows));
        MaxMaterializationRows = maxMaterializationRows;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Step 1: Materialize all input rows (window functions need complete data)
        var allRows = new List<QueryRow>();
        await foreach (var row in Input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            allRows.Add(row);

            if (MaxMaterializationRows > 0 && allRows.Count > MaxMaterializationRows)
            {
                throw new QueryExecutionException(
                    QueryErrorCode.MemoryLimitExceeded,
                    $"Window function materialized {allRows.Count:N0} rows, exceeding the " +
                    $"{MaxMaterializationRows:N0} row limit. Add a WHERE or TOP clause " +
                    "to reduce the result set.");
            }
        }

        if (allRows.Count == 0)
        {
            yield break;
        }

        // Step 2: For each row, compute all window function values
        // We accumulate window column values per row index
        var windowValues = new Dictionary<string, object?>[allRows.Count];
        for (var i = 0; i < allRows.Count; i++)
        {
            windowValues[i] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var windowDef in Windows)
        {
            ComputeWindowFunction(windowDef, allRows, windowValues, context);
        }

        // Step 3: Yield enriched rows with window columns added
        for (var i = 0; i < allRows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalRow = allRows[i];
            var enrichedValues = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

            // Copy all original columns
            foreach (var kvp in originalRow.Values)
            {
                enrichedValues[kvp.Key] = kvp.Value;
            }

            // Add window function columns
            foreach (var kvp in windowValues[i])
            {
                enrichedValues[kvp.Key] = QueryValue.Simple(kvp.Value);
            }

            yield return new QueryRow(enrichedValues, originalRow.EntityLogicalName);
        }
    }

    /// <summary>
    /// Computes a single window function across all rows and stores results in windowValues.
    /// </summary>
    private static void ComputeWindowFunction(
        WindowDefinition windowDef,
        List<QueryRow> allRows,
        Dictionary<string, object?>[] windowValues,
        QueryPlanContext context)
    {
        var expr = windowDef.Expression;
        var columnName = windowDef.OutputColumnName;

        // Build an index mapping: original row index -> row
        // Group rows by partition key
        var partitions = PartitionRows(allRows, expr.PartitionBy, context);

        foreach (var partition in partitions)
        {
            // Sort the partition by ORDER BY items
            var sortedIndices = SortPartition(partition, allRows, expr.OrderBy);

            // Compute function values
            var functionName = expr.FunctionName.ToUpperInvariant();
            switch (functionName)
            {
                case "ROW_NUMBER":
                    ComputeRowNumber(sortedIndices, windowValues, columnName);
                    break;
                case "RANK":
                    ComputeRank(sortedIndices, allRows, expr.OrderBy, windowValues, columnName);
                    break;
                case "DENSE_RANK":
                    ComputeDenseRank(sortedIndices, allRows, expr.OrderBy, windowValues, columnName);
                    break;
                case "SUM":
                case "COUNT":
                case "AVG":
                case "MIN":
                case "MAX":
                    ComputeAggregateWindow(
                        functionName, sortedIndices, allRows, expr, windowValues, columnName, context);
                    break;
                default:
                    throw new NotSupportedException($"Window function '{functionName}' is not supported.");
            }
        }
    }

    /// <summary>
    /// Groups row indices into partitions based on PARTITION BY expressions.
    /// If no PARTITION BY, all rows are in a single partition.
    /// </summary>
    private static List<List<int>> PartitionRows(
        List<QueryRow> allRows,
        IReadOnlyList<ISqlExpression>? partitionBy,
        QueryPlanContext context)
    {
        if (partitionBy == null || partitionBy.Count == 0)
        {
            // All rows in one partition
            var allIndices = new List<int>(allRows.Count);
            for (var i = 0; i < allRows.Count; i++)
            {
                allIndices.Add(i);
            }
            return new List<List<int>> { allIndices };
        }

        // Group by partition key (string representation of evaluated partition expressions)
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var i = 0; i < allRows.Count; i++)
        {
            var key = ComputePartitionKey(allRows[i], partitionBy, context);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new List<int>();
                groups[key] = group;
            }
            group.Add(i);
        }

        return groups.Values.ToList();
    }

    /// <summary>
    /// Computes a string partition key from PARTITION BY expressions for a given row.
    /// </summary>
    private static string ComputePartitionKey(
        QueryRow row,
        IReadOnlyList<ISqlExpression> partitionBy,
        QueryPlanContext context)
    {
        if (partitionBy.Count == 1)
        {
            var val = context.ExpressionEvaluator.Evaluate(partitionBy[0], row.Values);
            return val?.ToString() ?? "\0NULL\0";
        }

        var parts = new string[partitionBy.Count];
        for (var i = 0; i < partitionBy.Count; i++)
        {
            var val = context.ExpressionEvaluator.Evaluate(partitionBy[i], row.Values);
            parts[i] = val?.ToString() ?? "\0NULL\0";
        }
        return string.Join("\0SEP\0", parts);
    }

    /// <summary>
    /// Sorts the row indices within a partition according to ORDER BY items.
    /// Returns the sorted list of original row indices.
    /// </summary>
    private static List<int> SortPartition(
        List<int> partitionIndices,
        List<QueryRow> allRows,
        IReadOnlyList<SqlOrderByItem>? orderBy)
    {
        if (orderBy == null || orderBy.Count == 0)
        {
            // No ORDER BY: preserve original order
            return new List<int>(partitionIndices);
        }

        var sorted = new List<int>(partitionIndices);
        sorted.Sort((a, b) => CompareRowsByOrderBy(allRows[a], allRows[b], orderBy));
        return sorted;
    }

    /// <summary>
    /// Compares two rows by ORDER BY items.
    /// </summary>
    private static int CompareRowsByOrderBy(
        QueryRow rowA,
        QueryRow rowB,
        IReadOnlyList<SqlOrderByItem> orderBy)
    {
        foreach (var item in orderBy)
        {
            var colName = item.Column.GetFullName();
            var valA = GetColumnValue(rowA, colName);
            var valB = GetColumnValue(rowB, colName);

            var cmp = CompareValues(valA, valB);
            if (cmp != 0)
            {
                return item.Direction == SqlSortDirection.Descending ? -cmp : cmp;
            }
        }
        return 0;
    }

    /// <summary>
    /// Gets a column value from a row, with case-insensitive fallback.
    /// </summary>
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

        // Numeric comparison
        if (IsNumeric(a) && IsNumeric(b))
        {
            var da = Convert.ToDecimal(a, CultureInfo.InvariantCulture);
            var db = Convert.ToDecimal(b, CultureInfo.InvariantCulture);
            return da.CompareTo(db);
        }

        // DateTime comparison
        if (a is DateTime dtA && b is DateTime dtB)
        {
            return dtA.CompareTo(dtB);
        }

        // String comparison (case-insensitive)
        var sa = Convert.ToString(a, CultureInfo.InvariantCulture) ?? "";
        var sb = Convert.ToString(b, CultureInfo.InvariantCulture) ?? "";
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(object value)
    {
        return value is int or long or short or byte or decimal or double or float;
    }

    /// <summary>
    /// Checks if two rows have equal ORDER BY values (for RANK/DENSE_RANK tie detection).
    /// </summary>
    private static bool AreOrderByValuesEqual(
        QueryRow rowA,
        QueryRow rowB,
        IReadOnlyList<SqlOrderByItem> orderBy)
    {
        foreach (var item in orderBy)
        {
            var colName = item.Column.GetFullName();
            var valA = GetColumnValue(rowA, colName);
            var valB = GetColumnValue(rowB, colName);

            if (CompareValues(valA, valB) != 0)
            {
                return false;
            }
        }
        return true;
    }

    #region Window Function Implementations

    /// <summary>
    /// ROW_NUMBER: assigns sequential numbers 1, 2, 3, ... within each partition.
    /// </summary>
    private static void ComputeRowNumber(
        List<int> sortedIndices,
        Dictionary<string, object?>[] windowValues,
        string columnName)
    {
        for (var i = 0; i < sortedIndices.Count; i++)
        {
            windowValues[sortedIndices[i]][columnName] = i + 1;
        }
    }

    /// <summary>
    /// RANK: assigns rank with gaps for ties. E.g., 1, 1, 3, 4, 4, 6.
    /// </summary>
    private static void ComputeRank(
        List<int> sortedIndices,
        List<QueryRow> allRows,
        IReadOnlyList<SqlOrderByItem>? orderBy,
        Dictionary<string, object?>[] windowValues,
        string columnName)
    {
        if (sortedIndices.Count == 0) return;

        if (orderBy == null || orderBy.Count == 0)
        {
            // No ORDER BY: all rows get rank 1
            foreach (var idx in sortedIndices)
            {
                windowValues[idx][columnName] = 1;
            }
            return;
        }

        windowValues[sortedIndices[0]][columnName] = 1;

        for (var i = 1; i < sortedIndices.Count; i++)
        {
            var currentRow = allRows[sortedIndices[i]];
            var prevRow = allRows[sortedIndices[i - 1]];

            if (AreOrderByValuesEqual(currentRow, prevRow, orderBy))
            {
                // Same rank as previous row
                windowValues[sortedIndices[i]][columnName] = windowValues[sortedIndices[i - 1]][columnName];
            }
            else
            {
                // Rank = position (1-based)
                windowValues[sortedIndices[i]][columnName] = i + 1;
            }
        }
    }

    /// <summary>
    /// DENSE_RANK: assigns rank without gaps for ties. E.g., 1, 1, 2, 3, 3, 4.
    /// </summary>
    private static void ComputeDenseRank(
        List<int> sortedIndices,
        List<QueryRow> allRows,
        IReadOnlyList<SqlOrderByItem>? orderBy,
        Dictionary<string, object?>[] windowValues,
        string columnName)
    {
        if (sortedIndices.Count == 0) return;

        if (orderBy == null || orderBy.Count == 0)
        {
            // No ORDER BY: all rows get rank 1
            foreach (var idx in sortedIndices)
            {
                windowValues[idx][columnName] = 1;
            }
            return;
        }

        var currentRank = 1;
        windowValues[sortedIndices[0]][columnName] = currentRank;

        for (var i = 1; i < sortedIndices.Count; i++)
        {
            var currentRow = allRows[sortedIndices[i]];
            var prevRow = allRows[sortedIndices[i - 1]];

            if (!AreOrderByValuesEqual(currentRow, prevRow, orderBy))
            {
                currentRank++;
            }

            windowValues[sortedIndices[i]][columnName] = currentRank;
        }
    }

    /// <summary>
    /// Aggregate window functions: SUM, COUNT, AVG, MIN, MAX OVER (PARTITION BY ...).
    /// Computes the aggregate over the entire partition (no frame support).
    /// </summary>
    private static void ComputeAggregateWindow(
        string functionName,
        List<int> sortedIndices,
        List<QueryRow> allRows,
        SqlWindowExpression expr,
        Dictionary<string, object?>[] windowValues,
        string columnName,
        QueryPlanContext context)
    {
        // Gather values for the aggregate
        object? aggregateResult;

        switch (functionName)
        {
            case "COUNT":
            {
                if (expr.IsCountStar)
                {
                    aggregateResult = sortedIndices.Count;
                }
                else
                {
                    var count = 0;
                    foreach (var idx in sortedIndices)
                    {
                        var val = expr.Operand != null
                            ? context.ExpressionEvaluator.Evaluate(expr.Operand, allRows[idx].Values)
                            : null;
                        if (val != null) count++;
                    }
                    aggregateResult = count;
                }
                break;
            }
            case "SUM":
            {
                decimal sum = 0;
                var hasValue = false;
                foreach (var idx in sortedIndices)
                {
                    var val = expr.Operand != null
                        ? context.ExpressionEvaluator.Evaluate(expr.Operand, allRows[idx].Values)
                        : null;
                    if (val != null)
                    {
                        sum += Convert.ToDecimal(val, CultureInfo.InvariantCulture);
                        hasValue = true;
                    }
                }
                aggregateResult = hasValue ? sum : null;
                break;
            }
            case "AVG":
            {
                decimal sum = 0;
                var count = 0;
                foreach (var idx in sortedIndices)
                {
                    var val = expr.Operand != null
                        ? context.ExpressionEvaluator.Evaluate(expr.Operand, allRows[idx].Values)
                        : null;
                    if (val != null)
                    {
                        sum += Convert.ToDecimal(val, CultureInfo.InvariantCulture);
                        count++;
                    }
                }
                aggregateResult = count > 0 ? sum / count : null;
                break;
            }
            case "MIN":
            {
                object? minVal = null;
                foreach (var idx in sortedIndices)
                {
                    var val = expr.Operand != null
                        ? context.ExpressionEvaluator.Evaluate(expr.Operand, allRows[idx].Values)
                        : null;
                    if (val != null)
                    {
                        if (minVal == null || CompareValues(val, minVal) < 0)
                        {
                            minVal = val;
                        }
                    }
                }
                aggregateResult = minVal;
                break;
            }
            case "MAX":
            {
                object? maxVal = null;
                foreach (var idx in sortedIndices)
                {
                    var val = expr.Operand != null
                        ? context.ExpressionEvaluator.Evaluate(expr.Operand, allRows[idx].Values)
                        : null;
                    if (val != null)
                    {
                        if (maxVal == null || CompareValues(val, maxVal) > 0)
                        {
                            maxVal = val;
                        }
                    }
                }
                aggregateResult = maxVal;
                break;
            }
            default:
                throw new NotSupportedException($"Aggregate window function '{functionName}' is not supported.");
        }

        // Assign the same aggregate value to all rows in the partition
        foreach (var idx in sortedIndices)
        {
            windowValues[idx][columnName] = aggregateResult;
        }
    }

    #endregion
}

/// <summary>
/// Defines a single window function to compute within a ClientWindowNode.
/// </summary>
public sealed class WindowDefinition
{
    /// <summary>The output column name for this window function's result.</summary>
    public string OutputColumnName { get; }

    /// <summary>The window function expression from the AST.</summary>
    public SqlWindowExpression Expression { get; }

    /// <summary>Initializes a new instance of the <see cref="WindowDefinition"/> class.</summary>
    public WindowDefinition(string outputColumnName, SqlWindowExpression expression)
    {
        OutputColumnName = outputColumnName ?? throw new ArgumentNullException(nameof(outputColumnName));
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }
}
