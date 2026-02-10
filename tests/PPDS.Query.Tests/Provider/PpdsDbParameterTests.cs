using System.Data;
using FluentAssertions;
using PPDS.Query.Provider;
using Xunit;

namespace PPDS.Query.Tests.Provider;

[Trait("Category", "Unit")]
public class PpdsDbParameterTests
{
    // ────────────────────────────────────────────
    //  Constructor
    // ────────────────────────────────────────────

    [Fact]
    public void DefaultConstructor_HasDefaults()
    {
        var param = new PpdsDbParameter();

        param.ParameterName.Should().BeEmpty();
        param.Value.Should().BeNull();
        param.Direction.Should().Be(ParameterDirection.Input);
        param.IsNullable.Should().BeTrue();
        param.Size.Should().Be(0);
        param.SourceColumn.Should().BeEmpty();
        param.SourceVersion.Should().Be(DataRowVersion.Current);
    }

    [Fact]
    public void Constructor_WithNameAndValue_SetsProperties()
    {
        var param = new PpdsDbParameter("@name", "Contoso");

        param.ParameterName.Should().Be("@name");
        param.Value.Should().Be("Contoso");
    }

    // ────────────────────────────────────────────
    //  DbType inference
    // ────────────────────────────────────────────

    [Theory]
    [InlineData("hello", DbType.String)]
    [InlineData(42, DbType.Int32)]
    [InlineData(42L, DbType.Int64)]
    [InlineData(3.14, DbType.Double)]
    [InlineData(true, DbType.Boolean)]
    public void DbType_InfersFromValue(object value, DbType expected)
    {
        var param = new PpdsDbParameter("@p", value);
        param.DbType.Should().Be(expected);
    }

    [Fact]
    public void DbType_ExplicitOverride_TakesPrecedence()
    {
        var param = new PpdsDbParameter("@p", "hello");
        param.DbType = DbType.AnsiString;
        param.DbType.Should().Be(DbType.AnsiString);
    }

    [Fact]
    public void ResetDbType_RestoresInference()
    {
        var param = new PpdsDbParameter("@p", 42);
        param.DbType = DbType.String;
        param.DbType.Should().Be(DbType.String);

        param.ResetDbType();
        param.DbType.Should().Be(DbType.Int32); // Back to inferred
    }

    // ────────────────────────────────────────────
    //  SQL literal conversion
    // ────────────────────────────────────────────

    [Fact]
    public void ToSqlLiteral_NullValue_ReturnsNull()
    {
        var param = new PpdsDbParameter("@p", null);
        param.ToSqlLiteral().Should().Be("NULL");
    }

    [Fact]
    public void ToSqlLiteral_DBNullValue_ReturnsNull()
    {
        var param = new PpdsDbParameter("@p", DBNull.Value);
        param.ToSqlLiteral().Should().Be("NULL");
    }

    [Fact]
    public void ToSqlLiteral_StringValue_ReturnsQuoted()
    {
        var param = new PpdsDbParameter("@p", "Contoso");
        param.ToSqlLiteral().Should().Be("'Contoso'");
    }

    [Fact]
    public void ToSqlLiteral_StringWithQuotes_EscapesSingleQuotes()
    {
        var param = new PpdsDbParameter("@p", "O'Brien");
        param.ToSqlLiteral().Should().Be("'O''Brien'");
    }

    [Fact]
    public void ToSqlLiteral_IntegerValue_ReturnsNumber()
    {
        var param = new PpdsDbParameter("@p", 42);
        param.ToSqlLiteral().Should().Be("42");
    }

    [Fact]
    public void ToSqlLiteral_BooleanTrue_Returns1()
    {
        var param = new PpdsDbParameter("@p", true);
        param.ToSqlLiteral().Should().Be("1");
    }

    [Fact]
    public void ToSqlLiteral_BooleanFalse_Returns0()
    {
        var param = new PpdsDbParameter("@p", false);
        param.ToSqlLiteral().Should().Be("0");
    }

    [Fact]
    public void ToSqlLiteral_GuidValue_ReturnsQuotedGuid()
    {
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var param = new PpdsDbParameter("@p", guid);
        param.ToSqlLiteral().Should().Be("'12345678-1234-1234-1234-123456789abc'");
    }

    [Fact]
    public void ToSqlLiteral_DateTimeValue_ReturnsFormattedQuoted()
    {
        var dt = new DateTime(2024, 3, 15, 10, 30, 0);
        var param = new PpdsDbParameter("@p", dt);
        param.ToSqlLiteral().Should().Be("'2024-03-15 10:30:00'");
    }

    // ────────────────────────────────────────────
    //  ParameterName
    // ────────────────────────────────────────────

    [Fact]
    public void ParameterName_SetNull_BecomesEmpty()
    {
        var param = new PpdsDbParameter();
        param.ParameterName = null!;
        param.ParameterName.Should().BeEmpty();
    }
}
