using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning;

[Trait("Category", "PlanUnit")]
public class QueryPlannerTests
{
    private readonly QueryPlanner _planner = new();

    [Fact]
    public void Plan_SimpleSelect_ProducesFetchXmlScanNode()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");

        var result = _planner.Plan(stmt);

        Assert.NotNull(result.RootNode);
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
        Assert.Equal("account", result.EntityLogicalName);
        Assert.NotEmpty(result.FetchXml);
    }

    [Fact]
    public void Plan_SelectWithTop_SetsMaxRows()
    {
        var stmt = SqlParser.Parse("SELECT TOP 10 name FROM account");

        var result = _planner.Plan(stmt);

        var scanNode = Assert.IsType<FetchXmlScanNode>(result.RootNode);
        Assert.Equal(10, scanNode.MaxRows);
    }

    [Fact]
    public void Plan_SelectWithWhere_IncludesConditionInFetchXml()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account WHERE revenue > 1000000");

        var result = _planner.Plan(stmt);

        Assert.Contains("revenue", result.FetchXml);
        Assert.Contains("1000000", result.FetchXml);
    }

    [Fact]
    public void Plan_NonSelectStatement_Throws()
    {
        // ISqlStatement that is not SqlSelectStatement
        var nonSelect = new NonSelectStatement();

        var ex = Assert.Throws<SqlParseException>(() => _planner.Plan(nonSelect));
        Assert.Contains("SELECT", ex.Message);
    }

    [Fact]
    public void Plan_WithMaxRowsOption_OverridesTop()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");
        var options = new QueryPlanOptions { MaxRows = 500 };

        var result = _planner.Plan(stmt, options);

        var scanNode = Assert.IsType<FetchXmlScanNode>(result.RootNode);
        Assert.Equal(500, scanNode.MaxRows);
    }

    [Fact]
    public void Plan_VirtualColumns_IncludedInResult()
    {
        // Querying a *name column triggers virtual column detection
        var stmt = SqlParser.Parse("SELECT owneridname FROM account");

        var result = _planner.Plan(stmt);

        Assert.NotNull(result.VirtualColumns);
    }

    [Fact]
    public void Plan_BareCountStar_ProducesCountOptimizedNode()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");

        var result = _planner.Plan(stmt);

        var countNode = Assert.IsType<CountOptimizedNode>(result.RootNode);
        Assert.Equal("account", countNode.EntityLogicalName);
        Assert.Equal("count", countNode.CountAlias);
        Assert.NotNull(countNode.FallbackNode);
        Assert.Equal("account", result.EntityLogicalName);
        Assert.NotEmpty(result.FetchXml);
    }

    [Fact]
    public void Plan_BareCountStarWithAlias_UsesAlias()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS total FROM account");

        var result = _planner.Plan(stmt);

        var countNode = Assert.IsType<CountOptimizedNode>(result.RootNode);
        Assert.Equal("total", countNode.CountAlias);
    }

    [Fact]
    public void Plan_CountStarWithWhere_ProducesNormalScan()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account WHERE statecode = 0");

        var result = _planner.Plan(stmt);

        // Should NOT use CountOptimizedNode because WHERE clause is present
        Assert.IsNotType<CountOptimizedNode>(result.RootNode);
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_CountStarWithGroupBy_ProducesNormalScan()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account GROUP BY statecode");

        var result = _planner.Plan(stmt);

        // Should NOT use CountOptimizedNode because GROUP BY is present
        Assert.IsNotType<CountOptimizedNode>(result.RootNode);
    }

    [Fact]
    public void Plan_CountColumn_ProducesNormalScan()
    {
        // COUNT(name) is not COUNT(*) — not eligible for optimization
        var stmt = SqlParser.Parse("SELECT COUNT(name) FROM account");

        var result = _planner.Plan(stmt);

        Assert.IsNotType<CountOptimizedNode>(result.RootNode);
    }

    [Fact]
    public void Plan_SumAggregate_ProducesNormalScan()
    {
        // SUM is not COUNT(*) — not eligible for optimization
        var stmt = SqlParser.Parse("SELECT SUM(revenue) FROM account");

        var result = _planner.Plan(stmt);

        Assert.IsNotType<CountOptimizedNode>(result.RootNode);
    }

    /// <summary>
    /// A non-SELECT statement for testing unsupported type handling.
    /// </summary>
    private sealed class NonSelectStatement : ISqlStatement
    {
        public int SourcePosition => 0;
    }
}
