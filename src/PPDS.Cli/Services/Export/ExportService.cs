using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Services.Export;

/// <summary>
/// Application service for exporting query results to various formats.
/// </summary>
/// <remarks>
/// See ADR-0015 for architectural context.
/// </remarks>
public sealed class ExportService : IExportService
{
    private readonly ILogger<ExportService> _logger;

    /// <summary>
    /// Creates a new export service.
    /// </summary>
    public ExportService(ILogger<ExportService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ExportCsvAsync(
        DataTable table,
        Stream stream,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ExportDelimitedAsync(table, stream, ",", options, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ExportTsvAsync(
        DataTable table,
        Stream stream,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ExportDelimitedAsync(table, stream, "\t", options, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ExportJsonAsync(
        DataTable table,
        Stream stream,
        IReadOnlyDictionary<string, QueryColumnType>? columnTypes = null,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExportOptions();

        var columnIndices = options.ColumnIndices ?? Enumerable.Range(0, table.Columns.Count).ToList();
        var rowIndices = options.RowIndices ?? Enumerable.Range(0, table.Rows.Count).ToList();
        var totalRows = rowIndices.Count;

        progress?.ReportStatus($"Exporting {totalRows} rows to JSON...");

        var records = new List<Dictionary<string, object?>>(totalRows);
        var processedRows = 0;

        foreach (var rowIndex in rowIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (rowIndex < 0 || rowIndex >= table.Rows.Count)
                continue;

            var row = table.Rows[rowIndex];
            var record = new Dictionary<string, object?>();

            foreach (var colIndex in columnIndices)
            {
                if (colIndex >= table.Columns.Count)
                    continue;

                var columnName = table.Columns[colIndex].ColumnName;
                var rawValue = row[colIndex];
                var typedValue = ConvertToTypedValue(rawValue, columnName, columnTypes, options);
                record[columnName] = typedValue;
            }

            records.Add(record);
            processedRows++;

            if (totalRows > 100 && processedRows % 100 == 0)
            {
                progress?.ReportProgress(processedRows, totalRows);
            }
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        await JsonSerializer.SerializeAsync(stream, records, jsonOptions, cancellationToken);

        _logger.LogInformation("Exported {RowCount} rows to JSON", processedRows);
        progress?.ReportComplete($"Exported {processedRows} rows.");
    }

    /// <inheritdoc />
    public string FormatForClipboard(
        DataTable table,
        IReadOnlyList<int>? selectedRows = null,
        IReadOnlyList<int>? selectedColumns = null,
        bool includeHeaders = true)
    {
        var sb = new StringBuilder();

        var columnIndices = selectedColumns ?? Enumerable.Range(0, table.Columns.Count).ToList();
        var rowIndices = selectedRows ?? Enumerable.Range(0, table.Rows.Count).ToList();

        // Headers
        if (includeHeaders && columnIndices.Count > 0)
        {
            var headers = columnIndices.Select(i => table.Columns[i].ColumnName);
            sb.AppendLine(string.Join("\t", headers));
        }

        // Data rows
        foreach (var rowIndex in rowIndices)
        {
            if (rowIndex < 0 || rowIndex >= table.Rows.Count)
                continue;

            var row = table.Rows[rowIndex];
            var values = columnIndices.Select(i =>
            {
                var value = i < table.Columns.Count ? row[i] : null;
                return FormatCellForClipboard(value);
            });
            sb.AppendLine(string.Join("\t", values));
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <inheritdoc />
    public string FormatCellForClipboard(object? value)
    {
        if (value == null || value == DBNull.Value)
            return string.Empty;

        var str = value.ToString() ?? string.Empty;

        // Escape tabs and newlines for clipboard
        str = str.Replace("\t", " ").Replace("\r", "").Replace("\n", " ");

        return str;
    }

    #region Private Helpers

    private async Task ExportDelimitedAsync(
        DataTable table,
        Stream stream,
        string delimiter,
        ExportOptions? options,
        IOperationProgress? progress,
        CancellationToken cancellationToken)
    {
        options ??= new ExportOptions();

        var columnIndices = options.ColumnIndices ?? Enumerable.Range(0, table.Columns.Count).ToList();
        var rowIndices = options.RowIndices ?? Enumerable.Range(0, table.Rows.Count).ToList();
        var totalRows = rowIndices.Count;

        progress?.ReportStatus($"Exporting {totalRows} rows...");

        // Use UTF8 with or without BOM based on options
        var encoding = options.IncludeBom
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        await using var writer = new StreamWriter(stream, encoding, bufferSize: 65536, leaveOpen: true);

        // Write headers
        if (options.IncludeHeaders && columnIndices.Count > 0)
        {
            var headers = columnIndices.Select(i => EscapeCsvField(table.Columns[i].ColumnName, delimiter));
            await writer.WriteLineAsync(string.Join(delimiter, headers));
        }

        // Write data rows
        var processedRows = 0;
        foreach (var rowIndex in rowIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (rowIndex < 0 || rowIndex >= table.Rows.Count)
                continue;

            var row = table.Rows[rowIndex];
            var values = columnIndices.Select(i =>
            {
                var value = i < table.Columns.Count ? row[i] : null;
                return EscapeCsvField(FormatCellValue(value, options), delimiter);
            });

            await writer.WriteLineAsync(string.Join(delimiter, values));

            processedRows++;

            // Report progress every 100 rows for larger exports
            if (totalRows > 100 && processedRows % 100 == 0)
            {
                progress?.ReportProgress(processedRows, totalRows);
            }
        }

        await writer.FlushAsync(cancellationToken);

        _logger.LogInformation("Exported {RowCount} rows to {Format}",
            processedRows, delimiter == "," ? "CSV" : "TSV");

        progress?.ReportComplete($"Exported {processedRows} rows.");
    }

    private static string FormatCellValue(object? value, ExportOptions options)
    {
        if (value == null || value == DBNull.Value)
            return string.Empty;

        return value switch
        {
            DateTime dt => dt.ToString(options.DateTimeFormat),
            DateTimeOffset dto => dto.ToString(options.DateTimeFormat),
            bool b => b ? "true" : "false",
            decimal d => d.ToString("G"),
            double dbl => dbl.ToString("G"),
            float f => f.ToString("G"),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string EscapeCsvField(string field, string delimiter)
    {
        // CSV escaping rules:
        // 1. If field contains delimiter, quotes, or newlines, wrap in quotes
        // 2. Double any quotes within the field
        var needsQuotes = field.Contains(delimiter) ||
                          field.Contains('"') ||
                          field.Contains('\n') ||
                          field.Contains('\r');

        if (!needsQuotes)
            return field;

        return $"\"{field.Replace("\"", "\"\"")}\"";
    }

    /// <summary>
    /// Converts a string value back to its typed representation based on column metadata.
    /// </summary>
    private static object? ConvertToTypedValue(
        object? rawValue,
        string columnName,
        IReadOnlyDictionary<string, QueryColumnType>? columnTypes,
        ExportOptions options)
    {
        if (rawValue == null || rawValue == DBNull.Value)
            return null;

        var stringValue = rawValue.ToString();
        if (string.IsNullOrEmpty(stringValue))
            return null;

        // If no type metadata, return as string
        if (columnTypes == null || !columnTypes.TryGetValue(columnName, out var columnType))
            return stringValue;

        // Convert based on column type
        return columnType switch
        {
            QueryColumnType.Guid => Guid.TryParse(stringValue, out var guid) ? guid : stringValue,
            QueryColumnType.Integer => int.TryParse(stringValue, out var i) ? i : stringValue,
            QueryColumnType.BigInt => long.TryParse(stringValue, out var l) ? l : stringValue,
            QueryColumnType.Decimal or QueryColumnType.Money =>
                decimal.TryParse(stringValue, out var d) ? d : stringValue,
            QueryColumnType.Double => double.TryParse(stringValue, out var dbl) ? dbl : stringValue,
            QueryColumnType.Boolean => stringValue.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                                       stringValue.Equals("true", StringComparison.OrdinalIgnoreCase),
            QueryColumnType.DateTime =>
                DateTime.TryParse(stringValue, out var dt) ? dt.ToString("o") : stringValue,
            _ => stringValue // String, Lookup, OptionSet, etc. stay as strings
        };
    }

    #endregion
}
