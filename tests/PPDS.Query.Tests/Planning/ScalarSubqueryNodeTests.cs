using FluentAssertions;
using PPDS.Dataverse.Query.Execution;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ScalarSubqueryNodeTests
{
    [Fact]
    public async Task ExecuteScalarAsync_SingleRow_ReturnsScalarValue()
    {
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("cnt", 42)));

        var node = new ScalarSubqueryNode(inner);
        var context = TestHelpers.CreateTestContext();
        var value = await node.ExecuteScalarAsync(context);

        value.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteScalarAsync_NoRows_ReturnsNull()
    {
        var inner = TestSourceNode.Create("contact");

        var node = new ScalarSubqueryNode(inner);
        var context = TestHelpers.CreateTestContext();
        var value = await node.ExecuteScalarAsync(context);

        value.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteScalarAsync_MultipleRows_ThrowsException()
    {
        var inner = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("cnt", 1)),
            TestSourceNode.MakeRow("contact", ("cnt", 2)));

        var node = new ScalarSubqueryNode(inner);
        var context = TestHelpers.CreateTestContext();

        var act = async () => await node.ExecuteScalarAsync(context);
        await act.Should().ThrowAsync<QueryExecutionException>()
            .WithMessage("*more than one*");
    }
}
