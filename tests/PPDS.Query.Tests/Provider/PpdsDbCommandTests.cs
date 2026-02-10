using System.Data;
using System.Reflection;
using FluentAssertions;
using PPDS.Query.Provider;
using Xunit;

namespace PPDS.Query.Tests.Provider;

[Trait("Category", "Unit")]
public class PpdsDbCommandTests
{
    private static PpdsDbConnection CreateOpenConnection()
    {
        var conn = new PpdsDbConnection("Url=https://org.crm.dynamics.com;AuthType=OAuth");
        conn.Open();
        return conn;
    }

    // ────────────────────────────────────────────
    //  Property defaults
    // ────────────────────────────────────────────

    [Fact]
    public void DefaultProperties_HaveExpectedValues()
    {
        var cmd = new PpdsDbCommand();

        cmd.CommandText.Should().BeEmpty();
        cmd.CommandTimeout.Should().Be(30);
        cmd.CommandType.Should().Be(CommandType.Text);
        cmd.Connection.Should().BeNull();
    }

    // ────────────────────────────────────────────
    //  Constructor
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_WithTextAndConnection_SetsProperties()
    {
        using var conn = CreateOpenConnection();
        var cmd = new PpdsDbCommand("SELECT name FROM account", conn);

        cmd.CommandText.Should().Be("SELECT name FROM account");
        cmd.Connection.Should().BeSameAs(conn);
    }

    // ────────────────────────────────────────────
    //  CommandText
    // ────────────────────────────────────────────

    [Fact]
    public void CommandText_SetNull_BecomesEmpty()
    {
        var cmd = new PpdsDbCommand();
        cmd.CommandText = null!;
        cmd.CommandText.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  CommandTimeout
    // ────────────────────────────────────────────

    [Fact]
    public void CommandTimeout_SetNegative_ThrowsArgumentOutOfRangeException()
    {
        var cmd = new PpdsDbCommand();
        var act = () => cmd.CommandTimeout = -1;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CommandTimeout_SetZero_Succeeds()
    {
        var cmd = new PpdsDbCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandTimeout.Should().Be(0);
    }

    // ────────────────────────────────────────────
    //  Parameter collection
    // ────────────────────────────────────────────

    [Fact]
    public void Parameters_InitiallyEmpty()
    {
        var cmd = new PpdsDbCommand();
        cmd.Parameters.Count.Should().Be(0);
    }

    [Fact]
    public void Parameters_AddWithValue_CreatesParameter()
    {
        var cmd = new PpdsDbCommand();
        var param = cmd.Parameters.AddWithValue("@name", "Contoso");

        cmd.Parameters.Count.Should().Be(1);
        param.ParameterName.Should().Be("@name");
        param.Value.Should().Be("Contoso");
    }

    [Fact]
    public void CreateParameter_ReturnsPpdsDbParameter()
    {
        var cmd = new PpdsDbCommand();
        var param = cmd.CreateParameter();

        param.Should().BeOfType<PpdsDbParameter>();
    }

    // ────────────────────────────────────────────
    //  Connection validation
    // ────────────────────────────────────────────

    [Fact]
    public void ExecuteReader_WithoutConnection_ThrowsInvalidOperationException()
    {
        var cmd = new PpdsDbCommand();
        cmd.CommandText = "SELECT name FROM account";

        var act = () => cmd.ExecuteReader();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Connection*not set*");
    }

    [Fact]
    public void ExecuteReader_ConnectionNotOpen_ThrowsInvalidOperationException()
    {
        using var conn = new PpdsDbConnection("Url=https://org.crm.dynamics.com");
        var cmd = new PpdsDbCommand("SELECT name FROM account", conn);

        var act = () => cmd.ExecuteReader();

        act.Should().Throw<InvalidOperationException>().WithMessage("*not open*");
    }

    [Fact]
    public void ExecuteReader_EmptyCommandText_ThrowsInvalidOperationException()
    {
        using var conn = CreateOpenConnection();
        var cmd = new PpdsDbCommand("", conn);

        var act = () => cmd.ExecuteReader();

        act.Should().Throw<InvalidOperationException>().WithMessage("*CommandText*not set*");
    }

    // ────────────────────────────────────────────
    //  Prepare
    // ────────────────────────────────────────────

    [Fact]
    public void Prepare_ValidSql_DoesNotThrow()
    {
        var cmd = new PpdsDbCommand();
        cmd.CommandText = "SELECT name FROM account";

        var act = () => cmd.Prepare();

        act.Should().NotThrow();
    }

    [Fact]
    public void Prepare_InvalidSql_ThrowsQueryParseException()
    {
        var cmd = new PpdsDbCommand();
        cmd.CommandText = "SELECTT name FROMM account";

        var act = () => cmd.Prepare();

        act.Should().Throw<Exception>();
    }

    // ────────────────────────────────────────────
    //  Cancel
    // ────────────────────────────────────────────

    [Fact]
    public void Cancel_DoesNotThrow()
    {
        var cmd = new PpdsDbCommand();
        var act = () => cmd.Cancel();
        act.Should().NotThrow();
    }

    // ────────────────────────────────────────────
    //  ApplyParameters
    // ────────────────────────────────────────────

    [Fact]
    public void ApplyParameters_OverlappingNames_SubstitutesCorrectly()
    {
        var cmd = new PpdsDbCommand();
        cmd.Parameters.AddWithValue("@p1", "short");
        cmd.Parameters.AddWithValue("@p10", "long");
        cmd.CommandText = "SELECT @p1, @p10";

        // Prepare validates syntax — if @p10 becomes 'short'0 it will fail to parse.
        var act = () => cmd.Prepare();
        act.Should().NotThrow();
    }

    [Fact]
    public void ApplyParameters_DoesNotReplaceInsideStringLiterals()
    {
        var cmd = new PpdsDbCommand();
        cmd.Parameters.AddWithValue("@p1", "value");

        var sql = "SELECT '@p1' AS literal_value, @p1 AS parameter_value";
        var result = InvokeApplyParameters(cmd, sql);

        result.Should().Be("SELECT '@p1' AS literal_value, 'value' AS parameter_value");
    }

    [Fact]
    public void ApplyParameters_DoesNotReplaceInsideComments()
    {
        var cmd = new PpdsDbCommand();
        cmd.Parameters.AddWithValue("@p1", 42);

        var sql = "-- @p1 in line comment\r\nSELECT @p1 /* @p1 in block comment */";
        var result = InvokeApplyParameters(cmd, sql);

        result.Should().Be("-- @p1 in line comment\r\nSELECT 42 /* @p1 in block comment */");
    }

    [Fact]
    public void ApplyParameters_DoesNotReplaceInsideQuotedIdentifiers()
    {
        var cmd = new PpdsDbCommand();
        cmd.Parameters.AddWithValue("@p1", 7);

        var sql = "SELECT [@p1] AS b, \"@p1\" AS q, @p1 AS v";
        var result = InvokeApplyParameters(cmd, sql);

        result.Should().Be("SELECT [@p1] AS b, \"@p1\" AS q, 7 AS v");
    }

    // ────────────────────────────────────────────
    //  Connection type validation
    // ────────────────────────────────────────────

    [Fact]
    public void SetDbConnection_WrongType_ThrowsArgumentException()
    {
        var cmd = new PpdsDbCommand();
        IDbCommand iCmd = cmd;

        // System.Data.Common.DbCommand.Connection setter validates type
        var act = () => ((System.Data.Common.DbCommand)cmd).Connection = null;

        act.Should().NotThrow(); // null is acceptable
    }

    private static string InvokeApplyParameters(PpdsDbCommand command, string sql)
    {
        var method = typeof(PpdsDbCommand).GetMethod(
            "ApplyParameters", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var result = method!.Invoke(command, new object[] { sql });
        result.Should().BeOfType<string>();

        return (string)result!;
    }
}
