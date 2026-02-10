using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class CursorNodeTests
{
    // ────────────────────────────────────────────
    //  DeclareCursorNode
    // ────────────────────────────────────────────

    [Fact]
    public async Task DeclareCursorNode_RegistersCursorInSession()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Contoso")));
        var node = new DeclareCursorNode("myCursor", queryNode, session);

        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
        session.CursorExists("myCursor").Should().BeTrue();
    }

    [Fact]
    public void DeclareCursorNode_NullCursorName_Throws()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");

        var act = () => new DeclareCursorNode(null!, queryNode, session);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeclareCursorNode_NullQueryNode_Throws()
    {
        var session = new SessionContext();

        var act = () => new DeclareCursorNode("cursor1", null!, session);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeclareCursorNode_NullSession_Throws()
    {
        var queryNode = TestSourceNode.Create("account");

        var act = () => new DeclareCursorNode("cursor1", queryNode, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeclareCursorNode_Description_ContainsCursorName()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");
        var node = new DeclareCursorNode("myCursor", queryNode, session);

        node.Description.Should().Contain("myCursor");
    }

    [Fact]
    public void DeclareCursorNode_Children_ContainsQueryNode()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account");
        var node = new DeclareCursorNode("myCursor", queryNode, session);

        node.Children.Should().HaveCount(1);
        node.Children[0].Should().BeSameAs(queryNode);
    }

    // ────────────────────────────────────────────
    //  OpenCursorNode
    // ────────────────────────────────────────────

    [Fact]
    public async Task OpenCursorNode_ExecutesQueryAndMaterializesRows()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Contoso")),
            TestSourceNode.MakeRow("account", ("name", "Fabrikam")));

        session.DeclareCursor("myCursor", queryNode);

        var openNode = new OpenCursorNode("myCursor", session);
        var rows = await TestHelpers.CollectRowsAsync(openNode);

        rows.Should().BeEmpty();
        session.IsCursorOpen("myCursor").Should().BeTrue();
    }

    [Fact]
    public async Task OpenCursorNode_UndeclaredCursor_Throws()
    {
        var session = new SessionContext();
        var openNode = new OpenCursorNode("noCursor", session);

        var act = async () => await TestHelpers.CollectRowsAsync(openNode);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public void OpenCursorNode_NullCursorName_Throws()
    {
        var session = new SessionContext();

        var act = () => new OpenCursorNode(null!, session);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void OpenCursorNode_Description_ContainsCursorName()
    {
        var session = new SessionContext();
        var node = new OpenCursorNode("myCursor", session);

        node.Description.Should().Contain("myCursor");
    }

    // ────────────────────────────────────────────
    //  FetchCursorNode
    // ────────────────────────────────────────────

    [Fact]
    public async Task FetchCursorNode_FetchesRowAndSetsVariables()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Contoso"), ("revenue", 1000)));

        session.DeclareCursor("myCursor", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("myCursor", context);

        var scope = new VariableScope();
        scope.Declare("@name", "NVARCHAR");
        scope.Declare("@revenue", "INT");

        var contextWithScope = new QueryPlanContext(
            context.QueryExecutor,
            variableScope: scope);

        var fetchNode = new FetchCursorNode("myCursor", new[] { "@name", "@revenue" }, session);
        var rows = await TestHelpers.CollectRowsAsync(fetchNode, contextWithScope);

        rows.Should().BeEmpty();
        session.FetchStatus.Should().Be(0);
        scope.Get("@name").Should().Be("Contoso");
        scope.Get("@revenue").Should().Be(1000);
    }

    [Fact]
    public async Task FetchCursorNode_PastEnd_SetsFetchStatusMinusOne()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Only")));

        session.DeclareCursor("myCursor", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("myCursor", context);

        // First fetch: success
        session.FetchNextFromCursor("myCursor");
        session.FetchStatus.Should().Be(0);

        // Second fetch: past end
        session.FetchNextFromCursor("myCursor");
        session.FetchStatus.Should().Be(-1);
    }

    [Fact]
    public void FetchCursorNode_Description_ContainsCursorAndVariables()
    {
        var session = new SessionContext();
        var node = new FetchCursorNode("myCursor", new[] { "@name", "@id" }, session);

        node.Description.Should().Contain("myCursor");
        node.Description.Should().Contain("@name");
        node.Description.Should().Contain("@id");
    }

    // ────────────────────────────────────────────
    //  CloseCursorNode
    // ────────────────────────────────────────────

    [Fact]
    public async Task CloseCursorNode_ClosesOpenCursor()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Test")));

        session.DeclareCursor("myCursor", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("myCursor", context);
        session.IsCursorOpen("myCursor").Should().BeTrue();

        var closeNode = new CloseCursorNode("myCursor", session);
        await TestHelpers.CollectRowsAsync(closeNode);

        session.IsCursorOpen("myCursor").Should().BeFalse();
        session.CursorExists("myCursor").Should().BeTrue();
    }

    [Fact]
    public async Task CloseCursorNode_NotOpenCursor_Throws()
    {
        var session = new SessionContext();
        session.DeclareCursor("myCursor", TestSourceNode.Create("account"));

        var closeNode = new CloseCursorNode("myCursor", session);

        var act = async () => await TestHelpers.CollectRowsAsync(closeNode);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not open*");
    }

    // ────────────────────────────────────────────
    //  DeallocateCursorNode
    // ────────────────────────────────────────────

    [Fact]
    public async Task DeallocateCursorNode_RemovesCursorFromSession()
    {
        var session = new SessionContext();
        session.DeclareCursor("myCursor", TestSourceNode.Create("account"));

        session.CursorExists("myCursor").Should().BeTrue();

        var deallocateNode = new DeallocateCursorNode("myCursor", session);
        await TestHelpers.CollectRowsAsync(deallocateNode);

        session.CursorExists("myCursor").Should().BeFalse();
    }

    [Fact]
    public async Task DeallocateCursorNode_OpenCursor_Throws()
    {
        var session = new SessionContext();
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Test")));

        session.DeclareCursor("myCursor", queryNode);

        var context = TestHelpers.CreateTestContext();
        await session.OpenCursorAsync("myCursor", context);

        var deallocateNode = new DeallocateCursorNode("myCursor", session);

        var act = async () => await TestHelpers.CollectRowsAsync(deallocateNode);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*still open*");
    }

    // ────────────────────────────────────────────
    //  Full cursor lifecycle
    // ────────────────────────────────────────────

    [Fact]
    public async Task FullCursorLifecycle_DeclareOpenFetchCloseDeallocate()
    {
        var session = new SessionContext();
        var context = TestHelpers.CreateTestContext();

        // DECLARE
        var queryNode = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("name", "Row1")),
            TestSourceNode.MakeRow("account", ("name", "Row2")),
            TestSourceNode.MakeRow("account", ("name", "Row3")));

        var declareNode = new DeclareCursorNode("c1", queryNode, session);
        await TestHelpers.CollectRowsAsync(declareNode);
        session.CursorExists("c1").Should().BeTrue();

        // OPEN
        var openNode = new OpenCursorNode("c1", session);
        await TestHelpers.CollectRowsAsync(openNode);
        session.IsCursorOpen("c1").Should().BeTrue();

        // FETCH x3 + 1 past end
        var row1 = session.FetchNextFromCursor("c1");
        session.FetchStatus.Should().Be(0);
        row1.Should().NotBeNull();
        row1!.Values["name"].Value.Should().Be("Row1");

        var row2 = session.FetchNextFromCursor("c1");
        session.FetchStatus.Should().Be(0);
        row2!.Values["name"].Value.Should().Be("Row2");

        var row3 = session.FetchNextFromCursor("c1");
        session.FetchStatus.Should().Be(0);
        row3!.Values["name"].Value.Should().Be("Row3");

        var rowPastEnd = session.FetchNextFromCursor("c1");
        session.FetchStatus.Should().Be(-1);
        rowPastEnd.Should().BeNull();

        // CLOSE
        var closeNode = new CloseCursorNode("c1", session);
        await TestHelpers.CollectRowsAsync(closeNode);
        session.IsCursorOpen("c1").Should().BeFalse();

        // DEALLOCATE
        var deallocateNode = new DeallocateCursorNode("c1", session);
        await TestHelpers.CollectRowsAsync(deallocateNode);
        session.CursorExists("c1").Should().BeFalse();
    }
}
