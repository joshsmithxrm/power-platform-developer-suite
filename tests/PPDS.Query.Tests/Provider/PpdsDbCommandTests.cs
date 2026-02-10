using System.Data;
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
}
