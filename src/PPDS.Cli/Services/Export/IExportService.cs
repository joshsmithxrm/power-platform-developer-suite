using System.Data;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Services.Export;

/// <summary>
/// Application service for exporting query results to various formats.
/// </summary>
/// <remarks>
/// This service handles CSV, TSV, and clipboard export operations.
/// See ADR-0015 for architectural context.
/// </remarks>
public interface IExportService
{
    /// <summary>
    /// Exports a DataTable to CSV format.
    /// </summary>
    /// <param name="table">The data to export.</param>
    /// <param name="stream">The output stream.</param>
    /// <param name="options">Export options.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportCsvAsync(
        DataTable table,
        Stream stream,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a DataTable to TSV (tab-separated values) format.
    /// </summary>
    /// <param name="table">The data to export.</param>
    /// <param name="stream">The output stream.</param>
    /// <param name="options">Export options.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportTsvAsync(
        DataTable table,
        Stream stream,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a DataTable to JSON format with proper type preservation.
    /// </summary>
    /// <param name="table">The data to export.</param>
    /// <param name="stream">The output stream.</param>
    /// <param name="columnTypes">Column type metadata for type restoration.</param>
    /// <param name="options">Export options.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportJsonAsync(
        DataTable table,
        Stream stream,
        IReadOnlyDictionary<string, QueryColumnType>? columnTypes = null,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats data for clipboard copy (tab-separated for spreadsheet paste).
    /// </summary>
    /// <param name="table">The data to format.</param>
    /// <param name="selectedRows">Optional list of specific row indices to include.</param>
    /// <param name="selectedColumns">Optional list of specific column indices to include.</param>
    /// <param name="includeHeaders">Whether to include column headers.</param>
    /// <returns>Tab-separated text suitable for clipboard.</returns>
    string FormatForClipboard(
        DataTable table,
        IReadOnlyList<int>? selectedRows = null,
        IReadOnlyList<int>? selectedColumns = null,
        bool includeHeaders = true);

    /// <summary>
    /// Formats a single cell value for clipboard copy.
    /// </summary>
    /// <param name="value">The cell value.</param>
    /// <returns>Formatted text suitable for clipboard.</returns>
    string FormatCellForClipboard(object? value);
}

/// <summary>
/// Options for export operations.
/// </summary>
public sealed record ExportOptions
{
    /// <summary>
    /// Whether to include column headers in the output.
    /// </summary>
    public bool IncludeHeaders { get; init; } = true;

    /// <summary>
    /// Date/time format string.
    /// </summary>
    public string DateTimeFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Text encoding for the output.
    /// </summary>
    public System.Text.Encoding Encoding { get; init; } = System.Text.Encoding.UTF8;

    /// <summary>
    /// Whether to include BOM (byte order mark) for UTF-8.
    /// Useful for Excel compatibility.
    /// </summary>
    public bool IncludeBom { get; init; } = true;

    /// <summary>
    /// Specific column indices to export (null for all columns).
    /// </summary>
    public IReadOnlyList<int>? ColumnIndices { get; init; }

    /// <summary>
    /// Specific row indices to export (null for all rows).
    /// </summary>
    public IReadOnlyList<int>? RowIndices { get; init; }
}
