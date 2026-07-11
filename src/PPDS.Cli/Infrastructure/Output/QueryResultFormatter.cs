using PPDS.Dataverse.Query;

namespace PPDS.Cli.Infrastructure.Output;

/// <summary>
/// Provides shared formatting methods for query results.
/// Used by SQL command and history execute command.
/// </summary>
public static class QueryResultFormatter
{
    /// <summary>
    /// Minimum width for table columns to ensure readability.
    /// </summary>
    private const int MinColumnWidth = 20;

    /// <summary>
    /// Writes query results as an aligned table to stdout.
    /// Metadata (entity name, record count, timing) goes to stderr.
    /// </summary>
    /// <param name="result">The query result to display.</param>
    /// <param name="verbose">If true, displays the executed FetchXML.</param>
    /// <param name="fetchXml">The FetchXML that was executed (shown in verbose mode).</param>
    /// <param name="pagingHint">Optional custom hint for pagination continuation.</param>
    public static void WriteTableOutput(
        QueryResult result,
        bool verbose,
        string? fetchXml = null,
        string? pagingHint = null)
    {
        Console.Error.WriteLine();

        if (result.Count == 0)
        {
            Console.Error.WriteLine("No records found.");
            return;
        }

        Console.Error.WriteLine($"Entity: {result.EntityLogicalName}");
        Console.Error.WriteLine($"Records: {result.Count}");

        if (result.TotalCount.HasValue)
        {
            Console.Error.WriteLine($"Total Count: {result.TotalCount}");
        }

        if (result.MoreRecords)
        {
            var hint = pagingHint ?? "More records available (use --page or --paging-cookie for continuation)";
            Console.Error.WriteLine(hint);
        }

        Console.Error.WriteLine($"Execution Time: {result.ExecutionTimeMs}ms");

        if (verbose && !string.IsNullOrEmpty(fetchXml))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Executed FetchXML:");
            Console.Error.WriteLine(fetchXml);
        }

        Console.Error.WriteLine();

        // Print table header
        var columns = result.Columns;
        var columnWidths = new int[columns.Count];

        for (var i = 0; i < columns.Count; i++)
        {
            columnWidths[i] = Math.Max(
                columns[i].Alias?.Length ?? columns[i].LogicalName.Length,
                MinColumnWidth);
        }

        // Header row
        var header = string.Join(" | ", columns.Select((c, i) =>
            (c.Alias ?? c.LogicalName).PadRight(columnWidths[i])));
        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length));

        // Data rows
        foreach (var record in result.Records)
        {
            var row = new List<string>();
            for (var i = 0; i < columns.Count; i++)
            {
                var columnName = columns[i].Alias ?? columns[i].LogicalName;
                if (record.TryGetValue(columnName, out var queryValue) && queryValue != null)
                {
                    var displayValue = queryValue.FormattedValue ?? queryValue.Value?.ToString() ?? "";
                    row.Add(TruncateValue(displayValue, columnWidths[i]));
                }
                else
                {
                    row.Add("".PadRight(columnWidths[i]));
                }
            }

            Console.WriteLine(string.Join(" | ", row));
        }

        if (result.MoreRecords && !string.IsNullOrEmpty(result.PagingCookie))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Paging cookie (for continuation):");
            Console.Error.WriteLine(result.PagingCookie);
        }
    }

    /// <summary>
    /// Writes query results as CSV to stdout.
    /// </summary>
    /// <param name="result">The query result to output as CSV.</param>
    public static void WriteCsvOutput(QueryResult result)
    {
        // Nothing to emit without a schema. When columns are known the header is
        // written even for zero rows, so CSV consumers still get column names
        // rather than silent zero-byte output (#1078).
        if (result.Columns.Count == 0)
        {
            return;
        }

        // Header row (emitted even when there are no records)
        var headers = result.Columns.Select(c => EscapeCsvField(c.Alias ?? c.LogicalName));
        Console.WriteLine(string.Join(",", headers));

        // Data rows
        foreach (var record in result.Records)
        {
            var values = result.Columns.Select(c =>
            {
                var key = c.Alias ?? c.LogicalName;
                if (record.TryGetValue(key, out var qv) && qv != null)
                {
                    return EscapeCsvField(qv.FormattedValue ?? qv.Value?.ToString() ?? "");
                }
                return "";
            });
            Console.WriteLine(string.Join(",", values));
        }
    }

    /// <summary>
    /// Truncates a value to fit within the specified column width.
    /// </summary>
    private static string TruncateValue(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value.PadRight(maxLength);
        }

        return value[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Escapes a value for CSV output, quoting if necessary.
    /// </summary>
    private static string EscapeCsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}
