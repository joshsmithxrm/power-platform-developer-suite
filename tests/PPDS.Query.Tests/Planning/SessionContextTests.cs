using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class SessionContextTests
{
    // ────────────────────────────────────────────
    //  CreateTempTable
    // ────────────────────────────────────────────

    [Fact]
    public void CreateTempTable_ValidName_Succeeds()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "id", "name" });

        ctx.TempTableExists("#temp").Should().BeTrue();
    }

    [Fact]
    public void CreateTempTable_NameWithoutHash_Throws()
    {
        var ctx = new SessionContext();
        var act = () => ctx.CreateTempTable("temp", new[] { "id" });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateTempTable_DuplicateName_Throws()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "id" });

        var act = () => ctx.CreateTempTable("#temp", new[] { "name" });
        act.Should().Throw<InvalidOperationException>().WithMessage("*already*#temp*");
    }

    // ────────────────────────────────────────────
    //  InsertIntoTempTable
    // ────────────────────────────────────────────

    [Fact]
    public void InsertIntoTempTable_ValidTable_Succeeds()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "name" });

        var row = TestSourceNode.MakeRow("temp", ("name", "Alice"));
        ctx.InsertIntoTempTable("#temp", row);

        ctx.GetTempTableRows("#temp").Should().HaveCount(1);
    }

    [Fact]
    public void InsertIntoTempTable_NonexistentTable_Throws()
    {
        var ctx = new SessionContext();
        var row = TestSourceNode.MakeRow("temp", ("name", "Alice"));

        var act = () => ctx.InsertIntoTempTable("#nonexistent", row);
        act.Should().Throw<InvalidOperationException>().WithMessage("*#nonexistent*");
    }

    [Fact]
    public void InsertIntoTempTable_MultipleRows_AllStored()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "name" });

        var rows = new[]
        {
            TestSourceNode.MakeRow("temp", ("name", "Alice")),
            TestSourceNode.MakeRow("temp", ("name", "Bob"))
        };

        ctx.InsertIntoTempTable("#temp", rows);

        ctx.GetTempTableRows("#temp").Should().HaveCount(2);
    }

    // ────────────────────────────────────────────
    //  GetTempTableRows
    // ────────────────────────────────────────────

    [Fact]
    public void GetTempTableRows_EmptyTable_ReturnsEmpty()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "id" });

        ctx.GetTempTableRows("#temp").Should().BeEmpty();
    }

    [Fact]
    public void GetTempTableRows_NonexistentTable_Throws()
    {
        var ctx = new SessionContext();

        var act = () => ctx.GetTempTableRows("#nope");
        act.Should().Throw<InvalidOperationException>();
    }

    // ────────────────────────────────────────────
    //  DropTempTable
    // ────────────────────────────────────────────

    [Fact]
    public void DropTempTable_ExistingTable_Removes()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#temp", new[] { "id" });

        ctx.DropTempTable("#temp");

        ctx.TempTableExists("#temp").Should().BeFalse();
    }

    [Fact]
    public void DropTempTable_NonexistentTable_Throws()
    {
        var ctx = new SessionContext();

        var act = () => ctx.DropTempTable("#nope");
        act.Should().Throw<InvalidOperationException>();
    }

    // ────────────────────────────────────────────
    //  TempTableExists
    // ────────────────────────────────────────────

    [Fact]
    public void TempTableExists_CaseInsensitive()
    {
        var ctx = new SessionContext();
        ctx.CreateTempTable("#Temp", new[] { "id" });

        ctx.TempTableExists("#temp").Should().BeTrue();
        ctx.TempTableExists("#TEMP").Should().BeTrue();
    }

    // ────────────────────────────────────────────
    //  @@ERROR / ERROR_MESSAGE() tracking
    // ────────────────────────────────────────────

    [Fact]
    public void ErrorNumber_DefaultsToZero()
    {
        var session = new SessionContext();
        session.ErrorNumber.Should().Be(0);
    }

    [Fact]
    public void ErrorNumber_SetAndGet()
    {
        var session = new SessionContext();
        session.ErrorNumber = 50000;
        session.ErrorNumber.Should().Be(50000);
    }

    [Fact]
    public void ErrorMessage_DefaultsToEmpty()
    {
        var session = new SessionContext();
        session.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public void ErrorMessage_SetAndGet()
    {
        var session = new SessionContext();
        session.ErrorMessage = "Custom error";
        session.ErrorMessage.Should().Be("Custom error");
    }
}
