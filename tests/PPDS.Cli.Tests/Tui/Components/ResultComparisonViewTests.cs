using System.Data;
using PPDS.Cli.Tui.Components;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Components;

[Trait("Category", "TuiUnit")]
public sealed class ResultComparisonViewTests
{
    [Fact]
    public void CompareResults_IdenticalTables_AllInBoth()
    {
        var tableA = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableA, 1, "Alpha");
        AddRow(tableA, 2, "Beta");

        var tableB = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableB, 1, "Alpha");
        AddRow(tableB, 2, "Beta");

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Empty(result.InAOnly);
        Assert.Empty(result.InBOnly);
        Assert.Equal(2, result.InBoth);
    }

    [Fact]
    public void CompareResults_CompletelyDifferent_NoneInBoth()
    {
        var tableA = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableA, 1, "Alpha");
        AddRow(tableA, 2, "Beta");

        var tableB = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableB, 3, "Gamma");
        AddRow(tableB, 4, "Delta");

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Equal(2, result.InAOnly.Count);
        Assert.Equal(2, result.InBOnly.Count);
        Assert.Equal(0, result.InBoth);
    }

    [Fact]
    public void CompareResults_PartialOverlap_CorrectCounts()
    {
        var tableA = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableA, 1, "Alpha");
        AddRow(tableA, 2, "Beta");
        AddRow(tableA, 3, "Gamma");

        var tableB = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableB, 2, "Beta");
        AddRow(tableB, 3, "Gamma");
        AddRow(tableB, 4, "Delta");

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Single(result.InAOnly);     // Alpha
        Assert.Single(result.InBOnly);     // Delta
        Assert.Equal(2, result.InBoth);     // Beta, Gamma
    }

    [Fact]
    public void CompareResults_EmptyTableA_AllInBOnly()
    {
        var tableA = CreateTable(("id", typeof(int)));
        var tableB = CreateTable(("id", typeof(int)));
        AddRow(tableB, 1);
        AddRow(tableB, 2);

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Empty(result.InAOnly);
        Assert.Equal(2, result.InBOnly.Count);
        Assert.Equal(0, result.InBoth);
    }

    [Fact]
    public void CompareResults_EmptyTableB_AllInAOnly()
    {
        var tableA = CreateTable(("id", typeof(int)));
        AddRow(tableA, 1);
        AddRow(tableA, 2);

        var tableB = CreateTable(("id", typeof(int)));

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Equal(2, result.InAOnly.Count);
        Assert.Empty(result.InBOnly);
        Assert.Equal(0, result.InBoth);
    }

    [Fact]
    public void CompareResults_BothEmpty_AllZeros()
    {
        var tableA = CreateTable(("id", typeof(int)));
        var tableB = CreateTable(("id", typeof(int)));

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Empty(result.InAOnly);
        Assert.Empty(result.InBOnly);
        Assert.Equal(0, result.InBoth);
    }

    [Fact]
    public void CompareResults_DuplicateRows_HandledCorrectly()
    {
        // When using HashSet, duplicates in a single table collapse to one
        var tableA = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableA, 1, "Alpha");
        AddRow(tableA, 1, "Alpha"); // Duplicate

        var tableB = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableB, 1, "Alpha");

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Empty(result.InAOnly);
        Assert.Empty(result.InBOnly);
        Assert.Equal(1, result.InBoth); // Deduped
    }

    [Fact]
    public void CompareResults_NullValues_HandledCorrectly()
    {
        var tableA = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableA, 1, DBNull.Value);

        var tableB = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableB, 1, DBNull.Value);

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Equal(1, result.InBoth);
    }

    [Fact]
    public void RowToString_FormatsCorrectly()
    {
        var table = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(table, 42, "Test");

        var row = table.Rows[0];
        var result = ResultComparisonView.RowToString(row);

        Assert.Equal("42 | Test", result);
    }

    [Fact]
    public void RowToString_NullValue_ShowsNullMarker()
    {
        var table = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        table.Rows.Add(1, DBNull.Value);

        var row = table.Rows[0];
        var result = ResultComparisonView.RowToString(row);

        Assert.Equal("1 | (null)", result);
    }

    [Fact]
    public void DataTableToRowSet_ConvertsAllRows()
    {
        var table = CreateTable(("id", typeof(int)));
        AddRow(table, 1);
        AddRow(table, 2);
        AddRow(table, 3);

        var set = ResultComparisonView.DataTableToRowSet(table);

        Assert.Equal(3, set.Count);
        Assert.Contains("1", set);
        Assert.Contains("2", set);
        Assert.Contains("3", set);
    }

    [Fact]
    public void CompareResults_InAOnlyStrings_AreFormattedRows()
    {
        var tableA = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        AddRow(tableA, 1, "Alpha");

        var tableB = CreateTable(("id", typeof(int)), ("name", typeof(string)));
        // Empty B

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Single(result.InAOnly);
        Assert.Equal("1 | Alpha", result.InAOnly[0]);
    }

    [Fact]
    public void CompareResults_LargeDatasets_CompletesQuickly()
    {
        var tableA = CreateTable(("id", typeof(int)), ("value", typeof(string)));
        var tableB = CreateTable(("id", typeof(int)), ("value", typeof(string)));

        for (int i = 0; i < 1000; i++)
        {
            AddRow(tableA, i, $"Value_{i}");
        }
        for (int i = 500; i < 1500; i++)
        {
            AddRow(tableB, i, $"Value_{i}");
        }

        var result = ResultComparisonView.CompareResults(tableA, tableB);

        Assert.Equal(500, result.InAOnly.Count);   // 0-499
        Assert.Equal(500, result.InBOnly.Count);   // 1000-1499
        Assert.Equal(500, result.InBoth);            // 500-999
    }

    #region Helpers

    private static DataTable CreateTable(params (string name, Type type)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }
        return table;
    }

    private static void AddRow(DataTable table, params object[] values)
    {
        table.Rows.Add(values);
    }

    #endregion
}
