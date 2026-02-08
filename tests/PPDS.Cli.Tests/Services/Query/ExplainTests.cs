using Moq;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Unit tests for <see cref="SqlQueryService.ExplainAsync"/>.
/// ExplainAsync only parses and plans — it never executes queries,
/// so the mock query executor is never called.
/// </summary>
[Trait("Category", "PlanUnit")]
public class ExplainTests
{
    private readonly Mock<IQueryExecutor> _mockQueryExecutor;
    private readonly SqlQueryService _service;

    public ExplainTests()
    {
        _mockQueryExecutor = new Mock<IQueryExecutor>(MockBehavior.Strict);
        _service = new SqlQueryService(_mockQueryExecutor.Object);
    }

    #region Basic Plan Structure

    [Fact]
    public async Task ExplainAsync_SimpleSelect_ReturnsFetchXmlScanDescription()
    {
        var plan = await _service.ExplainAsync("SELECT name FROM account");

        Assert.Equal("FetchXmlScanNode", plan.NodeType);
        Assert.Contains("FetchXmlScan: account", plan.Description);
        Assert.Empty(plan.Children);
    }

    [Fact]
    public async Task ExplainAsync_SelectStar_ReturnsFetchXmlScanDescription()
    {
        var plan = await _service.ExplainAsync("SELECT * FROM account");

        Assert.Equal("FetchXmlScanNode", plan.NodeType);
        Assert.Contains("account", plan.Description);
    }

    [Fact]
    public async Task ExplainAsync_SelectWithTop_ShowsEstimatedRows()
    {
        var plan = await _service.ExplainAsync("SELECT TOP 50 name FROM account");

        Assert.Equal("FetchXmlScanNode", plan.NodeType);
        Assert.Equal(50, plan.EstimatedRows);
        Assert.Contains("top 50", plan.Description);
    }

    [Fact]
    public async Task ExplainAsync_SelectWithWhere_ReturnsFetchXmlScan()
    {
        var plan = await _service.ExplainAsync("SELECT * FROM account WHERE revenue > 1000000");

        // Simple WHERE with literal value is pushed to FetchXML, no ClientFilter
        Assert.Equal("FetchXmlScanNode", plan.NodeType);
        Assert.Contains("account", plan.Description);
    }

    [Fact]
    public async Task ExplainAsync_MultipleColumns_ReturnsFetchXmlScan()
    {
        var plan = await _service.ExplainAsync("SELECT name, revenue, statecode FROM account");

        Assert.Equal("FetchXmlScanNode", plan.NodeType);
        Assert.Contains("account", plan.Description);
    }

    #endregion

    #region CountOptimized

    [Fact]
    public async Task ExplainAsync_BareCountStar_ReturnsCountOptimizedNode()
    {
        var plan = await _service.ExplainAsync("SELECT COUNT(*) FROM account");

        Assert.Equal("CountOptimizedNode", plan.NodeType);
        Assert.Contains("CountOptimized: account", plan.Description);
        Assert.Equal(1, plan.EstimatedRows);
    }

    [Fact]
    public async Task ExplainAsync_BareCountStarWithAlias_ReturnsCountOptimizedNode()
    {
        var plan = await _service.ExplainAsync("SELECT COUNT(*) AS total FROM account");

        Assert.Equal("CountOptimizedNode", plan.NodeType);
        Assert.Contains("account", plan.Description);
    }

    [Fact]
    public async Task ExplainAsync_BareCountStar_HasFallbackChild()
    {
        var plan = await _service.ExplainAsync("SELECT COUNT(*) FROM account");

        // CountOptimizedNode has a FetchXmlScan fallback child
        Assert.Single(plan.Children);
        Assert.Equal("FetchXmlScanNode", plan.Children[0].NodeType);
    }

    [Fact]
    public async Task ExplainAsync_CountStarWithWhere_NotOptimized()
    {
        var plan = await _service.ExplainAsync("SELECT COUNT(*) FROM account WHERE statecode = 0");

        // With a WHERE clause, COUNT(*) cannot use the optimized path
        Assert.NotEqual("CountOptimizedNode", plan.NodeType);
        Assert.Equal("FetchXmlScanNode", plan.NodeType);
    }

    [Fact]
    public async Task ExplainAsync_CountStarWithGroupBy_NotOptimized()
    {
        var plan = await _service.ExplainAsync("SELECT COUNT(*) FROM account GROUP BY statecode");

        // With GROUP BY, COUNT(*) cannot use the optimized path
        Assert.NotEqual("CountOptimizedNode", plan.NodeType);
    }

    #endregion

    #region ClientFilter (HAVING and Expression Conditions)

    [Fact]
    public async Task ExplainAsync_HavingClause_ShowsClientFilterNode()
    {
        var plan = await _service.ExplainAsync(
            "SELECT statecode, COUNT(*) AS cnt FROM account GROUP BY statecode HAVING cnt > 5");

        // HAVING produces a ClientFilterNode wrapping the scan
        Assert.Equal("ClientFilterNode", plan.NodeType);
        Assert.Contains("ClientFilter:", plan.Description);
        Assert.NotEmpty(plan.Children);
    }

    [Fact]
    public async Task ExplainAsync_ColumnToColumnWhere_ShowsClientFilterNode()
    {
        var plan = await _service.ExplainAsync(
            "SELECT name FROM account WHERE revenue > cost");

        // Column-to-column comparison can't be pushed to FetchXML
        Assert.Equal("ClientFilterNode", plan.NodeType);
        Assert.Contains("ClientFilter:", plan.Description);

        // Child should be FetchXmlScanNode
        Assert.Single(plan.Children);
        Assert.Equal("FetchXmlScanNode", plan.Children[0].NodeType);
    }

    #endregion

    #region UNION Plans

    [Fact]
    public async Task ExplainAsync_UnionAll_ShowsConcatenateNode()
    {
        var plan = await _service.ExplainAsync(
            "SELECT name FROM account UNION ALL SELECT fullname FROM contact");

        Assert.Equal("ConcatenateNode", plan.NodeType);
        Assert.Contains("Concatenate:", plan.Description);
        Assert.Equal(2, plan.Children.Count);
        Assert.Equal("FetchXmlScanNode", plan.Children[0].NodeType);
        Assert.Equal("FetchXmlScanNode", plan.Children[1].NodeType);
    }

    [Fact]
    public async Task ExplainAsync_Union_ShowsDistinctOverConcatenate()
    {
        var plan = await _service.ExplainAsync(
            "SELECT name FROM account UNION SELECT fullname FROM contact");

        // UNION (without ALL) wraps with DistinctNode
        Assert.Equal("DistinctNode", plan.NodeType);
        Assert.Equal("Distinct", plan.Description);

        // Child is ConcatenateNode
        Assert.Single(plan.Children);
        var concatenate = plan.Children[0];
        Assert.Equal("ConcatenateNode", concatenate.NodeType);
        Assert.Equal(2, concatenate.Children.Count);
    }

    #endregion

    #region Plan Formatting Integration

    [Fact]
    public async Task ExplainAsync_FormattedOutput_ContainsExecutionPlanHeader()
    {
        var plan = await _service.ExplainAsync("SELECT name FROM account");
        var formatted = PlanFormatter.Format(plan);

        Assert.StartsWith("Execution Plan:", formatted);
    }

    [Fact]
    public async Task ExplainAsync_FormattedCountStar_ShowsTreeStructure()
    {
        var plan = await _service.ExplainAsync("SELECT COUNT(*) FROM account");
        var formatted = PlanFormatter.Format(plan);

        Assert.Contains("Execution Plan:", formatted);
        Assert.Contains("CountOptimized: account", formatted);
        Assert.Contains("(est. 1 rows)", formatted);
        Assert.Contains("FetchXmlScan: account", formatted);
    }

    [Fact]
    public async Task ExplainAsync_FormattedUnionAll_ShowsMultiBranchTree()
    {
        var plan = await _service.ExplainAsync(
            "SELECT name FROM account UNION ALL SELECT fullname FROM contact");
        var formatted = PlanFormatter.Format(plan);

        Assert.Contains("Concatenate:", formatted);
        // First child uses branch connector, second uses end connector
        Assert.Contains("\u251C\u2500\u2500", formatted); // branch connector
        Assert.Contains("\u2514\u2500\u2500", formatted); // end connector
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task ExplainAsync_InvalidSql_ThrowsSqlParseException()
    {
        await Assert.ThrowsAsync<SqlParseException>(
            () => _service.ExplainAsync("NOT VALID SQL"));
    }

    [Fact]
    public async Task ExplainAsync_NullSql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.ExplainAsync(null!));
    }

    [Fact]
    public async Task ExplainAsync_EmptySql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ExplainAsync(""));
    }

    [Fact]
    public async Task ExplainAsync_WhitespaceSql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ExplainAsync("   "));
    }

    #endregion

    #region Does Not Execute Queries

    [Fact]
    public async Task ExplainAsync_DoesNotCallQueryExecutor()
    {
        // The mock is Strict, so any unexpected call will throw.
        // This proves ExplainAsync never touches the query executor.
        var plan = await _service.ExplainAsync("SELECT name FROM account");

        Assert.NotNull(plan);
        _mockQueryExecutor.VerifyNoOtherCalls();
    }

    #endregion
}
