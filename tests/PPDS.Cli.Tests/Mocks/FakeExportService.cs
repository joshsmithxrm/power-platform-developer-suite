using System.Data;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Export;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Tests.Mocks;

/// <summary>
/// Fake implementation of <see cref="IExportService"/> for testing.
/// </summary>
public sealed class FakeExportService : IExportService
{
    private readonly List<ExportRecord> _exports = new();

    /// <summary>
    /// Gets all export operations performed.
    /// </summary>
    public IReadOnlyList<ExportRecord> Exports => _exports;

    /// <inheritdoc />
    public Task ExportCsvAsync(
        DataTable table,
        Stream stream,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        _exports.Add(new ExportRecord("CSV", table.Rows.Count));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ExportTsvAsync(
        DataTable table,
        Stream stream,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        _exports.Add(new ExportRecord("TSV", table.Rows.Count));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ExportJsonAsync(
        DataTable table,
        Stream stream,
        IReadOnlyDictionary<string, QueryColumnType>? columnTypes = null,
        ExportOptions? options = null,
        IOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        _exports.Add(new ExportRecord("JSON", table.Rows.Count));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public string FormatForClipboard(
        DataTable table,
        IReadOnlyList<int>? selectedRows = null,
        IReadOnlyList<int>? selectedColumns = null,
        bool includeHeaders = true)
    {
        _exports.Add(new ExportRecord("Clipboard", table.Rows.Count));
        return "fake clipboard data";
    }

    /// <inheritdoc />
    public string FormatCellForClipboard(object? value)
    {
        return value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Resets the fake service state.
    /// </summary>
    public void Reset()
    {
        _exports.Clear();
    }
}

/// <summary>
/// Record of an export operation.
/// </summary>
public sealed record ExportRecord(string Format, int RowCount);
