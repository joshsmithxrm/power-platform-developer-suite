using FluentAssertions;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class ApplyJoinTests
{
    [Fact]
    public async Task CrossApply_ReEvaluatesInnerPerOuterRow()
    {
        var outer = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("items", 2)),
            TestSourceNode.MakeRow("a", ("id", 2), ("items", 3)));

        // Factory produces N rows based on outer row's "items" value
        var innerFactory = (QueryRow outerRow) =>
        {
            var count = Convert.ToInt32(outerRow.Values["items"].Value);
            return Enumerable.Range(1, count)
                .Select(i => TestSourceNode.MakeRow("j", ("seq", i)));
        };

        var join = new NestedLoopJoinNode(
            outer,
            correlatedInnerFactory: innerFactory,
            joinType: JoinType.CrossApply);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(5); // 2 from first + 3 from second
        rows[0].Values["id"].Value.Should().Be(1);
        rows[0].Values["seq"].Value.Should().Be(1);
        rows[1].Values["id"].Value.Should().Be(1);
        rows[1].Values["seq"].Value.Should().Be(2);
        rows[2].Values["id"].Value.Should().Be(2);
        rows[2].Values["seq"].Value.Should().Be(1);
    }

    [Fact]
    public async Task CrossApply_EmptyInner_SkipsOuterRow()
    {
        var outer = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("items", 2)),
            TestSourceNode.MakeRow("a", ("id", 2), ("items", 0)));

        var innerFactory = (QueryRow outerRow) =>
        {
            var count = Convert.ToInt32(outerRow.Values["items"].Value);
            return Enumerable.Range(1, count)
                .Select(i => TestSourceNode.MakeRow("j", ("seq", i)));
        };

        var join = new NestedLoopJoinNode(
            outer,
            correlatedInnerFactory: innerFactory,
            joinType: JoinType.CrossApply);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(2); // Only from first outer row (items=2)
        rows.All(r => 1.Equals(r.Values["id"].Value)).Should().BeTrue();
    }

    [Fact]
    public async Task OuterApply_EmitsNullsWhenInnerEmpty()
    {
        var outer = TestSourceNode.Create("a",
            TestSourceNode.MakeRow("a", ("id", 1), ("items", 1)),
            TestSourceNode.MakeRow("a", ("id", 2), ("items", 0)));

        // Track the inner schema via a template row
        QueryRow? innerTemplate = null;

        var innerFactory = (QueryRow outerRow) =>
        {
            var count = Convert.ToInt32(outerRow.Values["items"].Value);
            var result = Enumerable.Range(1, count)
                .Select(i => TestSourceNode.MakeRow("j", ("seq", i)))
                .ToList();
            if (result.Count > 0)
                innerTemplate = result[0];
            return result.AsEnumerable();
        };

        var join = new NestedLoopJoinNode(
            outer,
            correlatedInnerFactory: innerFactory,
            joinType: JoinType.OuterApply);

        var rows = await TestHelpers.CollectRowsAsync(join);

        rows.Should().HaveCount(2);
        rows[0].Values["id"].Value.Should().Be(1);
        rows[0].Values["seq"].Value.Should().Be(1);
        rows[1].Values["id"].Value.Should().Be(2);
        // OUTER APPLY: the inner column "seq" should be null for the empty inner
        rows[1].Values["seq"].Value.Should().BeNull();
    }

    [Fact]
    public void Constructor_CrossApply_WithNonApplyJoinType_Throws()
    {
        var outer = TestSourceNode.Create("a");
        var factory = (QueryRow _) => Enumerable.Empty<QueryRow>();

        var act = () => new NestedLoopJoinNode(outer, correlatedInnerFactory: factory, joinType: JoinType.Inner);
        act.Should().Throw<ArgumentException>();
    }
}
