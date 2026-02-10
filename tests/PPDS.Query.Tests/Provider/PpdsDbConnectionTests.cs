using System.Data;
using FluentAssertions;
using PPDS.Query.Provider;
using Xunit;

namespace PPDS.Query.Tests.Provider;

[Trait("Category", "Unit")]
public class PpdsDbConnectionTests
{
    private const string ValidConnectionString = "Url=https://org.crm.dynamics.com;AuthType=OAuth";

    // ────────────────────────────────────────────
    //  Property defaults
    // ────────────────────────────────────────────

    [Fact]
    public void DefaultProperties_HaveExpectedValues()
    {
        using var conn = new PpdsDbConnection();

        conn.BatchSize.Should().Be(100);
        conn.MaxDegreeOfParallelism.Should().Be(5);
        conn.UseTdsEndpoint.Should().BeFalse();
        conn.BlockUpdateWithoutWhere.Should().BeTrue();
        conn.BlockDeleteWithoutWhere.Should().BeTrue();
        conn.State.Should().Be(ConnectionState.Closed);
        conn.Database.Should().BeEmpty();
        conn.ServerVersion.Should().Be("9.2");
    }

    // ────────────────────────────────────────────
    //  Open / Close lifecycle
    // ────────────────────────────────────────────

    [Fact]
    public void Open_WithValidConnectionString_TransitionsToOpen()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);

        conn.Open();

        conn.State.Should().Be(ConnectionState.Open);
    }

    [Fact]
    public void Open_WhenAlreadyOpen_DoesNotThrow()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        conn.Open();

        var act = () => conn.Open();

        act.Should().NotThrow();
        conn.State.Should().Be(ConnectionState.Open);
    }

    [Fact]
    public void Open_WithoutUrl_ThrowsInvalidOperationException()
    {
        using var conn = new PpdsDbConnection("AuthType=OAuth");

        var act = () => conn.Open();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Url*");
    }

    [Fact]
    public void Close_TransitionsToClosed()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        conn.Open();

        conn.Close();

        conn.State.Should().Be(ConnectionState.Closed);
    }

    // ────────────────────────────────────────────
    //  ConnectionString property
    // ────────────────────────────────────────────

    [Fact]
    public void ConnectionString_SetWhenOpen_ThrowsInvalidOperationException()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        conn.Open();

        var act = () => conn.ConnectionString = "Url=https://other.crm.dynamics.com";

        act.Should().Throw<InvalidOperationException>().WithMessage("*open*");
    }

    [Fact]
    public void ConnectionString_SetWhenClosed_Succeeds()
    {
        using var conn = new PpdsDbConnection();

        conn.ConnectionString = ValidConnectionString;

        conn.ConnectionString.Should().Be(ValidConnectionString);
    }

    // ────────────────────────────────────────────
    //  DataSource
    // ────────────────────────────────────────────

    [Fact]
    public void DataSource_ReturnsUrl()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);

        conn.DataSource.Should().Be("https://org.crm.dynamics.com");
    }

    // ────────────────────────────────────────────
    //  CreateCommand
    // ────────────────────────────────────────────

    [Fact]
    public void CreateCommand_ReturnsPpdsDbCommand()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);

        var cmd = conn.CreateCommand();

        cmd.Should().BeOfType<PpdsDbCommand>();
        cmd.Connection.Should().BeSameAs(conn);
    }

    [Fact]
    public void CreateDbCommand_ViaBaseClass_ReturnsPpdsDbCommand()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        using var cmd = ((IDbConnection)conn).CreateCommand();

        cmd.Should().BeAssignableTo<PpdsDbCommand>();
    }

    // ────────────────────────────────────────────
    //  Unsupported operations
    // ────────────────────────────────────────────

    [Fact]
    public void ChangeDatabase_ThrowsNotSupportedException()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);

        var act = () => conn.ChangeDatabase("other");

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void BeginTransaction_ThrowsNotSupportedException()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        conn.Open();

        var act = () => conn.BeginTransaction();

        act.Should().Throw<NotSupportedException>();
    }

    // ────────────────────────────────────────────
    //  Events
    // ────────────────────────────────────────────

    [Fact]
    public void PreInsertEvent_CanBeCancelled()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        conn.PreInsert += (_, args) => args.Cancel = true;

        var cancelled = conn.RaisePreInsert("INSERT INTO account ...", "account", 5);

        cancelled.Should().BeTrue();
    }

    [Fact]
    public void PreUpdateEvent_CanBeCancelled()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        conn.PreUpdate += (_, args) => args.Cancel = true;

        var cancelled = conn.RaisePreUpdate("UPDATE account ...", "account", 10);

        cancelled.Should().BeTrue();
    }

    [Fact]
    public void PreDeleteEvent_CanBeCancelled()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        conn.PreDelete += (_, args) => args.Cancel = true;

        var cancelled = conn.RaisePreDelete("DELETE FROM account ...", "account", 3);

        cancelled.Should().BeTrue();
    }

    [Fact]
    public void ProgressEvent_RaisesWithCorrectData()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);
        ProgressEventArgs? received = null;
        conn.Progress += (_, args) => received = args;

        conn.RaiseProgress("Loading page 2...", 100, 500);

        received.Should().NotBeNull();
        received!.Message.Should().Be("Loading page 2...");
        received.RowsProcessed.Should().Be(100);
        received.TotalRows.Should().Be(500);
        received.PercentComplete.Should().Be(20);
    }

    // ────────────────────────────────────────────
    //  Dispose
    // ────────────────────────────────────────────

    [Fact]
    public void Dispose_ClosesConnection()
    {
        var conn = new PpdsDbConnection(ValidConnectionString);
        conn.Open();

        conn.Dispose();

        conn.State.Should().Be(ConnectionState.Closed);
    }

    // ────────────────────────────────────────────
    //  Constructor with connection string
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_WithConnectionString_SetsProperty()
    {
        using var conn = new PpdsDbConnection(ValidConnectionString);

        conn.ConnectionString.Should().Be(ValidConnectionString);
    }
}
