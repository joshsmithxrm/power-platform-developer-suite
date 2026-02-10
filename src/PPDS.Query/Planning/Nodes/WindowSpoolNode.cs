using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Specifies the window frame boundaries for window function computation.
/// </summary>
public sealed class WindowFrameSpec
{
    /// <summary>Frame start type.</summary>
    public WindowFrameBound Start { get; }

    /// <summary>Frame end type.</summary>
    public WindowFrameBound End { get; }

    /// <summary>Creates a frame spec from start and end bounds.</summary>
    public WindowFrameSpec(WindowFrameBound start, WindowFrameBound end)
    {
        Start = start ?? throw new ArgumentNullException(nameof(start));
        End = end ?? throw new ArgumentNullException(nameof(end));
    }

    /// <summary>ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW (running totals).</summary>
    public static WindowFrameSpec RunningTotal =>
        new(WindowFrameBound.UnboundedPreceding(), WindowFrameBound.CurrentRow());

    /// <summary>ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING (full partition).</summary>
    public static WindowFrameSpec FullPartition =>
        new(WindowFrameBound.UnboundedPreceding(), WindowFrameBound.UnboundedFollowing());

    /// <summary>ROWS BETWEEN N PRECEDING AND N FOLLOWING (sliding window).</summary>
    public static WindowFrameSpec Sliding(int preceding, int following) =>
        new(WindowFrameBound.Preceding(preceding), WindowFrameBound.Following(following));
}

/// <summary>
/// Represents a single boundary point in a window frame.
/// </summary>
public sealed class WindowFrameBound
{
    /// <summary>The bound type.</summary>
    public WindowFrameBoundType BoundType { get; }

    /// <summary>Offset value for PRECEDING/FOLLOWING. 0 for UNBOUNDED and CURRENT ROW.</summary>
    public int Offset { get; }

    private WindowFrameBound(WindowFrameBoundType boundType, int offset = 0)
    {
        BoundType = boundType;
        Offset = offset;
    }

    /// <summary>UNBOUNDED PRECEDING.</summary>
    public static WindowFrameBound UnboundedPreceding() => new(WindowFrameBoundType.UnboundedPreceding);

    /// <summary>UNBOUNDED FOLLOWING.</summary>
    public static WindowFrameBound UnboundedFollowing() => new(WindowFrameBoundType.UnboundedFollowing);

    /// <summary>CURRENT ROW.</summary>
    public static WindowFrameBound CurrentRow() => new(WindowFrameBoundType.CurrentRow);

    /// <summary>N PRECEDING.</summary>
    public static WindowFrameBound Preceding(int n) => new(WindowFrameBoundType.Preceding, n);

    /// <summary>N FOLLOWING.</summary>
    public static WindowFrameBound Following(int n) => new(WindowFrameBoundType.Following, n);
}

/// <summary>
/// Types of window frame boundaries.
/// </summary>
public enum WindowFrameBoundType
{
    /// <summary>UNBOUNDED PRECEDING: from start of partition.</summary>
    UnboundedPreceding,
    /// <summary>N PRECEDING: N rows before current.</summary>
    Preceding,
    /// <summary>CURRENT ROW.</summary>
    CurrentRow,
    /// <summary>N FOLLOWING: N rows after current.</summary>
    Following,
    /// <summary>UNBOUNDED FOLLOWING: to end of partition.</summary>
    UnboundedFollowing
}

/// <summary>
/// Extended window definition that includes frame specification and additional functions
/// (LAG, LEAD, NTILE, FIRST_VALUE, LAST_VALUE). Uses compiled delegates instead of AST types.
/// </summary>
public sealed class ExtendedWindowDefinition
{
    /// <summary>The output column name.</summary>
    public string OutputColumnName { get; }

    /// <summary>The window function name (ROW_NUMBER, RANK, LAG, LEAD, NTILE, SUM, COUNT, etc.).</summary>
    public string FunctionName { get; }

    /// <summary>Optional compiled operand for aggregate/value window functions. Null for ROW_NUMBER/RANK/NTILE.</summary>
    public CompiledScalarExpression? Operand { get; }

    /// <summary>Compiled PARTITION BY expressions. Null or empty if not partitioned.</summary>
    public IReadOnlyList<CompiledScalarExpression>? PartitionBy { get; }

    /// <summary>Compiled ORDER BY items. Null or empty if not ordered.</summary>
    public IReadOnlyList<CompiledOrderByItem>? OrderBy { get; }

    /// <summary>True when this is COUNT(*) with star instead of a column expression.</summary>
    public bool IsCountStar { get; }

    /// <summary>Optional frame specification. Null means use the default frame.</summary>
    public WindowFrameSpec? Frame { get; }

    /// <summary>For LAG/LEAD: the offset parameter (default 1).</summary>
    public int Offset { get; }

    /// <summary>For LAG/LEAD: the default value when out of range.</summary>
    public object? DefaultValue { get; }

    /// <summary>For NTILE: the number of groups.</summary>
    public int NTileGroups { get; }

    /// <summary>Initializes a new instance of the <see cref="ExtendedWindowDefinition"/> class.</summary>
    public ExtendedWindowDefinition(
        string outputColumnName,
        string functionName,
        CompiledScalarExpression? operand,
        IReadOnlyList<CompiledScalarExpression>? partitionBy,
        IReadOnlyList<CompiledOrderByItem>? orderBy,
        bool isCountStar = false,
        WindowFrameSpec? frame = null,
        int offset = 1,
        object? defaultValue = null,
        int nTileGroups = 0)
    {
        OutputColumnName = outputColumnName ?? throw new ArgumentNullException(nameof(outputColumnName));
        FunctionName = functionName ?? throw new ArgumentNullException(nameof(functionName));
        Operand = operand;
        PartitionBy = partitionBy;
        OrderBy = orderBy;
        IsCountStar = isCountStar;
        Frame = frame;
        Offset = offset;
        DefaultValue = defaultValue;
        NTileGroups = nTileGroups;
    }
}

/// <summary>
/// Extended window function node that supports window frames and additional functions
/// (LAG, LEAD, NTILE, FIRST_VALUE, LAST_VALUE) beyond what <see cref="ClientWindowNode"/> provides.
/// </summary>
/// <remarks>
/// Uses compiled delegates for all expression evaluation, with no dependency on legacy AST types.
/// Supports frame types:
/// <list type="bullet">
///   <item>ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW (running totals)</item>
///   <item>ROWS BETWEEN N PRECEDING AND N FOLLOWING (sliding window)</item>
///   <item>ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING (full partition)</item>
/// </list>
/// Additional functions: LAG, LEAD, NTILE, FIRST_VALUE, LAST_VALUE.
/// </remarks>
public sealed class WindowSpoolNode : IQueryPlanNode
{
    private readonly IQueryPlanNode _input;

    /// <summary>The extended window function definitions.</summary>
    public IReadOnlyList<ExtendedWindowDefinition> Windows { get; }

    /// <summary>Maximum rows to materialize. 0 = unlimited.</summary>
    public int MaxMaterializationRows { get; }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            var funcs = string.Join(", ", Windows.Select(w =>
                $"{w.FunctionName} AS {w.OutputColumnName}"));
            return $"WindowSpool: [{funcs}]";
        }
    }

    /// <inheritdoc />
    public long EstimatedRows => _input.EstimatedRows;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { _input };

    /// <summary>Initializes a new instance of the <see cref="WindowSpoolNode"/> class.</summary>
    public WindowSpoolNode(
        IQueryPlanNode input,
        IReadOnlyList<ExtendedWindowDefinition> windows,
        int maxMaterializationRows = 500_000)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        Windows = windows ?? throw new ArgumentNullException(nameof(windows));
        MaxMaterializationRows = maxMaterializationRows;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Step 1: Materialize all input rows
        var allRows = new List<QueryRow>();
        await foreach (var row in _input.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            allRows.Add(row);

            if (MaxMaterializationRows > 0 && allRows.Count > MaxMaterializationRows)
            {
                throw new QueryExecutionException(
                    QueryErrorCode.MemoryLimitExceeded,
                    $"Window function materialized {allRows.Count:N0} rows, exceeding the " +
                    $"{MaxMaterializationRows:N0} row limit.");
            }
        }

        if (allRows.Count == 0) yield break;

        // Step 2: Compute window values per row
        var windowValues = new Dictionary<string, object?>[allRows.Count];
        for (var i = 0; i < allRows.Count; i++)
        {
            windowValues[i] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var windowDef in Windows)
        {
            ComputeExtendedWindowFunction(windowDef, allRows, windowValues);
        }

        // Step 3: Yield enriched rows
        for (var i = 0; i < allRows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalRow = allRows[i];
            var enriched = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in originalRow.Values)
            {
                enriched[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in windowValues[i])
            {
                enriched[kvp.Key] = QueryValue.Simple(kvp.Value);
            }

            yield return new QueryRow(enriched, originalRow.EntityLogicalName);
        }
    }

    private static void ComputeExtendedWindowFunction(
        ExtendedWindowDefinition windowDef,
        List<QueryRow> allRows,
        Dictionary<string, object?>[] windowValues)
    {
        var columnName = windowDef.OutputColumnName;
        var functionName = windowDef.FunctionName.ToUpperInvariant();

        var partitions = PartitionRows(allRows, windowDef.PartitionBy);

        foreach (var partition in partitions)
        {
            var sortedIndices = SortPartition(partition, allRows, windowDef.OrderBy);

            switch (functionName)
            {
                case "LAG":
                    ComputeLag(sortedIndices, allRows, windowDef.Operand, windowValues, columnName,
                        windowDef.Offset, windowDef.DefaultValue);
                    break;

                case "LEAD":
                    ComputeLead(sortedIndices, allRows, windowDef.Operand, windowValues, columnName,
                        windowDef.Offset, windowDef.DefaultValue);
                    break;

                case "NTILE":
                    ComputeNtile(sortedIndices, windowValues, columnName, windowDef.NTileGroups);
                    break;

                case "FIRST_VALUE":
                    ComputeFirstValue(sortedIndices, allRows, windowDef.Operand, windowValues, columnName,
                        windowDef.Frame);
                    break;

                case "LAST_VALUE":
                    ComputeLastValue(sortedIndices, allRows, windowDef.Operand, windowValues, columnName,
                        windowDef.Frame);
                    break;

                case "SUM":
                case "COUNT":
                case "AVG":
                case "MIN":
                case "MAX":
                    ComputeFramedAggregate(functionName, sortedIndices, allRows, windowDef.Operand,
                        windowDef.IsCountStar, windowValues, columnName, windowDef.Frame);
                    break;

                case "ROW_NUMBER":
                    ComputeRowNumber(sortedIndices, windowValues, columnName);
                    break;

                case "RANK":
                    ComputeRank(sortedIndices, allRows, windowDef.OrderBy, windowValues, columnName);
                    break;

                case "DENSE_RANK":
                    ComputeDenseRank(sortedIndices, allRows, windowDef.OrderBy, windowValues, columnName);
                    break;

                case "CUME_DIST":
                    ComputeCumeDist(sortedIndices, allRows, windowDef.OrderBy, windowValues, columnName);
                    break;

                case "PERCENT_RANK":
                    ComputePercentRank(sortedIndices, allRows, windowDef.OrderBy, windowValues, columnName);
                    break;

                default:
                    throw new NotSupportedException($"Window function '{functionName}' is not supported.");
            }
        }
    }

    #region Partitioning and Sorting

    private static List<List<int>> PartitionRows(
        List<QueryRow> allRows,
        IReadOnlyList<CompiledScalarExpression>? partitionBy)
    {
        if (partitionBy == null || partitionBy.Count == 0)
        {
            var allIndices = new List<int>(allRows.Count);
            for (var i = 0; i < allRows.Count; i++) allIndices.Add(i);
            return new List<List<int>> { allIndices };
        }

        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < allRows.Count; i++)
        {
            var key = ComputePartitionKey(allRows[i], partitionBy);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new List<int>();
                groups[key] = group;
            }
            group.Add(i);
        }

        return groups.Values.ToList();
    }

    private static string ComputePartitionKey(
        QueryRow row, IReadOnlyList<CompiledScalarExpression> partitionBy)
    {
        if (partitionBy.Count == 1)
        {
            var val = partitionBy[0](row.Values);
            return val?.ToString() ?? "\0NULL\0";
        }

        var parts = new string[partitionBy.Count];
        for (var i = 0; i < partitionBy.Count; i++)
        {
            var val = partitionBy[i](row.Values);
            parts[i] = val?.ToString() ?? "\0NULL\0";
        }
        return string.Join("\0SEP\0", parts);
    }

    private static List<int> SortPartition(
        List<int> partitionIndices, List<QueryRow> allRows, IReadOnlyList<CompiledOrderByItem>? orderBy)
    {
        if (orderBy == null || orderBy.Count == 0) return new List<int>(partitionIndices);

        var sorted = new List<int>(partitionIndices);
        sorted.Sort((a, b) => CompareRowsByOrderBy(allRows[a], allRows[b], orderBy));
        return sorted;
    }

    private static int CompareRowsByOrderBy(QueryRow rowA, QueryRow rowB, IReadOnlyList<CompiledOrderByItem> orderBy)
    {
        foreach (var item in orderBy)
        {
            var colName = item.ColumnName;
            var valA = GetColumnValue(rowA, colName);
            var valB = GetColumnValue(rowB, colName);
            var cmp = CompareValues(valA, valB);
            if (cmp != 0) return item.Descending ? -cmp : cmp;
        }
        return 0;
    }

    private static object? GetColumnValue(QueryRow row, string columnName)
    {
        if (row.Values.TryGetValue(columnName, out var qv)) return qv.Value;
        foreach (var kvp in row.Values)
        {
            if (string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase))
                return kvp.Value.Value;
        }
        return null;
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;

        if (IsNumeric(a) && IsNumeric(b))
        {
            var da = Convert.ToDecimal(a, CultureInfo.InvariantCulture);
            var db = Convert.ToDecimal(b, CultureInfo.InvariantCulture);
            return da.CompareTo(db);
        }

        if (a is DateTime dtA && b is DateTime dtB) return dtA.CompareTo(dtB);

        var sa = Convert.ToString(a, CultureInfo.InvariantCulture) ?? "";
        var sb = Convert.ToString(b, CultureInfo.InvariantCulture) ?? "";
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(object value) =>
        value is int or long or short or byte or decimal or double or float;

    #endregion

    #region Extended Window Functions

    private static void ComputeRowNumber(
        List<int> sortedIndices, Dictionary<string, object?>[] windowValues, string columnName)
    {
        for (var i = 0; i < sortedIndices.Count; i++)
        {
            windowValues[sortedIndices[i]][columnName] = i + 1;
        }
    }

    /// <summary>
    /// RANK(): 1-based rank within partition. Ties get the same rank;
    /// the next rank after a tie skips (1, 2, 2, 4).
    /// </summary>
    private static void ComputeRank(
        List<int> sortedIndices, List<QueryRow> allRows,
        IReadOnlyList<CompiledOrderByItem>? orderBy,
        Dictionary<string, object?>[] windowValues, string columnName)
    {
        if (sortedIndices.Count == 0) return;

        windowValues[sortedIndices[0]][columnName] = 1;

        for (var i = 1; i < sortedIndices.Count; i++)
        {
            if (orderBy != null && orderBy.Count > 0 &&
                CompareRowsByOrderBy(allRows[sortedIndices[i]], allRows[sortedIndices[i - 1]], orderBy) == 0)
            {
                // Tie: same rank as previous
                windowValues[sortedIndices[i]][columnName] = windowValues[sortedIndices[i - 1]][columnName];
            }
            else
            {
                // No tie: rank = position + 1 (1-based)
                windowValues[sortedIndices[i]][columnName] = i + 1;
            }
        }
    }

    /// <summary>
    /// DENSE_RANK(): 1-based rank within partition. Ties get the same rank;
    /// the next rank after a tie increments by 1 (1, 2, 2, 3).
    /// </summary>
    private static void ComputeDenseRank(
        List<int> sortedIndices, List<QueryRow> allRows,
        IReadOnlyList<CompiledOrderByItem>? orderBy,
        Dictionary<string, object?>[] windowValues, string columnName)
    {
        if (sortedIndices.Count == 0) return;

        var currentRank = 1;
        windowValues[sortedIndices[0]][columnName] = currentRank;

        for (var i = 1; i < sortedIndices.Count; i++)
        {
            if (orderBy == null || orderBy.Count == 0 ||
                CompareRowsByOrderBy(allRows[sortedIndices[i]], allRows[sortedIndices[i - 1]], orderBy) != 0)
            {
                currentRank++;
            }
            windowValues[sortedIndices[i]][columnName] = currentRank;
        }
    }

    /// <summary>
    /// CUME_DIST(): cumulative distribution = (rows with value &lt;= current) / (total rows in partition).
    /// </summary>
    private static void ComputeCumeDist(
        List<int> sortedIndices, List<QueryRow> allRows,
        IReadOnlyList<CompiledOrderByItem>? orderBy,
        Dictionary<string, object?>[] windowValues, string columnName)
    {
        var n = sortedIndices.Count;
        if (n == 0) return;

        // Walk from end to find how many rows share each ORDER BY value
        var i = n - 1;
        while (i >= 0)
        {
            // Find the start of the current tie group
            var groupEnd = i;
            while (i > 0 && orderBy != null && orderBy.Count > 0 &&
                   CompareRowsByOrderBy(allRows[sortedIndices[i]], allRows[sortedIndices[i - 1]], orderBy) == 0)
            {
                i--;
            }

            // All rows in this group get CUME_DIST = (groupEnd + 1) / n
            var cumeDist = (double)(groupEnd + 1) / n;
            for (var j = i; j <= groupEnd; j++)
            {
                windowValues[sortedIndices[j]][columnName] = cumeDist;
            }

            i--;
        }
    }

    /// <summary>
    /// PERCENT_RANK(): (RANK - 1) / (total rows in partition - 1). Returns 0 for single-row partitions.
    /// </summary>
    private static void ComputePercentRank(
        List<int> sortedIndices, List<QueryRow> allRows,
        IReadOnlyList<CompiledOrderByItem>? orderBy,
        Dictionary<string, object?>[] windowValues, string columnName)
    {
        var n = sortedIndices.Count;
        if (n == 0) return;

        if (n == 1)
        {
            windowValues[sortedIndices[0]][columnName] = 0.0;
            return;
        }

        // First compute RANK values
        var ranks = new int[n];
        ranks[0] = 1;
        for (var i = 1; i < n; i++)
        {
            if (orderBy != null && orderBy.Count > 0 &&
                CompareRowsByOrderBy(allRows[sortedIndices[i]], allRows[sortedIndices[i - 1]], orderBy) == 0)
            {
                ranks[i] = ranks[i - 1];
            }
            else
            {
                ranks[i] = i + 1;
            }
        }

        // PERCENT_RANK = (rank - 1) / (n - 1)
        for (var i = 0; i < n; i++)
        {
            windowValues[sortedIndices[i]][columnName] = (double)(ranks[i] - 1) / (n - 1);
        }
    }

    /// <summary>
    /// LAG(column, offset, default): value from N rows before current in the partition order.
    /// </summary>
    private static void ComputeLag(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        Dictionary<string, object?>[] windowValues, string columnName,
        int offset, object? defaultValue)
    {
        for (var i = 0; i < sortedIndices.Count; i++)
        {
            var sourceIdx = i - offset;
            if (sourceIdx >= 0 && sourceIdx < sortedIndices.Count)
            {
                var sourceRow = allRows[sortedIndices[sourceIdx]];
                var val = operand != null
                    ? operand(sourceRow.Values)
                    : null;
                windowValues[sortedIndices[i]][columnName] = val;
            }
            else
            {
                windowValues[sortedIndices[i]][columnName] = defaultValue;
            }
        }
    }

    /// <summary>
    /// LEAD(column, offset, default): value from N rows after current in the partition order.
    /// </summary>
    private static void ComputeLead(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        Dictionary<string, object?>[] windowValues, string columnName,
        int offset, object? defaultValue)
    {
        for (var i = 0; i < sortedIndices.Count; i++)
        {
            var sourceIdx = i + offset;
            if (sourceIdx >= 0 && sourceIdx < sortedIndices.Count)
            {
                var sourceRow = allRows[sortedIndices[sourceIdx]];
                var val = operand != null
                    ? operand(sourceRow.Values)
                    : null;
                windowValues[sortedIndices[i]][columnName] = val;
            }
            else
            {
                windowValues[sortedIndices[i]][columnName] = defaultValue;
            }
        }
    }

    /// <summary>
    /// NTILE(n): distributes rows into N approximately equal groups (1-based).
    /// </summary>
    private static void ComputeNtile(
        List<int> sortedIndices, Dictionary<string, object?>[] windowValues,
        string columnName, int groups)
    {
        if (groups <= 0) groups = 1;

        var count = sortedIndices.Count;
        var baseSize = count / groups;
        var remainder = count % groups;

        var group = 1;
        var rowInGroup = 0;
        var currentGroupSize = baseSize + (group <= remainder ? 1 : 0);

        for (var i = 0; i < sortedIndices.Count; i++)
        {
            windowValues[sortedIndices[i]][columnName] = group;
            rowInGroup++;

            if (rowInGroup >= currentGroupSize && group < groups)
            {
                group++;
                rowInGroup = 0;
                currentGroupSize = baseSize + (group <= remainder ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// FIRST_VALUE(column): first value in the window frame.
    /// </summary>
    private static void ComputeFirstValue(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        Dictionary<string, object?>[] windowValues, string columnName,
        WindowFrameSpec? frame)
    {
        for (var i = 0; i < sortedIndices.Count; i++)
        {
            var (frameStart, _) = ResolveFrame(frame, i, sortedIndices.Count);
            var sourceRow = allRows[sortedIndices[frameStart]];
            var val = operand != null
                ? operand(sourceRow.Values)
                : null;
            windowValues[sortedIndices[i]][columnName] = val;
        }
    }

    /// <summary>
    /// LAST_VALUE(column): last value in the window frame.
    /// </summary>
    private static void ComputeLastValue(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        Dictionary<string, object?>[] windowValues, string columnName,
        WindowFrameSpec? frame)
    {
        for (var i = 0; i < sortedIndices.Count; i++)
        {
            var (_, frameEnd) = ResolveFrame(frame, i, sortedIndices.Count);
            var sourceRow = allRows[sortedIndices[frameEnd]];
            var val = operand != null
                ? operand(sourceRow.Values)
                : null;
            windowValues[sortedIndices[i]][columnName] = val;
        }
    }

    /// <summary>
    /// Framed aggregate: computes SUM/COUNT/AVG/MIN/MAX over the window frame.
    /// </summary>
    private static void ComputeFramedAggregate(
        string functionName, List<int> sortedIndices, List<QueryRow> allRows,
        CompiledScalarExpression? operand, bool isCountStar,
        Dictionary<string, object?>[] windowValues,
        string columnName, WindowFrameSpec? frame)
    {
        for (var i = 0; i < sortedIndices.Count; i++)
        {
            var (frameStart, frameEnd) = ResolveFrame(frame, i, sortedIndices.Count);

            object? result = functionName switch
            {
                "SUM" => ComputeFrameSum(sortedIndices, allRows, operand, frameStart, frameEnd),
                "COUNT" => ComputeFrameCount(sortedIndices, allRows, operand, isCountStar, frameStart, frameEnd),
                "AVG" => ComputeFrameAvg(sortedIndices, allRows, operand, frameStart, frameEnd),
                "MIN" => ComputeFrameMin(sortedIndices, allRows, operand, frameStart, frameEnd),
                "MAX" => ComputeFrameMax(sortedIndices, allRows, operand, frameStart, frameEnd),
                _ => throw new NotSupportedException($"Aggregate '{functionName}' not supported in window frame.")
            };

            windowValues[sortedIndices[i]][columnName] = result;
        }
    }

    /// <summary>
    /// Resolves a window frame specification into concrete start/end indices within the partition.
    /// Returns (startIndex, endIndex) both inclusive.
    /// </summary>
    internal static (int Start, int End) ResolveFrame(WindowFrameSpec? frame, int currentIndex, int partitionSize)
    {
        if (frame == null)
        {
            // Default frame: UNBOUNDED PRECEDING to CURRENT ROW (running)
            return (0, currentIndex);
        }

        var start = ResolveBound(frame.Start, currentIndex, partitionSize, isStart: true);
        var end = ResolveBound(frame.End, currentIndex, partitionSize, isStart: false);

        // Clamp to partition boundaries
        start = Math.Max(0, Math.Min(start, partitionSize - 1));
        end = Math.Max(0, Math.Min(end, partitionSize - 1));

        return (start, end);
    }

    private static int ResolveBound(WindowFrameBound bound, int currentIndex, int partitionSize, bool isStart)
    {
        return bound.BoundType switch
        {
            WindowFrameBoundType.UnboundedPreceding => 0,
            WindowFrameBoundType.Preceding => currentIndex - bound.Offset,
            WindowFrameBoundType.CurrentRow => currentIndex,
            WindowFrameBoundType.Following => currentIndex + bound.Offset,
            WindowFrameBoundType.UnboundedFollowing => partitionSize - 1,
            _ => isStart ? 0 : partitionSize - 1
        };
    }

    private static object? ComputeFrameSum(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        int frameStart, int frameEnd)
    {
        decimal sum = 0;
        var hasValue = false;
        for (var j = frameStart; j <= frameEnd; j++)
        {
            var val = operand != null
                ? operand(allRows[sortedIndices[j]].Values)
                : null;
            if (val != null)
            {
                sum += Convert.ToDecimal(val, CultureInfo.InvariantCulture);
                hasValue = true;
            }
        }
        return hasValue ? sum : null;
    }

    private static object? ComputeFrameCount(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        bool isCountStar, int frameStart, int frameEnd)
    {
        if (isCountStar) return frameEnd - frameStart + 1;

        var count = 0;
        for (var j = frameStart; j <= frameEnd; j++)
        {
            var val = operand != null
                ? operand(allRows[sortedIndices[j]].Values)
                : null;
            if (val != null) count++;
        }
        return count;
    }

    private static object? ComputeFrameAvg(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        int frameStart, int frameEnd)
    {
        decimal sum = 0;
        var count = 0;
        for (var j = frameStart; j <= frameEnd; j++)
        {
            var val = operand != null
                ? operand(allRows[sortedIndices[j]].Values)
                : null;
            if (val != null)
            {
                sum += Convert.ToDecimal(val, CultureInfo.InvariantCulture);
                count++;
            }
        }
        return count > 0 ? sum / count : null;
    }

    private static object? ComputeFrameMin(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        int frameStart, int frameEnd)
    {
        object? min = null;
        for (var j = frameStart; j <= frameEnd; j++)
        {
            var val = operand != null
                ? operand(allRows[sortedIndices[j]].Values)
                : null;
            if (val != null && (min == null || CompareValues(val, min) < 0))
            {
                min = val;
            }
        }
        return min;
    }

    private static object? ComputeFrameMax(
        List<int> sortedIndices, List<QueryRow> allRows, CompiledScalarExpression? operand,
        int frameStart, int frameEnd)
    {
        object? max = null;
        for (var j = frameStart; j <= frameEnd; j++)
        {
            var val = operand != null
                ? operand(allRows[sortedIndices[j]].Values)
                : null;
            if (val != null && (max == null || CompareValues(val, max) > 0))
            {
                max = val;
            }
        }
        return max;
    }

    #endregion
}
