using System.Data;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Export;
using Xunit;

namespace PPDS.Cli.Tests.Services.Export;

/// <summary>
/// Unit tests for <see cref="ExportService"/>.
/// </summary>
public class ExportServiceTests
{
    private readonly Mock<ILogger<ExportService>> _mockLogger;
    private readonly ExportService _service;

    public ExportServiceTests()
    {
        _mockLogger = new Mock<ILogger<ExportService>>();
        _service = new ExportService(_mockLogger.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ExportService(null!));
    }

    #endregion

    #region FormatCellForClipboard Tests

    [Fact]
    public void FormatCellForClipboard_WithNull_ReturnsEmpty()
    {
        var result = _service.FormatCellForClipboard(null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatCellForClipboard_WithDbNull_ReturnsEmpty()
    {
        var result = _service.FormatCellForClipboard(DBNull.Value);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatCellForClipboard_WithSimpleString_ReturnsString()
    {
        var result = _service.FormatCellForClipboard("Hello World");

        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void FormatCellForClipboard_WithTabs_ReplacesWithSpaces()
    {
        var result = _service.FormatCellForClipboard("Hello\tWorld");

        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void FormatCellForClipboard_WithNewlines_ReplacesWithSpaces()
    {
        var result = _service.FormatCellForClipboard("Hello\nWorld");

        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void FormatCellForClipboard_WithCarriageReturn_RemovesIt()
    {
        var result = _service.FormatCellForClipboard("Hello\r\nWorld");

        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void FormatCellForClipboard_WithNumber_ReturnsString()
    {
        var result = _service.FormatCellForClipboard(42);

        Assert.Equal("42", result);
    }

    #endregion

    #region FormatForClipboard Tests

    [Fact]
    public void FormatForClipboard_WithEmptyTable_ReturnsEmpty()
    {
        var table = new DataTable();

        var result = _service.FormatForClipboard(table);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatForClipboard_WithHeaders_IncludesHeaders()
    {
        var table = CreateTestTable();

        var result = _service.FormatForClipboard(table, includeHeaders: true);

        Assert.StartsWith("Name\tAge", result);
    }

    [Fact]
    public void FormatForClipboard_WithoutHeaders_ExcludesHeaders()
    {
        var table = CreateTestTable();

        var result = _service.FormatForClipboard(table, includeHeaders: false);

        Assert.StartsWith("Alice\t30", result);
    }

    [Fact]
    public void FormatForClipboard_WithSelectedRows_IncludesOnlySelectedRows()
    {
        var table = CreateTestTable();

        var result = _service.FormatForClipboard(table, selectedRows: new[] { 1 }, includeHeaders: false);

        Assert.Equal("Bob\t25", result);
    }

    [Fact]
    public void FormatForClipboard_WithSelectedColumns_IncludesOnlySelectedColumns()
    {
        var table = CreateTestTable();

        var result = _service.FormatForClipboard(table, selectedColumns: new[] { 0 }, includeHeaders: true);

        Assert.Contains("Name", result);
        Assert.DoesNotContain("Age", result);
    }

    [Fact]
    public void FormatForClipboard_WithMultipleRows_SeparatesWithNewlines()
    {
        var table = CreateTestTable();

        var result = _service.FormatForClipboard(table, includeHeaders: false);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void FormatForClipboard_WithInvalidRowIndex_SkipsInvalidRow()
    {
        var table = CreateTestTable();

        var result = _service.FormatForClipboard(table, selectedRows: new[] { 0, 999 }, includeHeaders: false);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    #endregion

    #region ExportCsvAsync Tests

    [Fact]
    public async Task ExportCsvAsync_WithSimpleTable_WritesCsv()
    {
        var table = CreateTestTable();
        using var stream = new MemoryStream();

        await _service.ExportCsvAsync(table, stream);

        var result = GetStreamContent(stream);
        Assert.Contains("Name,Age", result);
        Assert.Contains("Alice,30", result);
        Assert.Contains("Bob,25", result);
    }

    [Fact]
    public async Task ExportCsvAsync_WithoutHeaders_ExcludesHeaders()
    {
        var table = CreateTestTable();
        using var stream = new MemoryStream();
        var options = new ExportOptions { IncludeHeaders = false };

        await _service.ExportCsvAsync(table, stream, options);

        var result = GetStreamContent(stream);
        Assert.DoesNotContain("Name,Age", result);
        Assert.Contains("Alice,30", result);
    }

    [Fact]
    public async Task ExportCsvAsync_WithCommaInValue_QuotesField()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add("Smith, John");

        using var stream = new MemoryStream();

        await _service.ExportCsvAsync(table, stream, new ExportOptions { IncludeHeaders = false });

        var result = GetStreamContent(stream);
        Assert.Contains("\"Smith, John\"", result);
    }

    [Fact]
    public async Task ExportCsvAsync_WithQuotesInValue_EscapesQuotes()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add("John \"Johnny\" Doe");

        using var stream = new MemoryStream();

        await _service.ExportCsvAsync(table, stream, new ExportOptions { IncludeHeaders = false });

        var result = GetStreamContent(stream);
        Assert.Contains("\"\"Johnny\"\"", result);
    }

    [Fact]
    public async Task ExportCsvAsync_WithNewlineInValue_QuotesField()
    {
        var table = new DataTable();
        table.Columns.Add("Address", typeof(string));
        table.Rows.Add("123 Main St\nApt 4");

        using var stream = new MemoryStream();

        await _service.ExportCsvAsync(table, stream, new ExportOptions { IncludeHeaders = false });

        var result = GetStreamContent(stream);
        Assert.Contains("\"123 Main St\nApt 4\"", result);
    }

    [Fact]
    public async Task ExportCsvAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        var table = CreateLargeTable(1000);
        using var stream = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.ExportCsvAsync(table, stream, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ExportCsvAsync_WithProgress_ReportsProgress()
    {
        var table = CreateLargeTable(200);
        using var stream = new MemoryStream();
        var mockProgress = new Mock<IOperationProgress>();

        await _service.ExportCsvAsync(table, stream, progress: mockProgress.Object);

        mockProgress.Verify(p => p.ReportStatus(It.IsAny<string>()), Times.AtLeastOnce);
        mockProgress.Verify(p => p.ReportComplete(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ExportCsvAsync_WithDateTime_FormatsCorrectly()
    {
        var table = new DataTable();
        table.Columns.Add("Date", typeof(DateTime));
        table.Rows.Add(new DateTime(2024, 1, 15, 10, 30, 0));

        using var stream = new MemoryStream();
        var options = new ExportOptions
        {
            IncludeHeaders = false,
            DateTimeFormat = "yyyy-MM-dd"
        };

        await _service.ExportCsvAsync(table, stream, options);

        var result = GetStreamContent(stream);
        Assert.Contains("2024-01-15", result);
    }

    [Fact]
    public async Task ExportCsvAsync_WithNull_WritesEmpty()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add(DBNull.Value);

        using var stream = new MemoryStream();

        await _service.ExportCsvAsync(table, stream, new ExportOptions { IncludeHeaders = false });

        var result = GetStreamContent(stream);
        Assert.Equal("\r\n", result); // Just a newline for the empty value
    }

    #endregion

    #region ExportTsvAsync Tests

    [Fact]
    public async Task ExportTsvAsync_WithSimpleTable_WritesTsv()
    {
        var table = CreateTestTable();
        using var stream = new MemoryStream();

        await _service.ExportTsvAsync(table, stream);

        var result = GetStreamContent(stream);
        Assert.Contains("Name\tAge", result);
        Assert.Contains("Alice\t30", result);
    }

    [Fact]
    public async Task ExportTsvAsync_WithTabInValue_QuotesField()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add("Smith\tJohn");

        using var stream = new MemoryStream();

        await _service.ExportTsvAsync(table, stream, new ExportOptions { IncludeHeaders = false });

        var result = GetStreamContent(stream);
        Assert.Contains("\"Smith\tJohn\"", result);
    }

    #endregion

    #region Selected Rows/Columns Tests

    [Fact]
    public async Task ExportCsvAsync_WithSelectedRows_ExportsOnlySelectedRows()
    {
        var table = CreateTestTable();
        using var stream = new MemoryStream();
        var options = new ExportOptions
        {
            RowIndices = new[] { 0 },
            IncludeHeaders = false
        };

        await _service.ExportCsvAsync(table, stream, options);

        var result = GetStreamContent(stream);
        Assert.Contains("Alice", result);
        Assert.DoesNotContain("Bob", result);
    }

    [Fact]
    public async Task ExportCsvAsync_WithSelectedColumns_ExportsOnlySelectedColumns()
    {
        var table = CreateTestTable();
        using var stream = new MemoryStream();
        var options = new ExportOptions
        {
            ColumnIndices = new[] { 0 },
            IncludeHeaders = true
        };

        await _service.ExportCsvAsync(table, stream, options);

        var result = GetStreamContent(stream);
        Assert.Contains("Name", result);
        Assert.DoesNotContain("Age", result);
    }

    #endregion

    #region Helper Methods

    private static DataTable CreateTestTable()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Age", typeof(int));
        table.Rows.Add("Alice", 30);
        table.Rows.Add("Bob", 25);
        return table;
    }

    private static DataTable CreateLargeTable(int rowCount)
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        for (int i = 0; i < rowCount; i++)
        {
            table.Rows.Add(i, $"Name_{i}");
        }
        return table;
    }

    private static string GetStreamContent(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    #endregion
}
