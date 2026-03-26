using Xunit;

namespace PPDS.Cli.Tests.Tui.Views;

/// <summary>
/// Tests for QueryResultsTableView DataTable dispose behavior (AC-21, AC-22).
/// Uses source-scan approach since Terminal.Gui views don't need to be instantiated.
/// </summary>
[Trait("Category", "TuiUnit")]
public class QueryResultsTableViewTests
{
    [Fact]
    public void Dispose_CleansUpDataTables()
    {
        var content = ReadViewSource();

        // Verify the Dispose override exists with proper cleanup
        Assert.Contains("protected override void Dispose(bool disposing)", content);
        Assert.Contains("_disposed = true", content);
        Assert.Contains("_dataTable?.Dispose()", content);
        Assert.Contains("_unfilteredDataTable?.Dispose()", content);
    }

    [Fact]
    public void LoadResults_DisposesOldDataTable()
    {
        var content = ReadViewSource();

        // LoadResults must dispose both tables before creating replacements.
        // Extract the LoadResults method body and verify dispose calls precede new table creation.
        var methodStart = content.IndexOf("public void LoadResults(QueryResult result)", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "LoadResults method not found");

        var bodyAfterMethod = content.Substring(methodStart);
        var disposeIndex = bodyAfterMethod.IndexOf("_dataTable?.Dispose()", StringComparison.Ordinal);
        var newTableIndex = bodyAfterMethod.IndexOf("ToDataTableWithTypes(result)", StringComparison.Ordinal);

        Assert.True(disposeIndex >= 0, "_dataTable?.Dispose() not found in LoadResults");
        Assert.True(newTableIndex >= 0, "ToDataTableWithTypes call not found in LoadResults");
        Assert.True(disposeIndex < newTableIndex,
            "_dataTable?.Dispose() must appear before ToDataTableWithTypes in LoadResults");
    }

    [Fact]
    public void ClearData_DisposesOldDataTable()
    {
        var content = ReadViewSource();

        // ClearData must dispose before creating new DataTable
        var methodStart = content.IndexOf("public void ClearData()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "ClearData method not found");

        var bodyAfterMethod = content.Substring(methodStart);
        var disposeIndex = bodyAfterMethod.IndexOf("_dataTable?.Dispose()", StringComparison.Ordinal);
        var newTableIndex = bodyAfterMethod.IndexOf("new DataTable()", StringComparison.Ordinal);

        Assert.True(disposeIndex >= 0, "_dataTable?.Dispose() not found in ClearData");
        Assert.True(newTableIndex >= 0, "new DataTable() not found in ClearData");
        Assert.True(disposeIndex < newTableIndex,
            "_dataTable?.Dispose() must appear before new DataTable() in ClearData");
    }

    [Fact]
    public void ApplyFilter_DisposesOldDataTable()
    {
        var content = ReadViewSource();

        // ApplyFilter must dispose old table in both the clear-filter and apply-filter paths
        var methodStart = content.IndexOf("public void ApplyFilter(string? filterText)", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "ApplyFilter method not found");

        var bodyAfterMethod = content.Substring(methodStart);

        // Clear-filter path: _dataTable?.Dispose() before _unfilteredDataTable.Copy()
        Assert.Contains("_dataTable?.Dispose()", bodyAfterMethod);

        // Apply-filter path: oldDataTable?.Dispose() after replacement
        Assert.Contains("oldDataTable?.Dispose()", bodyAfterMethod);
    }

    [Fact]
    public void InitializeStreamingColumns_DisposesOldDataTable()
    {
        var content = ReadViewSource();

        var methodStart = content.IndexOf(
            "public void InitializeStreamingColumns(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "InitializeStreamingColumns method not found");

        var bodyAfterMethod = content.Substring(methodStart);
        var disposeIndex = bodyAfterMethod.IndexOf("_dataTable?.Dispose()", StringComparison.Ordinal);
        var newTableIndex = bodyAfterMethod.IndexOf("new DataTable()", StringComparison.Ordinal);

        Assert.True(disposeIndex >= 0,
            "_dataTable?.Dispose() not found in InitializeStreamingColumns");
        Assert.True(newTableIndex >= 0,
            "new DataTable() not found in InitializeStreamingColumns");
        Assert.True(disposeIndex < newTableIndex,
            "_dataTable?.Dispose() must appear before new DataTable() in InitializeStreamingColumns");
    }

    [Fact]
    public void AllReplacementSites_DisposeOldDataTable()
    {
        var content = ReadViewSource();

        // Count occurrences of dispose — should be at least:
        // 1. Dispose(bool) for _dataTable
        // 2. Dispose(bool) for _unfilteredDataTable
        // 3. LoadResults for _dataTable
        // 4. LoadResults for _unfilteredDataTable
        // 5. ClearData for _dataTable
        // 6. ClearData for _unfilteredDataTable
        // 7. InitializeStreamingColumns for _dataTable
        // 8. InitializeStreamingColumns for _unfilteredDataTable
        // 9. ApplyFilter clear path for _dataTable
        // 10. ApplyFilter filter path (oldDataTable?.Dispose())
        var disposeDataTableCount = CountOccurrences(content, "_dataTable?.Dispose()");
        var disposeUnfilteredCount = CountOccurrences(content, "_unfilteredDataTable?.Dispose()");

        Assert.True(disposeDataTableCount >= 5,
            $"Expected at least 5 _dataTable?.Dispose() calls but found {disposeDataTableCount}");
        Assert.True(disposeUnfilteredCount >= 4,
            $"Expected at least 4 _unfilteredDataTable?.Dispose() calls but found {disposeUnfilteredCount}");
    }

    private static string ReadViewSource()
    {
        var srcDir = FindSrcDirectory();
        var viewFile = Path.Combine(srcDir, "PPDS.Cli", "Tui", "Views", "QueryResultsTableView.cs");
        return File.ReadAllText(viewFile);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string FindSrcDirectory()
    {
        // Walk up from the test assembly location to find the src/ directory
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var srcCandidate = Path.Combine(dir, "src");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not find src/ directory from " + AppContext.BaseDirectory);
    }
}
