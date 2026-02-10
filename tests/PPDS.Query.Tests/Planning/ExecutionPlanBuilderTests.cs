using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "PlanUnit")]
public class ExecutionPlanBuilderTests
{
    private readonly QueryParser _parser = new();

    private QueryPlanResult BuildPlan(string sql, QueryPlanOptions? options = null)
    {
        var script = _parser.ParseScript(sql);
        var statement = QueryParser.GetFirstStatement(script)!;
        var builder = new ExecutionPlanBuilder(options);
        return builder.Build(statement);
    }

    #region Simple SELECT

    [Fact]
    public void Build_SimpleSelect_ProducesFetchXmlScanNode()
    {
        var result = BuildPlan("SELECT name FROM account");

        Assert.NotNull(result.RootNode);
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
        Assert.Equal("account", result.EntityLogicalName);
        Assert.NotEmpty(result.FetchXml);
    }

    [Fact]
    public void Build_SelectWithTop_IncludesTopInFetchXml()
    {
        var result = BuildPlan("SELECT TOP 10 name FROM account");

        Assert.Contains("top=\"10\"", result.FetchXml);
    }

    [Fact]
    public void Build_SelectStar_ReturnsAllAttributes()
    {
        var result = BuildPlan("SELECT * FROM account");

        Assert.Contains("<all-attributes", result.FetchXml);
    }

    [Fact]
    public void Build_SelectWithWhere_IncludesFilterInFetchXml()
    {
        var result = BuildPlan("SELECT name FROM account WHERE statecode = 0");

        Assert.Contains("<filter", result.FetchXml);
        Assert.Contains("statecode", result.FetchXml);
    }

    [Fact]
    public void Build_SelectWithOrderBy_IncludesOrderInFetchXml()
    {
        var result = BuildPlan("SELECT name FROM account ORDER BY name");

        Assert.Contains("<order", result.FetchXml);
        Assert.Contains("name", result.FetchXml);
    }

    [Fact]
    public void Build_SelectWithJoin_IncludesLinkEntity()
    {
        var result = BuildPlan(
            "SELECT a.name, c.fullname FROM account a " +
            "JOIN contact c ON c.parentcustomerid = a.accountid");

        Assert.Contains("<link-entity", result.FetchXml);
    }

    #endregion

    #region Virtual Columns

    [Fact]
    public void Build_VirtualColumn_DetectedInResult()
    {
        var result = BuildPlan("SELECT owneridname FROM account");

        Assert.NotEmpty(result.VirtualColumns);
        Assert.True(result.VirtualColumns.ContainsKey("owneridname"));
    }

    #endregion

    #region UNION

    [Fact]
    public void Build_UnionAll_ProducesConcatenateNode()
    {
        var result = BuildPlan(
            "SELECT name FROM account UNION ALL SELECT fullname FROM contact");

        Assert.IsType<ConcatenateNode>(result.RootNode);
    }

    [Fact]
    public void Build_Union_ProducesDistinctOverConcatenate()
    {
        var result = BuildPlan(
            "SELECT name FROM account UNION SELECT fullname FROM contact");

        // UNION (not ALL) wraps ConcatenateNode in a DistinctNode
        Assert.IsType<DistinctNode>(result.RootNode);
    }

    #endregion

    #region MaxRows Option

    [Fact]
    public void Build_WithMaxRowsOption_IncludesTopInFetchXml()
    {
        var options = new QueryPlanOptions { MaxRows = 500 };

        var result = BuildPlan("SELECT name FROM account", options);

        Assert.Contains("top=\"500\"", result.FetchXml);
    }

    #endregion

    #region DELETE

    [Fact]
    public void Build_DeleteWithWhere_ProducesDmlExecuteNode()
    {
        var result = BuildPlan("DELETE FROM account WHERE name = 'test'");

        Assert.IsType<DmlExecuteNode>(result.RootNode);
    }

    #endregion

    #region UPDATE

    [Fact]
    public void Build_UpdateWithWhere_ProducesDmlExecuteNode()
    {
        var result = BuildPlan("UPDATE account SET name = 'Updated' WHERE statecode = 0");

        Assert.IsType<DmlExecuteNode>(result.RootNode);
    }

    #endregion

    #region Aggregate Queries

    [Fact]
    public void Build_SimpleAggregate_ProducesFetchXmlWithAggregate()
    {
        var result = BuildPlan("SELECT COUNT(*) FROM account");

        Assert.Contains("aggregate=\"true\"", result.FetchXml);
    }

    [Fact]
    public void Build_AggregateWithGroupBy_ProducesFetchXml()
    {
        var result = BuildPlan("SELECT statecode, COUNT(*) FROM account GROUP BY statecode");

        Assert.Contains("aggregate=\"true\"", result.FetchXml);
        Assert.Contains("statecode", result.FetchXml);
    }

    #endregion

    #region Parse Errors

    [Fact]
    public void Build_InvalidSql_Throws()
    {
        Assert.ThrowsAny<Exception>(() => BuildPlan("NOT VALID SQL"));
    }

    #endregion
}
