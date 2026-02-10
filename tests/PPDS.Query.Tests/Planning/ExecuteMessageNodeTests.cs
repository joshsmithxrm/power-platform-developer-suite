using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ExecuteMessageNodeTests
{
    // ────────────────────────────────────────────
    //  Constructor validation
    // ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullMessageName_Throws()
    {
        var act = () => new ExecuteMessageNode(null!, new List<MessageParameter>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullParameters_Throws()
    {
        var act = () => new ExecuteMessageNode("WhoAmI", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Description and metadata
    // ────────────────────────────────────────────

    [Fact]
    public void Description_ContainsMessageName()
    {
        var node = new ExecuteMessageNode("WhoAmI", new List<MessageParameter>());
        node.Description.Should().Contain("WhoAmI");
    }

    [Fact]
    public void Description_NoParams_OmitsParamCount()
    {
        var node = new ExecuteMessageNode("WhoAmI", new List<MessageParameter>());
        node.Description.Should().Be("ExecuteMessage: WhoAmI");
    }

    [Fact]
    public void Description_WithParams_IncludesParamCount()
    {
        var parameters = new List<MessageParameter>
        {
            new("Target", "some-guid"),
            new("State", "0")
        };
        var node = new ExecuteMessageNode("SetState", parameters);
        node.Description.Should().Contain("2 params");
    }

    [Fact]
    public void Children_IsEmpty()
    {
        var node = new ExecuteMessageNode("WhoAmI", new List<MessageParameter>());
        node.Children.Should().BeEmpty();
    }

    [Fact]
    public void EstimatedRows_IsOne()
    {
        var node = new ExecuteMessageNode("WhoAmI", new List<MessageParameter>());
        node.EstimatedRows.Should().Be(1);
    }

    // ────────────────────────────────────────────
    //  Execution
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ThrowsNotSupportedException()
    {
        var node = new ExecuteMessageNode("WhoAmI", new List<MessageParameter>());
        var act = async () => await TestHelpers.CollectRowsAsync(node);
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*EXECUTE*not yet supported*");
    }

    // ────────────────────────────────────────────
    //  MessageParameter
    // ────────────────────────────────────────────

    [Fact]
    public void MessageParameter_NullName_Throws()
    {
        var act = () => new MessageParameter(null!, "value");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MessageParameter_StoresNameAndValue()
    {
        var param = new MessageParameter("Target", "some-guid");
        param.Name.Should().Be("Target");
        param.Value.Should().Be("some-guid");
    }

    [Fact]
    public void MessageParameter_NullValueAllowed()
    {
        var param = new MessageParameter("OptionalParam", null);
        param.Name.Should().Be("OptionalParam");
        param.Value.Should().BeNull();
    }
}
