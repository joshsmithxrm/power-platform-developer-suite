using PPDS.Cli.Tui.Views;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Views;

/// <summary>
/// Tests for QueryResultConverter value formatting and DataTable conversion.
/// </summary>
public class QueryResultConverterTests
{
    #region FormatValue Tests

    [Fact]
    public void FormatValue_NullValue_ReturnsEmptyString()
    {
        var value = new QueryValue { Value = null };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatValue_WithFormattedValue_ReturnsFormattedValue()
    {
        var value = new QueryValue
        {
            Value = 42,
            FormattedValue = "Forty-Two"
        };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal("Forty-Two", result);
    }

    [Fact]
    public void FormatValue_DateTime_FormatsCorrectly()
    {
        var dt = new DateTime(2026, 1, 6, 14, 30, 45);
        var value = new QueryValue { Value = dt };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal("2026-01-06 14:30:45", result);
    }

    [Fact]
    public void FormatValue_DateTimeOffset_FormatsCorrectly()
    {
        var dto = new DateTimeOffset(2026, 1, 6, 14, 30, 45, TimeSpan.Zero);
        var value = new QueryValue { Value = dto };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal("2026-01-06 14:30:45", result);
    }

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    public void FormatValue_Boolean_FormatsCorrectly(bool input, string expected)
    {
        var value = new QueryValue { Value = input };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatValue_Decimal_FormatsWithTwoDecimals()
    {
        var value = new QueryValue { Value = 1234.5678m };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal("1,234.57", result);
    }

    [Fact]
    public void FormatValue_Double_FormatsWithTwoDecimals()
    {
        var value = new QueryValue { Value = 1234.5678d };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal("1,234.57", result);
    }

    [Fact]
    public void FormatValue_Guid_ReturnsGuidString()
    {
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789012");
        var value = new QueryValue { Value = guid };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal("12345678-1234-1234-1234-123456789012", result);
    }

    [Fact]
    public void FormatValue_String_ReturnsString()
    {
        var value = new QueryValue { Value = "Hello World" };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void FormatValue_Integer_ReturnsString()
    {
        var value = new QueryValue { Value = 42 };

        var result = QueryResultConverter.FormatValue(value);

        Assert.Equal("42", result);
    }

    #endregion

    #region ToDataTable Tests

    [Fact]
    public void ToDataTable_EmptyResult_ReturnsEmptyTable()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "accountid", DataType = QueryColumnType.Guid },
                new() { LogicalName = "name", DataType = QueryColumnType.String }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
            Count = 0
        };

        var table = QueryResultConverter.ToDataTable(result);

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("accountid", table.Columns[0].ColumnName);
        Assert.Equal("name", table.Columns[1].ColumnName);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void ToDataTable_WithRecords_ConvertsCorrectly()
    {
        var id = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "accountid", DataType = QueryColumnType.Guid },
                new() { LogicalName = "name", DataType = QueryColumnType.String }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["accountid"] = new() { Value = id },
                    ["name"] = new() { Value = "Contoso" }
                }
            },
            Count = 1
        };

        var table = QueryResultConverter.ToDataTable(result);

        Assert.Single(table.Rows);
        Assert.Equal(id.ToString(), table.Rows[0]["accountid"]);
        Assert.Equal("Contoso", table.Rows[0]["name"]);
    }

    [Fact]
    public void ToDataTable_MissingColumnValue_LeavesEmpty()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "accountid", DataType = QueryColumnType.Guid },
                new() { LogicalName = "name", DataType = QueryColumnType.String }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    // Only accountid, no name
                    ["accountid"] = new() { Value = Guid.NewGuid() }
                }
            },
            Count = 1
        };

        var table = QueryResultConverter.ToDataTable(result);

        Assert.Single(table.Rows);
        Assert.Equal(DBNull.Value, table.Rows[0]["name"]);
    }

    [Fact]
    public void ToDataTable_MultipleRecords_ConvertsAll()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "name", DataType = QueryColumnType.String }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue> { ["name"] = new() { Value = "First" } },
                new Dictionary<string, QueryValue> { ["name"] = new() { Value = "Second" } },
                new Dictionary<string, QueryValue> { ["name"] = new() { Value = "Third" } }
            },
            Count = 3
        };

        var table = QueryResultConverter.ToDataTable(result);

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("First", table.Rows[0]["name"]);
        Assert.Equal("Second", table.Rows[1]["name"]);
        Assert.Equal("Third", table.Rows[2]["name"]);
    }

    [Fact]
    public void ToDataTable_DuplicateColumnNames_DisambiguatesWithSuffix()
    {
        // This can happen with SQL like: SELECT accountid, accountid FROM account
        var id = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "accountid", DataType = QueryColumnType.Guid },
                new() { LogicalName = "accountid", DataType = QueryColumnType.Guid }, // Duplicate
                new() { LogicalName = "name", DataType = QueryColumnType.String }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["accountid"] = new() { Value = id },
                    ["name"] = new() { Value = "Contoso" }
                }
            },
            Count = 1
        };

        // Should not throw DuplicateNameException
        var table = QueryResultConverter.ToDataTable(result);

        // First column keeps original name, second gets _1 suffix
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal("accountid", table.Columns[0].ColumnName);
        Assert.Equal("accountid_1", table.Columns[1].ColumnName);
        Assert.Equal("name", table.Columns[2].ColumnName);

        // Both duplicate columns get the same value (from the record dict)
        Assert.Equal(id.ToString(), table.Rows[0][0]);
        Assert.Equal(id.ToString(), table.Rows[0][1]);
        Assert.Equal("Contoso", table.Rows[0][2]);
    }

    [Fact]
    public void ToDataTable_ThreeDuplicateColumnNames_DisambiguatesAll()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "name", DataType = QueryColumnType.String },
                new() { LogicalName = "name", DataType = QueryColumnType.String },
                new() { LogicalName = "name", DataType = QueryColumnType.String }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = new() { Value = "Test" }
                }
            },
            Count = 1
        };

        var table = QueryResultConverter.ToDataTable(result);

        Assert.Equal(3, table.Columns.Count);
        Assert.Equal("name", table.Columns[0].ColumnName);
        Assert.Equal("name_1", table.Columns[1].ColumnName);
        Assert.Equal("name_2", table.Columns[2].ColumnName);
    }

    #endregion

}
