using PPDS.Dataverse.Query.Planning;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning;

[Trait("Category", "PlanUnit")]
public class PlanFormatterTests
{
    [Fact]
    public void Format_SingleNode_ProducesCorrectTree()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var result = PlanFormatter.Format(plan);

        Assert.Contains("Execution Plan:", result);
        Assert.Contains("Scan account", result);
    }

    [Fact]
    public void Format_SingleNodeWithEstimatedRows_IncludesRowCount()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = 5000,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var result = PlanFormatter.Format(plan);

        Assert.Contains("(est. 5,000 rows)", result);
    }

    [Fact]
    public void Format_NestedNodes_ProducesIndentedTree()
    {
        var scan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = 5000,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var project = new QueryPlanDescription
        {
            NodeType = "ProjectNode",
            Description = "Project [name, revenue]",
            EstimatedRows = 5000,
            Children = new[] { scan }
        };

        var result = PlanFormatter.Format(project);

        Assert.Contains("Execution Plan:", result);
        Assert.Contains("Project [name, revenue]", result);
        Assert.Contains("Scan account", result);
    }

    [Fact]
    public void Format_MultipleChildren_UsesCorrectConnectors()
    {
        var leftScan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var rightScan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan contact",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var concatenate = new QueryPlanDescription
        {
            NodeType = "ConcatenateNode",
            Description = "Concatenate",
            EstimatedRows = -1,
            Children = new[] { leftScan, rightScan }
        };

        var result = PlanFormatter.Format(concatenate);

        // First child uses branch connector, last child uses end connector
        Assert.Contains("\u251C\u2500\u2500 Scan account", result);
        Assert.Contains("\u2514\u2500\u2500 Scan contact", result);
    }

    [Fact]
    public void Format_EmptyChildren_ProducesLeafNode()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var result = PlanFormatter.Format(plan);

        // Should have the header and one node line
        var lines = result.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("Execution Plan:", lines[0]);
    }

    [Fact]
    public void Format_UnknownEstimatedRows_OmitsRowCount()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var result = PlanFormatter.Format(plan);

        Assert.DoesNotContain("est.", result);
        Assert.DoesNotContain("rows", result);
    }

    [Fact]
    public void Format_ZeroEstimatedRows_IncludesRowCount()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "CountNode",
            Description = "Count",
            EstimatedRows = 0,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var result = PlanFormatter.Format(plan);

        Assert.Contains("(est. 0 rows)", result);
    }

    [Fact]
    public void Format_ThreeLevelNesting_ProducesCorrectIndentation()
    {
        var scan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = 1000,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var filter = new QueryPlanDescription
        {
            NodeType = "ClientFilterNode",
            Description = "Filter [revenue > 100000]",
            EstimatedRows = 500,
            Children = new[] { scan }
        };

        var project = new QueryPlanDescription
        {
            NodeType = "ProjectNode",
            Description = "Project [name, revenue]",
            EstimatedRows = 500,
            Children = new[] { filter }
        };

        var result = PlanFormatter.Format(project);

        Assert.Contains("Execution Plan:", result);
        Assert.Contains("Project [name, revenue]", result);
        Assert.Contains("Filter [revenue > 100000]", result);
        Assert.Contains("Scan account", result);

        // Verify all three nodes appear on separate lines
        var lines = result.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // header + 3 nodes
    }
}
