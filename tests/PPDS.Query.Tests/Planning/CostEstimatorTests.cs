using FluentAssertions;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class CostEstimatorTests
{
    private readonly CostEstimator _estimator = new();

    // ────────────────────────────────────────────
    //  FetchXmlScanNode: uses entity record counts
    // ────────────────────────────────────────────

    [Fact]
    public void Scan_WithEntityRecordCount_ReturnsKnownCount()
    {
        var scan = new FetchXmlScanNode(
            "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>",
            "account");

        var context = new CostContext();
        context.EntityRecordCounts["account"] = 50_000;

        var estimate = _estimator.EstimateCardinality(scan, context);

        estimate.Should().Be(50_000);
    }

    [Fact]
    public void Scan_WithMaxRows_ReturnsMaxRows()
    {
        var scan = new FetchXmlScanNode(
            "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>",
            "account",
            maxRows: 100);

        var context = new CostContext();
        var estimate = _estimator.EstimateCardinality(scan, context);

        estimate.Should().Be(100);
    }

    [Fact]
    public void Scan_NoMetadata_ReturnsDefaultCount()
    {
        var scan = new FetchXmlScanNode(
            "<fetch><entity name=\"unknown_entity\"><all-attributes /></entity></fetch>",
            "unknown_entity");

        var context = new CostContext { DefaultRecordCount = 5_000 };

        var estimate = _estimator.EstimateCardinality(scan, context);

        estimate.Should().Be(5_000);
    }

    // ────────────────────────────────────────────
    //  Filter selectivity heuristics
    // ────────────────────────────────────────────

    [Fact]
    public void Filter_AppliesSelectivity()
    {
        var scanNode = new FetchXmlScanNode(
            "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>",
            "account");

        Dataverse.Query.Execution.CompiledPredicate predicate = _ => true;
        var filterNode = new ClientFilterNode(scanNode, predicate, "name Equal Contoso");

        var context = new CostContext();
        context.EntityRecordCounts["account"] = 100_000;

        var estimate = _estimator.EstimateCardinality(filterNode, context);

        // Filter selectivity is ~10% by default
        estimate.Should().Be(10_000);
    }

    // ────────────────────────────────────────────
    //  Join cardinality
    // ────────────────────────────────────────────

    [Fact]
    public void HashJoin_EstimatesJoinCardinality()
    {
        var left = TestSourceNode.Create("account",
            TestSourceNode.MakeRow("account", ("accountid", 1)));
        var right = TestSourceNode.Create("contact",
            TestSourceNode.MakeRow("contact", ("contactid", 1)));

        // Override estimated rows
        var leftSource = new FixedEstimateNode(left, 1000);
        var rightSource = new FixedEstimateNode(right, 500);

        var join = new HashJoinNode(leftSource, rightSource, "accountid", "contactid", JoinType.Inner);

        var context = new CostContext();
        var estimate = _estimator.EstimateCardinality(join, context);

        // 1000 * 500 * 0.10 = 50,000
        estimate.Should().Be(50_000);
    }

    // ────────────────────────────────────────────
    //  Constants
    // ────────────────────────────────────────────

    [Fact]
    public void Selectivity_Constants_AreCorrect()
    {
        CostEstimator.EqualitySelectivity.Should().Be(0.10);
        CostEstimator.RangeSelectivity.Should().Be(0.33);
        CostEstimator.LikeSelectivity.Should().Be(0.25);
        CostEstimator.IsNullSelectivity.Should().Be(0.05);
        CostEstimator.DefaultJoinSelectivity.Should().Be(0.10);
    }

    // ────────────────────────────────────────────
    //  Null argument validation
    // ────────────────────────────────────────────

    [Fact]
    public void EstimateCardinality_NullNode_ThrowsArgumentNullException()
    {
        var act = () => _estimator.EstimateCardinality(null!, new CostContext());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EstimateCardinality_NullContext_ThrowsArgumentNullException()
    {
        var node = TestSourceNode.Create("account");
        var act = () => _estimator.EstimateCardinality(node, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  Helper: wraps a node with a fixed estimate
    // ────────────────────────────────────────────

    private sealed class FixedEstimateNode : IQueryPlanNode
    {
        private readonly IQueryPlanNode _inner;
        private readonly long _estimate;

        public string Description => _inner.Description;
        public long EstimatedRows => _estimate;
        public IReadOnlyList<IQueryPlanNode> Children => _inner.Children;

        public FixedEstimateNode(IQueryPlanNode inner, long estimate)
        {
            _inner = inner;
            _estimate = estimate;
        }

        public IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            System.Threading.CancellationToken cancellationToken = default)
            => _inner.ExecuteAsync(context, cancellationToken);
    }
}
