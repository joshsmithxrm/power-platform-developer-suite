using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ImpersonationNodeTests
{
    // ════════════════════════════════════════════════════════════════════
    //  ExecuteAsNode
    // ════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void ExecuteAsNode_NullUserPrincipalName_Throws()
    {
        var session = new SessionContext();
        var act = () => new ExecuteAsNode(null!, session);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ExecuteAsNode_NullSession_Throws()
    {
        var act = () => new ExecuteAsNode("user@domain.com", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void ExecuteAsNode_Description_ContainsUserName()
    {
        var session = new SessionContext();
        var node = new ExecuteAsNode("admin@contoso.com", session);
        node.Description.Should().Contain("admin@contoso.com");
    }

    [Fact]
    public void ExecuteAsNode_Children_IsEmpty()
    {
        var session = new SessionContext();
        var node = new ExecuteAsNode("user@domain.com", session);
        node.Children.Should().BeEmpty();
    }

    [Fact]
    public void ExecuteAsNode_EstimatedRows_IsZero()
    {
        var session = new SessionContext();
        var node = new ExecuteAsNode("user@domain.com", session);
        node.EstimatedRows.Should().Be(0);
    }

    // ────────────────────────────────────────────
    //  Execution
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsNode_WithoutPreResolvedGuid_ThrowsNotSupportedException()
    {
        var session = new SessionContext();
        var node = new ExecuteAsNode("user@contoso.com", session);

        var act = async () => await TestHelpers.CollectRowsAsync(node);
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*EXECUTE AS*systemuserid*");
    }

    [Fact]
    public async Task ExecuteAsNode_WithExplicitGuid_SetsCallerObjectId()
    {
        var session = new SessionContext();
        session.CallerObjectId.Should().BeNull();

        var explicitGuid = Guid.NewGuid();
        var node = new ExecuteAsNode("user@contoso.com", session, explicitGuid);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
        session.CallerObjectId.Should().Be(explicitGuid);
    }

    // ════════════════════════════════════════════════════════════════════
    //  RevertNode
    // ════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void RevertNode_NullSession_Throws()
    {
        var act = () => new RevertNode(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void RevertNode_Description_ContainsRevert()
    {
        var session = new SessionContext();
        var node = new RevertNode(session);
        node.Description.Should().Contain("Revert");
    }

    [Fact]
    public void RevertNode_Children_IsEmpty()
    {
        var session = new SessionContext();
        var node = new RevertNode(session);
        node.Children.Should().BeEmpty();
    }

    [Fact]
    public void RevertNode_EstimatedRows_IsZero()
    {
        var session = new SessionContext();
        var node = new RevertNode(session);
        node.EstimatedRows.Should().Be(0);
    }

    // ────────────────────────────────────────────
    //  Execution
    // ────────────────────────────────────────────

    [Fact]
    public async Task RevertNode_ClearsCallerObjectId()
    {
        var session = new SessionContext();
        session.CallerObjectId = Guid.NewGuid();
        session.CallerObjectId.Should().NotBeNull();

        var node = new RevertNode(session);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
        session.CallerObjectId.Should().BeNull();
    }

    // ────────────────────────────────────────────
    //  ExecuteAs + Revert lifecycle
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsAndRevert_FullLifecycle()
    {
        var session = new SessionContext();
        session.CallerObjectId.Should().BeNull();

        var guid = Guid.NewGuid();

        // EXECUTE AS with explicit GUID
        var executeAsNode = new ExecuteAsNode("admin@contoso.com", session, guid);
        await TestHelpers.CollectRowsAsync(executeAsNode);
        session.CallerObjectId.Should().Be(guid);

        // REVERT
        var revertNode = new RevertNode(session);
        await TestHelpers.CollectRowsAsync(revertNode);
        session.CallerObjectId.Should().BeNull();

        // EXECUTE AS again with same GUID
        var executeAsNode2 = new ExecuteAsNode("admin@contoso.com", session, guid);
        await TestHelpers.CollectRowsAsync(executeAsNode2);
        session.CallerObjectId.Should().Be(guid);
    }
}
