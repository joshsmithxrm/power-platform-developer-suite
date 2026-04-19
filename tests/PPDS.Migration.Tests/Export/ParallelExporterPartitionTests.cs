using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Export;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Export;

[Trait("Category", "Unit")]
public class ParallelExporterPartitionTests
{
    #region DeterminePartitionCount

    [Fact]
    public void DeterminePartitionCount_DisabledExplicitly_Returns1()
    {
        var options = new ExportOptions { PageLevelParallelism = 1 };

        var result = ParallelExporter.DeterminePartitionCount(100_000, options);

        result.Should().Be(1);
    }

    [Fact]
    public void DeterminePartitionCount_BelowThreshold_Returns1()
    {
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 5000
        };

        var result = ParallelExporter.DeterminePartitionCount(3000, options);

        result.Should().Be(1);
    }

    [Fact]
    public void DeterminePartitionCount_AtThreshold_Returns1()
    {
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 5000
        };

        var result = ParallelExporter.DeterminePartitionCount(5000, options);

        result.Should().Be(1);
    }

    [Fact]
    public void DeterminePartitionCount_ExplicitValue_ReturnsValue()
    {
        var options = new ExportOptions
        {
            PageLevelParallelism = 8,
            PageLevelParallelismThreshold = 5000
        };

        var result = ParallelExporter.DeterminePartitionCount(100_000, options);

        result.Should().Be(8);
    }

    [Fact]
    public void DeterminePartitionCount_AutoScales_WithEntitySize()
    {
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 5000
        };

        // 50000 / 5000 = 10 partitions
        var result = ParallelExporter.DeterminePartitionCount(50_000, options);

        result.Should().Be(10);
    }

    [Fact]
    public void DeterminePartitionCount_AutoCapsAt16()
    {
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 5000
        };

        // 1000000 / 5000 = 200, capped at 16
        var result = ParallelExporter.DeterminePartitionCount(1_000_000, options);

        result.Should().Be(16);
    }

    [Fact]
    public void DeterminePartitionCount_AutoMinimum2()
    {
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 5000
        };

        // 6000 / 5000 = 1, but min is 2 when above threshold
        var result = ParallelExporter.DeterminePartitionCount(6000, options);

        result.Should().Be(2);
    }

    [Fact]
    public void DeterminePartitionCount_ExplicitValue_OverridesThreshold()
    {
        // Explicit PageLevelParallelism should be honored even below threshold
        var options = new ExportOptions
        {
            PageLevelParallelism = 4,
            PageLevelParallelismThreshold = 5000
        };

        var result = ParallelExporter.DeterminePartitionCount(100, options);

        result.Should().Be(4);
    }

    [Fact]
    public void DeterminePartitionCount_ZeroThreshold_Returns1()
    {
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 0
        };

        var result = ParallelExporter.DeterminePartitionCount(100_000, options);

        result.Should().Be(1);
    }

    #endregion

    #region AddPartitionFilter

    [Fact]
    public void AddPartitionFilter_FullRange_ReturnsUnchanged()
    {
        var fetchXml = "<fetch count=\"5000\"><entity name=\"account\"><attribute name=\"accountid\" /></entity></fetch>";
        var fullRange = GuidRange.Full;

        var result = ParallelExporter.AddPartitionFilter(fetchXml, "accountid", fullRange);

        result.Should().Be(fetchXml);
    }

    [Fact]
    public void AddPartitionFilter_WithBounds_InjectsConditions()
    {
        var fetchXml = "<fetch count=\"5000\"><entity name=\"account\"><attribute name=\"accountid\" /></entity></fetch>";
        var lower = Guid.Parse("10000000-0000-0000-0000-000000000000");
        var upper = Guid.Parse("20000000-0000-0000-0000-000000000000");
        var range = new GuidRange(lower, upper);

        var result = ParallelExporter.AddPartitionFilter(fetchXml, "accountid", range);

        var doc = XDocument.Parse(result);
        var filter = doc.Root!.Element("entity")!.Element("filter");

        filter.Should().NotBeNull();
        filter!.Attribute("type")!.Value.Should().Be("and");

        var conditions = filter.Elements("condition").ToList();
        conditions.Should().HaveCount(2);

        var geCondition = conditions.Single(c => c.Attribute("operator")!.Value == "ge");
        geCondition.Attribute("attribute")!.Value.Should().Be("accountid");
        geCondition.Attribute("value")!.Value.Should().Be(lower.ToString());

        var ltCondition = conditions.Single(c => c.Attribute("operator")!.Value == "lt");
        ltCondition.Attribute("attribute")!.Value.Should().Be("accountid");
        ltCondition.Attribute("value")!.Value.Should().Be(upper.ToString());
    }

    [Fact]
    public void AddPartitionFilter_LowerBoundOnly_InjectsGeCondition()
    {
        var fetchXml = "<fetch count=\"5000\"><entity name=\"account\"><attribute name=\"accountid\" /></entity></fetch>";
        var lower = Guid.Parse("80000000-0000-0000-0000-000000000000");
        // Last partition: has lower bound but no upper bound
        var range = new GuidRange(lower, null);

        var result = ParallelExporter.AddPartitionFilter(fetchXml, "accountid", range);

        var doc = XDocument.Parse(result);
        var filter = doc.Root!.Element("entity")!.Element("filter");

        filter.Should().NotBeNull();
        var conditions = filter!.Elements("condition").ToList();
        conditions.Should().HaveCount(1);

        var geCondition = conditions.Single();
        geCondition.Attribute("operator")!.Value.Should().Be("ge");
        geCondition.Attribute("attribute")!.Value.Should().Be("accountid");
        geCondition.Attribute("value")!.Value.Should().Be(lower.ToString());
    }

    [Fact]
    public void AddPartitionFilter_UpperBoundOnly_InjectsLtCondition()
    {
        var fetchXml = "<fetch count=\"5000\"><entity name=\"account\"><attribute name=\"accountid\" /></entity></fetch>";
        var upper = Guid.Parse("80000000-0000-0000-0000-000000000000");
        // First partition: has upper bound but no lower bound
        var range = new GuidRange(null, upper);

        var result = ParallelExporter.AddPartitionFilter(fetchXml, "accountid", range);

        var doc = XDocument.Parse(result);
        var filter = doc.Root!.Element("entity")!.Element("filter");

        filter.Should().NotBeNull();
        var conditions = filter!.Elements("condition").ToList();
        conditions.Should().HaveCount(1);

        var ltCondition = conditions.Single();
        ltCondition.Attribute("operator")!.Value.Should().Be("lt");
        ltCondition.Attribute("attribute")!.Value.Should().Be("accountid");
        ltCondition.Attribute("value")!.Value.Should().Be(upper.ToString());
    }

    #endregion

    #region Integration-style tests via ExportAsync

    private static EntitySchema CreateAccountSchema()
    {
        return new EntitySchema
        {
            LogicalName = "account",
            PrimaryIdField = "accountid",
            Fields = new List<FieldSchema>
            {
                new FieldSchema { LogicalName = "accountid", Type = "primarykey" },
                new FieldSchema { LogicalName = "name", Type = "string" }
            },
            Relationships = new List<RelationshipSchema>()
        };
    }

    private static MigrationSchema CreateSchema(EntitySchema entitySchema)
    {
        return new MigrationSchema
        {
            Version = "1.0",
            Entities = new List<EntitySchema> { entitySchema }
        };
    }

    /// <summary>
    /// Creates a mock pooled client that returns a count response first, then data responses.
    /// </summary>
    private static Mock<IPooledClient> CreateMockClient(int countValue, List<Entity>? dataRecords = null)
    {
        var mockClient = new Mock<IPooledClient>();
        dataRecords ??= new List<Entity>
        {
            new Entity("account", Guid.NewGuid()),
            new Entity("account", Guid.NewGuid())
        };

        var callIndex = 0;

        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync((QueryBase query) =>
            {
                var index = Interlocked.Increment(ref callIndex);

                // First call is the aggregate count query
                if (index == 1)
                {
                    var countEntity = new Entity("account");
                    countEntity["cnt"] = new AliasedValue("account", "accountid", countValue);
                    return new EntityCollection(new List<Entity> { countEntity });
                }

                // Subsequent calls are data retrieval
                return new EntityCollection(dataRecords) { MoreRecords = false };
            });

        mockClient
            .Setup(c => c.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        return mockClient;
    }

    /// <summary>
    /// Creates a pool that always returns the given client mock.
    /// Tracks the number of GetClientAsync calls.
    /// </summary>
    private static (Mock<IDataverseConnectionPool> Pool, ConcurrentBag<int> CallLog) CreateMockPool(
        Mock<IPooledClient> mockClient)
    {
        var callLog = new ConcurrentBag<int>();
        var callCount = 0;
        var pool = new Mock<IDataverseConnectionPool>();

        pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var index = Interlocked.Increment(ref callCount);
                callLog.Add(index);
                return mockClient.Object;
            });

        return (pool, callLog);
    }

    private static ParallelExporter CreateExporter(
        Mock<IDataverseConnectionPool> pool,
        Mock<ICmtSchemaReader>? schemaReader = null,
        Mock<ICmtDataWriter>? dataWriter = null)
    {
        schemaReader ??= new Mock<ICmtSchemaReader>();
        dataWriter ??= new Mock<ICmtDataWriter>();

        dataWriter
            .Setup(w => w.WriteAsync(
                It.IsAny<MigrationData>(),
                It.IsAny<string>(),
                It.IsAny<IProgressReporter?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new ParallelExporter(pool.Object, schemaReader.Object, dataWriter.Object);
    }

    [Fact]
    public async Task UsesPartitioningAboveThreshold()
    {
        // Arrange: record count of 10000 > threshold of 5000 => partitioning should activate
        var entitySchema = CreateAccountSchema();
        var schema = CreateSchema(entitySchema);
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 5000,
            DegreeOfParallelism = 1
        };

        // We need independent clients for count vs data queries.
        // The count query uses one GetClientAsync call.
        // With 10000 records / 5000 threshold = auto 2 partitions, each gets its own GetClientAsync.
        // Total: 1 (count) + 2 (partitions) = 3 GetClientAsync calls.
        var getClientCallCount = 0;

        var pool = new Mock<IDataverseConnectionPool>();
        pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref getClientCallCount);
                var client = new Mock<IPooledClient>();

                client
                    .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
                    .ReturnsAsync((QueryBase query) =>
                    {
                        var fetchExpr = query as FetchExpression;
                        var xml = fetchExpr?.Query ?? "";

                        // Aggregate count query
                        if (xml.Contains("aggregate"))
                        {
                            var countEntity = new Entity("account");
                            countEntity["cnt"] = new AliasedValue("account", "accountid", 10000);
                            return new EntityCollection(new List<Entity> { countEntity });
                        }

                        // Data query
                        return new EntityCollection(new List<Entity>
                        {
                            new Entity("account", Guid.NewGuid())
                        }) { MoreRecords = false };
                    });

                client
                    .Setup(c => c.DisposeAsync())
                    .Returns(ValueTask.CompletedTask);

                return client.Object;
            });

        var exporter = CreateExporter(pool);

        // Act
        var result = await exporter.ExportAsync(schema, "output.zip", options);

        // Assert
        result.Success.Should().BeTrue();
        // 1 GetClientAsync for count + N GetClientAsync for partitions (N >= 2)
        getClientCallCount.Should().BeGreaterThanOrEqualTo(3,
            "should have at least 1 count query + 2 partition queries");
    }

    [Fact]
    public async Task CountQuery_RejectsInvalidLogicalName()
    {
        // C4 backstop: invalid logical names (e.g. XML injection attempts) must be
        // rejected by the regex guard before being interpolated into FetchXML.
        var entitySchema = new EntitySchema
        {
            LogicalName = "account'/><inject/>",
            PrimaryIdField = "accountid",
            Fields = new List<FieldSchema>
            {
                new FieldSchema { LogicalName = "name", Type = "string" }
            },
            Relationships = new List<RelationshipSchema>()
        };
        var schema = CreateSchema(entitySchema);
        var options = new ExportOptions { PageLevelParallelism = 1, DegreeOfParallelism = 1 };

        var pool = new Mock<IDataverseConnectionPool>();
        pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var client = new Mock<IPooledClient>();
                client
                    .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
                    .ReturnsAsync(new EntityCollection { MoreRecords = false });
                client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                return client.Object;
            });

        var exporter = CreateExporter(pool);

        // Act — exporter should not submit a malformed FetchXML. Count query is
        // swallowed by the partitioning path; the entity falls back to sequential.
        var result = await exporter.ExportAsync(schema, "output.zip", options);

        // Assert — fell back to sequential (count was rejected) and surfaced a warning.
        result.Warnings.Should().Contain(w =>
            w.Code == ExportWarningCodes.CountFailedSequentialFallback);
    }

    [Fact]
    public async Task SequentialExport_AcquiresClientPerPage_NotOncePerEntity()
    {
        // E1: ExportEntitySequentialAsync must acquire a pooled client per page
        // and release before the next await. CLAUDE.md NEVER: "Hold single pooled
        // client for multiple queries."
        var entitySchema = CreateAccountSchema();
        var schema = CreateSchema(entitySchema);
        var options = new ExportOptions
        {
            PageLevelParallelism = 1, // force sequential path
            PageSize = 2,
            DegreeOfParallelism = 1
        };

        var getClientCallCount = 0;
        var dataPageCalls = 0; // shared across client instances

        var pool = new Mock<IDataverseConnectionPool>();
        pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref getClientCallCount);
                var client = new Mock<IPooledClient>();

                client
                    .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
                    .ReturnsAsync((QueryBase query) =>
                    {
                        var fetchExpr = query as FetchExpression;
                        var xml = fetchExpr?.Query ?? "";

                        // Aggregate count query is a one-off
                        if (xml.Contains("aggregate"))
                        {
                            var countEntity = new Entity("account");
                            countEntity["cnt"] = new AliasedValue("account", "accountid", 4);
                            return new EntityCollection(new List<Entity> { countEntity });
                        }

                        // Data query — termination tracked across clients so the
                        // per-page acquire loop actually stops.
                        var dataIndex = Interlocked.Increment(ref dataPageCalls);
                        return new EntityCollection(new List<Entity>
                        {
                            new Entity("account", Guid.NewGuid()),
                            new Entity("account", Guid.NewGuid())
                        })
                        { MoreRecords = dataIndex == 1 };
                    });

                client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                return client.Object;
            });

        var exporter = CreateExporter(pool);

        // Act
        var result = await exporter.ExportAsync(schema, "output.zip", options);

        // Assert — 1 (count) + 2 (one per page) = 3 acquisitions minimum.
        result.Success.Should().BeTrue();
        getClientCallCount.Should().BeGreaterThanOrEqualTo(3,
            "E1: sequential export must acquire a client per page");
    }

    [Fact]
    public async Task PartitionExport_AcquiresClientPerPage_NotOncePerPartition()
    {
        // E2: ExportPartitionAsync must acquire per page as well.
        var entitySchema = CreateAccountSchema();
        var schema = CreateSchema(entitySchema);
        var options = new ExportOptions
        {
            PageLevelParallelism = 2, // force exactly 2 partitions
            PageSize = 2,
            DegreeOfParallelism = 1
        };

        var getClientCallCount = 0;
        var pool = new Mock<IDataverseConnectionPool>();
        pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref getClientCallCount);
                var client = new Mock<IPooledClient>();

                var calls = 0;
                client
                    .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
                    .ReturnsAsync((QueryBase query) =>
                    {
                        var fetchExpr = query as FetchExpression;
                        var xml = fetchExpr?.Query ?? "";

                        if (xml.Contains("aggregate"))
                        {
                            var countEntity = new Entity("account");
                            countEntity["cnt"] = new AliasedValue("account", "accountid", 100_000);
                            return new EntityCollection(new List<Entity> { countEntity });
                        }

                        // Data page: return one record, MoreRecords=false.
                        calls++;
                        return new EntityCollection(new List<Entity>
                        {
                            new Entity("account", Guid.NewGuid())
                        })
                        { MoreRecords = false };
                    });

                client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                return client.Object;
            });

        var exporter = CreateExporter(pool);

        // Act
        var result = await exporter.ExportAsync(schema, "output.zip", options);

        // Assert — 1 (count) + 2 (one per partition's single page) = 3 minimum.
        result.Success.Should().BeTrue();
        getClientCallCount.Should().BeGreaterThanOrEqualTo(3,
            "E2: partitioned export must acquire a client per page");
    }

    [Fact]
    public async Task CountFailure_EmitsWarning_AndSurfacesSequentialFallback()
    {
        // F1: when the count query fails, log at Warning, emit a WarningCollector
        // entry, and surface the sequential fallback to the caller.
        var entitySchema = CreateAccountSchema();
        var schema = CreateSchema(entitySchema);
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 5000,
            DegreeOfParallelism = 1
        };

        var pool = new Mock<IDataverseConnectionPool>();
        pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var client = new Mock<IPooledClient>();

                client
                    .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
                    .ReturnsAsync((QueryBase query) =>
                    {
                        var fetchExpr = query as FetchExpression;
                        var xml = fetchExpr?.Query ?? "";

                        if (xml.Contains("aggregate"))
                        {
                            throw new InvalidOperationException(
                                "Simulated count failure (service protection).");
                        }

                        return new EntityCollection(new List<Entity>
                        {
                            new Entity("account", Guid.NewGuid())
                        })
                        { MoreRecords = false };
                    });

                client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                return client.Object;
            });

        var exporter = CreateExporter(pool);

        // Act
        var result = await exporter.ExportAsync(schema, "output.zip", options);

        // Assert — warning surfaced on result and the entity still exported.
        result.Success.Should().BeTrue();
        result.Warnings.Should().ContainSingle(w =>
            w.Code == ExportWarningCodes.CountFailedSequentialFallback
            && w.Entity == "account");
    }

    [Fact]
    public async Task UsesSequentialBelowThreshold()
    {
        // Arrange: record count of 3000 < threshold of 5000 => sequential
        var entitySchema = CreateAccountSchema();
        var schema = CreateSchema(entitySchema);
        var options = new ExportOptions
        {
            PageLevelParallelism = 0,
            PageLevelParallelismThreshold = 5000,
            DegreeOfParallelism = 1
        };

        var getClientCallCount = 0;

        var pool = new Mock<IDataverseConnectionPool>();
        pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref getClientCallCount);
                var client = new Mock<IPooledClient>();

                client
                    .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
                    .ReturnsAsync((QueryBase query) =>
                    {
                        var fetchExpr = query as FetchExpression;
                        var xml = fetchExpr?.Query ?? "";

                        // Aggregate count query
                        if (xml.Contains("aggregate"))
                        {
                            var countEntity = new Entity("account");
                            countEntity["cnt"] = new AliasedValue("account", "accountid", 3000);
                            return new EntityCollection(new List<Entity> { countEntity });
                        }

                        // Data query
                        return new EntityCollection(new List<Entity>
                        {
                            new Entity("account", Guid.NewGuid())
                        }) { MoreRecords = false };
                    });

                client
                    .Setup(c => c.DisposeAsync())
                    .Returns(ValueTask.CompletedTask);

                return client.Object;
            });

        var exporter = CreateExporter(pool);

        // Act
        var result = await exporter.ExportAsync(schema, "output.zip", options);

        // Assert
        result.Success.Should().BeTrue();
        // 1 GetClientAsync for count + 1 GetClientAsync for sequential export = 2
        getClientCallCount.Should().Be(2,
            "should have exactly 1 count query + 1 sequential data query");
    }

    [Fact]
    public async Task ProgressAggregatesAcrossPartitions()
    {
        // Arrange: use partitioning and verify progress reports aggregate
        var entitySchema = CreateAccountSchema();
        var schema = CreateSchema(entitySchema);
        var options = new ExportOptions
        {
            PageLevelParallelism = 2,
            PageLevelParallelismThreshold = 5000,
            DegreeOfParallelism = 1,
            ProgressInterval = 1 // Report frequently
        };

        var pool = new Mock<IDataverseConnectionPool>();
        pool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var client = new Mock<IPooledClient>();

                client
                    .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
                    .ReturnsAsync((QueryBase query) =>
                    {
                        var fetchExpr = query as FetchExpression;
                        var xml = fetchExpr?.Query ?? "";

                        // Aggregate count query
                        if (xml.Contains("aggregate"))
                        {
                            var countEntity = new Entity("account");
                            countEntity["cnt"] = new AliasedValue("account", "accountid", 10000);
                            return new EntityCollection(new List<Entity> { countEntity });
                        }

                        // Data query: return 3 records per partition
                        return new EntityCollection(new List<Entity>
                        {
                            new Entity("account", Guid.NewGuid()),
                            new Entity("account", Guid.NewGuid()),
                            new Entity("account", Guid.NewGuid())
                        }) { MoreRecords = false };
                    });

                client
                    .Setup(c => c.DisposeAsync())
                    .Returns(ValueTask.CompletedTask);

                return client.Object;
            });

        var progressReports = new ConcurrentBag<ProgressEventArgs>();
        var mockProgress = new Mock<IProgressReporter>();
        mockProgress
            .Setup(p => p.Report(It.IsAny<ProgressEventArgs>()))
            .Callback<ProgressEventArgs>(args => progressReports.Add(args));

        var exporter = CreateExporter(pool);

        // Act
        var result = await exporter.ExportAsync(schema, "output.zip", options, mockProgress.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.RecordsExported.Should().Be(6, "2 partitions x 3 records each");

        // Verify progress was reported for the account entity during export
        var entityProgressReports = progressReports
            .Where(p => p.Entity == "account" && p.Phase == MigrationPhase.Exporting)
            .ToList();

        entityProgressReports.Should().NotBeEmpty(
            "progress should be reported for each partition completing");

        // At least one progress report should show the aggregated count
        // (when both partitions have completed, the total should be 6)
        var maxReportedCount = entityProgressReports.Max(p => p.Current);
        maxReportedCount.Should().Be(6,
            "the final progress report should show all records from all partitions");
    }

    #endregion
}
