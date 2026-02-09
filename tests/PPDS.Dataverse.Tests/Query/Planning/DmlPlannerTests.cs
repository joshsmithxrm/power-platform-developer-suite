using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning;

[Trait("Category", "TuiUnit")]
public class DmlPlannerTests
{
    private readonly QueryPlanner _planner = new();

    [Fact]
    public void PlanInsertValues_CreatesDmlExecuteNode()
    {
        var sql = "INSERT INTO account (name, revenue) VALUES ('Contoso', 1000000)";
        var statement = SqlParser.ParseSql(sql);

        var result = _planner.Plan(statement);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);
        Assert.Equal(DmlOperation.Insert, dmlNode.Operation);
        Assert.Equal("account", dmlNode.EntityLogicalName);
        Assert.NotNull(dmlNode.InsertColumns);
        Assert.Equal(2, dmlNode.InsertColumns!.Count);
        Assert.NotNull(dmlNode.InsertValueRows);
        Assert.Single(dmlNode.InsertValueRows!);
        Assert.Null(dmlNode.SourceNode);
    }

    [Fact]
    public void PlanInsertSelect_CreatesDmlExecuteNodeWithSourceScan()
    {
        var sql = "INSERT INTO account (name) SELECT fullname FROM contact WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var result = _planner.Plan(statement);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);
        Assert.Equal(DmlOperation.Insert, dmlNode.Operation);
        Assert.Equal("account", dmlNode.EntityLogicalName);
        Assert.NotNull(dmlNode.SourceNode);
        Assert.Null(dmlNode.InsertValueRows);
    }

    [Fact]
    public void PlanUpdate_CreatesDmlExecuteNodeWithSourceScan()
    {
        var sql = "UPDATE account SET name = 'Updated' WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var result = _planner.Plan(statement);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);
        Assert.Equal(DmlOperation.Update, dmlNode.Operation);
        Assert.Equal("account", dmlNode.EntityLogicalName);
        Assert.NotNull(dmlNode.SourceNode);
        Assert.NotNull(dmlNode.SetClauses);
        Assert.Single(dmlNode.SetClauses!);
    }

    [Fact]
    public void PlanDelete_CreatesDmlExecuteNodeWithSourceScan()
    {
        var sql = "DELETE FROM account WHERE statecode = 2";
        var statement = SqlParser.ParseSql(sql);

        var result = _planner.Plan(statement);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);
        Assert.Equal(DmlOperation.Delete, dmlNode.Operation);
        Assert.Equal("account", dmlNode.EntityLogicalName);
        Assert.NotNull(dmlNode.SourceNode);
    }

    [Fact]
    public void PlanUpdate_IncludesReferencedColumnsInSource()
    {
        // revenue appears in SET expression, so it must be in the source SELECT
        var sql = "UPDATE account SET revenue = revenue * 1.1 WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var result = _planner.Plan(statement);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);
        Assert.Equal(DmlOperation.Update, dmlNode.Operation);
        // The source scan should include both accountid and revenue
        Assert.NotNull(dmlNode.SourceNode);
    }

    [Fact]
    public void PlanInsert_FetchXmlContainsDmlMarker()
    {
        var sql = "INSERT INTO account (name) VALUES ('Contoso')";
        var statement = SqlParser.ParseSql(sql);

        var result = _planner.Plan(statement);

        Assert.Contains("DML", result.FetchXml);
        Assert.Contains("INSERT INTO account", result.FetchXml);
    }

    [Fact]
    public void PlanDelete_EntityLogicalNameIsCorrect()
    {
        var sql = "DELETE FROM contact WHERE statecode = 2";
        var statement = SqlParser.ParseSql(sql);

        var result = _planner.Plan(statement);

        Assert.Equal("contact", result.EntityLogicalName);
    }

    [Fact]
    public void SelectStillWorksAfterDmlChanges()
    {
        var sql = "SELECT name FROM account WHERE statecode = 0";
        var statement = SqlParser.ParseSql(sql);

        var result = _planner.Plan(statement);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }
}
