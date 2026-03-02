using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;
using PPDS.Query.Planning.Nodes;
using PPDS.Query.Tests.Planning;
using Xunit;

namespace PPDS.Query.Tests.Execution;

[Trait("Category", "Unit")]
public class SessionContextCursorTests
{
    // ────────────────────────────────────────────
    //  DeclareCursor
    // ────────────────────────────────────────────

    [Fact]
    public void DeclareCursor_ValidName_RegistersCursor()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");

        session.DeclareCursor("cursor1", queryNode);

        session.CursorExists("cursor1").Should().BeTrue();
    }

    [Fact]
    public void DeclareCursor_DuplicateName_Throws()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");

        session.DeclareCursor("cursor1", queryNode);

        var act = () => session.DeclareCursor("cursor1", queryNode);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public void DeclareCursor_CaseInsensitive()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");

        session.DeclareCursor("MyCursor", queryNode);

        session.CursorExists("mycursor").Should().BeTrue();
        session.CursorExists("MYCURSOR").Should().BeTrue();
    }

    // ────────────────────────────────────────────
    //  OpenCursorAsync
    // ────────────────────────────────────────────

    [Fact]
    public async Task OpenCursorAsync_DeclaredCursor_OpensAndMaterializesRows()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Row1")),
            TestSourceNode.MakeRow("account", ("name", "Row2")));

        session.DeclareCursor("c1", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("c1", context);

        session.IsCursorOpen("c1").Should().BeTrue();
    }

    [Fact]
    public async Task OpenCursorAsync_UndeclaredCursor_Throws()
    {
        var session = new SessionContext();
        var context = TestHelpers.CreateTestContext();

        var act = async () => await session.OpenCursorAsync("nope", context);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public async Task OpenCursorAsync_AlreadyOpen_Throws()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");
        session.DeclareCursor("c1", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("c1", context);

        var act = async () => await session.OpenCursorAsync("c1", context);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already open*");
    }

    // ────────────────────────────────────────────
    //  FetchNextFromCursor
    // ────────────────────────────────────────────

    [Fact]
    public async Task FetchNextFromCursor_ReturnsRowsInOrder()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Alpha")),
            TestSourceNode.MakeRow("account", ("name", "Beta")),
            TestSourceNode.MakeRow("account", ("name", "Gamma")));

        session.DeclareCursor("c1", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("c1", context);

        var row1 = session.FetchNextFromCursor("c1");
        row1.Should().NotBeNull();
        row1!.Values["name"].Value.Should().Be("Alpha");
        session.FetchStatus.Should().Be(0);

        var row2 = session.FetchNextFromCursor("c1");
        row2!.Values["name"].Value.Should().Be("Beta");
        session.FetchStatus.Should().Be(0);

        var row3 = session.FetchNextFromCursor("c1");
        row3!.Values["name"].Value.Should().Be("Gamma");
        session.FetchStatus.Should().Be(0);
    }

    [Fact]
    public async Task FetchNextFromCursor_PastEnd_ReturnsNullAndSetsStatusMinusOne()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("id", 1)));

        session.DeclareCursor("c1", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("c1", context);

        session.FetchNextFromCursor("c1"); // Row 1
        session.FetchStatus.Should().Be(0);

        var pastEnd = session.FetchNextFromCursor("c1"); // Past end
        pastEnd.Should().BeNull();
        session.FetchStatus.Should().Be(-1);
    }

    [Fact]
    public async Task FetchNextFromCursor_EmptyCursor_ImmediatelyPastEnd()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");

        session.DeclareCursor("c1", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("c1", context);

        var row = session.FetchNextFromCursor("c1");
        row.Should().BeNull();
        session.FetchStatus.Should().Be(-1);
    }

    [Fact]
    public void FetchNextFromCursor_UndeclaredCursor_Throws()
    {
        var session = new SessionContext();

        var act = () => session.FetchNextFromCursor("nope");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public void FetchNextFromCursor_ClosedCursor_Throws()
    {
        var session = new SessionContext();
        session.DeclareCursor("c1", TestSourceNode.Create("account"));

        var act = () => session.FetchNextFromCursor("c1");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not open*");
    }

    // ────────────────────────────────────────────
    //  CloseCursor
    // ────────────────────────────────────────────

    [Fact]
    public async Task CloseCursor_OpenCursor_MarksAsClosed()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");
        session.DeclareCursor("c1", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("c1", context);

        session.CloseCursor("c1");

        session.IsCursorOpen("c1").Should().BeFalse();
        session.CursorExists("c1").Should().BeTrue();
    }

    [Fact]
    public void CloseCursor_UndeclaredCursor_Throws()
    {
        var session = new SessionContext();

        var act = () => session.CloseCursor("nope");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public void CloseCursor_AlreadyClosed_Throws()
    {
        var session = new SessionContext();
        session.DeclareCursor("c1", TestSourceNode.Create("account"));

        var act = () => session.CloseCursor("c1");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not open*");
    }

    // ────────────────────────────────────────────
    //  DeallocateCursor
    // ────────────────────────────────────────────

    [Fact]
    public void DeallocateCursor_ClosedCursor_RemovesFromSession()
    {
        var session = new SessionContext();
        session.DeclareCursor("c1", TestSourceNode.Create("account"));

        session.DeallocateCursor("c1");

        session.CursorExists("c1").Should().BeFalse();
    }

    [Fact]
    public async Task DeallocateCursor_OpenCursor_Throws()
    {
        var session = new SessionContext();
        session.DeclareCursor("c1", TestSourceNode.Create("account"));

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("c1", context);

        var act = () => session.DeallocateCursor("c1");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*still open*");
    }

    [Fact]
    public void DeallocateCursor_UndeclaredCursor_Throws()
    {
        var session = new SessionContext();

        var act = () => session.DeallocateCursor("nope");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    // ────────────────────────────────────────────
    //  FetchStatus initial value
    // ────────────────────────────────────────────

    [Fact]
    public void FetchStatus_InitialValue_IsMinusOne()
    {
        var session = new SessionContext();
        session.FetchStatus.Should().Be(-1);
    }

    [Fact]
    public async Task FetchStatus_ResetOnOpen()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("id", 1)));

        session.DeclareCursor("c1", queryNode);
        var context = TestHelpers.CreateTestContext();

        // Open and fetch to set status to 0
        await session.OpenCursorAsync("c1", context);
        session.FetchNextFromCursor("c1");
        session.FetchStatus.Should().Be(0);

        // Fetch past end to set status to -1
        session.FetchNextFromCursor("c1");
        session.FetchStatus.Should().Be(-1);

        // Close and reopen should reset
        session.CloseCursor("c1");
        await session.OpenCursorAsync("c1", context);
        session.FetchStatus.Should().Be(-1);
    }

    // ────────────────────────────────────────────
    //  CallerObjectId
    // ────────────────────────────────────────────

    [Fact]
    public void CallerObjectId_InitiallyNull()
    {
        var session = new SessionContext();
        session.CallerObjectId.Should().BeNull();
    }

    [Fact]
    public void CallerObjectId_CanSetAndClear()
    {
        var session = new SessionContext();
        var guid = Guid.NewGuid();

        session.CallerObjectId = guid;
        session.CallerObjectId.Should().Be(guid);

        session.CallerObjectId = null;
        session.CallerObjectId.Should().BeNull();
    }

    // ────────────────────────────────────────────
    //  Multiple cursors
    // ────────────────────────────────────────────

    [Fact]
    public async Task MultipleCursors_IndependentState()
    {
        var session = new SessionContext();
        var context = TestHelpers.CreateTestContext();

        var q1 = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "A1")),
            TestSourceNode.MakeRow("account", ("name", "A2")));
        var q2 = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("name", "C1")));

        session.DeclareCursor("c1", q1);
        session.DeclareCursor("c2", q2);

        await session.OpenCursorAsync("c1", context);
        await session.OpenCursorAsync("c2", context);

        // Fetch from c1
        var row1 = session.FetchNextFromCursor("c1");
        row1!.Values["name"].Value.Should().Be("A1");

        // Fetch from c2 doesn't affect c1's position
        var row2 = session.FetchNextFromCursor("c2");
        row2!.Values["name"].Value.Should().Be("C1");

        // Continue fetching c1
        var row3 = session.FetchNextFromCursor("c1");
        row3!.Values["name"].Value.Should().Be("A2");

        // c2 is past end
        var c2End = session.FetchNextFromCursor("c2");
        c2End.Should().BeNull();
        session.FetchStatus.Should().Be(-1);
    }
}
