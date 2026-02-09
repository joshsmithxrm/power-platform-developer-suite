using System.Collections.Generic;
using System.Threading;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
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

    [Fact]
    public void Format_FromIQueryPlanNode_ConvertsAndFormats()
    {
        // Use a real FetchXmlScanNode to test the IQueryPlanNode overload
        var scanNode = new FetchXmlScanNode(
            "<fetch><entity name='account'><attribute name='name'/></entity></fetch>",
            "account",
            autoPage: true,
            maxRows: 100);

        var result = PlanFormatter.Format((IQueryPlanNode)scanNode);

        Assert.Contains("Execution Plan:", result);
        Assert.Contains("FetchXmlScan: account", result);
        Assert.Contains("(est. 100 rows)", result);
    }

    [Fact]
    public void Format_FromIQueryPlanNode_NestedTree_ConvertsRecursively()
    {
        var scanNode = new FetchXmlScanNode(
            "<fetch><entity name='account'><attribute name='name'/></entity></fetch>",
            "account",
            autoPage: true);

        var projectNode = new ProjectNode(scanNode, new[]
        {
            ProjectColumn.PassThrough("name")
        });

        var result = PlanFormatter.Format((IQueryPlanNode)projectNode);

        Assert.Contains("Execution Plan:", result);
        Assert.Contains("Project: [name]", result);
        Assert.Contains("FetchXmlScan: account", result);

        var lines = result.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + project + scan
    }

    [Fact]
    public void Format_ConcatenateWithThreeChildren_AllConnectorsCorrect()
    {
        var scan1 = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var scan2 = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan contact",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var scan3 = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan lead",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var concatenate = new QueryPlanDescription
        {
            NodeType = "ConcatenateNode",
            Description = "Concatenate: 3 inputs",
            EstimatedRows = -1,
            Children = new[] { scan1, scan2, scan3 }
        };

        var result = PlanFormatter.Format(concatenate);

        // First two children use branch connector, last child uses end connector
        Assert.Contains("\u251C\u2500\u2500 Scan account", result);
        Assert.Contains("\u251C\u2500\u2500 Scan contact", result);
        Assert.Contains("\u2514\u2500\u2500 Scan lead", result);

        var lines = result.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length); // header + concatenate + 3 children
    }

    [Fact]
    public void Format_LargeEstimatedRows_FormatsWithThousandsSeparator()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = 1_234_567,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var result = PlanFormatter.Format(plan);

        Assert.Contains("(est. 1,234,567 rows)", result);
    }

    [Fact]
    public void Format_ConcatenateNode_FromRealNodes()
    {
        var scan1 = new FetchXmlScanNode(
            "<fetch><entity name='account'><attribute name='name'/></entity></fetch>",
            "account",
            autoPage: true);

        var scan2 = new FetchXmlScanNode(
            "<fetch><entity name='contact'><attribute name='fullname'/></entity></fetch>",
            "contact",
            autoPage: true);

        var concatenateNode = new ConcatenateNode(new IQueryPlanNode[] { scan1, scan2 });

        var result = PlanFormatter.Format((IQueryPlanNode)concatenateNode);

        Assert.Contains("Execution Plan:", result);
        Assert.Contains("Concatenate: 2 inputs", result);
        Assert.Contains("FetchXmlScan: account", result);
        Assert.Contains("FetchXmlScan: contact", result);
    }

    [Fact]
    public void Format_DistinctOverConcatenate_ProducesCorrectHierarchy()
    {
        var scan1 = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var scan2 = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan contact",
            EstimatedRows = -1,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        var concatenate = new QueryPlanDescription
        {
            NodeType = "ConcatenateNode",
            Description = "Concatenate: 2 inputs",
            EstimatedRows = -1,
            Children = new[] { scan1, scan2 }
        };

        var distinct = new QueryPlanDescription
        {
            NodeType = "DistinctNode",
            Description = "Distinct",
            EstimatedRows = -1,
            Children = new[] { concatenate }
        };

        var result = PlanFormatter.Format(distinct);

        Assert.Contains("Distinct", result);
        Assert.Contains("Concatenate: 2 inputs", result);
        Assert.Contains("Scan account", result);
        Assert.Contains("Scan contact", result);

        var lines = result.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length); // header + distinct + concatenate + 2 scans
    }

    [Fact]
    public void QueryPlanDescription_FromNode_MapsAllProperties()
    {
        var scanNode = new FetchXmlScanNode(
            "<fetch><entity name='account'></entity></fetch>",
            "account",
            autoPage: true,
            maxRows: 500);

        var description = QueryPlanDescription.FromNode(scanNode);

        Assert.Equal("FetchXmlScanNode", description.NodeType);
        Assert.Contains("FetchXmlScan: account", description.Description);
        Assert.Equal(500, description.EstimatedRows);
        Assert.Empty(description.Children);
    }

    [Fact]
    public void QueryPlanDescription_FromNode_RecursivelyMapsChildren()
    {
        var scanNode = new FetchXmlScanNode(
            "<fetch><entity name='account'></entity></fetch>",
            "account");

        var projectNode = new ProjectNode(scanNode, new[]
        {
            ProjectColumn.PassThrough("name"),
            ProjectColumn.PassThrough("revenue")
        });

        var description = QueryPlanDescription.FromNode(projectNode);

        Assert.Equal("ProjectNode", description.NodeType);
        Assert.Single(description.Children);
        Assert.Equal("FetchXmlScanNode", description.Children[0].NodeType);
    }

    [Fact]
    public void Format_WithPoolMetadata_ShowsFooter()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "TestNode",
            Description = "Test",
            PoolCapacity = 48,
            EffectiveParallelism = 5
        };

        var output = PlanFormatter.Format(plan);

        Assert.Contains("Pool capacity: 48", output);
        Assert.Contains("Effective parallelism: 5", output);
    }

    [Fact]
    public void Format_WithoutPoolMetadata_OmitsFooter()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "TestNode",
            Description = "Test"
        };

        var output = PlanFormatter.Format(plan);

        Assert.DoesNotContain("Pool capacity", output);
        Assert.DoesNotContain("Effective parallelism", output);
    }

    [Fact]
    public void Format_WithOnlyPoolCapacity_ShowsPoolCapacityOnly()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "TestNode",
            Description = "Test",
            PoolCapacity = 24
        };

        var output = PlanFormatter.Format(plan);

        Assert.Contains("Pool capacity: 24", output);
        Assert.DoesNotContain("Effective parallelism", output);
    }

}
