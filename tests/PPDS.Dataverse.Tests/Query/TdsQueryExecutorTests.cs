using System;
using System.Collections.Generic;
using FluentAssertions;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Dataverse.Tests.Query;

[Trait("Category", "TuiUnit")]
public class TdsCompatibilityCheckerTests
{
    #region CheckCompatibility

    [Fact]
    public void CheckCompatibility_SelectQuery_ReturnsCompatible()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility("SELECT name FROM account");
        result.Should().Be(TdsCompatibility.Compatible);
    }

    [Fact]
    public void CheckCompatibility_SelectWithWhitespace_ReturnsCompatible()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility("  SELECT name FROM account");
        result.Should().Be(TdsCompatibility.Compatible);
    }

    [Theory]
    [InlineData("INSERT INTO account (name) VALUES ('test')")]
    [InlineData("UPDATE account SET name = 'test'")]
    [InlineData("DELETE FROM account WHERE name = 'test'")]
    [InlineData("MERGE account AS target USING ...")]
    [InlineData("TRUNCATE TABLE account")]
    [InlineData("DROP TABLE account")]
    [InlineData("CREATE TABLE account (id int)")]
    [InlineData("ALTER TABLE account ADD col int")]
    public void CheckCompatibility_DmlStatements_ReturnsIncompatibleDml(string sql)
    {
        var result = TdsCompatibilityChecker.CheckCompatibility(sql);
        result.Should().Be(TdsCompatibility.IncompatibleDml);
    }

    [Theory]
    [InlineData("insert INTO account (name) VALUES ('test')")]
    [InlineData("update account SET name = 'test'")]
    [InlineData("delete FROM account WHERE name = 'test'")]
    public void CheckCompatibility_DmlStatements_CaseInsensitive(string sql)
    {
        var result = TdsCompatibilityChecker.CheckCompatibility(sql);
        result.Should().Be(TdsCompatibility.IncompatibleDml);
    }

    [Fact]
    public void CheckCompatibility_IncompatibleEntity_ReturnsIncompatibleEntity()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility(
            "SELECT name FROM activityparty", "activityparty");
        result.Should().Be(TdsCompatibility.IncompatibleEntity);
    }

    [Fact]
    public void CheckCompatibility_VirtualEntityPrefix_ReturnsIncompatibleEntity()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility(
            "SELECT name FROM virtual_something", "virtual_something");
        result.Should().Be(TdsCompatibility.IncompatibleEntity);
    }

    [Fact]
    public void CheckCompatibility_NullSql_ReturnsIncompatibleFeature()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility(null!);
        result.Should().Be(TdsCompatibility.IncompatibleFeature);
    }

    [Fact]
    public void CheckCompatibility_EmptySql_ReturnsIncompatibleFeature()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility("");
        result.Should().Be(TdsCompatibility.IncompatibleFeature);
    }

    [Fact]
    public void CheckCompatibility_WhitespaceSql_ReturnsIncompatibleFeature()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility("   ");
        result.Should().Be(TdsCompatibility.IncompatibleFeature);
    }

    [Fact]
    public void CheckCompatibility_StandardEntity_ReturnsCompatible()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility(
            "SELECT name FROM account", "account");
        result.Should().Be(TdsCompatibility.Compatible);
    }

    [Fact]
    public void CheckCompatibility_NullEntityName_ReturnsCompatible()
    {
        var result = TdsCompatibilityChecker.CheckCompatibility(
            "SELECT name FROM account", null);
        result.Should().Be(TdsCompatibility.Compatible);
    }

    #endregion

    #region IsDmlStatement

    [Theory]
    [InlineData("INSERT INTO account (name) VALUES ('test')", true)]
    [InlineData("UPDATE account SET name = 'test'", true)]
    [InlineData("DELETE FROM account", true)]
    [InlineData("MERGE target USING source", true)]
    [InlineData("TRUNCATE TABLE account", true)]
    [InlineData("DROP TABLE account", true)]
    [InlineData("CREATE TABLE account (id int)", true)]
    [InlineData("ALTER TABLE account ADD col int", true)]
    [InlineData("SELECT name FROM account", false)]
    [InlineData("  SELECT * FROM contact", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDmlStatement_DetectsCorrectly(string? sql, bool expected)
    {
        TdsCompatibilityChecker.IsDmlStatement(sql!).Should().Be(expected);
    }

    [Fact]
    public void IsDmlStatement_WordStartingWithKeyword_ReturnsFalse()
    {
        // "INSERTING" starts with "INSERT" but is not the INSERT keyword
        TdsCompatibilityChecker.IsDmlStatement("INSERTING data").Should().BeFalse();
    }

    [Fact]
    public void IsDmlStatement_KeywordAlone_ReturnsTrue()
    {
        TdsCompatibilityChecker.IsDmlStatement("DELETE").Should().BeTrue();
    }

    #endregion

    #region IsIncompatibleEntity

    [Theory]
    [InlineData("activityparty", true)]
    [InlineData("msdyn_aborecord", true)]
    [InlineData("msdyn_aaborecord", true)]
    [InlineData("virtual_something", true)]
    [InlineData("account", false)]
    [InlineData("contact", false)]
    [InlineData("opportunity", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsIncompatibleEntity_DetectsCorrectly(string? entity, bool expected)
    {
        TdsCompatibilityChecker.IsIncompatibleEntity(entity!).Should().Be(expected);
    }

    [Fact]
    public void IsIncompatibleEntity_CaseInsensitive()
    {
        TdsCompatibilityChecker.IsIncompatibleEntity("ActivityParty").Should().BeTrue();
        TdsCompatibilityChecker.IsIncompatibleEntity("VIRTUAL_Test").Should().BeTrue();
    }

    #endregion
}

[Trait("Category", "TuiUnit")]
public class TdsQueryExecutorMappingTests
{
    #region MapSqlValue

    [Fact]
    public void MapSqlValue_Null_ReturnsNullQueryValue()
    {
        var result = TdsQueryExecutor.MapSqlValue(null);
        result.Value.Should().BeNull();
    }

    [Fact]
    public void MapSqlValue_DBNull_ReturnsNullQueryValue()
    {
        var result = TdsQueryExecutor.MapSqlValue(DBNull.Value);
        result.Value.Should().BeNull();
    }

    [Fact]
    public void MapSqlValue_String_ReturnsSameString()
    {
        var result = TdsQueryExecutor.MapSqlValue("hello");
        result.Value.Should().Be("hello");
    }

    [Fact]
    public void MapSqlValue_Int_ReturnsSameInt()
    {
        var result = TdsQueryExecutor.MapSqlValue(42);
        result.Value.Should().Be(42);
    }

    [Fact]
    public void MapSqlValue_Long_ReturnsSameLong()
    {
        var result = TdsQueryExecutor.MapSqlValue(123456789L);
        result.Value.Should().Be(123456789L);
    }

    [Fact]
    public void MapSqlValue_Decimal_ReturnsSameDecimal()
    {
        var result = TdsQueryExecutor.MapSqlValue(99.99m);
        result.Value.Should().Be(99.99m);
    }

    [Fact]
    public void MapSqlValue_Float_ReturnsAsDouble()
    {
        var result = TdsQueryExecutor.MapSqlValue(3.14f);
        result.Value.Should().BeOfType<double>();
    }

    [Fact]
    public void MapSqlValue_Double_ReturnsSameDouble()
    {
        var result = TdsQueryExecutor.MapSqlValue(3.14d);
        result.Value.Should().Be(3.14d);
    }

    [Fact]
    public void MapSqlValue_DateTime_ReturnsWithFormatting()
    {
        var dt = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var result = TdsQueryExecutor.MapSqlValue(dt);
        result.Value.Should().Be(dt);
        result.FormattedValue.Should().Be("2025-06-15 10:30:00");
    }

    [Fact]
    public void MapSqlValue_DateTimeOffset_ReturnsUtcDateTimeWithFormatting()
    {
        var dto = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var result = TdsQueryExecutor.MapSqlValue(dto);
        result.Value.Should().Be(dto.UtcDateTime);
        result.FormattedValue.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void MapSqlValue_Guid_ReturnsSameGuid()
    {
        var guid = Guid.NewGuid();
        var result = TdsQueryExecutor.MapSqlValue(guid);
        result.Value.Should().Be(guid);
    }

    [Fact]
    public void MapSqlValue_BoolTrue_ReturnsYes()
    {
        var result = TdsQueryExecutor.MapSqlValue(true);
        result.Value.Should().Be(true);
        result.FormattedValue.Should().Be("Yes");
    }

    [Fact]
    public void MapSqlValue_BoolFalse_ReturnsNo()
    {
        var result = TdsQueryExecutor.MapSqlValue(false);
        result.Value.Should().Be(false);
        result.FormattedValue.Should().Be("No");
    }

    [Fact]
    public void MapSqlValue_ByteArray_ReturnsBinaryDescription()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var result = TdsQueryExecutor.MapSqlValue(bytes);
        result.Value.Should().Be("[Binary: 5 bytes]");
    }

    [Fact]
    public void MapSqlValue_UnknownType_ReturnsToString()
    {
        var value = new Uri("https://example.com");
        var result = TdsQueryExecutor.MapSqlValue(value);
        result.Value.Should().Be("https://example.com/");
    }

    #endregion

    #region MapClrTypeToColumnType

    [Theory]
    [InlineData(typeof(string), QueryColumnType.String)]
    [InlineData(typeof(int), QueryColumnType.Integer)]
    [InlineData(typeof(long), QueryColumnType.BigInt)]
    [InlineData(typeof(decimal), QueryColumnType.Decimal)]
    [InlineData(typeof(float), QueryColumnType.Double)]
    [InlineData(typeof(double), QueryColumnType.Double)]
    [InlineData(typeof(bool), QueryColumnType.Boolean)]
    [InlineData(typeof(DateTime), QueryColumnType.DateTime)]
    [InlineData(typeof(DateTimeOffset), QueryColumnType.DateTime)]
    [InlineData(typeof(Guid), QueryColumnType.Guid)]
    [InlineData(typeof(byte[]), QueryColumnType.Image)]
    public void MapClrTypeToColumnType_MapsCorrectly(Type clrType, QueryColumnType expected)
    {
        TdsQueryExecutor.MapClrTypeToColumnType(clrType).Should().Be(expected);
    }

    [Fact]
    public void MapClrTypeToColumnType_Null_ReturnsUnknown()
    {
        TdsQueryExecutor.MapClrTypeToColumnType(null).Should().Be(QueryColumnType.Unknown);
    }

    [Fact]
    public void MapClrTypeToColumnType_UnknownType_ReturnsUnknown()
    {
        TdsQueryExecutor.MapClrTypeToColumnType(typeof(Uri)).Should().Be(QueryColumnType.Unknown);
    }

    #endregion

    #region InferEntityName

    [Theory]
    [InlineData("SELECT name FROM account", "account")]
    [InlineData("SELECT name FROM Account", "account")]
    [InlineData("SELECT * FROM contact WHERE name = 'test'", "contact")]
    [InlineData("SELECT a, b FROM opportunity;", "opportunity")]
    [InlineData("SELECT COUNT(*) FROM lead", "lead")]
    public void InferEntityName_ExtractsCorrectly(string sql, string expected)
    {
        TdsQueryExecutor.InferEntityName(sql).Should().Be(expected);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("")]
    public void InferEntityName_NoFromClause_ReturnsNull(string sql)
    {
        TdsQueryExecutor.InferEntityName(sql).Should().BeNull();
    }

    [Fact]
    public void InferEntityName_FromFollowedByNothing_ReturnsNull()
    {
        TdsQueryExecutor.InferEntityName("SELECT * FROM ").Should().BeNull();
    }

    #endregion

    #region BuildConnectionString

    [Fact]
    public void BuildConnectionString_IncludesHostAndPort()
    {
        var result = TdsQueryExecutor.BuildConnectionString("org.crm.dynamics.com");
        result.Should().Contain("org.crm.dynamics.com,5558");
    }

    [Fact]
    public void BuildConnectionString_IncludesEncryption()
    {
        var result = TdsQueryExecutor.BuildConnectionString("org.crm.dynamics.com");
        // SqlConnectionStringBuilder serializes Mandatory as "True" in the connection string
        result.Should().Contain("Encrypt=True");
    }

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NullOrgUrl_Throws()
    {
        var act = () => new TdsQueryExecutor(null!, _ => Task.FromResult("token"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_EmptyOrgUrl_Throws()
    {
        var act = () => new TdsQueryExecutor("", _ => Task.FromResult("token"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullTokenProvider_Throws()
    {
        var act = () => new TdsQueryExecutor("https://org.crm.dynamics.com", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var act = () => new TdsQueryExecutor(
            "https://org.crm.dynamics.com",
            _ => Task.FromResult("token"));
        act.Should().NotThrow();
    }

    #endregion

    #region ExecuteSqlAsync Validation

    [Fact]
    public async Task ExecuteSqlAsync_DmlStatement_ThrowsInvalidOperation()
    {
        var executor = new TdsQueryExecutor(
            "https://org.crm.dynamics.com",
            _ => Task.FromResult("token"));

        var act = () => executor.ExecuteSqlAsync("DELETE FROM account");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IncompatibleDml*");
    }

    [Fact]
    public async Task ExecuteSqlAsync_NullSql_ThrowsArgumentException()
    {
        var executor = new TdsQueryExecutor(
            "https://org.crm.dynamics.com",
            _ => Task.FromResult("token"));

        var act = () => executor.ExecuteSqlAsync(null!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteSqlAsync_EmptySql_ThrowsArgumentException()
    {
        var executor = new TdsQueryExecutor(
            "https://org.crm.dynamics.com",
            _ => Task.FromResult("token"));

        var act = () => executor.ExecuteSqlAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion
}
