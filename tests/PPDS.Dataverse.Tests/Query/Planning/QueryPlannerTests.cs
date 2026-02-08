using System;
using System.Linq;
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

    [Fact]
    public void Plan_WhereWithExpressionCondition_AddsClientFilterNode()
    {
        // column-to-column comparison can't be pushed to FetchXML
        var stmt = SqlParser.Parse("SELECT name FROM account WHERE revenue > cost");

        var result = _planner.Plan(stmt);

        // Root should be ClientFilterNode wrapping FetchXmlScanNode
        var filterNode = Assert.IsType<ClientFilterNode>(result.RootNode);
        Assert.IsType<FetchXmlScanNode>(filterNode.Input);

        // Filter condition should be the expression condition
        Assert.IsType<SqlExpressionCondition>(filterNode.Condition);
    }

    [Fact]
    public void Plan_WhereWithOnlyLiterals_NoClientFilterNode()
    {
        // Simple literal comparison should NOT add ClientFilterNode
        var stmt = SqlParser.Parse("SELECT name FROM account WHERE revenue > 1000");

        var result = _planner.Plan(stmt);

        // Root should be FetchXmlScanNode (no ClientFilterNode)
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_MixedWhere_ClientFilterOnlyForExpressions()
    {
        // AND of pushable and non-pushable: ClientFilterNode for expression only
        var stmt = SqlParser.Parse("SELECT name FROM account WHERE status = 1 AND revenue > cost");

        var result = _planner.Plan(stmt);

        // Root should be ClientFilterNode (for expression condition)
        var filterNode = Assert.IsType<ClientFilterNode>(result.RootNode);
        Assert.IsType<FetchXmlScanNode>(filterNode.Input);

        // Only the expression condition should be in the client filter
        Assert.IsType<SqlExpressionCondition>(filterNode.Condition);
    }

    [Fact]
    public void Plan_WhereExpressionCondition_FetchXmlIncludesReferencedColumns()
    {
        // Expression condition columns must appear in FetchXML for retrieval
        var stmt = SqlParser.Parse("SELECT name FROM account WHERE revenue > cost");

        var result = _planner.Plan(stmt);

        // FetchXML should include revenue and cost columns
        Assert.Contains("revenue", result.FetchXml);
        Assert.Contains("cost", result.FetchXml);
    }

    /// <summary>
    /// A non-SELECT statement for testing unsupported type handling.
    /// </summary>
    private sealed class NonSelectStatement : ISqlStatement
    {
        public int SourcePosition => 0;
    }

    #region Aggregate Partitioning Tests

    private static QueryPlanOptions MakePartitioningOptions(long estimatedCount = 100_000, int poolCapacity = 4)
    {
        return new QueryPlanOptions
        {
            PoolCapacity = poolCapacity,
            EstimatedRecordCount = estimatedCount,
            MinDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MaxDate = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc)
        };
    }

    [Fact]
    public void Plan_AggregateExceeding50K_ProducesPartitionedPlan()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        // Root should be MergeAggregateNode wrapping ParallelPartitionNode
        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        var parallelNode = Assert.IsType<ParallelPartitionNode>(mergeNode.Input);

        // Should have ceil(100000/40000) = 3 partitions
        Assert.Equal(3, parallelNode.Partitions.Count);
        Assert.Equal(4, parallelNode.MaxParallelism);
    }

    [Fact]
    public void Plan_AggregateBelow50K_ProducesNormalScan()
    {
        // 30K records: below the 50K limit, should NOT partition
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = MakePartitioningOptions(30_000);

        var result = _planner.Plan(stmt, options);

        // Should be a normal FetchXmlScanNode, NOT partitioned
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_AggregateWithPoolCapacity1_ProducesNormalScan()
    {
        // Pool capacity 1 means no parallelism, don't partition
        var stmt = SqlParser.Parse("SELECT SUM(revenue) AS total FROM account");
        var options = MakePartitioningOptions(100_000, poolCapacity: 1);

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_NonAggregateWithHighCount_DoesNotPartition()
    {
        // Non-aggregate query should never be partitioned regardless of count
        var stmt = SqlParser.Parse("SELECT name FROM account");
        var options = MakePartitioningOptions(200_000);

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_CountDistinct_DoesNotPartition()
    {
        // COUNT(DISTINCT) cannot be partitioned correctly
        var stmt = SqlParser.Parse("SELECT COUNT(DISTINCT ownerid) AS owners FROM account");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        // Should fall back to normal scan, NOT partitioned
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_AggregateWithGroupBy_ProducesPartitionedPlan()
    {
        var stmt = SqlParser.Parse("SELECT statecode, COUNT(*) AS cnt FROM account GROUP BY statecode");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        Assert.Single(mergeNode.GroupByColumns);
        Assert.Equal("statecode", mergeNode.GroupByColumns[0]);
    }

    [Fact]
    public void Plan_AggregateWithHaving_HavingAppliedAfterMerge()
    {
        var stmt = SqlParser.Parse(
            "SELECT statecode, COUNT(*) AS cnt FROM account GROUP BY statecode HAVING cnt > 100");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        // Root should be ClientFilterNode (HAVING) wrapping MergeAggregateNode
        var filterNode = Assert.IsType<ClientFilterNode>(result.RootNode);
        var mergeNode = Assert.IsType<MergeAggregateNode>(filterNode.Input);
        Assert.IsType<ParallelPartitionNode>(mergeNode.Input);
    }

    [Fact]
    public void Plan_SumAggregate_PartitionsCorrectly()
    {
        var stmt = SqlParser.Parse("SELECT SUM(revenue) AS total FROM account");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        Assert.Single(mergeNode.AggregateColumns);
        Assert.Equal(AggregateFunction.Sum, mergeNode.AggregateColumns[0].Function);
        Assert.Equal("total", mergeNode.AggregateColumns[0].Alias);
    }

    [Fact]
    public void Plan_AvgAggregate_IncludesCountAlias()
    {
        var stmt = SqlParser.Parse("SELECT AVG(revenue) AS avg_rev FROM account");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        Assert.Single(mergeNode.AggregateColumns);
        Assert.Equal(AggregateFunction.Avg, mergeNode.AggregateColumns[0].Function);
        Assert.Equal("avg_rev_count", mergeNode.AggregateColumns[0].CountAlias);
    }

    [Fact]
    public void Plan_MinMaxAggregate_PartitionsCorrectly()
    {
        var stmt = SqlParser.Parse("SELECT MIN(revenue) AS min_rev, MAX(revenue) AS max_rev FROM account");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        Assert.Equal(2, mergeNode.AggregateColumns.Count);
        Assert.Equal(AggregateFunction.Min, mergeNode.AggregateColumns[0].Function);
        Assert.Equal(AggregateFunction.Max, mergeNode.AggregateColumns[1].Function);
    }

    [Fact]
    public void Plan_MultipleAggregates_AllMapped()
    {
        var stmt = SqlParser.Parse(
            "SELECT COUNT(*) AS cnt, SUM(revenue) AS total, AVG(revenue) AS avg_rev, " +
            "MIN(revenue) AS min_rev, MAX(revenue) AS max_rev FROM account");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        Assert.Equal(5, mergeNode.AggregateColumns.Count);
    }

    [Fact]
    public void Plan_PartitionedPlan_FetchXmlHasDateRangeFilter()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        var parallelNode = Assert.IsType<ParallelPartitionNode>(mergeNode.Input);

        // Each partition's FetchXML should contain createdon date range filters
        foreach (var partition in parallelNode.Partitions)
        {
            var scanNode = Assert.IsType<FetchXmlScanNode>(partition);
            Assert.Contains("createdon", scanNode.FetchXml);
            Assert.Contains("operator=\"ge\"", scanNode.FetchXml);
            Assert.Contains("operator=\"lt\"", scanNode.FetchXml);
        }
    }

    [Fact]
    public void Plan_PartitionedPlan_ScanNodesAreNotAutoPage()
    {
        // Aggregate queries return a single page, no paging needed
        // Note: use WHERE to avoid the bare COUNT(*) optimization path
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        var parallelNode = Assert.IsType<ParallelPartitionNode>(mergeNode.Input);

        foreach (var partition in parallelNode.Partitions)
        {
            var scanNode = Assert.IsType<FetchXmlScanNode>(partition);
            Assert.False(scanNode.AutoPage, "Aggregate partition scans should not auto-page");
        }
    }

    [Fact]
    public void Plan_NoEstimatedCount_DoesNotPartition()
    {
        // No estimated count => can't decide whether to partition
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = new QueryPlanOptions
        {
            PoolCapacity = 4,
            MinDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MaxDate = new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc)
            // EstimatedRecordCount not set
        };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_NoDateBounds_DoesNotPartition()
    {
        // No date bounds => can't create date range partitions
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = new QueryPlanOptions
        {
            PoolCapacity = 4,
            EstimatedRecordCount = 100_000
            // MinDate and MaxDate not set
        };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    #endregion

    #region ShouldPartitionAggregate Tests

    [Fact]
    public void ShouldPartitionAggregate_TrueForEligibleQuery()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = MakePartitioningOptions(100_000);

        Assert.True(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    [Fact]
    public void ShouldPartitionAggregate_FalseForNonAggregate()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT name FROM account");
        var options = MakePartitioningOptions(100_000);

        Assert.False(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    [Fact]
    public void ShouldPartitionAggregate_FalseForCountDistinct()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(DISTINCT ownerid) FROM account");
        var options = MakePartitioningOptions(100_000);

        Assert.False(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    [Fact]
    public void ShouldPartitionAggregate_FalseWhenBelowLimit()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(*) FROM account");
        var options = MakePartitioningOptions(30_000);

        Assert.False(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    [Fact]
    public void ShouldPartitionAggregate_FalseWhenPoolCapacity1()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(*) FROM account");
        var options = MakePartitioningOptions(100_000, poolCapacity: 1);

        Assert.False(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    #endregion

    #region ContainsCountDistinct Tests

    [Fact]
    public void ContainsCountDistinct_TrueForCountDistinct()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(DISTINCT ownerid) FROM account");

        Assert.True(QueryPlanner.ContainsCountDistinct(stmt));
    }

    [Fact]
    public void ContainsCountDistinct_FalseForCountAll()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(*) FROM account");

        Assert.False(QueryPlanner.ContainsCountDistinct(stmt));
    }

    [Fact]
    public void ContainsCountDistinct_FalseForCountColumn()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(name) FROM account");

        Assert.False(QueryPlanner.ContainsCountDistinct(stmt));
    }

    [Fact]
    public void ContainsCountDistinct_FalseForSum()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT SUM(revenue) FROM account");

        Assert.False(QueryPlanner.ContainsCountDistinct(stmt));
    }

    #endregion

    #region InjectDateRangeFilter Tests

    [Fact]
    public void InjectDateRangeFilter_AddsFilterBeforeEntityClose()
    {
        var fetchXml =
            "<fetch aggregate=\"true\">\n" +
            "  <entity name=\"account\">\n" +
            "    <attribute name=\"accountid\" aggregate=\"count\" alias=\"cnt\" />\n" +
            "  </entity>\n" +
            "</fetch>";

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = QueryPlanner.InjectDateRangeFilter(fetchXml, start, end);

        Assert.Contains("createdon", result);
        Assert.Contains("operator=\"ge\"", result);
        Assert.Contains("operator=\"lt\"", result);
        Assert.Contains("2024-01-01T00:00:00.000Z", result);
        Assert.Contains("2024-07-01T00:00:00.000Z", result);
        // Should still have closing tags
        Assert.Contains("</entity>", result);
        Assert.Contains("</fetch>", result);
    }

    [Fact]
    public void InjectDateRangeFilter_PreservesExistingFilter()
    {
        var fetchXml =
            "<fetch aggregate=\"true\">\n" +
            "  <entity name=\"account\">\n" +
            "    <attribute name=\"accountid\" aggregate=\"count\" alias=\"cnt\" />\n" +
            "    <filter type=\"and\">\n" +
            "      <condition attribute=\"statecode\" operator=\"eq\" value=\"0\" />\n" +
            "    </filter>\n" +
            "  </entity>\n" +
            "</fetch>";

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = QueryPlanner.InjectDateRangeFilter(fetchXml, start, end);

        // Should have both the original filter and the date range filter
        Assert.Contains("statecode", result);
        Assert.Contains("createdon", result);
    }

    #endregion

    #region BuildMergeAggregateColumns Tests

    [Fact]
    public void BuildMergeAggregateColumns_MapsAllFunctions()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse(
            "SELECT COUNT(*) AS cnt, SUM(revenue) AS total, AVG(revenue) AS avg_rev, " +
            "MIN(revenue) AS min_rev, MAX(revenue) AS max_rev FROM account");

        var columns = QueryPlanner.BuildMergeAggregateColumns(stmt);

        Assert.Equal(5, columns.Count);
        Assert.Equal(AggregateFunction.Count, columns[0].Function);
        Assert.Equal("cnt", columns[0].Alias);
        Assert.Null(columns[0].CountAlias);

        Assert.Equal(AggregateFunction.Sum, columns[1].Function);
        Assert.Equal("total", columns[1].Alias);
        Assert.Null(columns[1].CountAlias);

        Assert.Equal(AggregateFunction.Avg, columns[2].Function);
        Assert.Equal("avg_rev", columns[2].Alias);
        Assert.Equal("avg_rev_count", columns[2].CountAlias); // AVG needs companion count

        Assert.Equal(AggregateFunction.Min, columns[3].Function);
        Assert.Equal("min_rev", columns[3].Alias);

        Assert.Equal(AggregateFunction.Max, columns[4].Function);
        Assert.Equal("max_rev", columns[4].Alias);
    }

    [Fact]
    public void BuildMergeAggregateColumns_CountStarUsesAlias()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(*) AS total_records FROM account");

        var columns = QueryPlanner.BuildMergeAggregateColumns(stmt);

        Assert.Single(columns);
        Assert.Equal("total_records", columns[0].Alias);
    }

    [Fact]
    public void BuildMergeAggregateColumns_UnaliasedCountUsesDefaultAlias()
    {
        var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT COUNT(*) FROM account");

        var columns = QueryPlanner.BuildMergeAggregateColumns(stmt);

        Assert.Single(columns);
        // COUNT(*) with no alias defaults to "count"
        Assert.Equal("count", columns[0].Alias);
    }

    #endregion
}
