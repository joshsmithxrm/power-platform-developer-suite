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
        // ISqlStatement that is not a recognized statement type
        var nonSelect = new NonSelectStatement();

        var ex = Assert.Throws<SqlParseException>(() => _planner.Plan(nonSelect));
        Assert.Contains("Unsupported", ex.Message);
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
    public void Plan_BareCountStar_WithoutPartitioningOptions_ProducesFetchXmlScan()
    {
        // Without EstimatedRecordCount, bare COUNT(*) uses single aggregate FetchXML
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");

        var result = _planner.Plan(stmt);

        // Should be a standard FetchXmlScanNode with aggregate FetchXML
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
        Assert.Contains("aggregate", result.FetchXml);
    }

    [Fact]
    public void Plan_BareCountStarWithAlias_PreservesAlias()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS total FROM account");
        var result = _planner.Plan(stmt);

        // Alias should appear in the FetchXML
        Assert.Contains("alias=\"total\"", result.FetchXml);
    }

    [Fact]
    public void Plan_BareCountStar_WithPartitioningOptions_ProducesPartitionedPlan()
    {
        // With EstimatedRecordCount > limit, bare COUNT(*) gets partitioned
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");
        var options = MakePartitioningOptions(200_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        var parallelNode = Assert.IsType<ParallelPartitionNode>(mergeNode.Input);
        Assert.True(parallelNode.Partitions.Count > 1);
    }

    [Fact]
    public void Plan_BareCountStar_BelowLimit_ProducesSingleAggregateScan()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");
        var options = new QueryPlanOptions
        {
            EstimatedRecordCount = 30_000, // Below 50K limit
            MinDate = new DateTime(2020, 1, 1),
            MaxDate = new DateTime(2026, 1, 1),
            PoolCapacity = 4
        };

        var result = _planner.Plan(stmt, options);

        // Below limit: single scan, no partitioning
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_CountStarWithWhere_ProducesNormalScan()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account WHERE statecode = 0");
        var result = _planner.Plan(stmt);
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_CountStarWithGroupBy_ProducesNormalScan()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account GROUP BY statecode");
        var result = _planner.Plan(stmt);
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_CountColumn_ProducesNormalScan()
    {
        // COUNT(name) is not COUNT(*) — uses aggregate FetchXML
        var stmt = SqlParser.Parse("SELECT COUNT(name) FROM account");
        var result = _planner.Plan(stmt);
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_SumAggregate_ProducesNormalScan()
    {
        // SUM uses aggregate FetchXML
        var stmt = SqlParser.Parse("SELECT SUM(revenue) FROM account");
        var result = _planner.Plan(stmt);
        Assert.IsType<FetchXmlScanNode>(result.RootNode);
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

        // Filter should have a compiled predicate and description
        Assert.NotNull(filterNode.Predicate);
        Assert.False(string.IsNullOrEmpty(filterNode.PredicateDescription));
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

        // Filter should have a compiled predicate and description
        Assert.NotNull(filterNode.Predicate);
        Assert.False(string.IsNullOrEmpty(filterNode.PredicateDescription));
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
    public void Plan_PartitionedPlan_AdaptiveNodesHaveDateRangeBounds()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        var parallelNode = Assert.IsType<ParallelPartitionNode>(mergeNode.Input);

        // Each partition should be an AdaptiveAggregateScanNode with date range bounds
        foreach (var partition in parallelNode.Partitions)
        {
            var adaptiveNode = Assert.IsType<AdaptiveAggregateScanNode>(partition);
            Assert.True(adaptiveNode.RangeStart < adaptiveNode.RangeEnd,
                "Partition date range start must be before end");
        }
    }

    [Fact]
    public void Plan_PartitionedPlan_UsesAdaptiveAggregateScanNodes()
    {
        // Partitions should use AdaptiveAggregateScanNode which internally
        // creates FetchXmlScanNode with autoPage: false at execution time
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        var parallelNode = Assert.IsType<ParallelPartitionNode>(mergeNode.Input);

        foreach (var partition in parallelNode.Partitions)
        {
            var adaptiveNode = Assert.IsType<AdaptiveAggregateScanNode>(partition);
            Assert.Equal("account", adaptiveNode.EntityLogicalName);
            Assert.Equal(0, adaptiveNode.Depth);
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

    [Fact]
    public void Plan_AvgAggregate_PartitionTemplateIncludesCompanionCount()
    {
        var stmt = SqlParser.Parse("SELECT AVG(revenue) AS avg_rev FROM account");
        var options = MakePartitioningOptions(100_000);

        var result = _planner.Plan(stmt, options);

        var mergeNode = Assert.IsType<MergeAggregateNode>(result.RootNode);
        var parallelNode = Assert.IsType<ParallelPartitionNode>(mergeNode.Input);

        // Each partition's template FetchXML should contain the companion countcolumn
        foreach (var partition in parallelNode.Partitions)
        {
            var adaptiveNode = Assert.IsType<AdaptiveAggregateScanNode>(partition);
            Assert.Contains("avg_rev_count", adaptiveNode.TemplateFetchXml);
            Assert.Contains("aggregate=\"countcolumn\"", adaptiveNode.TemplateFetchXml);
        }
    }

    #endregion

    #region ShouldPartitionAggregate Tests

    [Fact]
    public void ShouldPartitionAggregate_TrueForEligibleQuery()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = MakePartitioningOptions(100_000);

        Assert.True(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    [Fact]
    public void ShouldPartitionAggregate_FalseForNonAggregate()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");
        var options = MakePartitioningOptions(100_000);

        Assert.False(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    [Fact]
    public void ShouldPartitionAggregate_FalseForCountDistinct()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(DISTINCT ownerid) FROM account");
        var options = MakePartitioningOptions(100_000);

        Assert.False(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    [Fact]
    public void ShouldPartitionAggregate_FalseWhenBelowLimit()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");
        var options = MakePartitioningOptions(30_000);

        Assert.False(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    [Fact]
    public void ShouldPartitionAggregate_FalseWhenPoolCapacity1()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");
        var options = MakePartitioningOptions(100_000, poolCapacity: 1);

        Assert.False(QueryPlanner.ShouldPartitionAggregate(stmt, options));
    }

    #endregion

    #region ContainsCountDistinct Tests

    [Fact]
    public void ContainsCountDistinct_TrueForCountDistinct()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(DISTINCT ownerid) FROM account");

        Assert.True(QueryPlanner.ContainsCountDistinct(stmt));
    }

    [Fact]
    public void ContainsCountDistinct_FalseForCountAll()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");

        Assert.False(QueryPlanner.ContainsCountDistinct(stmt));
    }

    [Fact]
    public void ContainsCountDistinct_FalseForCountColumn()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(name) FROM account");

        Assert.False(QueryPlanner.ContainsCountDistinct(stmt));
    }

    [Fact]
    public void ContainsCountDistinct_FalseForSum()
    {
        var stmt = SqlParser.Parse("SELECT SUM(revenue) FROM account");

        Assert.False(QueryPlanner.ContainsCountDistinct(stmt));
    }

    #endregion

    #region DmlRowCap Tests

    [Fact]
    public void Plan_DeleteWithRowCap_PassesCapToDmlNode()
    {
        var stmt = SqlParser.ParseSql("DELETE FROM account WHERE statecode = 1");
        var options = new QueryPlanOptions { DmlRowCap = 100 };

        var result = _planner.Plan(stmt, options);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);
        Assert.Equal(100, dmlNode.RowCap);
    }

    [Fact]
    public void Plan_DeleteWithoutRowCap_UsesMaxValue()
    {
        var stmt = SqlParser.ParseSql("DELETE FROM account WHERE statecode = 1");
        var options = new QueryPlanOptions();

        var result = _planner.Plan(stmt, options);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);
        Assert.Equal(int.MaxValue, dmlNode.RowCap);
    }

    [Fact]
    public void Plan_InsertSelect_MapsSourceColumnsOrdinallyWhenNamesDiffer()
    {
        var stmt = SqlParser.ParseSql(
            "INSERT INTO account (name) SELECT fullname FROM contact");

        var result = _planner.Plan(stmt);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);
        Assert.Equal(DmlOperation.Insert, dmlNode.Operation);
        Assert.Equal("account", dmlNode.EntityLogicalName);

        // Insert column is "name", but source SELECT column is "fullname"
        Assert.NotNull(dmlNode.InsertColumns);
        Assert.Single(dmlNode.InsertColumns!);
        Assert.Equal("name", dmlNode.InsertColumns![0]);

        // SourceColumns should capture the SELECT column names for ordinal mapping
        Assert.NotNull(dmlNode.SourceColumns);
        Assert.Single(dmlNode.SourceColumns!);
        Assert.Equal("fullname", dmlNode.SourceColumns![0]);
    }

    [Fact]
    public void Plan_InsertSelectMultipleColumns_MapsSourceColumnsByOrdinal()
    {
        var stmt = SqlParser.ParseSql(
            "INSERT INTO account (name, description) SELECT fullname, emailaddress1 FROM contact");

        var result = _planner.Plan(stmt);

        var dmlNode = Assert.IsType<DmlExecuteNode>(result.RootNode);

        Assert.Equal(2, dmlNode.InsertColumns!.Count);
        Assert.Equal("name", dmlNode.InsertColumns[0]);
        Assert.Equal("description", dmlNode.InsertColumns[1]);

        Assert.Equal(2, dmlNode.SourceColumns!.Count);
        Assert.Equal("fullname", dmlNode.SourceColumns[0]);
        Assert.Equal("emailaddress1", dmlNode.SourceColumns[1]);
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
        var stmt = SqlParser.Parse(
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
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS total_records FROM account");

        var columns = QueryPlanner.BuildMergeAggregateColumns(stmt);

        Assert.Single(columns);
        Assert.Equal("total_records", columns[0].Alias);
    }

    [Fact]
    public void BuildMergeAggregateColumns_UnaliasedCountUsesDefaultAlias()
    {
        var stmt = SqlParser.Parse("SELECT COUNT(*) FROM account");

        var columns = QueryPlanner.BuildMergeAggregateColumns(stmt);

        Assert.Single(columns);
        // COUNT(*) with no explicit alias uses transpiler convention: "count_1"
        Assert.Equal("count_1", columns[0].Alias);
    }

    #endregion

    #region PrefetchScanNode Integration Tests

    [Fact]
    public void Plan_NonAggregateSelect_WrapsPrefetchScanNode()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");
        var options = new QueryPlanOptions { EnablePrefetch = true };

        var result = _planner.Plan(stmt, options);

        // Root should be PrefetchScanNode wrapping FetchXmlScanNode
        var prefetchNode = Assert.IsType<PrefetchScanNode>(result.RootNode);
        Assert.IsType<FetchXmlScanNode>(prefetchNode.Source);
    }

    [Fact]
    public void Plan_AggregateSelect_NoPrefetchScanNode()
    {
        // Aggregate queries should NOT get prefetch (returns few rows)
        var stmt = SqlParser.Parse("SELECT COUNT(*) AS cnt FROM account WHERE statecode = 0");
        var options = new QueryPlanOptions { EnablePrefetch = true };

        var result = _planner.Plan(stmt, options);

        // Should be FetchXmlScanNode directly, not wrapped
        Assert.IsNotType<PrefetchScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_PrefetchDisabled_NoPrefetchScanNode()
    {
        var stmt = SqlParser.Parse("SELECT name FROM account");
        var options = new QueryPlanOptions { EnablePrefetch = false };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    #endregion
}
