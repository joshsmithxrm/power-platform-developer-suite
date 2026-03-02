using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class MetadataScanNodeTests
{
    private static QueryPlanContext CreateContext(IMetadataQueryExecutor? metadataExecutor = null)
    {
        var mockQueryExecutor = new Mock<IQueryExecutor>();
        return new QueryPlanContext(
            mockQueryExecutor.Object,
            metadataQueryExecutor: metadataExecutor);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, QueryValue>> MakeEntityRecords(
        params (string logicalName, string displayName, bool isCustom)[] entities)
    {
        var records = new List<IReadOnlyDictionary<string, QueryValue>>();
        foreach (var (logicalName, displayName, isCustom) in entities)
        {
            records.Add(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["logicalname"] = QueryValue.Simple(logicalName),
                ["displayname"] = QueryValue.Simple(displayName),
                ["iscustomentity"] = QueryValue.Simple(isCustom)
            });
        }

        return records;
    }

    [Fact]
    public async Task ExecuteAsync_YieldsAllRows_FromMockExecutor()
    {
        // Arrange
        var mockRecords = MakeEntityRecords(
            ("account", "Account", false),
            ("contact", "Contact", false),
            ("ppds_project", "Project", true));

        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        mockExecutor
            .Setup(x => x.QueryMetadataAsync("entity", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockRecords);

        var node = new MetadataScanNode("entity", mockExecutor.Object);
        var ctx = CreateContext(mockExecutor.Object);

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("metadata.entity", r.EntityLogicalName));
        Assert.Equal("account", rows[0].Values["logicalname"].Value);
        Assert.Equal("contact", rows[1].Values["logicalname"].Value);
        Assert.Equal("ppds_project", rows[2].Values["logicalname"].Value);
    }

    [Fact]
    public async Task ExecuteAsync_WithRequestedColumns_PassesColumnsToExecutor()
    {
        // Arrange
        var requestedColumns = new[] { "logicalname", "displayname" };
        var mockRecords = MakeEntityRecords(("account", "Account", false));

        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        mockExecutor
            .Setup(x => x.QueryMetadataAsync("entity", requestedColumns, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockRecords);

        var node = new MetadataScanNode("entity", mockExecutor.Object, requestedColumns: requestedColumns);
        var ctx = CreateContext(mockExecutor.Object);

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Single(rows);
        mockExecutor.Verify(
            x => x.QueryMetadataAsync("entity", requestedColumns, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithFilter_FiltersRowsClientSide()
    {
        // Arrange: 3 entities, filter to only custom entities (iscustomentity = true)
        var mockRecords = MakeEntityRecords(
            ("account", "Account", false),
            ("contact", "Contact", false),
            ("ppds_project", "Project", true));

        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        mockExecutor
            .Setup(x => x.QueryMetadataAsync("entity", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockRecords);

        // Filter: iscustomentity = true (compiled predicate)
        CompiledPredicate filter = row =>
        {
            if (row.TryGetValue("iscustomentity", out var qv))
            {
                return string.Equals(qv.Value?.ToString(), "True", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        };

        var node = new MetadataScanNode("entity", mockExecutor.Object, filter: filter);
        var ctx = CreateContext(mockExecutor.Object);

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Single(rows);
        Assert.Equal("ppds_project", rows[0].Values["logicalname"].Value);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyResults_YieldsNothing()
    {
        // Arrange
        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        mockExecutor
            .Setup(x => x.QueryMetadataAsync("entity", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IReadOnlyDictionary<string, QueryValue>>());

        var node = new MetadataScanNode("entity", mockExecutor.Object);
        var ctx = CreateContext(mockExecutor.Object);

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Empty(rows);
        Assert.Equal(0, ctx.Statistics.RowsRead);
    }

    [Fact]
    public async Task ExecuteAsync_IncrementsStatistics()
    {
        // Arrange
        var mockRecords = MakeEntityRecords(
            ("account", "Account", false),
            ("contact", "Contact", false));

        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        mockExecutor
            .Setup(x => x.QueryMetadataAsync("entity", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockRecords);

        var node = new MetadataScanNode("entity", mockExecutor.Object);
        var ctx = CreateContext(mockExecutor.Object);

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, ctx.Statistics.RowsRead);
    }

    [Fact]
    public async Task ExecuteAsync_RespectssCancellation()
    {
        // Arrange
        var mockRecords = MakeEntityRecords(
            ("account", "Account", false),
            ("contact", "Contact", false),
            ("ppds_project", "Project", true));

        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        mockExecutor
            .Setup(x => x.QueryMetadataAsync("entity", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockRecords);

        var node = new MetadataScanNode("entity", mockExecutor.Object);
        using var cts = new CancellationTokenSource();
        var ctx = CreateContext(mockExecutor.Object);

        // Act & Assert
        var rowCount = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(ctx, cts.Token))
            {
                rowCount++;
                if (rowCount >= 2)
                {
                    cts.Cancel();
                }
            }
        });
    }

    [Fact]
    public void Description_IncludesMetadataTableName()
    {
        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        var node = new MetadataScanNode("entity", mockExecutor.Object);

        Assert.Contains("MetadataScan", node.Description);
        Assert.Contains("metadata.entity", node.Description);
    }

    [Fact]
    public void Description_IncludesFilteredIndicator_WhenFilterPresent()
    {
        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        CompiledPredicate filter = _ => true;

        var node = new MetadataScanNode("entity", mockExecutor.Object, filter: filter);

        Assert.Contains("(filtered)", node.Description);
    }

    [Fact]
    public void Description_OmitsFilteredIndicator_WhenNoFilter()
    {
        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        var node = new MetadataScanNode("entity", mockExecutor.Object);

        Assert.DoesNotContain("(filtered)", node.Description);
    }

    [Fact]
    public void EstimatedRows_ReturnsNegativeOne()
    {
        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        var node = new MetadataScanNode("entity", mockExecutor.Object);

        Assert.Equal(-1, node.EstimatedRows);
    }

    [Fact]
    public void Children_IsEmpty()
    {
        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        var node = new MetadataScanNode("entity", mockExecutor.Object);

        Assert.Empty(node.Children);
    }

    [Fact]
    public void Constructor_ThrowsOnNullMetadataTable()
    {
        var mockExecutor = new Mock<IMetadataQueryExecutor>();

        Assert.Throws<ArgumentNullException>(() => new MetadataScanNode(null!, mockExecutor.Object));
    }

    [Fact]
    public void Constructor_AllowsNullMetadataExecutor_ResolvedFromContextAtExecution()
    {
        // MetadataScanNode accepts null executor at plan time — the executor
        // is resolved from QueryPlanContext during ExecuteAsync.
        var node = new MetadataScanNode("entity", null);
        Assert.Null(node.MetadataExecutor);
    }

    [Fact]
    public void Constructor_StoresProperties()
    {
        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        var columns = new[] { "logicalname" };
        CompiledPredicate filter = _ => true;

        var node = new MetadataScanNode("attribute", mockExecutor.Object, columns, filter);

        Assert.Equal("attribute", node.MetadataTable);
        Assert.Same(mockExecutor.Object, node.MetadataExecutor);
        Assert.Same(columns, node.RequestedColumns);
        Assert.Same(filter, node.Filter);
    }

    [Fact]
    public async Task ExecuteAsync_WithFilter_DoesNotCountFilteredRows()
    {
        // Arrange: 3 records, filter removes 2
        var mockRecords = MakeEntityRecords(
            ("account", "Account", false),
            ("contact", "Contact", false),
            ("ppds_project", "Project", true));

        var mockExecutor = new Mock<IMetadataQueryExecutor>();
        mockExecutor
            .Setup(x => x.QueryMetadataAsync("entity", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockRecords);

        // Filter: iscustomentity = true
        CompiledPredicate filter = row =>
        {
            if (row.TryGetValue("iscustomentity", out var qv))
            {
                return string.Equals(qv.Value?.ToString(), "True", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        };

        var node = new MetadataScanNode("entity", mockExecutor.Object, filter: filter);
        var ctx = CreateContext(mockExecutor.Object);

        // Act
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // Assert: only the yielded row should increment RowsRead
        Assert.Single(rows);
        Assert.Equal(1, ctx.Statistics.RowsRead);
    }
}
