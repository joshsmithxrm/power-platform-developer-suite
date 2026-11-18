using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>TopOverride limits result sets; it must never change which rows a DML statement affects.</summary>
[Trait("Category", "Unit")]
public class DmlTopOverrideTests
{
    private static readonly Guid[] Ids =
    {
        Guid.Parse("00000000-0000-0000-0000-00000000000a"),
        Guid.Parse("00000000-0000-0000-0000-00000000000b"),
        Guid.Parse("00000000-0000-0000-0000-00000000000c"),
    };

    private static (SqlQueryService service, Mock<IBulkOperationExecutor> bulk, List<int> deleted, List<int> updated) Create()
    {
        var executor = new Mock<IQueryExecutor>(MockBehavior.Loose);
        var records = Ids.Select(id => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>
        {
            ["accountid"] = QueryValue.Simple(id)
        }).ToArray();
        var fetched = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[] { new QueryColumn { LogicalName = "accountid" } },
            Records = records,
            Count = records.Length
        };
        // Like Dataverse, the fake honours a top or count in the FetchXML it receives, so a row limit that leaked into the
        // DML source scan would make these statements change fewer rows and the tests fail.
        QueryResult Limited(string fetchXml)
        {
            var root = System.Xml.Linq.XDocument.Parse(fetchXml).Root!;
            var limit = (string?)root.Attribute("top") ?? (string?)root.Attribute("count");
            if (limit == null)
                return fetched;
            var taken = records.Take(int.Parse(limit, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return new QueryResult { EntityLogicalName = "account", Columns = fetched.Columns, Records = taken, Count = taken.Length };
        }
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string f, int? _, string? _, bool _, CancellationToken _) => Limited(f));
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string f, int? _, string? _, bool _, QueryExecutionOptions? _, CancellationToken _) => Limited(f));

        var deleted = new List<int>();
        var updated = new List<int>();
        var bulk = new Mock<IBulkOperationExecutor>();
        bulk.Setup(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<Guid> ids, BulkOperationOptions? _, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? _, CancellationToken _) =>
            {
                var n = ids.Count();
                deleted.Add(n);
                return Task.FromResult(new BulkOperationResult { SuccessCount = n });
            });
        bulk.Setup(b => b.UpdateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<Microsoft.Xrm.Sdk.Entity> e, BulkOperationOptions? _, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? _, CancellationToken _) =>
            {
                var n = e.Count();
                updated.Add(n);
                return Task.FromResult(new BulkOperationResult { SuccessCount = n });
            });

        var service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object, metadataProvider: PPDS.Cli.Tests.Services.Query.TestMetadata.Provider)
        {
            EnvironmentProtectionLevel = ProtectionLevel.Development
        };
        return (service, bulk, deleted, updated);
    }

    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    [Fact]
    public async Task Delete_WithTopOverride_StillDeletesEveryMatchingRow()
    {
        var (service, _, deleted, _) = Create();

        var result = await service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x'",
            TopOverride = 1,
            DmlSafety = Confirmed
        });

        Assert.Equal(new[] { 3 }, deleted);
        Assert.Equal(3, result.DmlOutcome!.AffectedRows);
    }

    [Fact]
    public async Task Update_WithTopOverride_StillUpdatesEveryMatchingRow()
    {
        var (service, _, _, updated) = Create();

        await service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'y' WHERE name = 'x'",
            TopOverride = 1,
            DmlSafety = Confirmed
        });

        Assert.Equal(new[] { 3 }, updated);
    }

    [Fact]
    public async Task Script_WithTopOverride_StillDeletesEveryMatchingRow()
    {
        var (service, _, deleted, _) = Create();

        await service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x'; DELETE FROM account WHERE name = 'z'",
            TopOverride = 1,
            DmlSafety = Confirmed
        });

        Assert.Equal(new[] { 3, 3 }, deleted);
    }

    [Fact]
    public async Task Streaming_Delete_WithTopOverride_StillDeletesEveryMatchingRow()
    {
        var (service, _, deleted, _) = Create();

        await foreach (var _ in service.ExecuteStreamingAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x'",
            TopOverride = 1,
            DmlSafety = Confirmed
        })) { }

        Assert.Equal(new[] { 3 }, deleted);
    }

    [Fact]
    public async Task TheFake_HonoursTop_SoALeakedLimitWouldBeSeen()
    {
        var (service, _, _, _) = Create();

        var result = await service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT TOP 1 accountid FROM account" });

        Assert.Equal(1, result.Result.Count);
    }
}
