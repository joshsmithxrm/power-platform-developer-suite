using PPDS.Cli.Services;
using PPDS.Cli.Services.Solutions;
using Xunit;

namespace PPDS.Cli.Tests.Services;

/// <summary>
/// Tests for <see cref="SolutionComponentGrouper"/> covering the pre-extraction
/// behavior of <c>SolutionsScreen</c> (LINQ <c>GroupBy</c> + <c>OrderBy</c>).
/// </summary>
[Trait("Category", "Unit")]
public sealed class SolutionComponentGrouperTests
{
    private static SolutionComponentInfo MakeComponent(string typeName, string? displayName = null)
    {
        return new SolutionComponentInfo(
            Id: Guid.NewGuid(),
            ObjectId: Guid.NewGuid(),
            ComponentType: 1,
            ComponentTypeName: typeName,
            RootComponentBehavior: 0,
            IsMetadata: false,
            DisplayName: displayName);
    }

    [Fact]
    public void Group_EmptyInput_ReturnsEmpty()
    {
        var result = SolutionComponentGrouper.Group([]);

        Assert.Empty(result);
    }

    [Fact]
    public void Group_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SolutionComponentGrouper.Group(null!));
    }

    [Fact]
    public void Group_SingleType_ReturnsOneGroup()
    {
        var a = MakeComponent("Entity", "account");
        var b = MakeComponent("Entity", "contact");

        var result = SolutionComponentGrouper.Group([a, b]);

        var group = Assert.Single(result);
        Assert.Equal("Entity", group.TypeName);
        Assert.Equal(2, group.Components.Count);
    }

    [Fact]
    public void Group_MultipleTypes_SortsAlphabetically()
    {
        // Deliberately unordered input.
        var workflow = MakeComponent("Workflow");
        var entity = MakeComponent("Entity");
        var optionSet = MakeComponent("OptionSet");
        var attribute = MakeComponent("Attribute");

        var result = SolutionComponentGrouper.Group([workflow, entity, optionSet, attribute]);

        Assert.Equal(
            ["Attribute", "Entity", "OptionSet", "Workflow"],
            result.Select(g => g.TypeName));
    }

    [Fact]
    public void Group_PreservesComponentOrderWithinGroup()
    {
        var first = MakeComponent("Entity", "account");
        var second = MakeComponent("Entity", "contact");
        var third = MakeComponent("Entity", "lead");

        var result = SolutionComponentGrouper.Group([first, second, third]);

        var group = Assert.Single(result);
        Assert.Equal([first, second, third], group.Components);
    }

    /// <summary>
    /// Regression guard: the shape produced by the grouper must match the
    /// pre-extraction LINQ chain exactly so the TUI dialog and Extension panel
    /// render identical groupings.
    /// </summary>
    [Fact]
    public void Group_MatchesPreExtractionLinqShape()
    {
        var components = new List<SolutionComponentInfo>
        {
            MakeComponent("Workflow", "w1"),
            MakeComponent("Entity", "account"),
            MakeComponent("Workflow", "w2"),
            MakeComponent("Entity", "contact"),
            MakeComponent("OptionSet", "priority"),
        };

        // Old shape — captured verbatim from SolutionsScreen.cs prior to extraction.
        var expected = components
            .GroupBy(c => c.ComponentTypeName)
            .OrderBy(g => g.Key)
            .Select(g => (TypeName: g.Key, Components: g.ToList()))
            .ToList();

        var actual = SolutionComponentGrouper.Group(components);

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].TypeName, actual[i].TypeName);
            Assert.Equal(expected[i].Components, actual[i].Components);
        }
    }
}
