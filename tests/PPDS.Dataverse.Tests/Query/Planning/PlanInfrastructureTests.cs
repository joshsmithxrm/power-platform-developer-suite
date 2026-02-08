using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning;

[Trait("Category", "PlanUnit")]
public class PlanInfrastructureTests
{
    [Fact]
    public void QueryRow_StoresValuesAndEntityName()
    {
        var values = new Dictionary<string, QueryValue>
        {
            ["name"] = QueryValue.Simple("Contoso"),
            ["revenue"] = QueryValue.Simple(1000000m)
        };

        var row = new QueryRow(values, "account");

        Assert.Equal("account", row.EntityLogicalName);
        Assert.Equal(2, row.Values.Count);
        Assert.Equal("Contoso", row.Values["name"].Value);
        Assert.Equal(1000000m, row.Values["revenue"].Value);
    }

    [Fact]
    public void QueryRow_FromRecord_CreatesRow()
    {
        var record = new Dictionary<string, QueryValue>
        {
            ["id"] = QueryValue.Simple(System.Guid.Empty)
        };

        var row = QueryRow.FromRecord(record, "contact");

        Assert.Equal("contact", row.EntityLogicalName);
        Assert.Same(record, row.Values);
    }

    [Fact]
    public void QueryPlanStatistics_DefaultValues()
    {
        var stats = new QueryPlanStatistics();

        Assert.Equal(0, stats.RowsRead);
        Assert.Equal(0, stats.RowsOutput);
        Assert.Equal(0, stats.PagesFetched);
        Assert.Equal(0, stats.ExecutionTimeMs);
        Assert.Empty(stats.NodeStats);
    }

    [Fact]
    public void QueryPlanStatistics_NodeStats_ThreadSafe()
    {
        var stats = new QueryPlanStatistics();

        stats.NodeStats["FetchXmlScan"] = new NodeStatistics { RowsProduced = 100, TimeMs = 50 };
        stats.NodeStats["Project"] = new NodeStatistics { RowsProduced = 100, TimeMs = 5 };

        Assert.Equal(2, stats.NodeStats.Count);
        Assert.Equal(100, stats.NodeStats["FetchXmlScan"].RowsProduced);
    }

    [Fact]
    public void QueryPlanOptions_DefaultValues()
    {
        var opts = new QueryPlanOptions();

        Assert.Equal(0, opts.PoolCapacity);
        Assert.False(opts.UseTdsEndpoint);
        Assert.False(opts.ExplainOnly);
        Assert.Null(opts.MaxRows);
    }

    [Fact]
    public void QueryPlanOptions_InitProperties()
    {
        var opts = new QueryPlanOptions
        {
            PoolCapacity = 48,
            UseTdsEndpoint = true,
            ExplainOnly = true,
            MaxRows = 1000
        };

        Assert.Equal(48, opts.PoolCapacity);
        Assert.True(opts.UseTdsEndpoint);
        Assert.True(opts.ExplainOnly);
        Assert.Equal(1000, opts.MaxRows);
    }

    [Fact]
    public void QueryPlanDescription_CreatesFromValues()
    {
        var desc = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Scan account",
            EstimatedRows = 5000,
            Children = System.Array.Empty<QueryPlanDescription>()
        };

        Assert.Equal("FetchXmlScanNode", desc.NodeType);
        Assert.Equal("Scan account", desc.Description);
        Assert.Equal(5000, desc.EstimatedRows);
        Assert.Empty(desc.Children);
    }
}
