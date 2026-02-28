using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class QueryValueHelperTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1L)]
    [InlineData((short)1)]
    [InlineData((byte)1)]
    [InlineData(1.0)]
    [InlineData(1.0f)]
    public void IsNumeric_NumericTypes_ReturnsTrue(object value)
        => QueryValueHelper.IsNumeric(value).Should().BeTrue();

    [Theory]
    [InlineData("hello")]
    [InlineData(true)]
    public void IsNumeric_NonNumericTypes_ReturnsFalse(object value)
        => QueryValueHelper.IsNumeric(value).Should().BeFalse();

    [Fact]
    public void GetColumnValue_ExactMatch_ReturnsValue()
    {
        var row = TestSourceNode.MakeRow("test", ("name", "Alice"));
        QueryValueHelper.GetColumnValue(row, "name").Should().Be("Alice");
    }

    [Fact]
    public void GetColumnValue_CaseInsensitive_ReturnsValue()
    {
        var row = TestSourceNode.MakeRow("test", ("Name", "Alice"));
        QueryValueHelper.GetColumnValue(row, "name").Should().Be("Alice");
    }

    [Fact]
    public void GetColumnValue_NoMatch_ReturnsNull()
    {
        var row = TestSourceNode.MakeRow("test", ("name", "Alice"));
        QueryValueHelper.GetColumnValue(row, "missing").Should().BeNull();
    }

    [Fact]
    public void FormatDataType_Int_ReturnsLowercase()
    {
        var dt = new SqlDataTypeReference { SqlDataTypeOption = SqlDataTypeOption.Int };
        QueryValueHelper.FormatDataType(dt).Should().Be("int");
    }

    [Fact]
    public void FormatDataType_NVarCharWithSize_ReturnsFormatted()
    {
        var dt = new SqlDataTypeReference { SqlDataTypeOption = SqlDataTypeOption.NVarChar };
        dt.Parameters.Add(new IntegerLiteral { Value = "50" });
        QueryValueHelper.FormatDataType(dt).Should().Be("nvarchar(50)");
    }
}
