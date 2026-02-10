using PPDS.Cli.Tui.Components;
using PPDS.Dataverse.Query.Planning;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Components;

[Trait("Category", "TuiUnit")]
public sealed class QueryPlanViewTests
{
    [Fact]
    public void FormatPlanTree_SingleNode_ShowsNodeTypeAndRows()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "account",
            EstimatedRows = 5432
        };

        var result = QueryPlanView.FormatPlanTree(plan);

        Assert.Contains("FetchXmlScanNode", result);
        Assert.Contains("account", result);
        Assert.Contains("5,432 rows", result);
    }

    [Fact]
    public void FormatPlanTree_NestedNodes_ShowsTreeStructure()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "ProjectNode",
            Description = "3 cols",
            EstimatedRows = 100,
            Children = new[]
            {
                new QueryPlanDescription
                {
                    NodeType = "FilterNode",
                    Description = "statecode = 0",
                    EstimatedRows = 100,
                    Children = new[]
                    {
                        new QueryPlanDescription
                        {
                            NodeType = "FetchXmlScanNode",
                            Description = "account",
                            EstimatedRows = 5432
                        }
                    }
                }
            }
        };

        var result = QueryPlanView.FormatPlanTree(plan);

        // Verify all nodes present
        Assert.Contains("ProjectNode", result);
        Assert.Contains("FilterNode", result);
        Assert.Contains("FetchXmlScanNode", result);
        // Verify tree connectors
        Assert.Contains("\u2514\u2500", result); // └─
    }

    [Fact]
    public void FormatPlanTree_WithExecutionTime_ShowsTotalTime()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "account",
            EstimatedRows = 100
        };

        var result = QueryPlanView.FormatPlanTree(plan, 1500);

        Assert.Contains("Total execution time:", result);
        Assert.Contains("1.5s", result);
    }

    [Fact]
    public void FormatPlanTree_WithPoolCapacity_ShowsMetadata()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "account",
            EstimatedRows = 100,
            PoolCapacity = 10,
            EffectiveParallelism = 4
        };

        var result = QueryPlanView.FormatPlanTree(plan);

        Assert.Contains("Pool capacity: 10", result);
        Assert.Contains("Effective parallelism: 4", result);
    }

    [Fact]
    public void FormatPlanTree_MultipleChildren_ShowsBranchConnectors()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "ConcatenateNode",
            Description = "UNION",
            EstimatedRows = -1,
            Children = new[]
            {
                new QueryPlanDescription
                {
                    NodeType = "FetchXmlScanNode",
                    Description = "account",
                    EstimatedRows = 100
                },
                new QueryPlanDescription
                {
                    NodeType = "FetchXmlScanNode",
                    Description = "contact",
                    EstimatedRows = 200
                }
            }
        };

        var result = QueryPlanView.FormatPlanTree(plan);

        // First child uses ├─, last child uses └─
        Assert.Contains("\u251c\u2500", result); // ├─
        Assert.Contains("\u2514\u2500", result); // └─
    }

    [Fact]
    public void FormatPlanTree_UnknownRows_OmitsRowCount()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "ProjectNode",
            Description = "3 cols",
            EstimatedRows = -1
        };

        var result = QueryPlanView.FormatPlanTree(plan);

        Assert.DoesNotContain("rows", result);
    }

    [Fact]
    public void FormatPlanTree_SameDescriptionAsNodeType_DoesNotDuplicate()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "DistinctNode",
            Description = "DistinctNode",
            EstimatedRows = 50
        };

        var result = QueryPlanView.FormatPlanTree(plan);

        // Should not show "DistinctNode (DistinctNode)"
        Assert.DoesNotContain("DistinctNode (DistinctNode)", result);
        Assert.Contains("DistinctNode", result);
    }

    [Fact]
    public void FormatTime_SubMillisecond_ReturnsLessThan1ms()
    {
        Assert.Equal("<1ms", QueryPlanView.FormatTime(0));
    }

    [Fact]
    public void FormatTime_Milliseconds_ReturnsMs()
    {
        Assert.Equal("450ms", QueryPlanView.FormatTime(450));
    }

    [Fact]
    public void FormatTime_Seconds_ReturnsFormattedSeconds()
    {
        Assert.Equal("1.5s", QueryPlanView.FormatTime(1500));
    }

    [Fact]
    public void FormatTime_Minutes_ReturnsFormattedMinutes()
    {
        Assert.Equal("2.5m", QueryPlanView.FormatTime(150000));
    }

    [Fact]
    public void FormatPlanTree_ZeroExecutionTime_OmitsTimeSection()
    {
        var plan = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "account",
            EstimatedRows = 100
        };

        var result = QueryPlanView.FormatPlanTree(plan, 0);

        Assert.DoesNotContain("Total execution time:", result);
    }
}
