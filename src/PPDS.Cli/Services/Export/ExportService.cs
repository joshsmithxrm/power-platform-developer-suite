using System.Data;
using System.Text;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure;

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

    #endregion
}
