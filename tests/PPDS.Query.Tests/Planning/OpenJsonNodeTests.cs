using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class OpenJsonNodeTests
{
    [Fact]
    public async Task ExecuteAsync_JsonArray_ReturnsKeyValueRows()
    {
        var json = "[\"red\",\"green\",\"blue\"]";
        CompiledScalarExpression jsonExpr = _ => json;

        var node = new OpenJsonNode(jsonExpr);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["key"].Value.Should().Be("0");
        rows[0].Values["value"].Value.Should().Be("red");
        rows[0].Values["type"].Value.Should().Be(1); // string type
        rows[1].Values["value"].Value.Should().Be("green");
        rows[2].Values["value"].Value.Should().Be("blue");
    }

    [Fact]
    public async Task ExecuteAsync_JsonObject_ReturnsPropertyRows()
    {
        var json = "{\"name\":\"Contoso\",\"city\":\"Redmond\",\"count\":42}";
        CompiledScalarExpression jsonExpr = _ => json;

        var node = new OpenJsonNode(jsonExpr);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["key"].Value.Should().Be("name");
        rows[0].Values["value"].Value.Should().Be("Contoso");
        rows[1].Values["key"].Value.Should().Be("city");
        rows[2].Values["key"].Value.Should().Be("count");
        rows[2].Values["value"].Value.Should().Be("42");
    }

    [Fact]
    public async Task ExecuteAsync_NullJson_ReturnsEmpty()
    {
        CompiledScalarExpression jsonExpr = _ => null;

        var node = new OpenJsonNode(jsonExpr);
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithPath_ExtractsNestedArray()
    {
        var json = "{\"data\":[1,2,3]}";
        CompiledScalarExpression jsonExpr = _ => json;

        var node = new OpenJsonNode(jsonExpr, path: "$.data");
        var rows = await TestHelpers.CollectRowsAsync(node);

        rows.Should().HaveCount(3);
        rows[0].Values["value"].Value.Should().Be("1");
    }

    [Fact]
    public void Constructor_NullExpression_Throws()
    {
        var act = () => new OpenJsonNode(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Description_ReturnsOpenJson()
    {
        CompiledScalarExpression jsonExpr = _ => "[]";
        var node = new OpenJsonNode(jsonExpr);
        node.Description.Should().Contain("OpenJson");
    }

    [Fact]
    public void Children_IsEmpty()
    {
        CompiledScalarExpression jsonExpr = _ => "[]";
        var node = new OpenJsonNode(jsonExpr);
        node.Children.Should().BeEmpty();
    }
}
