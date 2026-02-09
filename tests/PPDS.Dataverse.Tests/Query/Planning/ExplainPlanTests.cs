using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning;

/// <summary>
/// End-to-end tests for the EXPLAIN plan pipeline:
/// SqlParser -> QueryPlanner -> QueryPlanDescription -> PlanFormatter.
/// Validates that plan descriptions and formatted output accurately reflect
/// the planned execution for various query types.
/// </summary>
[Trait("Category", "PlanUnit")]
public class ExplainPlanTests
{
    private readonly QueryPlanner _planner = new();

    #region Simple SELECT Plans

    [Fact]
    public void Explain_SimpleSelect_DescriptionContainsFetchXmlScan()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("FetchXmlScanNode", description.NodeType);
        Assert.Contains("FetchXmlScan", description.Description);
        Assert.Contains("account", description.Description);
    }

    [Fact]
    public void Explain_SimpleSelect_FormattedOutputContainsExecutionPlanHeader()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");

        var result = _planner.Plan(stmt);
        var formatted = PlanFormatter.Format(result.RootNode);

        Assert.StartsWith("Execution Plan:", formatted);
        Assert.Contains("FetchXmlScan: account", formatted);
    }

    [Fact]
    public void Explain_SelectWithTop_ShowsEstimatedRows()
    {
        var stmt = SqlParser.Parse("SELECT TOP 100 name FROM account");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal(100, description.EstimatedRows);

        var formatted = PlanFormatter.Format(result.RootNode);
        Assert.Contains("(est. 100 rows)", formatted);
    }

    #endregion

    #region COUNT(*) Aggregate Plans

    [Fact]
    public void Explain_BareCountStar_ShowsFetchXmlScanNode()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        // Bare COUNT(*) now uses aggregate FetchXML instead of CountOptimizedNode
        Assert.Equal("FetchXmlScanNode", description.NodeType);
        Assert.Contains("FetchXmlScan: account", description.Description);
    }

    [Fact]
    public void Explain_BareCountStar_FormattedShowsAggregateScan()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS total FROM account");

        var result = _planner.Plan(stmt);
        var formatted = PlanFormatter.Format(result.RootNode);

        Assert.Contains("FetchXmlScan: account", formatted);
    }

    [Fact]
    public void Explain_CountStarWithWhere_ShowsFetchXmlScan()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account WHERE statecode = 0");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("FetchXmlScanNode", description.NodeType);
    }

    #endregion

    #region JOIN Plans

    [Fact]
    public void Explain_InnerJoin_FetchXmlContainsLinkEntity()
    {
        var stmt = SqlParser.Parse(
            "SELECT a.name, c.fullname FROM account a " +
            "INNER JOIN contact c ON c.parentcustomerid = a.accountid");

        var result = _planner.Plan(stmt);

        Assert.Contains("link-entity", result.FetchXml);
        Assert.Contains("contact", result.FetchXml);
    }

    [Fact]
    public void Explain_LeftJoin_FetchXmlContainsOuterLinkEntity()
    {
        var stmt = SqlParser.Parse(
            "SELECT a.name, c.fullname FROM account a " +
            "LEFT JOIN contact c ON c.parentcustomerid = a.accountid");

        var result = _planner.Plan(stmt);

        Assert.Contains("link-entity", result.FetchXml);
        Assert.Contains("outer", result.FetchXml);
    }

    [Fact]
    public void Explain_InnerJoin_PlanNodeIsFetchXmlScan()
    {
        var stmt = SqlParser.Parse(
            "SELECT a.name, c.fullname FROM account a " +
            "INNER JOIN contact c ON c.parentcustomerid = a.accountid");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        // JOINs are handled within FetchXML (link-entity), not as separate plan nodes
        Assert.Equal("FetchXmlScanNode", description.NodeType);
        Assert.Contains("account", description.Description);
    }

    #endregion

    #region DML Plans

    [Fact]
    public void Explain_Delete_ShowsDmlExecuteNode()
    {
        var stmt = SqlParser.ParseSql("DELETE FROM account WHERE statecode = 1");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("DmlExecuteNode", description.NodeType);
        Assert.Contains("DmlExecute", description.Description);
        Assert.Contains("DELETE", description.Description);
        Assert.Contains("account", description.Description);
    }

    [Fact]
    public void Explain_Delete_FormattedOutputShowsDmlAndSourceScan()
    {
        var stmt = SqlParser.ParseSql("DELETE FROM account WHERE statecode = 1");

        var result = _planner.Plan(stmt);
        var formatted = PlanFormatter.Format(result.RootNode);

        Assert.Contains("DmlExecute: DELETE account", formatted);
        Assert.Contains("FetchXmlScan: account", formatted);
    }

    [Fact]
    public void Explain_Update_ShowsDmlExecuteNode()
    {
        var stmt = SqlParser.ParseSql("UPDATE account SET name = 'Updated' WHERE statecode = 0");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("DmlExecuteNode", description.NodeType);
        Assert.Contains("UPDATE", description.Description);
        Assert.Contains("account", description.Description);
    }

    [Fact]
    public void Explain_Update_FormattedOutputShowsTreeStructure()
    {
        var stmt = SqlParser.ParseSql("UPDATE account SET name = 'Updated' WHERE statecode = 0");

        var result = _planner.Plan(stmt);
        var formatted = PlanFormatter.Format(result.RootNode);

        Assert.Contains("Execution Plan:", formatted);
        Assert.Contains("DmlExecute: UPDATE account", formatted);
        // UPDATE has a source scan child for finding matching records
        Assert.Contains("FetchXmlScan: account", formatted);
    }

    [Fact]
    public void Explain_InsertValues_ShowsDmlExecuteWithRowCount()
    {
        var stmt = SqlParser.ParseSql("INSERT INTO account (name) VALUES ('Contoso')");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("DmlExecuteNode", description.NodeType);
        Assert.Contains("INSERT", description.Description);
        Assert.Contains("1 rows", description.Description);
        // INSERT VALUES has no source child
        Assert.Empty(description.Children);
    }

    [Fact]
    public void Explain_InsertSelect_ShowsDmlExecuteWithSourceChild()
    {
        var stmt = SqlParser.ParseSql(
            "INSERT INTO account (name) SELECT fullname FROM contact WHERE statecode = 0");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("DmlExecuteNode", description.NodeType);
        Assert.Contains("INSERT", description.Description);
        Assert.Contains("from SELECT", description.Description);
        // INSERT SELECT has a source scan child
        Assert.Single(description.Children);
        Assert.Equal("FetchXmlScanNode", description.Children[0].NodeType);
    }

    #endregion

    #region UNION Plans

    [Fact]
    public void Explain_UnionAll_ShowsConcatenateWithTwoBranches()
    {
        var stmt = SqlParser.ParseSql(
            "SELECT name FROM account UNION ALL SELECT fullname FROM contact");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("ConcatenateNode", description.NodeType);
        Assert.Equal(2, description.Children.Count);
        Assert.Equal("FetchXmlScanNode", description.Children[0].NodeType);
        Assert.Equal("FetchXmlScanNode", description.Children[1].NodeType);
    }

    [Fact]
    public void Explain_Union_ShowsDistinctOverConcatenate()
    {
        var stmt = SqlParser.ParseSql(
            "SELECT name FROM account UNION SELECT fullname FROM contact");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("DistinctNode", description.NodeType);
        Assert.Single(description.Children);
        Assert.Equal("ConcatenateNode", description.Children[0].NodeType);
    }

    [Fact]
    public void Explain_UnionAll_FormattedShowsBranchConnectors()
    {
        var stmt = SqlParser.ParseSql(
            "SELECT name FROM account UNION ALL SELECT fullname FROM contact");

        var result = _planner.Plan(stmt);
        var formatted = PlanFormatter.Format(result.RootNode);

        Assert.Contains("Concatenate:", formatted);
        Assert.Contains("\u251C\u2500\u2500", formatted); // branch connector (first child)
        Assert.Contains("\u2514\u2500\u2500", formatted); // end connector (last child)
    }

    #endregion

    #region ClientFilter Plans

    [Fact]
    public void Explain_HavingClause_ShowsClientFilterOverScan()
    {
        var stmt = SqlParser.Parse(
            "SELECT statecode, COUNT(*) AS cnt FROM account GROUP BY statecode HAVING cnt > 5");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("ClientFilterNode", description.NodeType);
        Assert.Contains("ClientFilter:", description.Description);
        Assert.NotEmpty(description.Children);
    }

    [Fact]
    public void Explain_ColumnToColumnWhere_ShowsClientFilterNode()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account WHERE revenue > cost");

        var result = _planner.Plan(stmt);
        var description = QueryPlanDescription.FromNode(result.RootNode);

        Assert.Equal("ClientFilterNode", description.NodeType);
        Assert.Single(description.Children);
        Assert.Equal("FetchXmlScanNode", description.Children[0].NodeType);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Explain_InvalidSql_ThrowsSqlParseException()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.ParseSql("SELEC INVALID GARBAGE"));
    }

    [Fact]
    public void Explain_InvalidSql_ExceptionIncludesPositionInfo()
    {
        var ex = Assert.Throws<SqlParseException>(() => SqlParser.ParseSql("SELEC name FROM account"));
        Assert.True(ex.Position >= 0, "Position should be set");
    }

    #endregion
}
