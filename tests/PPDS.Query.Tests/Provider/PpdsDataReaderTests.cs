using System.Data;
using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Provider;
using Xunit;

namespace PPDS.Query.Tests.Provider;

[Trait("Category", "Unit")]
public class PpdsDataReaderTests
{
    // ────────────────────────────────────────────
    //  Helper to create reader with test data
    // ────────────────────────────────────────────

    private static PpdsDataReader CreateTestReader(int rowCount = 3)
    {
        var columns = new[]
        {
            new QueryColumn { LogicalName = "accountid", DataType = QueryColumnType.Guid },
            new QueryColumn { LogicalName = "name", DataType = QueryColumnType.String },
            new QueryColumn { LogicalName = "revenue", DataType = QueryColumnType.Money },
            new QueryColumn { LogicalName = "employeecount", DataType = QueryColumnType.Integer },
            new QueryColumn { LogicalName = "isactive", DataType = QueryColumnType.Boolean },
            new QueryColumn { LogicalName = "createdon", DataType = QueryColumnType.DateTime }
        };

        var rows = new List<QueryRow>();
        for (var i = 0; i < rowCount; i++)
        {
            var values = new Dictionary<string, QueryValue>
            {
                ["accountid"] = QueryValue.Simple(Guid.Parse($"00000000-0000-0000-0000-{i:D12}")),
                ["name"] = QueryValue.Simple($"Account {i}"),
                ["revenue"] = QueryValue.Simple((decimal)(1000.0 + i * 100)),
                ["employeecount"] = QueryValue.Simple(50 + i),
                ["isactive"] = QueryValue.Simple(i % 2 == 0),
                ["createdon"] = QueryValue.Simple(new DateTime(2024, 1, 1).AddDays(i))
            };
            rows.Add(new QueryRow(values, "account"));
        }

        return new PpdsDataReader(rows, columns);
    }

    private static PpdsDataReader CreateEmptyReader()
    {
        var columns = new[]
        {
            new QueryColumn { LogicalName = "name", DataType = QueryColumnType.String }
        };
        return new PpdsDataReader(new List<QueryRow>(), columns);
    }

    private static PpdsDataReader CreateReaderWithNulls()
    {
        var columns = new[]
        {
            new QueryColumn { LogicalName = "name", DataType = QueryColumnType.String },
            new QueryColumn { LogicalName = "revenue", DataType = QueryColumnType.Money }
        };

        var values = new Dictionary<string, QueryValue>
        {
            ["name"] = QueryValue.Null,
            ["revenue"] = QueryValue.Null
        };
        var rows = new List<QueryRow> { new QueryRow(values, "account") };

        return new PpdsDataReader(rows, columns);
    }

    // ────────────────────────────────────────────
    //  Properties
    // ────────────────────────────────────────────

    [Fact]
    public void Depth_IsZero()
    {
        using var reader = CreateTestReader();
        reader.Depth.Should().Be(0);
    }

    [Fact]
    public void FieldCount_ReturnsColumnCount()
    {
        using var reader = CreateTestReader();
        reader.FieldCount.Should().Be(6);
    }

    [Fact]
    public void HasRows_WithData_ReturnsTrue()
    {
        using var reader = CreateTestReader();
        reader.HasRows.Should().BeTrue();
    }

    [Fact]
    public void HasRows_Empty_ReturnsFalse()
    {
        using var reader = CreateEmptyReader();
        reader.HasRows.Should().BeFalse();
    }

    [Fact]
    public void IsClosed_InitiallyFalse()
    {
        using var reader = CreateTestReader();
        reader.IsClosed.Should().BeFalse();
    }

    [Fact]
    public void RecordsAffected_ReturnsNegativeOne()
    {
        using var reader = CreateTestReader();
        reader.RecordsAffected.Should().Be(-1);
    }

    // ────────────────────────────────────────────
    //  Read / navigation
    // ────────────────────────────────────────────

    [Fact]
    public void Read_AdvancesRowByRow()
    {
        using var reader = CreateTestReader(3);

        reader.Read().Should().BeTrue();
        reader.GetString(1).Should().Be("Account 0");

        reader.Read().Should().BeTrue();
        reader.GetString(1).Should().Be("Account 1");

        reader.Read().Should().BeTrue();
        reader.GetString(1).Should().Be("Account 2");

        reader.Read().Should().BeFalse(); // Past end
    }

    [Fact]
    public void Read_EmptyReader_ReturnsFalse()
    {
        using var reader = CreateEmptyReader();
        reader.Read().Should().BeFalse();
    }

    [Fact]
    public void NextResult_ReturnsFalse()
    {
        using var reader = CreateTestReader();
        reader.NextResult().Should().BeFalse();
    }

    // ────────────────────────────────────────────
    //  Close
    // ────────────────────────────────────────────

    [Fact]
    public void Close_SetsIsClosed()
    {
        using var reader = CreateTestReader();
        reader.Close();
        reader.IsClosed.Should().BeTrue();
    }

    [Fact]
    public void Read_AfterClose_ThrowsInvalidOperationException()
    {
        using var reader = CreateTestReader();
        reader.Close();

        var act = () => reader.Read();

        act.Should().Throw<InvalidOperationException>();
    }

    // ────────────────────────────────────────────
    //  Column metadata
    // ────────────────────────────────────────────

    [Fact]
    public void GetName_ReturnsColumnName()
    {
        using var reader = CreateTestReader();

        reader.GetName(0).Should().Be("accountid");
        reader.GetName(1).Should().Be("name");
        reader.GetName(5).Should().Be("createdon");
    }

    [Fact]
    public void GetName_InvalidOrdinal_ThrowsIndexOutOfRange()
    {
        using var reader = CreateTestReader();

        var act = () => reader.GetName(99);

        act.Should().Throw<IndexOutOfRangeException>();
    }

    [Fact]
    public void GetOrdinal_ByName_ReturnsIndex()
    {
        using var reader = CreateTestReader();

        reader.GetOrdinal("name").Should().Be(1);
        reader.GetOrdinal("revenue").Should().Be(2);
    }

    [Fact]
    public void GetOrdinal_CaseInsensitive()
    {
        using var reader = CreateTestReader();

        reader.GetOrdinal("NAME").Should().Be(1);
        reader.GetOrdinal("Name").Should().Be(1);
    }

    [Fact]
    public void GetOrdinal_NotFound_ThrowsIndexOutOfRange()
    {
        using var reader = CreateTestReader();

        var act = () => reader.GetOrdinal("nonexistent");

        act.Should().Throw<IndexOutOfRangeException>();
    }

    // ────────────────────────────────────────────
    //  Typed value accessors
    // ────────────────────────────────────────────

    [Fact]
    public void GetString_ReturnsStringValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetString(1).Should().Be("Account 0");
    }

    [Fact]
    public void GetInt32_ReturnsIntValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetInt32(3).Should().Be(50);
    }

    [Fact]
    public void GetGuid_ReturnsGuidValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetGuid(0).Should().Be(Guid.Parse("00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void GetDateTime_ReturnsDateTimeValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetDateTime(5).Should().Be(new DateTime(2024, 1, 1));
    }

    [Fact]
    public void GetBoolean_ReturnsBoolValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetBoolean(4).Should().BeTrue(); // Index 0: i=0, i%2==0 -> true
    }

    [Fact]
    public void GetDecimal_ReturnsDecimalValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetDecimal(2).Should().Be(1000.0m);
    }

    [Fact]
    public void GetDouble_ReturnsConvertedValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetDouble(2).Should().Be(1000.0);
    }

    // ────────────────────────────────────────────
    //  Value / DBNull
    // ────────────────────────────────────────────

    [Fact]
    public void GetValue_NullValue_ReturnsDBNull()
    {
        using var reader = CreateReaderWithNulls();
        reader.Read();

        reader.GetValue(0).Should().Be(DBNull.Value);
    }

    [Fact]
    public void IsDBNull_NullValue_ReturnsTrue()
    {
        using var reader = CreateReaderWithNulls();
        reader.Read();

        reader.IsDBNull(0).Should().BeTrue();
    }

    [Fact]
    public void IsDBNull_NonNullValue_ReturnsFalse()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.IsDBNull(1).Should().BeFalse();
    }

    // ────────────────────────────────────────────
    //  GetValues
    // ────────────────────────────────────────────

    [Fact]
    public void GetValues_FillsArray()
    {
        using var reader = CreateTestReader();
        reader.Read();

        var values = new object[6];
        var count = reader.GetValues(values);

        count.Should().Be(6);
        values[1].Should().Be("Account 0");
    }

    [Fact]
    public void GetValues_SmallerArray_FillsPartially()
    {
        using var reader = CreateTestReader();
        reader.Read();

        var values = new object[2];
        var count = reader.GetValues(values);

        count.Should().Be(2);
    }

    // ────────────────────────────────────────────
    //  Indexer
    // ────────────────────────────────────────────

    [Fact]
    public void Indexer_ByOrdinal_ReturnsValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader[1].Should().Be("Account 0");
    }

    [Fact]
    public void Indexer_ByName_ReturnsValue()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader["name"].Should().Be("Account 0");
    }

    // ────────────────────────────────────────────
    //  Error cases
    // ────────────────────────────────────────────

    [Fact]
    public void GetValue_BeforeRead_ThrowsInvalidOperationException()
    {
        using var reader = CreateTestReader();

        var act = () => reader.GetValue(0);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetString_FromNullValue_ThrowsInvalidCastException()
    {
        using var reader = CreateReaderWithNulls();
        reader.Read();

        var act = () => reader.GetString(0);

        act.Should().Throw<InvalidCastException>();
    }

    // ────────────────────────────────────────────
    //  GetSchemaTable
    // ────────────────────────────────────────────

    [Fact]
    public void GetSchemaTable_ReturnsColumnMetadata()
    {
        using var reader = CreateTestReader();

        var schema = reader.GetSchemaTable();

        schema.Should().NotBeNull();
        schema!.Rows.Count.Should().Be(6);
        schema.Rows[0]["ColumnName"].Should().Be("accountid");
        schema.Rows[1]["ColumnName"].Should().Be("name");
    }

    // ────────────────────────────────────────────
    //  GetEnumerator
    // ────────────────────────────────────────────

    [Fact]
    public void GetEnumerator_CanIterate()
    {
        using var reader = CreateTestReader(2);

        var enumerator = reader.GetEnumerator();
        var count = 0;
        while (enumerator.MoveNext())
        {
            count++;
        }

        count.Should().Be(2);
    }

    // ────────────────────────────────────────────
    //  FromQueryResult
    // ────────────────────────────────────────────

    [Fact]
    public void FromQueryResult_CreatesWorkingReader()
    {
        var records = new List<IReadOnlyDictionary<string, QueryValue>>
        {
            new Dictionary<string, QueryValue>
            {
                ["name"] = QueryValue.Simple("Test")
            }
        };

        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[] { new QueryColumn { LogicalName = "name" } },
            Records = records,
            Count = 1
        };

        using var reader = PpdsDataReader.FromQueryResult(result);

        reader.FieldCount.Should().Be(1);
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("Test");
        reader.Read().Should().BeFalse();
    }

    // ────────────────────────────────────────────
    //  DataTypeName / FieldType
    // ────────────────────────────────────────────

    [Fact]
    public void GetDataTypeName_ReturnsInferredType()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetDataTypeName(1).Should().Be("nvarchar"); // string
        reader.GetDataTypeName(3).Should().Be("int"); // int
        reader.GetDataTypeName(4).Should().Be("bit"); // bool
    }

    [Fact]
    public void GetFieldType_ReturnsClrType()
    {
        using var reader = CreateTestReader();
        reader.Read();

        reader.GetFieldType(0).Should().Be(typeof(Guid));
        reader.GetFieldType(1).Should().Be(typeof(string));
        reader.GetFieldType(3).Should().Be(typeof(int));
    }
}
